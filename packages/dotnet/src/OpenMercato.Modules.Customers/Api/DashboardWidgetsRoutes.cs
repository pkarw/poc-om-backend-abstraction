using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Dashboard-widget data endpoints — the 1:1 port of upstream
/// <c>api/dashboard/widgets/{customer-todos,new-customers,new-deals,next-interactions}/route.ts</c>.
/// All four are GET, gated by <c>dashboards.view</c> + the per-widget feature, and resolve their
/// tenant/org scope through <see cref="ResolveScopeAsync"/> — the analogue of the dashboards module's
/// <c>resolveWidgetScope</c> (401 <c>dashboards.errors.unauthorized</c> → "Unauthorized", 400
/// <c>dashboards.errors.tenant_required</c> / <c>organization_required</c>). Invalid query → 400
/// <c>{error:'Invalid query parameters'}</c>. Read-only aggregations over existing tables; each returns
/// the exact response shape the customers dashboard widgets expect (see contract "Dashboard widgets").
/// </summary>
public sealed class DashboardWidgetsRoutes : ICustomersRouteGroup
{
    // Widget features layered on top of dashboards.view (contract "Dashboard widgets" table).
    private static readonly string[] TodosFeatures = { "dashboards.view", "customers.widgets.todos" };
    private static readonly string[] NewCustomersFeatures = { "dashboards.view", "customers.widgets.new-customers" };
    private static readonly string[] NewDealsFeatures = { "dashboards.view", "customers.widgets.new-deals" };
    private static readonly string[] NextInteractionsFeatures = { "dashboards.view", "customers.widgets.next-interactions" };

    // Upstream CUSTOMER_INTERACTION_* source constants (lib/interactionCompatibility.ts).
    private const string InteractionTaskType = "task";
    private const string InteractionTaskSource = "customers:interaction";
    private const string TodoAdapterSource = "adapter:todo";
    private const string ExampleTodoSource = "example:todo";

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/customers/dashboard/widgets/customer-todos", (Func<HttpContext, Task<IResult>>)CustomerTodosAsync);
        routes.MapGet("/api/customers/dashboard/widgets/new-customers", (Func<HttpContext, Task<IResult>>)NewCustomersAsync);
        routes.MapGet("/api/customers/dashboard/widgets/new-deals", (Func<HttpContext, Task<IResult>>)NewDealsAsync);
        routes.MapGet("/api/customers/dashboard/widgets/next-interactions", (Func<HttpContext, Task<IResult>>)NextInteractionsAsync);
    }

    // ---- new-customers -----------------------------------------------------------------------------

    private static async Task<IResult> NewCustomersAsync(HttpContext http)
    {
        if (!TryParseCommon(http, out var limit, out var overrideTenant, out var overrideOrg, out var badQuery))
            return badQuery!;
        var kind = http.Request.Query["kind"].ToString();
        if (!string.IsNullOrEmpty(kind) && kind is not ("person" or "company"))
            return InvalidQuery();

        var (_, tenantId, orgIds, error) = await ResolveScopeAsync(http, NewCustomersFeatures, overrideTenant, overrideOrg);
        if (error is not null) return error;

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var orgs = orgIds?.Distinct().ToList();
        var q = db.Set<CustomerEntity>().AsNoTracking().Where(e => e.TenantId == tenantId && e.DeletedAt == null);
        if (orgs is not null) q = q.Where(e => orgs.Contains(e.OrganizationId));
        if (!string.IsNullOrEmpty(kind)) q = q.Where(e => e.Kind == kind);

        var entities = await q.OrderByDescending(e => e.CreatedAt).Take(limit).ToListAsync();
        var items = entities.Select(e => new
        {
            id = e.Id.ToString(),
            displayName = e.DisplayName,
            kind = e.Kind,
            organizationId = e.OrganizationId.ToString(),
            createdAt = CustomersHttp.Iso(e.CreatedAt),
            ownerUserId = e.OwnerUserId?.ToString(),
        }).ToList();
        return CustomersHttp.Json(new { items }, 200);
    }

    // ---- new-deals ---------------------------------------------------------------------------------

    private static async Task<IResult> NewDealsAsync(HttpContext http)
    {
        if (!TryParseCommon(http, out var limit, out var overrideTenant, out var overrideOrg, out var badQuery))
            return badQuery!;

        var (_, tenantId, orgIds, error) = await ResolveScopeAsync(http, NewDealsFeatures, overrideTenant, overrideOrg);
        if (error is not null) return error;

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var orgs = orgIds?.Distinct().ToList();
        var q = db.Set<CustomerDeal>().AsNoTracking().Where(d => d.TenantId == tenantId && d.DeletedAt == null);
        if (orgs is not null) q = q.Where(d => orgs.Contains(d.OrganizationId));

        var deals = await q.OrderByDescending(d => d.CreatedAt).Take(limit).ToListAsync();
        var items = deals.Select(d => new
        {
            id = d.Id.ToString(),
            title = d.Title,
            status = d.Status,
            organizationId = d.OrganizationId.ToString(),
            createdAt = CustomersHttp.Iso(d.CreatedAt),
            ownerUserId = d.OwnerUserId?.ToString(),
            valueAmount = d.ValueAmount?.ToString(CultureInfo.InvariantCulture),
            valueCurrency = d.ValueCurrency,
        }).ToList();
        return CustomersHttp.Json(new { items }, 200);
    }

    // ---- next-interactions -------------------------------------------------------------------------

    private static async Task<IResult> NextInteractionsAsync(HttpContext http)
    {
        if (!TryParseCommon(http, out var limit, out var overrideTenant, out var overrideOrg, out var badQuery))
            return badQuery!;
        var includePastRaw = http.Request.Query["includePast"].ToString();
        if (!string.IsNullOrEmpty(includePastRaw) && includePastRaw is not ("true" or "false"))
            return InvalidQuery();
        var includePast = includePastRaw == "true";

        var (_, tenantId, orgIds, error) = await ResolveScopeAsync(http, NextInteractionsFeatures, overrideTenant, overrideOrg);
        if (error is not null) return error;

        var now = DateTimeOffset.UtcNow;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var orgs = orgIds?.Distinct().ToList();
        var q = db.Set<CustomerEntity>().AsNoTracking().Where(e => e.TenantId == tenantId && e.DeletedAt == null);
        if (orgs is not null) q = q.Where(e => orgs.Contains(e.OrganizationId));
        q = includePast
            ? q.Where(e => e.NextInteractionAt != null)
            : q.Where(e => e.NextInteractionAt >= now);

        var entities = await q.OrderBy(e => e.NextInteractionAt).Take(limit).ToListAsync();
        var items = entities.Select(e => new
        {
            id = e.Id.ToString(),
            displayName = e.DisplayName,
            kind = e.Kind,
            organizationId = e.OrganizationId.ToString(),
            nextInteractionAt = CustomersHttp.Iso(e.NextInteractionAt),
            nextInteractionName = e.NextInteractionName,
            nextInteractionIcon = e.NextInteractionIcon,
            nextInteractionColor = e.NextInteractionColor,
            ownerUserId = e.OwnerUserId?.ToString(),
        }).ToList();
        return CustomersHttp.Json(new { items, now = CustomersHttp.Iso(now) }, 200);
    }

    // ---- customer-todos ----------------------------------------------------------------------------

    private static async Task<IResult> CustomerTodosAsync(HttpContext http)
    {
        if (!TryParseCommon(http, out var limit, out var overrideTenant, out var overrideOrg, out var badQuery))
            return badQuery!;

        var (_, tenantId, orgIds, error) = await ResolveScopeAsync(http, TodosFeatures, overrideTenant, overrideOrg);
        if (error is not null) return error;

        try
        {
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            // feature_toggles is not ported → the interaction "unified" flag defaults to false, i.e. the
            // legacy+canonical merge path. See ADR: the legacy todo detail (title/isDone/…) resolution via
            // the query engine and the unified-flag branch are deferred; the widget shape is preserved.
            var mergedWindow = Math.Min(limit * 4, 50);
            var rows = await BuildMergedTodoRowsAsync(db, tenantId!.Value, orgIds, mergedWindow);

            var items = rows.Take(limit).Select(row => new
            {
                id = row.Id.ToString(),
                todoId = row.TodoId.ToString(),
                todoSource = row.TodoSource,
                todoTitle = row.TodoTitle,
                createdAt = CustomersHttp.Iso(row.CreatedAt),
                organizationId = row.OrganizationId?.ToString(),
                entity = BuildTodoEntity(row.Entity),
            }).ToList();
            return CustomersHttp.Json(new { items }, 200);
        }
        catch
        {
            return CustomersHttp.Json(new { error = "Failed to load customer tasks" }, 500);
        }
    }

    private sealed record TodoRow(Guid Id, Guid TodoId, string TodoSource, string? TodoTitle, DateTimeOffset CreatedAt, Guid? OrganizationId, CustomerEntity? Entity);

    /// <summary>Entity passthrough for a todo row (upstream shape: id/displayName/kind/ownerUserId, ownerUserId always null).</summary>
    private static object BuildTodoEntity(CustomerEntity? e) => e is null
        ? new { id = (string?)null, displayName = (string?)null, kind = (string?)null, ownerUserId = (string?)null }
        : new { id = (string?)e.Id.ToString(), displayName = (string?)e.DisplayName, kind = (string?)e.Kind, ownerUserId = (string?)null };

    private static async Task<List<TodoRow>> BuildMergedTodoRowsAsync(AppDbContext db, Guid tenantId, List<Guid>? orgIds, int window)
    {
        var orgs = orgIds?.Distinct().ToList();

        // Canonical task interactions bridged from the todo adapter (non-unified path uses source filter,
        // and includes soft-deleted rows in the bridge-id dedup set).
        var canonicalQuery = db.Set<CustomerInteraction>().AsNoTracking()
            .Where(i => i.TenantId == tenantId && i.InteractionType == InteractionTaskType && i.Source == TodoAdapterSource);
        if (orgs is not null) canonicalQuery = canonicalQuery.Where(i => orgs.Contains(i.OrganizationId));
        var canonicalAll = await canonicalQuery.ToListAsync();
        var bridgeIds = new HashSet<Guid>(canonicalAll.Select(i => i.Id));
        var canonicalActive = canonicalAll.Where(i => i.DeletedAt == null)
            .OrderByDescending(i => i.CreatedAt).Take(window).ToList();

        // Legacy todo-bridge links (detail resolution via the query engine is deferred → title stays null).
        var legacyQuery = db.Set<CustomerTodoLink>().AsNoTracking().Where(l => l.TenantId == tenantId);
        if (orgs is not null) legacyQuery = legacyQuery.Where(l => orgs.Contains(l.OrganizationId));
        var legacyLinks = await legacyQuery.OrderByDescending(l => l.CreatedAt).Take(window).ToListAsync();

        var entityIds = canonicalActive.Select(i => i.EntityId)
            .Concat(legacyLinks.Select(l => l.EntityId)).Distinct().ToList();
        var entities = await db.Set<CustomerEntity>().AsNoTracking()
            .Where(e => entityIds.Contains(e.Id)).ToListAsync();
        var byId = entities.ToDictionary(e => e.Id);

        var rows = new List<TodoRow>();
        foreach (var link in legacyLinks)
        {
            if (bridgeIds.Contains(link.TodoId)) continue; // dedup by bridgeIds
            rows.Add(new TodoRow(
                link.Id, link.TodoId,
                string.IsNullOrWhiteSpace(link.TodoSource) ? ExampleTodoSource : link.TodoSource,
                null, link.CreatedAt, link.OrganizationId,
                byId.TryGetValue(link.EntityId, out var le) ? le : null));
        }
        foreach (var i in canonicalActive)
        {
            rows.Add(new TodoRow(
                i.Id, i.Id, InteractionTaskSource, i.Title, i.CreatedAt, i.OrganizationId,
                byId.TryGetValue(i.EntityId, out var ce) ? ce : null));
        }

        // sortTodoRows: createdAt desc, tie → id string desc.
        return rows
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    // ---- scope + query helpers ---------------------------------------------------------------------

    /// <summary>Port of dashboards' <c>resolveWidgetScope</c> on top of the customers auth bridge.</summary>
    private static async Task<(CommandContext? Ctx, Guid? TenantId, List<Guid>? OrgIds, IResult? Error)> ResolveScopeAsync(
        HttpContext http, string[] features, Guid? overrideTenant, Guid? overrideOrg)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, features);
        if (denied is not null) return (null, null, null, denied);

        var tenantId = overrideTenant ?? ctx!.TenantId;
        if (tenantId is null)
            return (null, null, null, CustomersHttp.Json(new { error = "Tenant context is required" }, 400));

        List<Guid>? orgIds;
        if (overrideOrg is { } ov) orgIds = new List<Guid> { ov };
        else if (ctx!.OrganizationId is { } sel) orgIds = new List<Guid> { sel };            // scope.selectedId
        else if (ctx.OrganizationIds is { Count: > 0 } fil) orgIds = fil.ToList();           // scope.filterIds
        else if (ctx.OrganizationIds is null || ctx.AllowedOrganizationIds is null) orgIds = null; // unrestricted
        else orgIds = new List<Guid>();

        if (orgIds is not null && orgIds.Count == 0)
            return (null, null, null, CustomersHttp.Json(new { error = "Organization context is required" }, 400));

        return (ctx, tenantId, orgIds, null);
    }

    /// <summary>Parse the common widget query (limit 1..20 def 5, tenantId?, organizationId?).</summary>
    private static bool TryParseCommon(HttpContext http, out int limit, out Guid? tenantId, out Guid? organizationId, out IResult? badQuery)
    {
        limit = 5; tenantId = null; organizationId = null; badQuery = null;
        var q = http.Request.Query;

        var limitRaw = q["limit"].ToString();
        if (!string.IsNullOrEmpty(limitRaw))
        {
            // Mirror zod z.coerce.number().min(1).max(20): any number in [1,20]; truncate to an int page size.
            if (!double.TryParse(limitRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
                || d < 1 || d > 20)
            {
                badQuery = InvalidQuery();
                return false;
            }
            limit = (int)d;
        }

        var tenantRaw = q["tenantId"].ToString();
        if (!string.IsNullOrEmpty(tenantRaw))
        {
            if (!Guid.TryParse(tenantRaw, out var t)) { badQuery = InvalidQuery(); return false; }
            tenantId = t;
        }

        var orgRaw = q["organizationId"].ToString();
        if (!string.IsNullOrEmpty(orgRaw))
        {
            if (!Guid.TryParse(orgRaw, out var o)) { badQuery = InvalidQuery(); return false; }
            organizationId = o;
        }

        return true;
    }

    private static IResult InvalidQuery() => CustomersHttp.Json(new { error = "Invalid query parameters" }, 400);
}
