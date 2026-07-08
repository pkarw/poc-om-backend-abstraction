using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Core.Events;
using OpenMercato.Modules.Customers.Api;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Commands;

/// <summary>
/// Shared write helpers for the deals command handlers — pipeline-stage resolution + the
/// <c>pipeline_stage</c> dictionary upsert (upstream <c>ensureDictionaryEntry</c>), deal↔person/company
/// link syncing, stage-transition upsert (unique per deal+stage), and undo/redo snapshots.
/// </summary>
internal static class DealWriteHelpers
{
    public const string DealEntityType = "customers:customer_deal";

    /// <summary>Read a <c>string[]</c> uuid array from the body. Returns null when the key is absent
    /// (update: "leave links untouched"); empty list when present-but-empty (clears links).</summary>
    public static List<Guid>? ReadGuidArray(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var arr)) return null;
        var result = new List<Guid>();
        if (arr.ValueKind == JsonValueKind.Array)
            foreach (var el in arr.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String && Guid.TryParse(el.GetString(), out var g)) result.Add(g);
        return result;
    }

    public static Task<CustomerPipelineStage?> LoadStageAsync(AppDbContext db, Guid stageId, Guid tenantId, Guid organizationId) =>
        db.Set<CustomerPipelineStage>().FirstOrDefaultAsync(s =>
            s.Id == stageId && s.TenantId == tenantId && s.OrganizationId == organizationId);

    /// <summary>Upsert the <c>pipeline_stage</c> dictionary entry for a stage label and return the canonical
    /// value (upstream <c>ensureDictionaryEntry(...).value ?? label</c>).</summary>
    public static async Task<string> EnsureStageDictionaryValueAsync(
        AppDbContext db, Guid tenantId, Guid organizationId, string label, bool colorPresent = false,
        string? color = null, bool iconPresent = false, string? icon = null)
    {
        var trimmed = label.Trim();
        if (trimmed.Length == 0) return label;
        var normalized = trimmed.ToLowerInvariant();
        var existing = await db.Set<CustomerDictionaryEntry>().FirstOrDefaultAsync(e =>
            e.TenantId == tenantId && e.OrganizationId == organizationId &&
            e.Kind == "pipeline_stage" && e.NormalizedValue == normalized);
        if (existing is not null)
        {
            var changed = false;
            if (colorPresent && existing.Color != color) { existing.Color = color; changed = true; }
            if (iconPresent && existing.Icon != icon) { existing.Icon = icon; changed = true; }
            if (changed) existing.UpdatedAt = DateTimeOffset.UtcNow;
            return existing.Value;
        }
        var now = DateTimeOffset.UtcNow;
        var entry = new CustomerDictionaryEntry
        {
            Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId, Kind = "pipeline_stage",
            Value = trimmed, NormalizedValue = normalized, Label = trimmed,
            Color = colorPresent ? color : null, Icon = iconPresent ? icon : null, CreatedAt = now, UpdatedAt = now,
        };
        db.Set<CustomerDictionaryEntry>().Add(entry);
        return trimmed;
    }

    public static async Task UpsertTransitionAsync(
        AppDbContext db, CustomerDeal deal, Guid pipelineId, Guid stageId, string stageLabel, int stageOrder, Guid? byUserId)
    {
        var existing = await db.Set<CustomerDealStageTransition>().FirstOrDefaultAsync(t =>
            t.DealId == deal.Id && t.StageId == stageId && t.DeletedAt == null);
        var now = DateTimeOffset.UtcNow;
        if (existing is not null)
        {
            existing.PipelineId = pipelineId; existing.StageLabel = stageLabel; existing.StageOrder = stageOrder;
            existing.TransitionedAt = now; existing.TransitionedByUserId = byUserId; existing.DeletedAt = null;
            existing.IsActive = true; existing.UpdatedAt = now;
            return;
        }
        db.Set<CustomerDealStageTransition>().Add(new CustomerDealStageTransition
        {
            Id = Guid.NewGuid(), OrganizationId = deal.OrganizationId, TenantId = deal.TenantId, DealId = deal.Id,
            PipelineId = pipelineId, StageId = stageId, StageLabel = stageLabel, StageOrder = stageOrder,
            TransitionedAt = now, TransitionedByUserId = byUserId, IsActive = true, CreatedAt = now, UpdatedAt = now,
        });
    }

    /// <summary>Replace the deal↔person links (null = leave untouched). Validates each person exists in scope.</summary>
    public static async Task SyncPeopleAsync(AppDbContext db, CustomerDeal deal, List<Guid>? personIds)
    {
        if (personIds is null) return;
        var existing = await db.Set<CustomerDealPersonLink>().Where(l => l.DealId == deal.Id).ToListAsync();
        db.Set<CustomerDealPersonLink>().RemoveRange(existing);
        var now = DateTimeOffset.UtcNow;
        foreach (var personId in personIds.Distinct())
        {
            var person = await db.Set<CustomerEntity>().FirstOrDefaultAsync(e =>
                e.Id == personId && e.Kind == "person" && e.DeletedAt == null &&
                e.TenantId == deal.TenantId && e.OrganizationId == deal.OrganizationId);
            if (person is null) throw CommandHttpException.NotFound("Person not found");
            db.Set<CustomerDealPersonLink>().Add(new CustomerDealPersonLink
            {
                Id = Guid.NewGuid(), DealId = deal.Id, PersonEntityId = personId, CreatedAt = now,
            });
        }
    }

    public static async Task SyncCompaniesAsync(AppDbContext db, CustomerDeal deal, List<Guid>? companyIds)
    {
        if (companyIds is null) return;
        var existing = await db.Set<CustomerDealCompanyLink>().Where(l => l.DealId == deal.Id).ToListAsync();
        db.Set<CustomerDealCompanyLink>().RemoveRange(existing);
        var now = DateTimeOffset.UtcNow;
        foreach (var companyId in companyIds.Distinct())
        {
            var company = await db.Set<CustomerEntity>().FirstOrDefaultAsync(e =>
                e.Id == companyId && e.Kind == "company" && e.DeletedAt == null &&
                e.TenantId == deal.TenantId && e.OrganizationId == deal.OrganizationId);
            if (company is null) throw CommandHttpException.NotFound("Company not found");
            db.Set<CustomerDealCompanyLink>().Add(new CustomerDealCompanyLink
            {
                Id = Guid.NewGuid(), DealId = deal.Id, CompanyEntityId = companyId, CreatedAt = now,
            });
        }
    }

    public static async Task<DealSnapshot?> LoadSnapshotAsync(AppDbContext db, Guid dealId)
    {
        var d = await db.Set<CustomerDeal>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == dealId);
        if (d is null) return null;
        var people = await db.Set<CustomerDealPersonLink>().AsNoTracking().Where(l => l.DealId == dealId)
            .Select(l => l.PersonEntityId).ToListAsync();
        var companies = await db.Set<CustomerDealCompanyLink>().AsNoTracking().Where(l => l.DealId == dealId)
            .Select(l => l.CompanyEntityId).ToListAsync();
        var transitions = await db.Set<CustomerDealStageTransition>().AsNoTracking()
            .Where(t => t.DealId == dealId && t.DeletedAt == null)
            .OrderBy(t => t.StageOrder).ThenBy(t => t.TransitionedAt)
            .Select(t => new DealTransitionSnapshot(t.Id, t.PipelineId, t.StageId, t.StageLabel, t.StageOrder, t.TransitionedAt, t.TransitionedByUserId))
            .ToListAsync();
        return new DealSnapshot(d.Id, d.OrganizationId, d.TenantId, d.Title, d.Description, d.Status, d.PipelineStage,
            d.PipelineId, d.PipelineStageId, d.ValueAmount, d.ValueCurrency, d.Probability, d.ExpectedCloseAt,
            d.OwnerUserId, d.Source, d.ClosureOutcome, d.LossReasonId, d.LossNotes, d.CreatedAt, d.UpdatedAt,
            people, companies, transitions);
    }

    /// <summary>Re-materialize a deal (+ links + transitions) from a snapshot, reusing its id (undo/redo).</summary>
    public static async Task RestoreAsync(AppDbContext db, DealSnapshot s)
    {
        var deal = await db.Set<CustomerDeal>().FirstOrDefaultAsync(x => x.Id == s.Id);
        if (deal is null)
        {
            deal = new CustomerDeal { Id = s.Id };
            db.Set<CustomerDeal>().Add(deal);
        }
        deal.OrganizationId = s.OrganizationId; deal.TenantId = s.TenantId; deal.Title = s.Title;
        deal.Description = s.Description; deal.Status = s.Status; deal.PipelineStage = s.PipelineStage;
        deal.PipelineId = s.PipelineId; deal.PipelineStageId = s.PipelineStageId; deal.ValueAmount = s.ValueAmount;
        deal.ValueCurrency = s.ValueCurrency; deal.Probability = s.Probability; deal.ExpectedCloseAt = s.ExpectedCloseAt;
        deal.OwnerUserId = s.OwnerUserId; deal.Source = s.Source; deal.ClosureOutcome = s.ClosureOutcome;
        deal.LossReasonId = s.LossReasonId; deal.LossNotes = s.LossNotes; deal.CreatedAt = s.CreatedAt;
        deal.UpdatedAt = s.UpdatedAt; deal.DeletedAt = null;

        db.Set<CustomerDealPersonLink>().RemoveRange(await db.Set<CustomerDealPersonLink>().Where(l => l.DealId == s.Id).ToListAsync());
        db.Set<CustomerDealCompanyLink>().RemoveRange(await db.Set<CustomerDealCompanyLink>().Where(l => l.DealId == s.Id).ToListAsync());
        db.Set<CustomerDealStageTransition>().RemoveRange(await db.Set<CustomerDealStageTransition>().Where(t => t.DealId == s.Id).ToListAsync());
        var now = DateTimeOffset.UtcNow;
        foreach (var p in s.People.Distinct())
            db.Set<CustomerDealPersonLink>().Add(new CustomerDealPersonLink { Id = Guid.NewGuid(), DealId = s.Id, PersonEntityId = p, CreatedAt = now });
        foreach (var c in s.Companies.Distinct())
            db.Set<CustomerDealCompanyLink>().Add(new CustomerDealCompanyLink { Id = Guid.NewGuid(), DealId = s.Id, CompanyEntityId = c, CreatedAt = now });
        foreach (var t in s.Transitions)
            db.Set<CustomerDealStageTransition>().Add(new CustomerDealStageTransition
            {
                Id = t.Id, OrganizationId = s.OrganizationId, TenantId = s.TenantId, DealId = s.Id, PipelineId = t.PipelineId,
                StageId = t.StageId, StageLabel = t.StageLabel, StageOrder = t.StageOrder, TransitionedAt = t.TransitionedAt,
                TransitionedByUserId = t.TransitionedByUserId, IsActive = true, CreatedAt = now, UpdatedAt = now,
            });
    }

    public static async Task HardDeleteAsync(AppDbContext db, Guid dealId)
    {
        db.Set<CustomerDealStageTransition>().RemoveRange(await db.Set<CustomerDealStageTransition>().Where(t => t.DealId == dealId).ToListAsync());
        db.Set<CustomerDealPersonLink>().RemoveRange(await db.Set<CustomerDealPersonLink>().Where(l => l.DealId == dealId).ToListAsync());
        db.Set<CustomerDealCompanyLink>().RemoveRange(await db.Set<CustomerDealCompanyLink>().Where(l => l.DealId == dealId).ToListAsync());
        var deal = await db.Set<CustomerDeal>().FirstOrDefaultAsync(x => x.Id == dealId);
        if (deal is not null) db.Set<CustomerDeal>().Remove(deal);
    }
}

/// <summary>
/// <c>customers.deals.create</c> — inserts <c>customer_deals</c>, resolves the pipeline stage (400
/// <c>Pipeline stage not found</c> when the requested stage id is unknown; ensures the
/// <c>pipeline_stage</c> dictionary entry), records the initial stage transition, syncs deal↔person/company
/// links, and persists <c>cf_*</c> under <c>customers:customer_deal</c>. Undoable (undo hard-deletes).
/// </summary>
public sealed class CreateDealCommand
    : ICommand<DealCreateInput, DealResult>,
      ICommandLogMetadataBuilder<DealCreateInput, DealResult>,
      IUndoableCommand
{
    public string CommandId => "customers.deals.create";
    private DealSnapshot? _after;

    public async Task<DealResult> ExecuteAsync(DealCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var body = input.Body;
        var now = DateTimeOffset.UtcNow;

        var pipelineStageId = J.GuidOf(body, "pipelineStageId");
        var requestedPipelineId = J.GuidOf(body, "pipelineId");
        CustomerPipelineStage? stage = null;
        if (pipelineStageId is { } psid)
        {
            stage = await DealWriteHelpers.LoadStageAsync(db, psid, input.TenantId, input.OrganizationId);
            if (stage is null) throw CommandHttpException.BadRequest("Pipeline stage not found");
        }
        if (requestedPipelineId is { } pid && stage is not null && stage.PipelineId != pid)
            throw CommandHttpException.BadRequest("Pipeline stage does not belong to the selected pipeline");

        var effectivePipelineId = requestedPipelineId ?? stage?.PipelineId;
        var resolvedStageLabel = stage is not null
            ? await DealWriteHelpers.EnsureStageDictionaryValueAsync(db, input.TenantId, input.OrganizationId, stage.Label)
            : J.Str(body, "pipelineStage");

        var deal = new CustomerDeal
        {
            Id = Guid.NewGuid(), OrganizationId = input.OrganizationId, TenantId = input.TenantId,
            Title = J.Str(body, "title")?.Trim() ?? string.Empty,
            Description = J.Str(body, "description"),
            Status = J.Str(body, "status") ?? "open",
            PipelineStage = resolvedStageLabel,
            PipelineId = effectivePipelineId,
            PipelineStageId = pipelineStageId,
            ValueAmount = J.Decimal(body, "valueAmount"),
            ValueCurrency = J.Str(body, "valueCurrency"),
            Probability = J.Int(body, "probability"),
            ExpectedCloseAt = CustomersHttp.Date(body, "expectedCloseAt"),
            OwnerUserId = J.GuidOf(body, "ownerUserId"),
            Source = J.Str(body, "source"),
            ClosureOutcome = J.Str(body, "closureOutcome"),
            LossReasonId = J.GuidOf(body, "lossReasonId"),
            LossNotes = J.Str(body, "lossNotes"),
            CreatedAt = now, UpdatedAt = now,
        };
        db.Set<CustomerDeal>().Add(deal);
        await db.SaveChangesAsync();

        if (stage is not null)
            await DealWriteHelpers.UpsertTransitionAsync(db, deal, stage.PipelineId, stage.Id, resolvedStageLabel ?? stage.Label, stage.Order, ctx.UserId);

        await DealWriteHelpers.SyncPeopleAsync(db, deal, DealWriteHelpers.ReadGuidArray(body, "personIds") ?? new List<Guid>());
        await DealWriteHelpers.SyncCompaniesAsync(db, deal, DealWriteHelpers.ReadGuidArray(body, "companyIds") ?? new List<Guid>());
        await db.SaveChangesAsync();

        await CustomerWriteHelpers.PersistCustomFieldsAsync(services, DealWriteHelpers.DealEntityType, deal.Id, body, ctx);
        _after = await DealWriteHelpers.LoadSnapshotAsync(db, deal.Id);
        return new DealResult(deal.Id.ToString());
    }

    public CommandLogMetadata BuildLog(DealCreateInput input, DealResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create deal",
        ResourceKind = "customers.deal",
        ResourceId = result.DealId,
        TenantId = input.TenantId,
        OrganizationId = input.OrganizationId,
        SnapshotAfter = _after,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        await DealWriteHelpers.HardDeleteAsync(db, Guid.Parse(log.ResourceId!));
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var after = log.GetSnapshotAfter<DealSnapshot>();
        if (after is not null) await DealWriteHelpers.RestoreAsync(db, after);
    }
}

/// <summary>
/// <c>customers.deals.update</c> — applies changed fields, re-derives the pipeline-stage label on stage
/// change (recording a <c>customer_deal_stage_transitions</c> row when the stage id changes), syncs links,
/// persists cf, and emits <c>customers.deal.won</c>/<c>customers.deal.lost</c> when status transitions to
/// win/won or loose/lost (normalized). 404 <c>Deal not found</c>. Undoable.
/// </summary>
public sealed class UpdateDealCommand
    : ICommand<DealUpdateInput, DealResult>,
      ICommandLogMetadataBuilder<DealUpdateInput, DealResult>,
      IUndoableCommand
{
    public string CommandId => "customers.deals.update";
    private DealSnapshot? _before;
    private DealSnapshot? _after;

    public async Task<DealResult> ExecuteAsync(DealUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var body = input.Body;
        var deal = await db.Set<CustomerDeal>().FirstOrDefaultAsync(d => d.Id == input.Id && d.DeletedAt == null);
        if (deal is null) throw CommandHttpException.NotFound("Deal not found");
        if (ctx.TenantId is { } t && deal.TenantId != t) throw CommandHttpException.NotFound("Deal not found");

        _before = await DealWriteHelpers.LoadSnapshotAsync(db, deal.Id);
        var previousStatus = deal.Status;
        var previousStageId = deal.PipelineStageId;

        bool Has(string name) => CustomersHttp.Has(body, name);

        // ---- pipeline assignment ----
        var stageChanged = Has("pipelineStageId");
        var pipelineChanged = Has("pipelineId");
        CustomerPipelineStage? nextStage = null;
        string? nextStageLabel = null;
        if (stageChanged)
        {
            var requestedStageId = J.GuidOf(body, "pipelineStageId");
            if (requestedStageId is { } rsid)
            {
                nextStage = await DealWriteHelpers.LoadStageAsync(db, rsid, deal.TenantId, deal.OrganizationId);
                if (nextStage is null) throw CommandHttpException.BadRequest("Pipeline stage not found");
                var requestedPipelineId = pipelineChanged ? J.GuidOf(body, "pipelineId") : deal.PipelineId;
                if (requestedPipelineId is { } pid && nextStage.PipelineId != pid)
                    throw CommandHttpException.BadRequest("Pipeline stage does not belong to the selected pipeline");
                nextStageLabel = await DealWriteHelpers.EnsureStageDictionaryValueAsync(db, deal.TenantId, deal.OrganizationId, nextStage.Label);
                deal.PipelineStageId = nextStage.Id;
                deal.PipelineId = requestedPipelineId ?? nextStage.PipelineId;
                deal.PipelineStage = nextStageLabel;
            }
            else
            {
                deal.PipelineStageId = null;
                if (pipelineChanged) deal.PipelineId = J.GuidOf(body, "pipelineId");
            }
        }
        else if (pipelineChanged)
        {
            deal.PipelineId = J.GuidOf(body, "pipelineId");
        }

        // ---- scalar fields ----
        if (Has("title")) deal.Title = J.Str(body, "title")?.Trim() ?? deal.Title;
        if (Has("description")) deal.Description = J.Str(body, "description");
        if (Has("status")) deal.Status = J.Str(body, "status") ?? deal.Status;
        if (Has("pipelineStage") && !stageChanged) deal.PipelineStage = J.Str(body, "pipelineStage");
        if (Has("valueAmount")) deal.ValueAmount = J.Decimal(body, "valueAmount");
        if (Has("valueCurrency")) deal.ValueCurrency = J.Str(body, "valueCurrency");
        if (Has("probability")) deal.Probability = J.Int(body, "probability");
        if (Has("expectedCloseAt")) deal.ExpectedCloseAt = CustomersHttp.Date(body, "expectedCloseAt");
        if (Has("ownerUserId")) deal.OwnerUserId = J.GuidOf(body, "ownerUserId");
        if (Has("source")) deal.Source = J.Str(body, "source");
        if (Has("closureOutcome")) deal.ClosureOutcome = J.Str(body, "closureOutcome");
        if (Has("lossReasonId")) deal.LossReasonId = J.GuidOf(body, "lossReasonId");
        if (Has("lossNotes")) deal.LossNotes = J.Str(body, "lossNotes");

        deal.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // Record the transition only when the stage id actually changed to a new non-null stage.
        if (nextStage is not null && deal.PipelineStageId is { } newStageId && newStageId != previousStageId)
            await DealWriteHelpers.UpsertTransitionAsync(db, deal, nextStage.PipelineId, nextStage.Id, nextStageLabel ?? nextStage.Label, nextStage.Order, ctx.UserId);

        await DealWriteHelpers.SyncPeopleAsync(db, deal, DealWriteHelpers.ReadGuidArray(body, "personIds"));
        await DealWriteHelpers.SyncCompaniesAsync(db, deal, DealWriteHelpers.ReadGuidArray(body, "companyIds"));
        await db.SaveChangesAsync();

        await CustomerWriteHelpers.PersistCustomFieldsAsync(services, DealWriteHelpers.DealEntityType, deal.Id, body, ctx);

        // Closure lifecycle event — win/won → customers.deal.won, loose/lost → customers.deal.lost.
        var newStatus = deal.Status;
        var normalized = newStatus == "win" ? "won" : newStatus == "loose" ? "lost" : newStatus;
        if (previousStatus != newStatus && normalized is "won" or "lost")
        {
            var evt = normalized == "won" ? "customers.deal.won" : "customers.deal.lost";
            var events = services.GetService<IEventBus>();
            if (events is not null)
                await events.PublishAsync(evt, new
                {
                    id = deal.Id.ToString(),
                    tenantId = deal.TenantId.ToString(),
                    organizationId = deal.OrganizationId.ToString(),
                    ownerUserId = deal.OwnerUserId?.ToString(),
                    title = deal.Title,
                    valueAmount = deal.ValueAmount,
                    valueCurrency = deal.ValueCurrency,
                });
        }

        _after = await DealWriteHelpers.LoadSnapshotAsync(db, deal.Id);
        return new DealResult(deal.Id.ToString());
    }

    public CommandLogMetadata BuildLog(DealUpdateInput input, DealResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update deal",
        ResourceKind = "customers.deal",
        ResourceId = result.DealId,
        TenantId = _before?.TenantId,
        OrganizationId = _before?.OrganizationId,
        SnapshotBefore = _before,
        SnapshotAfter = _after,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var before = log.GetSnapshotBefore<DealSnapshot>();
        if (before is not null) await DealWriteHelpers.RestoreAsync(db, before);
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var after = log.GetSnapshotAfter<DealSnapshot>();
        if (after is not null) await DealWriteHelpers.RestoreAsync(db, after);
    }
}

/// <summary><c>customers.deals.delete</c> — hard-deletes the deal + its links + stage transitions
/// (upstream <c>em.remove</c>). 404 <c>Deal not found</c>. Undoable (re-materializes from snapshot).</summary>
public sealed class DeleteDealCommand
    : ICommand<DealDeleteInput, DealResult>,
      ICommandLogMetadataBuilder<DealDeleteInput, DealResult>,
      IUndoableCommand
{
    public string CommandId => "customers.deals.delete";
    private DealSnapshot? _before;

    public async Task<DealResult> ExecuteAsync(DealDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var deal = await db.Set<CustomerDeal>().FirstOrDefaultAsync(d => d.Id == input.Id && d.DeletedAt == null);
        if (deal is null) throw CommandHttpException.NotFound("Deal not found");
        if (ctx.TenantId is { } t && deal.TenantId != t) throw CommandHttpException.NotFound("Deal not found");
        _before = await DealWriteHelpers.LoadSnapshotAsync(db, deal.Id);
        await DealWriteHelpers.HardDeleteAsync(db, deal.Id);
        return new DealResult(deal.Id.ToString());
    }

    public CommandLogMetadata BuildLog(DealDeleteInput input, DealResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete deal",
        ResourceKind = "customers.deal",
        ResourceId = result.DealId,
        TenantId = _before?.TenantId,
        OrganizationId = _before?.OrganizationId,
        SnapshotBefore = _before,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var before = log.GetSnapshotBefore<DealSnapshot>();
        if (before is not null) await DealWriteHelpers.RestoreAsync(db, before);
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        await DealWriteHelpers.HardDeleteAsync(db, Guid.Parse(log.ResourceId!));
    }
}
