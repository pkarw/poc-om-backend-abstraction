using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Entities.Data;

namespace OpenMercato.Modules.Entities.Api;

/// <summary>
/// <c>/api/entities/entities</c> — the custom-entity REGISTRY (upstream api/entities.ts). Powers the
/// entities admin shell: GET lists every selectable entity id (module-declared "code" entities + tenant/
/// org-scoped user-defined "custom" entities) with a per-entity custom-field count; POST upserts a custom
/// entity, DELETE soft-deactivates one (both require <c>entities.definitions.manage</c>).
///
/// The "code" entity source is <see cref="ModuleRegistry.AllCustomFieldSets"/> unioned with the entity ids
/// that already have field definitions — the .NET stand-in for upstream's generated <c>getEntityIds()</c>
/// registry (every entity you can manage fields on). Custom entities come from <see cref="CustomEntity"/>.
/// </summary>
public static class EntitiesRegistryRoutes
{
    private static readonly Regex EntityIdRegex = new("^[a-z0-9_]+:[a-z0-9_]+$", RegexOptions.Compiled);
    private static readonly Regex LabelFieldRegex = new("^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);
    private static readonly HashSet<string> Editors = new(StringComparer.Ordinal) { "markdown", "simpleMarkdown", "htmlRichText" };

    public static void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/entities/entities", (Func<HttpContext, Task<IResult>>)GetAsync);
        routes.MapPost("/api/entities/entities", (Func<HttpContext, Task<IResult>>)PostAsync);
        routes.MapDelete("/api/entities/entities", (Func<HttpContext, Task<IResult>>)DeleteAsync);
    }

    private static async Task<IResult> GetAsync(HttpContext http)
    {
        var (ctx, denied) = await EntitiesHttp.AuthorizeAsync(http);
        if (denied is not null) return denied;
        if (ctx!.TenantId is null) return EntitiesHttp.Result(new { error = "Unauthorized" }, 401);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var registry = http.RequestServices.GetRequiredService<ModuleRegistry>();
        var tenantId = ctx.TenantId;
        var orgId = ctx.OrganizationId;

        // ---- code (module-declared) entities: cf-set entity ids ∪ entity ids that have field defs -------
        var codeIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var set in registry.AllCustomFieldSets) codeIds.Add(set.EntityId);

        // ---- field-definition rows in scope (drive both counts AND extra "code" entity ids) -------------
        var defs = await db.Set<CustomFieldDef>().AsNoTracking()
            .Where(d => d.IsActive && d.DeletedAt == null && (d.TenantId == null || d.TenantId == tenantId))
            .Select(d => new { d.EntityId, d.Key })
            .ToListAsync();
        var keySets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var d in defs)
        {
            codeIds.Add(d.EntityId);
            if (!keySets.TryGetValue(d.EntityId, out var set)) keySets[d.EntityId] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(d.Key);
        }

        var byId = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var id in codeIds)
            byId[id] = new Dictionary<string, object?> { ["entityId"] = id, ["source"] = "code", ["label"] = id };

        // ---- custom (user-defined) entities in scope, with overlay precedence (org+tenant > global) -----
        var customs = await db.Set<CustomEntity>().AsNoTracking()
            .Where(c => c.IsActive && c.DeletedAt == null
                        && (c.OrganizationId == null || c.OrganizationId == orgId)
                        && (c.TenantId == null || c.TenantId == tenantId))
            .OrderBy(c => c.EntityId)
            .ToListAsync();
        var customByEntity = new Dictionary<string, CustomEntity>(StringComparer.Ordinal);
        foreach (var c in customs)
        {
            var spec = (c.OrganizationId is not null ? 2 : 0) + (c.TenantId is not null ? 1 : 0);
            if (!customByEntity.TryGetValue(c.EntityId, out var prev)
                || spec > (prev.OrganizationId is not null ? 2 : 0) + (prev.TenantId is not null ? 1 : 0))
                customByEntity[c.EntityId] = c;
        }
        foreach (var c in customByEntity.Values)
        {
            var existing = byId.TryGetValue(c.EntityId, out var e) ? e : null;
            var item = existing ?? new Dictionary<string, object?>();
            item["entityId"] = c.EntityId;
            item["source"] = existing?["source"] ?? "custom"; // code source wins if the entity is also code-declared
            item["label"] = c.Label;
            if (!string.IsNullOrEmpty(c.Description)) item["description"] = c.Description;
            if (!string.IsNullOrEmpty(c.LabelField)) item["labelField"] = c.LabelField;
            if (!string.IsNullOrEmpty(c.DefaultEditor)) item["defaultEditor"] = c.DefaultEditor;
            item["showInSidebar"] = c.ShowInSidebar;
            item["updatedAt"] = c.UpdatedAt.ToUniversalTime().ToString("o");
            byId[c.EntityId] = item;
        }

        var items = byId.Values
            .OrderBy(it => (string)it["entityId"]!, StringComparer.Ordinal)
            .Select(it => { it["count"] = keySets.TryGetValue((string)it["entityId"]!, out var s) ? s.Count : 0; return it; })
            .ToList();
        return EntitiesHttp.Result(new { items }, 200);
    }

    private static async Task<IResult> PostAsync(HttpContext http)
    {
        var (ctx, denied) = await EntitiesHttp.AuthorizeAsync(http, "entities.definitions.manage");
        if (denied is not null) return denied;

        var body = await EntitiesHttp.ReadBodyAsync(http);
        var entityId = EntitiesHttp.StringProp(body, "entityId")?.Trim();
        var label = EntitiesHttp.StringProp(body, "label")?.Trim();
        var description = EntitiesHttp.StringProp(body, "description");
        var labelField = EntitiesHttp.StringProp(body, "labelField");
        var defaultEditor = EntitiesHttp.StringProp(body, "defaultEditor");
        var showInSidebar = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("showInSidebar", out var sv) && sv.ValueKind == JsonValueKind.True;
        bool? isActive = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("isActive", out var iav)
            ? iav.ValueKind == JsonValueKind.True : null;

        if (string.IsNullOrEmpty(entityId) || !EntityIdRegex.IsMatch(entityId))
            return EntitiesHttp.Result(new { error = "Validation failed", details = new { entityId = "Enter the entity id in the format: module_name:entity_id" } }, 400);
        if (string.IsNullOrEmpty(label) || label.Length > 200)
            return EntitiesHttp.Result(new { error = "Validation failed", details = new { label = "Label is required (1–200 chars)" } }, 400);
        if (!string.IsNullOrEmpty(labelField) && (labelField.Length > 100 || !LabelFieldRegex.IsMatch(labelField)))
            return EntitiesHttp.Result(new { error = "Validation failed", details = new { labelField = "Invalid label field" } }, 400);
        if (!string.IsNullOrEmpty(defaultEditor) && !Editors.Contains(defaultEditor))
            return EntitiesHttp.Result(new { error = "Validation failed", details = new { defaultEditor = "Invalid editor" } }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var ent = await db.Set<CustomEntity>()
            .FirstOrDefaultAsync(c => c.EntityId == entityId && c.OrganizationId == ctx!.OrganizationId && c.TenantId == ctx.TenantId);
        if (ent is null)
        {
            ent = new CustomEntity
            {
                Id = Guid.NewGuid(),
                EntityId = entityId,
                OrganizationId = ctx!.OrganizationId,
                TenantId = ctx.TenantId,
                CreatedAt = now,
            };
            db.Set<CustomEntity>().Add(ent);
        }
        ent.Label = label;
        ent.Description = description;
        ent.LabelField = string.IsNullOrEmpty(labelField) ? null : labelField;
        ent.DefaultEditor = string.IsNullOrEmpty(defaultEditor) ? null : defaultEditor;
        ent.ShowInSidebar = showInSidebar;
        ent.IsActive = isActive ?? true;
        ent.DeletedAt = null;
        ent.UpdatedAt = now;
        await db.SaveChangesAsync();

        return EntitiesHttp.Result(new { ok = true, item = new { id = ent.Id.ToString(), entityId = ent.EntityId, label = ent.Label, description = ent.Description } }, 200);
    }

    private static async Task<IResult> DeleteAsync(HttpContext http)
    {
        var (ctx, denied) = await EntitiesHttp.AuthorizeAsync(http, "entities.definitions.manage");
        if (denied is not null) return denied;

        var body = await EntitiesHttp.ReadBodyAsync(http);
        var entityId = EntitiesHttp.StringProp(body, "entityId")?.Trim()
            ?? (http.Request.Query.TryGetValue("entityId", out var q) ? q.ToString() : null);
        if (string.IsNullOrEmpty(entityId)) return EntitiesHttp.Result(new { error = "Validation failed" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var ent = await db.Set<CustomEntity>()
            .FirstOrDefaultAsync(c => c.EntityId == entityId && c.OrganizationId == ctx!.OrganizationId && c.TenantId == ctx.TenantId);
        if (ent is null) return EntitiesHttp.Result(new { error = "Not found" }, 404);

        var now = DateTimeOffset.UtcNow;
        ent.IsActive = false;
        ent.DeletedAt ??= now;
        ent.UpdatedAt = now;
        await db.SaveChangesAsync();
        return EntitiesHttp.Result(new { ok = true }, 200);
    }
}
