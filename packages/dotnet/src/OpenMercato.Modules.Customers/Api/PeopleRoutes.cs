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
/// People routes — the port of upstream <c>api/people/*</c>. List/create/update/delete go through the
/// CRUD factory (<see cref="CrudRoute.Map{TEntity}"/>) with <c>UseIndexList</c> so lists filter/sort on
/// custom fields via the query index; the base + satellite profile write is done atomically in the
/// <c>customers.people.*</c> command handlers. The detail view, person↔company links (+enriched), roles
/// and check-phone are hand-written per the contract's "Route detail" section.
/// </summary>
public sealed class PeopleRoutes : ICustomersRouteGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        CrudRoute.Map(routes, Config());

        routes.MapGet("/api/customers/people/check-phone", (Func<HttpContext, Task<IResult>>)CheckPhoneAsync);
        routes.MapGet("/api/customers/people/{id}", (Func<HttpContext, string, Task<IResult>>)DetailAsync);
        routes.MapGet("/api/customers/people/{id}/companies", (Func<HttpContext, string, Task<IResult>>)ListCompaniesAsync);
        routes.MapPost("/api/customers/people/{id}/companies", (Func<HttpContext, string, Task<IResult>>)LinkCompanyAsync);
        routes.MapGet("/api/customers/people/{id}/companies/enriched", (Func<HttpContext, string, Task<IResult>>)EnrichedCompaniesAsync);
        routes.MapPatch("/api/customers/people/{id}/companies/{linkId}", (Func<HttpContext, string, string, Task<IResult>>)PatchLinkAsync);
        routes.MapDelete("/api/customers/people/{id}/companies/{linkId}", (Func<HttpContext, string, string, Task<IResult>>)DeleteLinkAsync);
        EntityRolesRoutes.Map(routes, "person", "/api/customers/people/{id}/roles");
    }

    private static readonly string[] View = { "customers.people.view" };
    private static readonly string[] Manage = { "customers.people.manage" };

    // ---- CRUD factory config ------------------------------------------------------------------

    internal static CrudConfig<CustomerEntity> Config() => new()
    {
        BasePath = "customers/people",
        EntityType = CustomerWriteHelpers.PersonEntityType,
        ResourceKind = "customers.person",
        DefaultSortField = "createdAt",
        UseIndexList = true,
        MapItemGet = false, // hand-written /people/{id} detail below
        ListFeatures = View,
        CreateFeatures = Manage,
        UpdateFeatures = Manage,
        DeleteFeatures = Manage,
        IdSelector = e => e.Id,
        DeletedAtSelector = e => e.DeletedAt,
        TenantIdSelector = e => e.TenantId,
        OrganizationIdSelector = e => e.OrganizationId,
        Sorts = BaseSorts(),
        ApplyFilters = (q, _, _) => q.Where(e => e.Kind == "person"),
        ProjectItem = ProjectBase,
        ListHook = OverlayPersonProfilesAsync,
        CreatedEvent = "customers.person.created",
        UpdatedEvent = "customers.person.updated",
        DeletedEvent = "customers.person.deleted",
        ValidateCreate = Data.CustomersValidators.Person,
        CreateStatus = 201,
        CreateResponse = o => new { id = o.Id, personId = (o.Result as PersonResult)?.PersonId },
        CreateDispatch = async m =>
        {
            var r = await m.Bus.ExecuteWithLog<PersonCreateInput, PersonResult>(
                "customers.people.create", new PersonCreateInput(m.Ctx.OrganizationId ?? Guid.Empty, m.Ctx.TenantId ?? Guid.Empty, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.EntityId, r.LogEntry, r.Result);
        },
        UpdateDispatch = async m =>
        {
            var id = CustomersHttp.GuidOf(m.Body, "id") ?? Guid.Empty;
            var r = await m.Bus.ExecuteWithLog<PersonUpdateInput, PersonResult>(
                "customers.people.update", new PersonUpdateInput(id, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.EntityId, r.LogEntry, new { ok = true, updatedAt = CustomersHttp.Iso(r.Result.UpdatedAt) });
        },
        UpdateResponse = o => o.Result ?? new { ok = true },
        DeleteDispatch = async m =>
        {
            var id = ResolveDeleteId(m);
            if (id is null) throw CommandHttpException.BadRequest("Person id is required");
            var r = await m.Bus.ExecuteWithLog<PersonDeleteInput, PersonResult>(
                "customers.people.delete", new PersonDeleteInput(id.Value), m.Ctx);
            return new CrudMutationOutcome(r.Result.EntityId, r.LogEntry);
        },
    };

    internal static Guid? ResolveDeleteId(CrudMutationContext m)
    {
        if (CustomersHttp.GuidOf(m.Body, "id") is { } fromBody) return fromBody;
        if (m.Query.TryGetValue("id", out var raw) && Guid.TryParse(raw, out var g)) return g;
        return null;
    }

    internal static Dictionary<string, Func<IQueryable<CustomerEntity>, bool, IOrderedQueryable<CustomerEntity>>> BaseSorts() => new()
    {
        ["name"] = (q, d) => d ? q.OrderByDescending(e => e.DisplayName) : q.OrderBy(e => e.DisplayName),
        ["displayName"] = (q, d) => d ? q.OrderByDescending(e => e.DisplayName) : q.OrderBy(e => e.DisplayName),
        ["status"] = (q, d) => d ? q.OrderByDescending(e => e.Status) : q.OrderBy(e => e.Status),
        ["createdAt"] = (q, d) => d ? q.OrderByDescending(e => e.CreatedAt) : q.OrderBy(e => e.CreatedAt),
        ["updatedAt"] = (q, d) => d ? q.OrderByDescending(e => e.UpdatedAt) : q.OrderBy(e => e.UpdatedAt),
    };

    internal static IDictionary<string, object?> ProjectBase(CustomerEntity e) => new Dictionary<string, object?>
    {
        ["id"] = e.Id.ToString(),
        ["displayName"] = e.DisplayName,
        ["description"] = e.Description,
        ["ownerUserId"] = e.OwnerUserId?.ToString(),
        ["primaryEmail"] = e.PrimaryEmail,
        ["primaryPhone"] = e.PrimaryPhone,
        ["status"] = e.Status,
        ["lifecycleStage"] = e.LifecycleStage,
        ["source"] = e.Source,
        ["temperature"] = e.Temperature,
        ["renewalQuarter"] = e.RenewalQuarter,
        ["nextInteractionAt"] = CustomersHttp.Iso(e.NextInteractionAt),
        ["isActive"] = e.IsActive,
        ["organizationId"] = e.OrganizationId.ToString(),
        ["tenantId"] = e.TenantId.ToString(),
        ["createdAt"] = CustomersHttp.Iso(e.CreatedAt),
        ["updatedAt"] = CustomersHttp.Iso(e.UpdatedAt),
    };

    private static async Task OverlayPersonProfilesAsync(IReadOnlyList<IDictionary<string, object?>> items, CommandContext ctx, HttpContext http)
    {
        if (items.Count == 0) return;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var ids = items.Select(i => i.TryGetValue("id", out var v) && Guid.TryParse(v?.ToString(), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        var profiles = await db.Set<CustomerPersonProfile>().AsNoTracking().Where(p => ids.Contains(p.EntityId)).ToListAsync();
        var byEntity = profiles.ToDictionary(p => p.EntityId);
        foreach (var item in items)
        {
            if (!Guid.TryParse(item["id"]?.ToString(), out var id) || !byEntity.TryGetValue(id, out var p)) continue;
            item["firstName"] = p.FirstName; item["lastName"] = p.LastName; item["preferredName"] = p.PreferredName;
            item["jobTitle"] = p.JobTitle; item["department"] = p.Department; item["seniority"] = p.Seniority;
            item["timezone"] = p.Timezone; item["linkedInUrl"] = p.LinkedInUrl; item["twitterUrl"] = p.TwitterUrl;
            item["companyEntityId"] = p.CompanyEntityId?.ToString();
        }
    }

    // ---- GET /people/[id] (hand-written detail) ------------------------------------------------

    private static async Task<IResult> DetailAsync(HttpContext http, string id)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var personId)) return CustomersHttp.Json(new { error = "Invalid person id" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var entity = await db.Set<CustomerEntity>().AsNoTracking().FirstOrDefaultAsync(e =>
            e.Id == personId && e.Kind == "person" && e.DeletedAt == null && e.TenantId == ctx!.TenantId);
        if (entity is null) return CustomersHttp.Json(new { error = "Person not found" }, 404);
        if (ctx!.OrganizationIds is { Count: > 0 } orgIds && !orgIds.Contains(entity.OrganizationId))
            return CustomersHttp.Json(new { error = "Access denied" }, 403);

        var profile = await db.Set<CustomerPersonProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.EntityId == personId);
        var addresses = await db.Set<CustomerAddress>().AsNoTracking().Where(a => a.EntityId == personId).ToListAsync();
        var links = await db.Set<CustomerPersonCompanyLink>().AsNoTracking().Where(l => l.PersonEntityId == personId && l.DeletedAt == null).ToListAsync();
        var companyIds = links.Select(l => l.CompanyEntityId).Distinct().ToList();
        var companies = await db.Set<CustomerEntity>().AsNoTracking().Where(e => companyIds.Contains(e.Id)).ToListAsync();
        var tags = await LoadTagsAsync(db, personId);

        var item = ProjectBase(entity);
        var codec = http.RequestServices.GetRequiredService<ICrudCustomFields>();
        await codec.MergeIntoDetailAsync(CustomerWriteHelpers.PersonEntityType, item, ctx!);

        var companySummaries = links.Select(l =>
        {
            var c = companies.FirstOrDefault(x => x.Id == l.CompanyEntityId);
            return new { id = l.CompanyEntityId.ToString(), displayName = c?.DisplayName, isPrimary = l.IsPrimary };
        }).ToList();

        return CustomersHttp.Json(new
        {
            interactionMode = "canonical",
            person = item,
            profile = profile is null ? null : ProjectPersonProfile(profile),
            customFields = item.TryGetValue("customValues", out var cv) ? cv : null,
            tags,
            addresses = addresses.Select(ProjectAddress).ToList(),
            comments = Array.Empty<object>(),
            activities = Array.Empty<object>(),
            interactions = Array.Empty<object>(),
            deals = Array.Empty<object>(),
            todos = Array.Empty<object>(),
            isPrimary = links.Any(l => l.IsPrimary),
            companies = companySummaries,
            company = companySummaries.FirstOrDefault(),
            plannedActivitiesPreview = Array.Empty<object>(),
            counts = new
            {
                tags = tags.Count,
                comments = 0, activities = 0, interactions = 0, todos = 0,
                addresses = addresses.Count, deals = 0, companies = companySummaries.Count,
            },
            viewer = new { userId = ctx!.UserId?.ToString(), name = (string?)null, email = (string?)null },
        }, 200);
        // PARITY-TODO: comments/activities/interactions/deals/todos enrichment + include-token gating lands in Phase 3.
    }

    // ---- person↔company links -----------------------------------------------------------------

    private static async Task<IResult> ListCompaniesAsync(HttpContext http, string id)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var personId)) return CustomersHttp.Json(new { error = "Invalid person id" }, 400);
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var items = await SummarizePersonCompaniesAsync(db, personId);
        return CustomersHttp.Json(new { items }, 200);
    }

    private static async Task<IResult> LinkCompanyAsync(HttpContext http, string id)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var personId)) return CustomersHttp.Json(new { error = "Invalid person id" }, 400);
        if (ctx!.OrganizationId is not { } orgId) return CustomersHttp.Json(new { error = "Organization context is required" }, 400);
        var body = await CustomersHttp.ReadBodyAsync(http);
        var companyId = CustomersHttp.GuidOf(body, "companyId");
        if (companyId is null) return CustomersHttp.Json(new { error = "Invalid input" }, 400);
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<PersonCompanyLinkCreateInput, PersonCompanyLinkResult>(
                "customers.personCompanyLinks.create",
                new PersonCompanyLinkCreateInput(orgId, ctx.TenantId ?? Guid.Empty, personId, companyId.Value, CustomersHttp.Bool(body, "isPrimary")), ctx);
            SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(new { ok = true, result = new { id = r.Result.Id, companyId = r.Result.CompanyId, displayName = r.Result.DisplayName, isPrimary = r.Result.IsPrimary } }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
    }

    private static async Task<IResult> PatchLinkAsync(HttpContext http, string id, string linkId)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var personId)) return CustomersHttp.Json(new { error = "Invalid person id" }, 400);
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var resolved = await ResolveLinkIdAsync(db, personId, linkId);
        if (resolved is null) return CustomersHttp.Json(new { error = "Person-company link not found" }, 404);
        var body = await CustomersHttp.ReadBodyAsync(http);
        if (!CustomersHttp.Has(body, "isPrimary")) return CustomersHttp.Json(new { ok = true, result = (object?)null }, 200);
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<PersonCompanyLinkUpdateInput, PersonCompanyLinkResult>(
                "customers.personCompanyLinks.update",
                new PersonCompanyLinkUpdateInput(ctx!.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, resolved.Value, CustomersHttp.Bool(body, "isPrimary") ?? false), ctx);
            SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(new { ok = true, result = new { id = r.Result.Id, companyId = r.Result.CompanyId, displayName = r.Result.DisplayName, isPrimary = r.Result.IsPrimary } }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
    }

    private static async Task<IResult> DeleteLinkAsync(HttpContext http, string id, string linkId)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var personId)) return CustomersHttp.Json(new { error = "Invalid person id" }, 400);
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var resolved = await ResolveLinkIdAsync(db, personId, linkId);
        if (resolved is null) return CustomersHttp.Json(new { error = "Person-company link not found" }, 404);
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<PersonCompanyLinkDeleteInput, PersonCompanyLinkResult>(
                "customers.personCompanyLinks.delete",
                new PersonCompanyLinkDeleteInput(ctx!.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, resolved.Value), ctx);
            SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(new { ok = true }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
    }

    private static async Task<IResult> EnrichedCompaniesAsync(HttpContext http, string id)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var personId)) return CustomersHttp.Json(new { error = "Invalid person id" }, 400);
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var q = http.Request.Query;
        var page = int.TryParse(q["page"], out var pg) && pg >= 1 ? pg : 1;
        var pageSize = int.TryParse(q["pageSize"], out var ps) ? Math.Clamp(ps, 1, 100) : 20;

        var links = await db.Set<CustomerPersonCompanyLink>().AsNoTracking().Where(l => l.PersonEntityId == personId && l.DeletedAt == null).ToListAsync();
        var companyIds = links.Select(l => l.CompanyEntityId).Distinct().ToList();
        var companies = await db.Set<CustomerEntity>().AsNoTracking().Where(e => companyIds.Contains(e.Id)).ToListAsync();
        var profiles = await db.Set<CustomerCompanyProfile>().AsNoTracking().Where(p => companyIds.Contains(p.EntityId)).ToListAsync();

        var rows = links.Select(l =>
        {
            var c = companies.FirstOrDefault(x => x.Id == l.CompanyEntityId);
            var pr = profiles.FirstOrDefault(x => x.EntityId == l.CompanyEntityId);
            return new
            {
                id = l.Id.ToString(), companyId = l.CompanyEntityId.ToString(), displayName = c?.DisplayName,
                isPrimary = l.IsPrimary, industry = pr?.Industry, status = c?.Status, lifecycleStage = c?.LifecycleStage,
                temperature = c?.Temperature, renewalQuarter = c?.RenewalQuarter,
                activeDeal = (object?)null, lastContactAt = (string?)null, clv = (decimal?)null,
            };
        }).ToList();

        var total = rows.Count;
        var pageItems = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return CustomersHttp.Json(new { items = pageItems, total, page, pageSize, totalPages = (int)Math.Ceiling(total / (double)pageSize) }, 200);
        // PARITY-TODO: activeDeal/lastContactAt/clv + in-memory search/sort land with Phase 3 deals/interactions.
    }

    // ---- GET /people/check-phone --------------------------------------------------------------

    private static async Task<IResult> CheckPhoneAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        var digits = http.Request.Query["digits"].ToString();
        if (string.IsNullOrEmpty(digits) || !System.Text.RegularExpressions.Regex.IsMatch(digits, @"^\d{4,}$"))
            return CustomersHttp.Json(new { match = (object?)null }, 200);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var candidates = await db.Set<CustomerEntity>().AsNoTracking()
            .Where(e => e.Kind == "person" && e.DeletedAt == null && e.TenantId == ctx!.TenantId && e.PrimaryPhone != null)
            .ToListAsync();
        var match = candidates.FirstOrDefault(e =>
            System.Text.RegularExpressions.Regex.Replace(e.PrimaryPhone ?? string.Empty, @"\D", string.Empty) == digits);
        return CustomersHttp.Json(new { match = match is null ? null : (object)new { id = match.Id.ToString(), displayName = match.DisplayName } }, 200);
    }

    // ---- shared helpers -----------------------------------------------------------------------

    internal static async Task<List<Dictionary<string, object?>>> SummarizePersonCompaniesAsync(AppDbContext db, Guid personId)
    {
        var links = await db.Set<CustomerPersonCompanyLink>().AsNoTracking().Where(l => l.PersonEntityId == personId && l.DeletedAt == null).ToListAsync();
        if (links.Count > 0)
        {
            var companyIds = links.Select(l => l.CompanyEntityId).Distinct().ToList();
            var companies = await db.Set<CustomerEntity>().AsNoTracking().Where(e => companyIds.Contains(e.Id)).ToListAsync();
            return links.Select(l =>
            {
                var c = companies.FirstOrDefault(x => x.Id == l.CompanyEntityId);
                return new Dictionary<string, object?> { ["id"] = l.Id.ToString(), ["companyId"] = l.CompanyEntityId.ToString(), ["displayName"] = c?.DisplayName, ["isPrimary"] = l.IsPrimary };
            }).ToList();
        }
        // Fallback: synthetic single row from the legacy company_entity_id.
        var profile = await db.Set<CustomerPersonProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.EntityId == personId);
        if (profile?.CompanyEntityId is { } legacyId)
        {
            var c = await db.Set<CustomerEntity>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == legacyId);
            return new List<Dictionary<string, object?>> { new() { ["id"] = legacyId.ToString(), ["companyId"] = legacyId.ToString(), ["displayName"] = c?.DisplayName, ["isPrimary"] = true, ["synthetic"] = true } };
        }
        return new List<Dictionary<string, object?>>();
    }

    private static async Task<Guid?> ResolveLinkIdAsync(AppDbContext db, Guid personId, string linkId)
    {
        if (!Guid.TryParse(linkId, out var candidate)) return null;
        var byId = await db.Set<CustomerPersonCompanyLink>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == candidate && l.PersonEntityId == personId && l.DeletedAt == null);
        if (byId is not null) return byId.Id;
        var byCompany = await db.Set<CustomerPersonCompanyLink>().AsNoTracking().FirstOrDefaultAsync(l => l.CompanyEntityId == candidate && l.PersonEntityId == personId && l.DeletedAt == null);
        return byCompany?.Id;
    }

    internal static async Task<List<Dictionary<string, object?>>> LoadTagsAsync(AppDbContext db, Guid entityId)
    {
        var assignments = await db.Set<CustomerTagAssignment>().AsNoTracking().Where(a => a.EntityId == entityId).ToListAsync();
        var tagIds = assignments.Select(a => a.TagId).Distinct().ToList();
        var tags = await db.Set<CustomerTag>().AsNoTracking().Where(t => tagIds.Contains(t.Id)).ToListAsync();
        return tags.Select(t => new Dictionary<string, object?> { ["id"] = t.Id.ToString(), ["label"] = t.Label, ["color"] = t.Color }).ToList();
    }

    internal static object ProjectPersonProfile(CustomerPersonProfile p) => new
    {
        id = p.Id.ToString(), firstName = p.FirstName, lastName = p.LastName, preferredName = p.PreferredName,
        jobTitle = p.JobTitle, department = p.Department, seniority = p.Seniority, timezone = p.Timezone,
        linkedInUrl = p.LinkedInUrl, twitterUrl = p.TwitterUrl, companyEntityId = p.CompanyEntityId?.ToString(),
    };

    internal static object ProjectAddress(CustomerAddress a) => new
    {
        id = a.Id.ToString(), entityId = a.EntityId.ToString(), name = a.Name, purpose = a.Purpose,
        companyName = a.CompanyName, addressLine1 = a.AddressLine1, addressLine2 = a.AddressLine2,
        city = a.City, region = a.Region, postalCode = a.PostalCode, country = a.Country,
        buildingNumber = a.BuildingNumber, flatNumber = a.FlatNumber, latitude = a.Latitude, longitude = a.Longitude,
        isPrimary = a.IsPrimary,
    };

    internal static void SetOperationHeader(HttpContext http, ActionLog? log)
    {
        var header = OperationHeader.Build(log);
        if (header is not null) http.Response.Headers["x-om-operation"] = header;
    }
}
