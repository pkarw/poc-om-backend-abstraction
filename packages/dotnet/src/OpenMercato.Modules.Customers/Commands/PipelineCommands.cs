using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Api;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Commands;

// Pipelines + pipeline-stages command handlers — the port of commands/pipelines.ts +
// commands/pipeline-stages.ts. Hand-written command-bus writes (NOT makeCrudRoute): non-undoable,
// so no undo token / x-om-operation header is minted (parity with upstream). Validation failures throw
// a plain exception so the route's catch-all maps them to the generic 400 'Failed to …' body (mirrors
// the ZodError → generic-catch behavior upstream). Scope errors (404/409) throw CommandHttpException.

/// <summary><c>customers.pipelines.create</c> — inserts a pipeline; when <c>isDefault</c>, clears the
/// previous default in the same scope.</summary>
public sealed class CreatePipelineCommand : ICommand<PipelineCreateInput, PipelineResult>
{
    public string CommandId => "customers.pipelines.create";

    public async Task<PipelineResult> ExecuteAsync(PipelineCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var body = input.Body;
        var name = CustomersHttp.Str(body, "name")?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200) throw new ArgumentException("name is required");
        var isDefault = CustomersHttp.Bool(body, "isDefault") ?? false;
        var now = DateTimeOffset.UtcNow;

        if (isDefault)
        {
            var defaults = await db.Set<CustomerPipeline>()
                .Where(p => p.OrganizationId == input.OrganizationId && p.TenantId == input.TenantId && p.IsDefault).ToListAsync();
            foreach (var d in defaults) d.IsDefault = false;
        }

        var pipeline = new CustomerPipeline
        {
            Id = Guid.NewGuid(), OrganizationId = input.OrganizationId, TenantId = input.TenantId,
            Name = name, IsDefault = isDefault, CreatedAt = now, UpdatedAt = now,
        };
        db.Set<CustomerPipeline>().Add(pipeline);
        return new PipelineResult(pipeline.Id.ToString());
    }
}

/// <summary><c>customers.pipelines.update</c> — 404 when missing/out-of-scope; toggles default exclusivity.</summary>
public sealed class UpdatePipelineCommand : ICommand<PipelineUpdateInput, PipelineResult>
{
    public string CommandId => "customers.pipelines.update";

    public async Task<PipelineResult> ExecuteAsync(PipelineUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var body = input.Body;
        var id = CustomersHttp.GuidOf(body, "id");
        if (id is null) throw new ArgumentException("id is required");
        var pipeline = await db.Set<CustomerPipeline>().FirstOrDefaultAsync(p => p.Id == id.Value);
        if (pipeline is null || pipeline.TenantId != input.TenantId || pipeline.OrganizationId != input.OrganizationId)
            throw CommandHttpException.NotFound("Pipeline not found");

        var hasName = CustomersHttp.Has(body, "name");
        var name = CustomersHttp.Str(body, "name")?.Trim();
        if (hasName && (string.IsNullOrEmpty(name) || name!.Length > 200)) throw new ArgumentException("name is invalid");
        var isDefault = CustomersHttp.Bool(body, "isDefault");

        if (isDefault == true && !pipeline.IsDefault)
        {
            var defaults = await db.Set<CustomerPipeline>()
                .Where(p => p.OrganizationId == pipeline.OrganizationId && p.TenantId == pipeline.TenantId && p.IsDefault).ToListAsync();
            foreach (var d in defaults) d.IsDefault = false;
        }
        if (hasName) pipeline.Name = name!;
        if (isDefault is { } def) pipeline.IsDefault = def;
        pipeline.UpdatedAt = DateTimeOffset.UtcNow;
        return new PipelineResult(pipeline.Id.ToString());
    }
}

/// <summary><c>customers.pipelines.delete</c> — 404 missing; 409 <c>Cannot delete pipeline with active deals</c>.</summary>
public sealed class DeletePipelineCommand : ICommand<PipelineDeleteInput, PipelineResult>
{
    public string CommandId => "customers.pipelines.delete";

    public async Task<PipelineResult> ExecuteAsync(PipelineDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var pipeline = await db.Set<CustomerPipeline>().FirstOrDefaultAsync(p => p.Id == input.Id);
        if (pipeline is null || pipeline.TenantId != input.TenantId || pipeline.OrganizationId != input.OrganizationId)
            throw CommandHttpException.NotFound("Pipeline not found");
        var activeDeals = await db.Set<CustomerDeal>().CountAsync(d => d.PipelineId == input.Id && d.DeletedAt == null);
        if (activeDeals > 0) throw CommandHttpException.Conflict("Cannot delete pipeline with active deals");
        db.Set<CustomerPipeline>().Remove(pipeline);
        return new PipelineResult(pipeline.Id.ToString());
    }
}

/// <summary><c>customers.pipeline-stages.create</c> — inserts a stage (clamped/shift-on-insert order) and
/// upserts its <c>pipeline_stage</c> dictionary entry (color/icon).</summary>
public sealed class CreatePipelineStageCommand : ICommand<PipelineStageCreateInput, PipelineStageResult>
{
    public string CommandId => "customers.pipeline-stages.create";

    public async Task<PipelineStageResult> ExecuteAsync(PipelineStageCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var body = input.Body;
        var pipelineId = CustomersHttp.GuidOf(body, "pipelineId");
        if (pipelineId is null) throw new ArgumentException("pipelineId is required");
        var label = CustomersHttp.Str(body, "label")?.Trim();
        if (string.IsNullOrEmpty(label) || label.Length > 200) throw new ArgumentException("label is required");
        var (colorPresent, color) = ReadAppearance(body, "color");
        var (iconPresent, icon) = ReadAppearance(body, "icon");

        var existing = await db.Set<CustomerPipelineStage>()
            .Where(s => s.OrganizationId == input.OrganizationId && s.TenantId == input.TenantId && s.PipelineId == pipelineId.Value)
            .OrderBy(s => s.Order).ToListAsync();

        var requestedOrder = CustomersHttp.Int(body, "order");
        var insertOrder = requestedOrder is null ? existing.Count : Math.Max(0, Math.Min(requestedOrder.Value, existing.Count));
        if (requestedOrder is not null)
            foreach (var s in existing)
                if (s.Order >= insertOrder) { s.Order += 1; s.UpdatedAt = DateTimeOffset.UtcNow; }

        var now = DateTimeOffset.UtcNow;
        var stage = new CustomerPipelineStage
        {
            Id = Guid.NewGuid(), OrganizationId = input.OrganizationId, TenantId = input.TenantId,
            PipelineId = pipelineId.Value, Label = label, Order = insertOrder, CreatedAt = now, UpdatedAt = now,
        };
        db.Set<CustomerPipelineStage>().Add(stage);
        await DealWriteHelpers.EnsureStageDictionaryValueAsync(db, input.TenantId, input.OrganizationId, label, colorPresent, color, iconPresent, icon);
        return new PipelineStageResult(stage.Id.ToString());
    }

    internal static (bool Present, string? Value) ReadAppearance(JsonElement body, string key)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(key, out var v)) return (false, null);
        if (v.ValueKind == JsonValueKind.Null) return (true, null);
        var raw = v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
        return (true, string.IsNullOrEmpty(raw) ? null : raw);
    }
}

/// <summary><c>customers.pipeline-stages.update</c> — 404 out-of-scope; re-upserts the dictionary entry
/// when label/color/icon are supplied.</summary>
public sealed class UpdatePipelineStageCommand : ICommand<PipelineStageUpdateInput, PipelineStageResult>
{
    public string CommandId => "customers.pipeline-stages.update";

    public async Task<PipelineStageResult> ExecuteAsync(PipelineStageUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var body = input.Body;
        var id = CustomersHttp.GuidOf(body, "id");
        if (id is null) throw new ArgumentException("id is required");
        var stage = await db.Set<CustomerPipelineStage>().FirstOrDefaultAsync(s =>
            s.Id == id.Value && s.TenantId == input.TenantId && s.OrganizationId == input.OrganizationId);
        if (stage is null) throw CommandHttpException.NotFound("Pipeline stage not found");

        var hasLabel = CustomersHttp.Has(body, "label");
        var label = CustomersHttp.Str(body, "label")?.Trim();
        if (hasLabel && (string.IsNullOrEmpty(label) || label!.Length > 200)) throw new ArgumentException("label is invalid");
        var hasOrder = CustomersHttp.Has(body, "order");
        var order = CustomersHttp.Int(body, "order");
        var (colorPresent, color) = CreatePipelineStageCommand.ReadAppearance(body, "color");
        var (iconPresent, icon) = CreatePipelineStageCommand.ReadAppearance(body, "icon");

        if (hasLabel) stage.Label = label!;
        if (hasOrder && order is not null) stage.Order = order.Value;
        stage.UpdatedAt = DateTimeOffset.UtcNow;

        if (hasLabel || colorPresent || iconPresent)
            await DealWriteHelpers.EnsureStageDictionaryValueAsync(db, stage.TenantId, stage.OrganizationId, stage.Label, colorPresent, color, iconPresent, icon);
        return new PipelineStageResult(stage.Id.ToString());
    }
}

/// <summary><c>customers.pipeline-stages.delete</c> — 404 missing; 409 <c>Cannot delete pipeline stage with active deals</c>.</summary>
public sealed class DeletePipelineStageCommand : ICommand<PipelineStageDeleteInput, PipelineStageResult>
{
    public string CommandId => "customers.pipeline-stages.delete";

    public async Task<PipelineStageResult> ExecuteAsync(PipelineStageDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var stage = await db.Set<CustomerPipelineStage>().FirstOrDefaultAsync(s =>
            s.Id == input.Id && s.TenantId == input.TenantId && s.OrganizationId == input.OrganizationId);
        if (stage is null) throw CommandHttpException.NotFound("Pipeline stage not found");
        var activeDeals = await db.Set<CustomerDeal>().CountAsync(d => d.PipelineStageId == input.Id && d.DeletedAt == null);
        if (activeDeals > 0) throw CommandHttpException.Conflict("Cannot delete pipeline stage with active deals");
        db.Set<CustomerPipelineStage>().Remove(stage);
        return new PipelineStageResult(stage.Id.ToString());
    }
}

/// <summary><c>customers.pipeline-stages.reorder</c> — bulk-sets <c>position</c> for the supplied stages.</summary>
public sealed class ReorderPipelineStagesCommand : ICommand<PipelineStageReorderInput, PipelineStageResult>
{
    public string CommandId => "customers.pipeline-stages.reorder";

    public async Task<PipelineStageResult> ExecuteAsync(PipelineStageReorderInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var body = input.Body;
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("stages", out var stagesEl) || stagesEl.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("stages is required");
        var desired = new List<(Guid Id, int Order)>();
        foreach (var el in stagesEl.EnumerateArray())
        {
            var sid = CustomersHttp.GuidOf(el, "id");
            var order = CustomersHttp.Int(el, "order");
            if (sid is null || order is null || order < 0) throw new ArgumentException("invalid stage entry");
            desired.Add((sid.Value, order.Value));
        }
        if (desired.Count == 0) throw new ArgumentException("stages must not be empty");

        var ids = desired.Select(d => d.Id).ToList();
        var stages = await db.Set<CustomerPipelineStage>()
            .Where(s => ids.Contains(s.Id) && s.TenantId == input.TenantId && s.OrganizationId == input.OrganizationId).ToListAsync();
        var byId = stages.ToDictionary(s => s.Id);
        foreach (var (sid, order) in desired)
            if (byId.TryGetValue(sid, out var stage)) { stage.Order = order; stage.UpdatedAt = DateTimeOffset.UtcNow; }
        return new PipelineStageResult(null);
    }
}
