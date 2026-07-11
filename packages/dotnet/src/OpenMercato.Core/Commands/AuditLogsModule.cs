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
        // Port of api/audit-logs/actions/{,undo,redo}/route.ts. The command-write infra (ActionLogService +
        // CommandBus.Undo/Redo + IUndoableCommand handlers) already exists; this exposes it over HTTP so
        // the OM "Undo"/"Redo" UI + the changelog tab work against the ported backend.
        routes.MapGet("/api/audit_logs/audit-logs/actions", (Func<HttpContext, Task<IResult>>)ListAsync);
        routes.MapGet("/api/audit_logs/audit-logs/actions/export", (Func<HttpContext, Task<IResult>>)ExportAsync);
        routes.MapPost("/api/audit_logs/audit-logs/actions/undo", (Func<HttpContext, Task<IResult>>)UndoAsync);
        routes.MapPost("/api/audit_logs/audit-logs/actions/redo", (Func<HttpContext, Task<IResult>>)RedoAsync);
    }

    private static async Task<IResult> ListAsync(HttpContext http)
    {
        var (ctx, denied) = await AuthorizeAsync(http, "audit_logs.view_self");
        if (denied is not null) return denied;

        var reqCtx = http.RequestServices.GetRequiredService<ICrudRequestContext>();
        var canViewTenant = ctx!.IsSuperAdmin || await reqCtx.HasAllFeaturesAsync(ctx, new[] { "audit_logs.view_tenant" });

        var qp = http.Request.Query;
        string? Q(string k) => qp.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToString().Trim() : null;
        var rows = await LoadFilteredAsync(http, ctx, canViewTenant);

        // Pagination: pageSize wins (clamp 1..200), else legacy limit (1..1000), else 50; offset else (page-1)*pageSize.
        var rawLimit = int.TryParse(Q("limit"), out var l) && l > 0 ? Math.Clamp(l, 1, 1000) : 0;
        var pageSize = int.TryParse(Q("pageSize"), out var psv) && psv > 0 ? Math.Clamp(psv, 1, 200) : (rawLimit > 0 ? rawLimit : 50);
        var pageNum = int.TryParse(Q("page"), out var pgv) && pgv > 0 ? pgv : 1;
        var offset = int.TryParse(Q("offset"), out var o) && o >= 0 ? o : (pageNum - 1) * pageSize;

        var total = rows.Count;
        var pageRows = rows.Skip(offset).Take(pageSize).ToList();
        var maps = await HydrateAsync(http, pageRows);
        var items = pageRows.Select(a => Project(a, maps)).ToList();
        // Upstream always returns the envelope (includeTotal is a legacy no-op); page derived from offset.
        return Results.Json(new
        {
            items,
            canViewTenant,
            page = pageSize > 0 ? (offset / pageSize) + 1 : 1,
            pageSize,
            total,
            totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)),
        }, Web, statusCode: 200);
    }

    /// <summary>Apply the shared scope + filter + sort (everything except pagination), returning the rows.</summary>
    private static async Task<List<ActionLog>> LoadFilteredAsync(HttpContext http, CommandContext ctx, bool canViewTenant)
    {
        var db = http.RequestServices.GetRequiredService<Data.AppDbContext>();
        var qp = http.Request.Query;
        string? Q(string k) => qp.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToString().Trim() : null;
        var resourceKind = Q("resourceKind");
        var resourceId = Q("resourceId");
        var actionTypes = (Q("actionType") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fieldNames = (Q("fieldName") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var includeRelated = string.Equals(Q("includeRelated"), "true", StringComparison.OrdinalIgnoreCase);
        var undoableOnly = string.Equals(Q("undoableOnly"), "true", StringComparison.OrdinalIgnoreCase);
        var sortDir = string.Equals(Q("sortDir"), "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc";
        DateTime? after = DateTime.TryParse(Q("after"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var af) ? af : null;
        DateTime? before = DateTime.TryParse(Q("before"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var bf) ? bf : null;

        var actorFilter = Q("actorUserId");
        var query = db.Set<ActionLog>().AsNoTracking().Where(a => a.DeletedAt == null && a.TenantId == ctx.TenantId);
        if (!canViewTenant) query = query.Where(a => a.ActorUserId == ctx.UserId);
        else if (actorFilter is not null && Guid.TryParse(actorFilter, out var af2)) query = query.Where(a => a.ActorUserId == af2);
        if (!canViewTenant && ctx.OrganizationId is { } org) query = query.Where(a => a.OrganizationId == null || a.OrganizationId == org);
        if (resourceKind is not null && resourceId is not null && includeRelated)
            query = query.Where(a =>
                (a.ResourceKind == resourceKind && a.ResourceId == resourceId)
                || (a.ParentResourceKind == resourceKind && a.ParentResourceId == resourceId)
                || (a.RelatedResourceKind == resourceKind && a.RelatedResourceId == resourceId));
        else
        {
            if (resourceKind is not null) query = query.Where(a => a.ResourceKind == resourceKind);
            if (resourceId is not null) query = query.Where(a => a.ResourceId == resourceId);
        }
        if (actionTypes.Length > 0) query = query.Where(a => a.ActionType != null && actionTypes.Contains(a.ActionType));
        if (undoableOnly) query = query.Where(a => a.UndoToken != null);
        if (after is { } afv) query = query.Where(a => a.CreatedAt >= afv);
        if (before is { } bfv) query = query.Where(a => a.CreatedAt < bfv);

        var rows = await query.ToListAsync();
        if (fieldNames.Length > 0)
            rows = rows.Where(a => fieldNames.Any(f =>
                (a.ChangedFields is { } cf && cf.Contains(f))
                || (ParseJson(a.ChangesJson) is { ValueKind: JsonValueKind.Object } ce && ce.TryGetProperty(f, out _)))).ToList();

        return (sortDir == "asc"
            ? rows.OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            : rows.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)).ToList();
    }

    private static readonly IReadOnlyList<CrudExportColumn> ExportColumns = new[]
    {
        new CrudExportColumn("when", "When"), new CrudExportColumn("user", "User"),
        new CrudExportColumn("action", "Action"), new CrudExportColumn("field", "Field"),
        new CrudExportColumn("oldValue", "Old Value"), new CrudExportColumn("newValue", "New Value"),
        new CrudExportColumn("source", "Source"),
    };

    private static async Task<IResult> ExportAsync(HttpContext http)
    {
        var (ctx, denied) = await AuthorizeAsync(http, "audit_logs.view_self");
        if (denied is not null) return denied;
        var reqCtx = http.RequestServices.GetRequiredService<ICrudRequestContext>();
        var canViewTenant = ctx!.IsSuperAdmin || await reqCtx.HasAllFeaturesAsync(ctx, new[] { "audit_logs.view_tenant" });

        var all = await LoadFilteredAsync(http, ctx, canViewTenant);
        var cap = int.TryParse(http.Request.Query["limit"], out var lm) && lm > 0 ? Math.Clamp(lm, 1, 1000) : 1000;
        var capped = all.Take(cap).ToList();
        var maps = await HydrateAsync(http, capped);
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var a in capped)
        {
            var when = a.CreatedAt.ToUniversalTime().ToString("o");
            var user = a.ActorUserId is { } au && maps.Users.TryGetValue(au, out var un) ? un
                : a.ActorUserId?.ToString() ?? "System";
            var action = DeriveAction(a);
            var source = DeriveSource(a);
            var changes = ChangeRows(a.ChangesJson);
            if (changes.Count == 0)
                rows.Add(Row(when, user, action, "", "", "", source));
            else
                foreach (var (field, oldV, newV) in changes) rows.Add(Row(when, user, action, field, oldV, newV, source));
        }

        var serialized = CrudExport.Serialize(new PreparedExport(ExportColumns, rows), CrudExport.Csv);
        http.Response.Headers["Content-Disposition"] = "attachment; filename=\"changelog-export.csv\"";
        return Results.Text(serialized.Body, "text/csv; charset=utf-8");
    }

    private static IReadOnlyDictionary<string, object?> Row(string when, string user, string action, string field, string oldV, string newV, string source) =>
        new Dictionary<string, object?> { ["when"] = when, ["user"] = user, ["action"] = action, ["field"] = field, ["oldValue"] = oldV, ["newValue"] = newV, ["source"] = source };

    /// <summary>Action label: explicit actionType, else the command's trailing verb, else the label.</summary>
    private static string DeriveAction(ActionLog a)
    {
        var verb = a.ActionType;
        if (string.IsNullOrEmpty(verb) && !string.IsNullOrEmpty(a.CommandId))
        {
            var last = a.CommandId.Split('.').LastOrDefault();
            if (last is "create" or "update" or "edit" or "delete" or "assign") verb = last;
        }
        if (!string.IsNullOrEmpty(verb)) return char.ToUpperInvariant(verb[0]) + verb[1..];
        return a.ActionLabel ?? "Action";
    }

    private static string DeriveSource(ActionLog a)
    {
        if (!string.IsNullOrEmpty(a.SourceKey)) return a.SourceKey.ToUpperInvariant();
        if (ParseJson(a.ContextJson) is { ValueKind: JsonValueKind.Object } ctxEl
            && ctxEl.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String)
            return (s.GetString() ?? "").ToUpperInvariant();
        return a.ActorUserId is null ? "SYSTEM" : "UI";
    }

    /// <summary>Flatten a changes doc <c>{ field: { from, to } }</c> (or {old,new}) into (field, old, new) rows.</summary>
    private static List<(string Field, string Old, string New)> ChangeRows(string? changesJson)
    {
        var list = new List<(string, string, string)>();
        var el = ParseJson(changesJson);
        if (el.ValueKind != JsonValueKind.Object) return list;
        foreach (var prop in el.EnumerateObject())
        {
            string old = "", @new = "";
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                if (prop.Value.TryGetProperty("from", out var f)) old = FormatVal(f);
                else if (prop.Value.TryGetProperty("old", out var f2)) old = FormatVal(f2);
                if (prop.Value.TryGetProperty("to", out var t)) @new = FormatVal(t);
                else if (prop.Value.TryGetProperty("new", out var t2)) @new = FormatVal(t2);
            }
            else @new = FormatVal(prop.Value);
            list.Add((prop.Name, old, @new));
        }
        return list;
    }

    private static string FormatVal(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => el.GetRawText(),
    };

    /// <summary>Resolve actor/tenant/org display names for the page via the optional directory (Customers-side impl).</summary>
    private static async Task<AuditLogDisplayMaps> HydrateAsync(HttpContext http, IReadOnlyList<ActionLog> rows)
    {
        var dir = http.RequestServices.GetService<IAuditLogDisplayDirectory>();
        if (dir is null || rows.Count == 0) return AuditLogDisplayMaps.Empty;
        var users = rows.Where(r => r.ActorUserId is not null).Select(r => r.ActorUserId!.Value).Distinct().ToList();
        var tenants = rows.Where(r => r.TenantId is not null).Select(r => r.TenantId!.Value).Distinct().ToList();
        var orgs = rows.Where(r => r.OrganizationId is not null).Select(r => r.OrganizationId!.Value).Distinct().ToList();
        return await dir.ResolveAsync(users, tenants, orgs);
    }

    private static object Project(ActionLog a, AuditLogDisplayMaps maps) => new
    {
        id = a.Id.ToString(),
        commandId = a.CommandId,
        actionLabel = a.ActionLabel,
        actionType = a.ActionType,
        executionState = a.ExecutionState,
        actorUserId = a.ActorUserId?.ToString(),
        actorUserName = a.ActorUserId is { } au && maps.Users.TryGetValue(au, out var un) ? un : null,
        tenantId = a.TenantId?.ToString(),
        tenantName = a.TenantId is { } t && maps.Tenants.TryGetValue(t, out var tn) ? tn : null,
        organizationId = a.OrganizationId?.ToString(),
        organizationName = a.OrganizationId is { } og && maps.Organizations.TryGetValue(og, out var on) ? on : null,
        resourceKind = a.ResourceKind,
        resourceId = a.ResourceId,
        parentResourceKind = a.ParentResourceKind,
        parentResourceId = a.ParentResourceId,
        undoToken = a.UndoToken,
        createdAt = a.CreatedAt.ToUniversalTime().ToString("o"),
        updatedAt = a.UpdatedAt.ToUniversalTime().ToString("o"),
        snapshotBefore = ParseJsonNullable(a.SnapshotBefore),
        snapshotAfter = ParseJsonNullable(a.SnapshotAfter),
        changes = ParseJsonNullable(a.ChangesJson),
        context = ParseJsonNullable(a.ContextJson),
    };

    private static JsonElement ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); } catch { return default; }
    }

    private static object? ParseJsonNullable(string? json)
    {
        var el = ParseJson(json);
        return el.ValueKind == JsonValueKind.Undefined ? null : (object)el;
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
