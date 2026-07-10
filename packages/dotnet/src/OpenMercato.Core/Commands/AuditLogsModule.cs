using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Modules;

namespace OpenMercato.Core.Commands;

/// <summary>
/// The audit_logs module (upstream packages/core/src/modules/audit_logs) — port scope limited to the
/// command-write infrastructure: it maps the <c>action_logs</c> table (<see cref="ActionLog"/>) onto
/// the shared DbContext and registers the command-write services (<see cref="ActionLogService"/>,
/// <see cref="CommandBus"/>). Its byte-exact DDL is created by the raw-SQL migration
/// <c>20260707040000_AddActionLogs</c>.
///
/// The module lives in OpenMercato.Core (next to the command bus) so every host that references Core
/// gets the command infrastructure without a new project. Registering commands is a per-module
/// concern: each module adds its handlers via <c>services.AddScoped&lt;ICommand, XCommand&gt;()</c>.
///
/// PARITY-TODO: the audit_logs HTTP API (list / undo / redo / access routes), the AccessLog table,
/// projections/encryption, and CRUD-cache/side-effect flushing are deferred to the audit_logs API
/// port + CRUD factory. <see cref="MapRoutes"/> is therefore a no-op here.
/// </summary>
public sealed class AuditLogsModule : IModule
{
    public string Id => "audit_logs";

    // Match the pinned upstream acl.ts (audit_logs/acl.ts): self/tenant-scoped view + undo + redo.
    public IReadOnlyList<string> AclFeatures { get; } = new[]
    {
        "audit_logs.view_self",
        "audit_logs.view_tenant",
        "audit_logs.undo_self",
        "audit_logs.undo_tenant",
        "audit_logs.redo_self",
        "audit_logs.redo_tenant",
    };

    // Upstream setup.ts defaultRoleFeatures: admin gets the audit_logs.* wildcard (so it can undo/redo
    // any action), employee gets self-scoped view + undo. Without this the admin role couldn't undo.
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultRoleFeatures =>
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["admin"] = new[] { "audit_logs.*" },
            ["employee"] = new[] { "audit_logs.view_self", "audit_logs.undo_self" },
        };

    public void ConfigureServices(IServiceCollection services)
    {
        // di.ts equivalents: the action-log persistence service + the command bus. Scoped because both
        // depend on the request-scoped AppDbContext (and the bus resolves scoped ICommand handlers).
        services.AddScoped<ActionLogService>();
        services.AddScoped<CommandBus>();
    }

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ActionLog>(e =>
        {
            e.ToTable("action_logs");
            e.HasKey(x => x.Id).HasName("action_logs_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            e.Property(x => x.CommandId).HasColumnName("command_id");
            e.Property(x => x.ActionLabel).HasColumnName("action_label");
            e.Property(x => x.ActionType).HasColumnName("action_type");
            e.Property(x => x.ResourceKind).HasColumnName("resource_kind");
            e.Property(x => x.ResourceId).HasColumnName("resource_id");
            e.Property(x => x.ParentResourceKind).HasColumnName("parent_resource_kind");
            e.Property(x => x.ParentResourceId).HasColumnName("parent_resource_id");
            e.Property(x => x.ExecutionState).HasColumnName("execution_state");
            e.Property(x => x.UndoToken).HasColumnName("undo_token");
            e.Property(x => x.CommandPayload).HasColumnName("command_payload").HasColumnType("jsonb");
            e.Property(x => x.SnapshotBefore).HasColumnName("snapshot_before").HasColumnType("jsonb");
            e.Property(x => x.SnapshotAfter).HasColumnName("snapshot_after").HasColumnType("jsonb");
            e.Property(x => x.ChangesJson).HasColumnName("changes_json").HasColumnType("jsonb");
            e.Property(x => x.ChangedFields).HasColumnName("changed_fields").HasColumnType("text[]");
            e.Property(x => x.PrimaryChangedField).HasColumnName("primary_changed_field");
            e.Property(x => x.ContextJson).HasColumnName("context_json").HasColumnType("jsonb");
            e.Property(x => x.SourceKey).HasColumnName("source_key");
            e.Property(x => x.RelatedResourceKind).HasColumnName("related_resource_kind");
            e.Property(x => x.RelatedResourceId).HasColumnName("related_resource_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        // Port of api/audit-logs/actions/{undo,redo}/route.ts. The command-write infra (ActionLogService +
        // CommandBus.Undo/Redo + IUndoableCommand handlers) already exists; this exposes it over HTTP so
        // the OM "Undo"/"Redo" UI works against the ported backend. (PARITY-TODO: the list + access routes.)
        routes.MapPost("/api/audit_logs/audit-logs/actions/undo", (Func<HttpContext, Task<IResult>>)UndoAsync);
        routes.MapPost("/api/audit_logs/audit-logs/actions/redo", (Func<HttpContext, Task<IResult>>)RedoAsync);
    }

    private static async Task<IResult> UndoAsync(HttpContext http)
    {
        var (ctx, denied) = await AuthorizeAsync(http, "audit_logs.undo_self");
        if (denied is not null) return denied;

        var undoToken = (await ReadStringAsync(http, "undoToken"))?.Trim();
        if (string.IsNullOrEmpty(undoToken))
            return Results.Json(new { error = "Invalid undo token" }, Web, statusCode: 400);

        var reqCtx = http.RequestServices.GetRequiredService<ICrudRequestContext>();
        var logs = http.RequestServices.GetRequiredService<ActionLogService>();
        var canUndoTenant = await reqCtx.HasAllFeaturesAsync(ctx!, new[] { "audit_logs.undo_tenant" });

        var target = await logs.FindByUndoTokenAsync(undoToken);
        // Fail-closed scope checks mirroring the upstream route (self vs tenant, tenant/org match).
        if (target is null || target.ExecutionState != "done"
            || (target.ActorUserId is { } actor && actor != ctx!.UserId && !canUndoTenant)
            || (target.TenantId is { } t && t != ctx!.TenantId)
            || (!canUndoTenant && target.OrganizationId is { } o && o != ctx!.OrganizationId))
            return Results.Json(new { error = "Undo token not available" }, Web, statusCode: 400);

        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            await bus.Undo(undoToken, ctx!);
            return Results.Json(new { ok = true, logId = target.Id.ToString() }, Web, statusCode: 200);
        }
        catch
        {
            return Results.Json(new { error = "Undo failed" }, Web, statusCode: 400);
        }
    }

    private static async Task<IResult> RedoAsync(HttpContext http)
    {
        var (ctx, denied) = await AuthorizeAsync(http, "audit_logs.redo_self");
        if (denied is not null) return denied;

        var logIdRaw = (await ReadStringAsync(http, "logId"))?.Trim();
        if (string.IsNullOrEmpty(logIdRaw) || !Guid.TryParse(logIdRaw, out var logId))
            return Results.Json(new { error = "Invalid log id" }, Web, statusCode: 400);

        var reqCtx = http.RequestServices.GetRequiredService<ICrudRequestContext>();
        var logs = http.RequestServices.GetRequiredService<ActionLogService>();
        var canRedoTenant = await reqCtx.HasAllFeaturesAsync(ctx!, new[] { "audit_logs.redo_tenant" });

        var target = await logs.FindByIdAsync(logId);
        if (target is null || target.ExecutionState != "undone"
            || (target.ActorUserId is { } actor && actor != ctx!.UserId && !canRedoTenant)
            || (target.TenantId is { } t && t != ctx!.TenantId)
            || (!canRedoTenant && target.OrganizationId is { } o && o != ctx!.OrganizationId))
            return Results.Json(new { error = "Redo target not available" }, Web, statusCode: 400);

        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            await bus.Redo(logId, ctx!);
            return Results.Json(new { ok = true, logId = target.Id.ToString() }, Web, statusCode: 200);
        }
        catch
        {
            return Results.Json(new { error = "Redo failed" }, Web, statusCode: 400);
        }
    }

    private static async Task<(OpenMercato.Core.Commands.CommandContext? Ctx, IResult? Denied)> AuthorizeAsync(HttpContext http, string feature)
    {
        var reqCtx = http.RequestServices.GetRequiredService<ICrudRequestContext>();
        var ctx = await reqCtx.ResolveAsync(http);
        if (ctx is null) return (null, Results.Json(new { error = "Unauthorized" }, Web, statusCode: 401));
        if (!await reqCtx.HasAllFeaturesAsync(ctx, new[] { feature }))
            return (null, Results.Json(new { error = "Forbidden", requiredFeatures = new[] { feature } }, Web, statusCode: 403));
        return (ctx, null);
    }

    private static async Task<string?> ReadStringAsync(HttpContext http, string key)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(http.Request.Body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch { return null; }
    }
}
