using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Commands;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Companies routes — the port of upstream <c>api/companies/*</c>. Same shape as people: CRUD via the
/// factory (index-backed list), base + <c>customer_companies</c> satellite written atomically in the
/// <c>customers.companies.*</c> commands; hand-written detail, linked-people list, and roles.
/// </summary>
public sealed class CompaniesRoutes : ICustomersRouteGroup
{
    private static readonly string[] View = { "customers.companies.view" };
    private static readonly string[] Manage = { "customers.companies.manage" };

    public void Map(IEndpointRouteBuilder routes)
    {
        CrudRoute.Map(routes, Config());
        routes.MapGet("/api/customers/companies/{id}", (Func<HttpContext, string, Task<IResult>>)DetailAsync);
        routes.MapGet("/api/customers/companies/{id}/people", (Func<HttpContext, string, Task<IResult>>)ListPeopleAsync);
        EntityRolesRoutes.Map(routes, "company", "/api/customers/companies/{id}/roles");
    }

    internal static CrudConfig<CustomerEntity> Config() => new()
    {
        BasePath = "customers/companies",
        EntityType = CustomerWriteHelpers.CompanyEntityType,
        ResourceKind = "customers.company",
        DefaultSortField = "createdAt",
        UseIndexList = true,
        MapItemGet = false, // hand-written /companies/{id} detail below
        ListFeatures = View,
        CreateFeatures = Manage,
        UpdateFeatures = Manage,
        DeleteFeatures = Manage,
        IdSelector = e => e.Id,
        DeletedAtSelector = e => e.DeletedAt,
        TenantIdSelector = e => e.TenantId,
        OrganizationIdSelector = e => e.OrganizationId,
        Sorts = PeopleRoutes.BaseSorts(),
        ApplyFilters = (q, _, _) => q.Where(e => e.Kind == "company"),
        ProjectItem = PeopleRoutes.ProjectBase,
        ListHook = OverlayCompanyProfilesAsync,
        CreatedEvent = "customers.company.created",
        UpdatedEvent = "customers.company.updated",
        DeletedEvent = "customers.company.deleted",
        ValidateCreate = Data.CustomersValidators.Company,
        CreateStatus = 201,
        CreateResponse = o => new { id = o.Id, companyId = (o.Result as CompanyResult)?.CompanyId },
        CreateDispatch = async m =>
        {
            var r = await m.Bus.ExecuteWithLog<CompanyCreateInput, CompanyResult>(
                "customers.companies.create", new CompanyCreateInput(m.Ctx.OrganizationId ?? Guid.Empty, m.Ctx.TenantId ?? Guid.Empty, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.EntityId, r.LogEntry, r.Result);
        },
        UpdateDispatch = async m =>
        {
            var id = CustomersHttp.GuidOf(m.Body, "id") ?? Guid.Empty;
            var r = await m.Bus.ExecuteWithLog<CompanyUpdateInput, CompanyResult>(
                "customers.companies.update", new CompanyUpdateInput(id, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.EntityId, r.LogEntry, new { ok = true, updatedAt = CustomersHttp.Iso(r.Result.UpdatedAt) });
        },
        UpdateResponse = o => o.Result ?? new { ok = true },
        DeleteDispatch = async m =>
        {
            var id = PeopleRoutes.ResolveDeleteId(m);
            if (id is null) throw CommandHttpException.BadRequest("Company id is required");
            var r = await m.Bus.ExecuteWithLog<CompanyDeleteInput, CompanyResult>(
                "customers.companies.delete", new CompanyDeleteInput(id.Value), m.Ctx);
            return new CrudMutationOutcome(r.Result.EntityId, r.LogEntry);
        },
    };

    private static async Task OverlayCompanyProfilesAsync(IReadOnlyList<IDictionary<string, object?>> items, CommandContext ctx, HttpContext http)
    {
        if (items.Count == 0) return;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var ids = items.Select(i => Guid.TryParse(i["id"]?.ToString(), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        var profiles = await db.Set<CustomerCompanyProfile>().AsNoTracking().Where(p => ids.Contains(p.EntityId)).ToListAsync();
        var byEntity = profiles.ToDictionary(p => p.EntityId);
        foreach (var item in items)
        {
            if (!Guid.TryParse(item["id"]?.ToString(), out var id) || !byEntity.TryGetValue(id, out var p)) continue;
            item["legalName"] = p.LegalName; item["brandName"] = p.BrandName; item["domain"] = p.Domain;
            item["websiteUrl"] = p.WebsiteUrl; item["industry"] = p.Industry; item["sizeBucket"] = p.SizeBucket;
            item["annualRevenue"] = p.AnnualRevenue;
        }
    }

    private static async Task<IResult> DetailAsync(HttpContext http, string id)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var companyId)) return CustomersHttp.Json(new { error = "Invalid company id" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var entity = await db.Set<CustomerEntity>().AsNoTracking().FirstOrDefaultAsync(e =>
            e.Id == companyId && e.Kind == "company" && e.DeletedAt == null && e.TenantId == ctx!.TenantId);
        if (entity is null) return CustomersHttp.Json(new { error = "Company not found" }, 404);
        if (ctx!.OrganizationIds is { Count: > 0 } orgIds && !orgIds.Contains(entity.OrganizationId))
            return CustomersHttp.Json(new { error = "Access denied" }, 403);

        var profile = await db.Set<CustomerCompanyProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.EntityId == companyId);
        var addresses = await db.Set<CustomerAddress>().AsNoTracking().Where(a => a.EntityId == companyId).ToListAsync();
        var people = await LoadCompanyPeopleAsync(db, companyId);
        var tags = await PeopleRoutes.LoadTagsAsync(db, companyId);

        var item = PeopleRoutes.ProjectBase(entity);
        var codec = http.RequestServices.GetRequiredService<ICrudCustomFields>();
        await codec.MergeIntoDetailAsync(CustomerWriteHelpers.CompanyEntityType, item, ctx!);

        // Best-effort enrichment of the timeline collections, include-token gated (see CustomerDetailEnrichment).
        var tokens = CustomerDetailEnrichment.ParseIncludeTokens(http);
        var enriched = await CustomerDetailEnrichment.LoadAsync(db, entity, isCompany: true, tokens);

        // Best-effort deal KPIs from the company's linked deals (LTV/tenure/activity trend still deferred).
        var companyDealIds = await db.Set<CustomerDealCompanyLink>().AsNoTracking().Where(l => l.CompanyEntityId == companyId).Select(l => l.DealId).ToListAsync();
        var companyDeals = companyDealIds.Count == 0
            ? new List<CustomerDeal>()
            : await db.Set<CustomerDeal>().AsNoTracking().Where(d => companyDealIds.Contains(d.Id) && d.DeletedAt == null).ToListAsync();
        var activeDeals = companyDeals.Where(d => d.ClosureOutcome == null && d.Status != "won" && d.Status != "lost").ToList();
        var completedDeals = companyDeals.Where(d => d.ClosureOutcome == "won" || d.Status == "won").ToList();
        var activeDealsValue = activeDeals.Sum(d => d.ValueAmount ?? 0m);
        var dealCurrency = companyDeals.FirstOrDefault(d => d.ValueCurrency != null)?.ValueCurrency;

        return CustomersHttp.Json(new
        {
            interactionMode = "canonical",
            company = item,
            profile = profile is null ? null : new
            {
                id = profile.Id.ToString(), legalName = profile.LegalName, brandName = profile.BrandName,
                domain = profile.Domain, websiteUrl = profile.WebsiteUrl, industry = profile.Industry,
                sizeBucket = profile.SizeBucket, annualRevenue = profile.AnnualRevenue,
            },
            customFields = item.TryGetValue("customValues", out var cv) ? cv : null,
            tags,
            addresses = addresses.Select(PeopleRoutes.ProjectAddress).ToList(),
            people,
            comments = enriched.Comments,
            activities = enriched.Activities,
            interactions = enriched.Interactions,
            deals = enriched.Deals,
            todos = enriched.Todos,
            temperature = entity.Temperature,
            renewalQuarter = entity.RenewalQuarter,
            kpis = new
            {
                activeDealsCount = activeDeals.Count, activeDealsValue, dealCurrency,
                activityCount = enriched.ActivitiesCount, activityTrend = (object?)null, ltvValue = 0m,
                completedDealsCount = completedDeals.Count, clientTenureYears = 0,
            },
            counts = new
            {
                tags = tags.Count, comments = enriched.CommentsCount, activities = enriched.ActivitiesCount,
                interactions = enriched.InteractionsCount, todos = enriched.TodosCount,
                addresses = addresses.Count, deals = enriched.DealsCount, people = people.Count,
            },
            viewer = new { userId = ctx!.UserId?.ToString(), name = (string?)null, email = (string?)null },
        }, 200);
        // PARITY-TODO: kpis LTV/tenure/activity-trend + author hydration + private-email visibility remain deferred (ADR).
    }

    private static async Task<IResult> ListPeopleAsync(HttpContext http, string id)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var companyId)) return CustomersHttp.Json(new { error = "Invalid company id" }, 400);
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var q = http.Request.Query;
        var page = int.TryParse(q["page"], out var pg) && pg >= 1 ? pg : 1;
        var pageSize = int.TryParse(q["pageSize"], out var ps) ? Math.Clamp(ps, 1, 100) : 20;
        var all = await LoadCompanyPeopleAsync(db, companyId);
        var total = all.Count;
        var items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return CustomersHttp.Json(new { items, total, page, pageSize, totalPages = (int)Math.Ceiling(total / (double)pageSize) }, 200);
    }

    private static async Task<List<Dictionary<string, object?>>> LoadCompanyPeopleAsync(AppDbContext db, Guid companyId)
    {
        var links = await db.Set<CustomerPersonCompanyLink>().AsNoTracking().Where(l => l.CompanyEntityId == companyId && l.DeletedAt == null).ToListAsync();
        var personIds = links.Select(l => l.PersonEntityId).Distinct().ToList();
        var persons = await db.Set<CustomerEntity>().AsNoTracking().Where(e => personIds.Contains(e.Id)).ToListAsync();
        return links.Select(l =>
        {
            var p = persons.FirstOrDefault(x => x.Id == l.PersonEntityId);
            return new Dictionary<string, object?> { ["id"] = l.PersonEntityId.ToString(), ["displayName"] = p?.DisplayName, ["isPrimary"] = l.IsPrimary };
        }).ToList();
    }
}
