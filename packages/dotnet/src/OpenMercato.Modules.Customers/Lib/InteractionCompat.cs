using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Core.Events;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Lib;

/// <summary>
/// Cross-cutting constants + helpers shared by the interactions/activities/comments/todos surface —
/// the port of upstream <c>lib/interactionCompatibility.ts</c>, <c>lib/interactionFeatureFlags.ts</c>,
/// and <c>lib/visibilityFilter.ts</c>. Kept in one file so the timeline routes stay DRY and byte-consistent.
/// </summary>
internal static class InteractionCompat
{
    // Entity ids (indexer + custom fields).
    public const string InteractionEntityType = "customers:customer_interaction";
    public const string CustomerEntityType = "customers:customer_entity";

    // Source markers (interactionCompatibility.ts). NOTE: authoritative upstream constants are
    // `adapter:activity` / `adapter:todo` (the contract's `customer:interaction:*-adapter` is stale).
    public const string TaskSource = "customers:interaction";
    public const string TaskType = "task";
    public const string ActivityAdapterSource = "adapter:activity";
    public const string TodoAdapterSource = "adapter:todo";
    public const string ExampleTodoSource = "example:todo";

    /// <summary>
    /// Interaction feature flags — the port of <c>resolveCustomerInteractionFeatureFlags</c>. The
    /// <c>feature_toggles</c> module is not ported (contract §dependencies), so these resolve to the
    /// documented seed defaults: unified=false, legacyAdapters=true, externalSync=false.
    /// PARITY-TODO: read live overrides once feature_toggles lands.
    /// </summary>
    public readonly record struct InteractionFlags(bool Unified, bool LegacyAdapters, bool ExternalSync);

    public static InteractionFlags ResolveFlags(IServiceProvider services, Guid? tenantId)
        => new(Unified: false, LegacyAdapters: true, ExternalSync: false);

    /// <summary>
    /// Email visibility predicate — the port of <c>applyEmailVisibilityFilter</c>. A row is hidden ONLY
    /// when it is an email explicitly marked <c>private</c> and the caller is not its author. Non-email
    /// rows, shared emails, and legacy <c>visibility IS NULL</c> rows always pass. v1 strict owner-only:
    /// no admin bypass. API-key callers (no user identity) resolve <paramref name="viewerUserId"/> to
    /// null and never gain the author bypass (fail-closed).
    /// </summary>
    public static IQueryable<CustomerInteraction> ApplyEmailVisibility(IQueryable<CustomerInteraction> q, Guid? viewerUserId)
        => q.Where(i =>
            i.InteractionType != "email" ||
            i.Visibility == null ||
            i.Visibility != "private" ||
            (viewerUserId != null && i.AuthorUserId == viewerUserId));

    /// <summary>
    /// Authorization predicate for CHANGING an email interaction's visibility (<c>canChangeEmailVisibility</c>).
    /// v1 strict owner-only: only the author may flip a private↔shared email; no-op and non-email always ok.
    /// </summary>
    public static bool CanChangeEmailVisibility(string interactionType, string? currentVisibility, string? nextVisibility, Guid? authorUserId, Guid? actorUserId)
    {
        if (interactionType != "email") return true;
        if ((nextVisibility ?? null) == (currentVisibility ?? null)) return true;
        return actorUserId is not null && authorUserId == actorUserId;
    }

    // ---- jsonb helpers (participants / linked_entities / guest_permissions stored as raw text) -----

    /// <summary>Raw JSON text for an array/object body property, or null (for jsonb column storage).</summary>
    public static string? RawJson(JsonElement body, string key)
    {
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var v)
            && v.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            return v.GetRawText();
        return null;
    }

    /// <summary>Parse a stored jsonb text column back into a JSON element for wire output (null-safe).</summary>
    public static object? ParseJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    // ---- side effects (indexer + events), the emitCrudSideEffects equivalent for hand-written routes -

    /// <summary>
    /// Emit the query-index projection + lifecycle events for an interaction write and its parent
    /// entity's next-interaction recompute — the port of <c>emitCrudSideEffects</c> +
    /// <c>emitNextInteractionUpdatedEvent</c>. Called by the hand-written interaction routes (and the
    /// activity/todo bridges) after the command commits.
    /// </summary>
    public static async Task EmitInteractionSideEffectsAsync(
        IServiceProvider services, Commands.InteractionResult r, string action, string? lifecycleEvent = null)
    {
        var indexer = services.GetRequiredService<ICrudIndexer>();
        var events = services.GetRequiredService<IEventBus>();

        if (r.InteractionId is { } iid)
        {
            if (action == "deleted")
                await indexer.DeleteOneAsync(InteractionEntityType, iid, r.OrganizationId, r.TenantId);
            else
                await indexer.UpsertOneAsync(InteractionEntityType, iid, r.OrganizationId, r.TenantId, action == "created" ? "create" : "update");

            await events.PublishAsync($"customers.interaction.{action}", new
            {
                id = iid, organizationId = r.OrganizationId, tenantId = r.TenantId,
            });
        }

        if (lifecycleEvent is not null)
        {
            await events.PublishAsync(lifecycleEvent, new
            {
                id = r.InteractionId, organizationId = r.OrganizationId, tenantId = r.TenantId,
                entityId = r.EntityId.ToString(), interactionType = r.InteractionType, status = r.Status,
                source = r.Source, occurredAt = r.OccurredAt?.ToUniversalTime().ToString("o"),
            });
        }

        // Next-interaction projection refresh: reindex the parent entity + emit the lifecycle event.
        await indexer.UpsertOneAsync(CustomerEntityType, r.EntityId.ToString(), r.OrganizationId, r.TenantId, "update");
        await events.PublishAsync("customers.next_interaction.updated", new
        {
            id = r.EntityId.ToString(), organizationId = r.OrganizationId, tenantId = r.TenantId,
            entityId = r.EntityId.ToString(), nextInteractionId = r.NextInteractionId,
        });
    }

    /// <summary>
    /// Recompute the next-interaction projection fields on a <c>customer_entities</c> row — the port of
    /// <c>recomputeNextInteraction</c>. Earliest planned interaction with a scheduled_at wins
    /// (scheduled_at asc, priority desc nulls-last, created_at asc, id asc). No candidate → all null.
    /// PARITY-TODO: <c>priority desc nulls last</c> matches on the in-memory provider; on Postgres the
    /// default DESC is NULLS FIRST (deep tie-break nuance).
    /// </summary>
    public static async Task<string?> RecomputeNextInteractionAsync(AppDbContext db, Guid entityId)
    {
        var candidate = await db.Set<CustomerInteraction>().AsNoTracking()
            .Where(i => i.EntityId == entityId && i.Status == "planned" && i.ScheduledAt != null && i.DeletedAt == null)
            .OrderBy(i => i.ScheduledAt)
            .ThenByDescending(i => i.Priority)
            .ThenBy(i => i.CreatedAt)
            .ThenBy(i => i.Id)
            .FirstOrDefaultAsync();

        var entity = await db.Set<CustomerEntity>().FirstOrDefaultAsync(x => x.Id == entityId);
        if (entity is null) return null;

        if (candidate is not null)
        {
            entity.NextInteractionAt = candidate.ScheduledAt;
            entity.NextInteractionName = string.IsNullOrWhiteSpace(candidate.Title) ? candidate.InteractionType : candidate.Title;
            entity.NextInteractionRefId = candidate.Id.ToString();
            entity.NextInteractionIcon = candidate.AppearanceIcon;
            entity.NextInteractionColor = candidate.AppearanceColor;
        }
        else
        {
            entity.NextInteractionAt = null;
            entity.NextInteractionName = null;
            entity.NextInteractionRefId = null;
            entity.NextInteractionIcon = null;
            entity.NextInteractionColor = null;
        }
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return candidate?.Id.ToString();
    }
}
