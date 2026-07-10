using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Currencies.Services;
using OpenMercato.Modules.Customers.Commands;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Deals routes — the port of upstream <c>api/deals/*</c>. List/create/update/delete go through the CRUD
/// factory (<see cref="CrudRoute.Map{TEntity}"/>, <c>UseIndexList</c>) with base fields projected snake-case
/// to match the deal list schema and an <c>afterList</c> hook decorating each item with linked
/// <c>people</c>/<c>companies</c>; the base + link + stage-transition writes live in the undoable
/// <c>customers.deals.*</c> commands. Detail, linked-entity lists, closure stats, the kanban aggregate,
/// the KPI summary, and the two bulk-update endpoints are hand-written per the contract.
/// </summary>
public sealed class DealsRoutes : ICustomersRouteGroup
{
    private static readonly string[] View = { "customers.deals.view" };
    private static readonly string[] Manage = { "customers.deals.manage" };

    public void Map(IEndpointRouteBuilder routes)
    {
        CrudRoute.Map(routes, Config());
        routes.MapGet("/api/customers/deals/aggregate", (Func<HttpContext, Task<IResult>>)AggregateAsync);
        routes.MapGet("/api/customers/deals/summary", (Func<HttpContext, Task<IResult>>)SummaryAsync);
        routes.MapPost("/api/customers/deals/bulk-update-owner", (Func<HttpContext, Task<IResult>>)(http => BulkUpdateAsync(http, stage: false)));
        routes.MapPost("/api/customers/deals/bulk-update-stage", (Func<HttpContext, Task<IResult>>)(http => BulkUpdateAsync(http, stage: true)));
        routes.MapGet("/api/customers/deals/{id}", (Func<HttpContext, string, Task<IResult>>)DetailAsync);
        routes.MapGet("/api/customers/deals/{id}/people", (Func<HttpContext, string, Task<IResult>>)(( http, id) => LinkedAsync(http, id, "person")));
        routes.MapGet("/api/customers/deals/{id}/companies", (Func<HttpContext, string, Task<IResult>>)((http, id) => LinkedAsync(http, id, "company")));
        routes.MapGet("/api/customers/deals/{id}/stats", (Func<HttpContext, string, Task<IResult>>)StatsAsync);
    }

    // ---- CRUD factory config ------------------------------------------------------------------

    internal static CrudConfig<CustomerDeal> Config() => new()
    {
        BasePath = "customers/deals",
        EntityType = DealWriteHelpers.DealEntityType,
        ResourceKind = "customers.deal",
        DefaultSortField = "createdAt",
        UseIndexList = true,
        MapItemGet = false, // hand-written /deals/{id} detail below
        ListFeatures = View,
        CreateFeatures = Manage,
        UpdateFeatures = Manage,
        DeleteFeatures = Manage,
        IdSelector = d => d.Id,
        DeletedAtSelector = d => d.DeletedAt,
        TenantIdSelector = d => d.TenantId,
        OrganizationIdSelector = d => d.OrganizationId,
        Sorts = new Dictionary<string, Func<IQueryable<CustomerDeal>, bool, IOrderedQueryable<CustomerDeal>>>
        {
            ["createdAt"] = (q, d) => d ? q.OrderByDescending(x => x.CreatedAt) : q.OrderBy(x => x.CreatedAt),
            ["updatedAt"] = (q, d) => d ? q.OrderByDescending(x => x.UpdatedAt) : q.OrderBy(x => x.UpdatedAt),
            ["title"] = (q, d) => d ? q.OrderByDescending(x => x.Title) : q.OrderBy(x => x.Title),
            ["value"] = (q, d) => d ? q.OrderByDescending(x => x.ValueAmount) : q.OrderBy(x => x.ValueAmount),
            ["probability"] = (q, d) => d ? q.OrderByDescending(x => x.Probability) : q.OrderBy(x => x.Probability),
            ["expectedCloseAt"] = (q, d) => d ? q.OrderByDescending(x => x.ExpectedCloseAt) : q.OrderBy(x => x.ExpectedCloseAt),
        },
        ApplyFilters = ApplyDealFilters,
        ProjectItem = ProjectDealListItem,
        ListHook = DecorateAssociationsAsync,
        ResolveListRestrictIds = ResolveDealAssociationIdsAsync,
        CreatedEvent = "customers.deal.created",
        UpdatedEvent = "customers.deal.updated",
        DeletedEvent = "customers.deal.deleted",
        ValidateCreate = DealValidators.Deal,
        CreateStatus = 201,
        CreateDispatch = async m =>
        {
            var r = await m.Bus.ExecuteWithLog<DealCreateInput, DealResult>(
                "customers.deals.create", new DealCreateInput(m.Ctx.OrganizationId ?? Guid.Empty, m.Ctx.TenantId ?? Guid.Empty, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.DealId, r.LogEntry);
        },
        UpdateDispatch = async m =>
        {
            var id = CustomersHttp.GuidOf(m.Body, "id") ?? Guid.Empty;
            var r = await m.Bus.ExecuteWithLog<DealUpdateInput, DealResult>(
                "customers.deals.update", new DealUpdateInput(id, m.Body), m.Ctx);
            return new CrudMutationOutcome(r.Result.DealId, r.LogEntry);
        },
        DeleteDispatch = async m =>
        {
            var id = PeopleRoutes.ResolveDeleteId(m);
            if (id is null) throw CommandHttpException.BadRequest("Deal id is required");
            var r = await m.Bus.ExecuteWithLog<DealDeleteInput, DealResult>(
                "customers.deals.delete", new DealDeleteInput(id.Value), m.Ctx);
            return new CrudMutationOutcome(r.Result.DealId, r.LogEntry);
        },
    };

    // Resolve the deals ?personId=/?companyId=/?personIds=/?companyIds= association filters into the set of
    // linked deal ids (upstream applyEntityIdRestriction). Union within a multi-id list, intersect across
    // the person-set and company-set. Returns null when no association filter is present, an empty list when
    // one is present but matches nothing. (OM integration tests TC-CRM-046/047.)
    private static async Task<IReadOnlyList<Guid>?> ResolveDealAssociationIdsAsync(CrudListQuery query, CommandContext ctx, HttpContext http)
    {
        List<Guid> Ids(string key)
        {
            var acc = new List<Guid>();
            if (query.Filters.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
                foreach (var part in raw.Split(','))
                    if (Guid.TryParse(part.Trim(), out var g)) acc.Add(g);
            return acc;
        }

        var personIds = Ids("personId").Concat(Ids("personIds")).Distinct().ToList();
        var companyIds = Ids("companyId").Concat(Ids("companyIds")).Distinct().ToList();
        if (personIds.Count == 0 && companyIds.Count == 0) return null;

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        HashSet<Guid>? result = null;
        if (personIds.Count > 0)
        {
            var pd = await db.Set<CustomerDealPersonLink>().AsNoTracking()
                .Where(l => personIds.Contains(l.PersonEntityId)).Select(l => l.DealId).Distinct().ToListAsync();
            result = pd.ToHashSet();
        }
        if (companyIds.Count > 0)
        {
            var cd = (await db.Set<CustomerDealCompanyLink>().AsNoTracking()
                .Where(l => companyIds.Contains(l.CompanyEntityId)).Select(l => l.DealId).Distinct().ToListAsync()).ToHashSet();
            result = result is null ? cd : result.Intersect(cd).ToHashSet();
        }
        return (result ?? new HashSet<Guid>()).ToList();
    }

    internal static IDictionary<string, object?> ProjectDealListItem(CustomerDeal d) => new Dictionary<string, object?>
    {
        ["id"] = d.Id.ToString(),
        ["title"] = d.Title,
        ["description"] = d.Description,
        ["status"] = d.Status,
        ["pipeline_stage"] = d.PipelineStage,
        ["pipeline_id"] = d.PipelineId?.ToString(),
        ["pipeline_stage_id"] = d.PipelineStageId?.ToString(),
        ["value_amount"] = d.ValueAmount,
        ["value_currency"] = d.ValueCurrency,
        ["probability"] = d.Probability,
        ["expected_close_at"] = CustomersHttp.Iso(d.ExpectedCloseAt),
        ["owner_user_id"] = d.OwnerUserId?.ToString(),
        ["source"] = d.Source,
        ["closure_outcome"] = d.ClosureOutcome,
        ["loss_reason_id"] = d.LossReasonId?.ToString(),
        ["loss_notes"] = d.LossNotes,
        ["organization_id"] = d.OrganizationId.ToString(),
        ["tenant_id"] = d.TenantId.ToString(),
        ["created_at"] = CustomersHttp.Iso(d.CreatedAt),
        ["updated_at"] = CustomersHttp.Iso(d.UpdatedAt),
    };

    // Fallback-path filters (the index-backed path resolves cf:/base eq filters via the query index).
    private static IQueryable<CustomerDeal> ApplyDealFilters(IQueryable<CustomerDeal> q, CrudListQuery query, CommandContext ctx)
    {
        var f = query.Filters;
        if (f.TryGetValue("status", out var status) && !string.IsNullOrWhiteSpace(status))
            q = q.Where(d => d.Status == status);
        if (f.TryGetValue("pipelineStage", out var ps) && !string.IsNullOrWhiteSpace(ps))
            q = q.Where(d => d.PipelineStage == ps);
        if (f.TryGetValue("pipelineId", out var pid) && Guid.TryParse(pid, out var pidG))
            q = q.Where(d => d.PipelineId == pidG);
        if (f.TryGetValue("pipelineStageId", out var psid))
        {
            if (psid == "__unassigned") q = q.Where(d => d.PipelineStageId == null);
            else if (Guid.TryParse(psid, out var psidG)) q = q.Where(d => d.PipelineStageId == psidG);
        }
        if (f.TryGetValue("ownerUserId", out var owner) && Guid.TryParse(owner, out var ownerG))
            q = q.Where(d => d.OwnerUserId == ownerG);
        if (f.TryGetValue("valueCurrency", out var cur) && !string.IsNullOrWhiteSpace(cur))
        {
            var upper = cur.Trim().ToUpperInvariant();
            q = q.Where(d => d.ValueCurrency == upper);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(d => d.Title.ToLower().Contains(term) || (d.Description != null && d.Description.ToLower().Contains(term)));
        }
        return q;
    }

    private static async Task DecorateAssociationsAsync(IReadOnlyList<IDictionary<string, object?>> items, CommandContext ctx, HttpContext http)
    {
        if (items.Count == 0) return;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var ids = items.Select(i => Guid.TryParse(i["id"]?.ToString(), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
        if (ids.Count == 0) return;

        var personLinks = await db.Set<CustomerDealPersonLink>().AsNoTracking().Where(l => ids.Contains(l.DealId)).ToListAsync();
        var companyLinks = await db.Set<CustomerDealCompanyLink>().AsNoTracking().Where(l => ids.Contains(l.DealId)).ToListAsync();
        var entityIds = personLinks.Select(l => l.PersonEntityId).Concat(companyLinks.Select(l => l.CompanyEntityId)).Distinct().ToList();
        var entities = await db.Set<CustomerEntity>().AsNoTracking().Where(e => entityIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName);

        foreach (var item in items)
        {
            if (!Guid.TryParse(item["id"]?.ToString(), out var dealId)) continue;
            var people = personLinks.Where(l => l.DealId == dealId)
                .Select(l => new { id = l.PersonEntityId.ToString(), label = entities.GetValueOrDefault(l.PersonEntityId) }).ToList();
            var companies = companyLinks.Where(l => l.DealId == dealId)
                .Select(l => new { id = l.CompanyEntityId.ToString(), label = entities.GetValueOrDefault(l.CompanyEntityId) }).ToList();
            item["personIds"] = people.Select(p => p.id).ToList();
            item["people"] = people;
            item["companyIds"] = companies.Select(c => c.id).ToList();
            item["companies"] = companies;
        }
    }

    // ---- GET /deals/{id} (hand-written detail) ------------------------------------------------

    private static async Task<IResult> DetailAsync(HttpContext http, string id)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View, "Authentication required");
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var dealId)) return CustomersHttp.Json(new { error = "Deal not found" }, 404);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var deal = await db.Set<CustomerDeal>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == dealId && d.DeletedAt == null);
        if (deal is null) return CustomersHttp.Json(new { error = "Deal not found" }, 404);
        if (ctx!.TenantId is { } t && deal.TenantId != t) return CustomersHttp.Json(new { error = "Deal not found" }, 404);
        if (ctx.OrganizationIds is { Count: > 0 } orgIds && !orgIds.Contains(deal.OrganizationId))
            return CustomersHttp.Json(new { error = "Access denied" }, 403);

        var include = ReadIncludeFlags(http);
        var view = http.Request.Query["view"].ToString();
        var lite = view is "lite" or "detail-lite";
        var includeStages = include.Contains("stages");

        var personLinks = await db.Set<CustomerDealPersonLink>().AsNoTracking().Where(l => l.DealId == dealId).OrderBy(l => l.CreatedAt).ToListAsync();
        var companyLinks = await db.Set<CustomerDealCompanyLink>().AsNoTracking().Where(l => l.DealId == dealId).OrderBy(l => l.CreatedAt).ToListAsync();
        var linkedPersonIds = personLinks.Select(l => l.PersonEntityId).Distinct().ToList();
        var linkedCompanyIds = companyLinks.Select(l => l.CompanyEntityId).Distinct().ToList();

        var personSlice = lite ? linkedPersonIds.Take(3).ToList() : linkedPersonIds;
        var companySlice = lite ? linkedCompanyIds.Take(3).ToList() : linkedCompanyIds;
        var personEntities = await db.Set<CustomerEntity>().AsNoTracking().Where(e => personSlice.Contains(e.Id) && e.DeletedAt == null).ToListAsync();
        var companyEntities = await db.Set<CustomerEntity>().AsNoTracking().Where(e => companySlice.Contains(e.Id) && e.DeletedAt == null).ToListAsync();
        var personProfiles = await db.Set<CustomerPersonProfile>().AsNoTracking().Where(p => personSlice.Contains(p.EntityId)).ToDictionaryAsync(p => p.EntityId);
        var companyProfiles = await db.Set<CustomerCompanyProfile>().AsNoTracking().Where(p => companySlice.Contains(p.EntityId)).ToDictionaryAsync(p => p.EntityId);

        var people = personSlice.Select(pid => personEntities.FirstOrDefault(e => e.Id == pid)).Where(e => e is not null).Select(e =>
        {
            var profile = personProfiles.GetValueOrDefault(e!.Id);
            var jobTitle = profile?.JobTitle?.Trim();
            var email = string.IsNullOrWhiteSpace(e.PrimaryEmail) ? null : e.PrimaryEmail!.Trim();
            var phone = string.IsNullOrWhiteSpace(e.PrimaryPhone) ? null : e.PrimaryPhone!.Trim();
            var label = !string.IsNullOrWhiteSpace(e.DisplayName) ? e.DisplayName : email ?? phone ?? e.Id.ToString();
            return new { id = e.Id.ToString(), label, subtitle = jobTitle ?? email ?? phone, kind = "person" };
        }).ToList();

        var companies = companySlice.Select(cid => companyEntities.FirstOrDefault(e => e.Id == cid)).Where(e => e is not null).Select(e =>
        {
            var profile = companyProfiles.GetValueOrDefault(e!.Id);
            var domain = string.IsNullOrWhiteSpace(profile?.Domain) ? null : profile!.Domain!.Trim();
            var website = string.IsNullOrWhiteSpace(profile?.WebsiteUrl) ? null : profile!.WebsiteUrl!.Trim();
            var label = !string.IsNullOrWhiteSpace(e.DisplayName) ? e.DisplayName : domain ?? website ?? e.Id.ToString();
            return new { id = e.Id.ToString(), label, subtitle = domain ?? website, kind = "company" };
        }).ToList();

        // Custom fields (merged EAV) for the deal.
        var cfDict = new Dictionary<string, object?> { ["id"] = deal.Id.ToString() };
        var codec = http.RequestServices.GetRequiredService<ICrudCustomFields>();
        await codec.MergeIntoDetailAsync(DealWriteHelpers.DealEntityType, cfDict, ctx);
        var customFields = cfDict.TryGetValue("customValues", out var cv) && cv is not null ? cv : new Dictionary<string, object?>();

        // Pipeline context (only when include=stages).
        object[] pipelineStages = Array.Empty<object>();
        object[] stageTransitions = Array.Empty<object>();
        string? pipelineName = null;
        if (includeStages)
        {
            var effectivePipelineId = deal.PipelineId;
            if (effectivePipelineId is { } pipeId)
            {
                var stages = await db.Set<CustomerPipelineStage>().AsNoTracking()
                    .Where(s => s.PipelineId == pipeId && s.OrganizationId == deal.OrganizationId && s.TenantId == deal.TenantId)
                    .OrderBy(s => s.Order).ToListAsync();
                var labels = stages.Select(s => s.Label.Trim().ToLowerInvariant()).ToList();
                var appearances = await db.Set<CustomerDictionaryEntry>().AsNoTracking()
                    .Where(e => e.OrganizationId == deal.OrganizationId && e.TenantId == deal.TenantId && e.Kind == "pipeline_stage" && labels.Contains(e.NormalizedValue))
                    .ToDictionaryAsync(e => e.NormalizedValue);
                pipelineStages = stages.Select(s =>
                {
                    var a = appearances.GetValueOrDefault(s.Label.Trim().ToLowerInvariant());
                    return (object)new { id = s.Id.ToString(), label = s.Label, order = s.Order, color = a?.Color, icon = a?.Icon };
                }).ToArray();
                var pipeline = await db.Set<CustomerPipeline>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipeId && p.OrganizationId == deal.OrganizationId && p.TenantId == deal.TenantId);
                pipelineName = pipeline?.Name;
            }
            var transitions = await db.Set<CustomerDealStageTransition>().AsNoTracking()
                .Where(x => x.DealId == deal.Id && x.DeletedAt == null)
                .OrderBy(x => x.StageOrder).ThenBy(x => x.TransitionedAt).ToListAsync();
            stageTransitions = transitions.Select(x => (object)new
            {
                stageId = x.StageId.ToString(), stageLabel = x.StageLabel, stageOrder = x.StageOrder,
                transitionedAt = CustomersHttp.Iso(x.TransitionedAt),
            }).ToArray();
        }

        // Owner — user name/email are encrypted at rest and not decrypted here (// PARITY-TODO).
        object? owner = deal.OwnerUserId is { } ownerId ? new { id = ownerId.ToString(), name = ownerId.ToString(), email = "" } : null;

        return CustomersHttp.Json(new
        {
            deal = new
            {
                id = deal.Id.ToString(),
                title = deal.Title,
                description = deal.Description,
                status = deal.Status,
                pipelineStage = deal.PipelineStage,
                pipelineId = deal.PipelineId?.ToString(),
                pipelineStageId = deal.PipelineStageId?.ToString(),
                valueAmount = deal.ValueAmount,
                valueCurrency = deal.ValueCurrency,
                probability = deal.Probability,
                expectedCloseAt = CustomersHttp.Iso(deal.ExpectedCloseAt),
                ownerUserId = deal.OwnerUserId?.ToString(),
                source = deal.Source,
                closureOutcome = deal.ClosureOutcome,
                lossReasonId = deal.LossReasonId?.ToString(),
                lossNotes = deal.LossNotes,
                organizationId = deal.OrganizationId.ToString(),
                tenantId = deal.TenantId.ToString(),
                createdAt = CustomersHttp.Iso(deal.CreatedAt),
                updatedAt = CustomersHttp.Iso(deal.UpdatedAt),
            },
            people,
            companies,
            linkedPersonIds = linkedPersonIds.Select(x => x.ToString()).ToList(),
            linkedCompanyIds = linkedCompanyIds.Select(x => x.ToString()).ToList(),
            counts = new { people = linkedPersonIds.Count, companies = linkedCompanyIds.Count },
            customFields,
            viewer = new { userId = ctx.UserId?.ToString(), name = (string?)null, email = (string?)null },
            pipelineStages,
            pipelineName,
            stageTransitions,
            owner,
        }, 200);
    }

    private static HashSet<string> ReadIncludeFlags(HttpContext http)
    {
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in http.Request.Query["include"])
            if (raw is not null)
                foreach (var token in raw.Split(','))
                    if (!string.IsNullOrWhiteSpace(token)) flags.Add(token.Trim().ToLowerInvariant());
        return flags;
    }

    // ---- GET /deals/{id}/people | /companies (in-memory paged) --------------------------------

    private static async Task<IResult> LinkedAsync(HttpContext http, string id, string kind)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var dealId)) return CustomersHttp.Json(new { error = "Deal not found" }, 404);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var deal = await db.Set<CustomerDeal>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == dealId && d.DeletedAt == null && d.TenantId == ctx!.TenantId);
        if (deal is null) return CustomersHttp.Json(new { error = "Deal not found" }, 404);
        if (ctx!.OrganizationIds is { Count: > 0 } orgIds && !orgIds.Contains(deal.OrganizationId))
            return CustomersHttp.Json(new { error = "Access denied" }, 403);

        var q = http.Request.Query;
        var page = int.TryParse(q["page"], out var pg) && pg >= 1 ? pg : 1;
        var pageSize = int.TryParse(q["pageSize"], out var ps) ? Math.Clamp(ps, 1, 100) : 20;
        var search = q["search"].ToString();
        var sort = NormalizeLinkedSort(q["sort"].ToString());

        List<LinkedItem> items;
        if (kind == "person")
        {
            var links = await db.Set<CustomerDealPersonLink>().AsNoTracking().Where(l => l.DealId == dealId).ToListAsync();
            var ids = links.Select(l => l.PersonEntityId).Distinct().ToList();
            var entities = await db.Set<CustomerEntity>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id);
            items = links.Where(l => entities.ContainsKey(l.PersonEntityId)).Select(l =>
            {
                var e = entities[l.PersonEntityId];
                return new LinkedItem(e.Id.ToString(), !string.IsNullOrWhiteSpace(e.DisplayName) ? e.DisplayName : e.PrimaryEmail ?? e.Id.ToString(),
                    e.PrimaryEmail ?? e.PrimaryPhone, "person", l.CreatedAt);
            }).ToList();
        }
        else
        {
            var links = await db.Set<CustomerDealCompanyLink>().AsNoTracking().Where(l => l.DealId == dealId).ToListAsync();
            var ids = links.Select(l => l.CompanyEntityId).Distinct().ToList();
            var entities = await db.Set<CustomerEntity>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id);
            var profiles = await db.Set<CustomerCompanyProfile>().AsNoTracking().Where(p => ids.Contains(p.EntityId)).ToDictionaryAsync(p => p.EntityId);
            items = links.Where(l => entities.ContainsKey(l.CompanyEntityId)).Select(l =>
            {
                var e = entities[l.CompanyEntityId];
                var domain = profiles.GetValueOrDefault(e.Id)?.Domain;
                return new LinkedItem(e.Id.ToString(), !string.IsNullOrWhiteSpace(e.DisplayName) ? e.DisplayName : domain ?? e.Id.ToString(),
                    domain ?? e.PrimaryEmail ?? e.PrimaryPhone, "company", l.CreatedAt);
            }).ToList();
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            items = items.Where(i => i.Label.ToLowerInvariant().Contains(term) || (i.Subtitle?.ToLowerInvariant().Contains(term) ?? false)).ToList();
        }
        items = sort switch
        {
            "label-desc" => items.OrderByDescending(i => i.Label, StringComparer.OrdinalIgnoreCase).ToList(),
            "recent" => items.OrderByDescending(i => i.LinkedAt).ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => items.OrderBy(i => i.Label, StringComparer.OrdinalIgnoreCase).ToList(),
        };

        var total = items.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Min(page, totalPages);
        var pageItems = items.Skip((page - 1) * pageSize).Take(pageSize).Select(i => new
        {
            id = i.Id, label = i.Label, subtitle = i.Subtitle, kind = i.Kind, linkedAt = CustomersHttp.Iso(i.LinkedAt),
        }).ToList();
        return CustomersHttp.Json(new { items = pageItems, total, page, pageSize, totalPages }, 200);
    }

    private static string NormalizeLinkedSort(string? sort) => sort switch
    {
        "label-desc" or "name-desc" => "label-desc",
        "recent" => "recent",
        _ => "label-asc",
    };

    private sealed record LinkedItem(string Id, string Label, string? Subtitle, string Kind, DateTimeOffset LinkedAt);

    // ---- GET /deals/{id}/stats ----------------------------------------------------------------

    private static async Task<IResult> StatsAsync(HttpContext http, string id)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View, "Authentication required");
        if (denied is not null) return denied;
        if (!Guid.TryParse(id, out var dealId)) return CustomersHttp.Json(new { error = "Deal not found" }, 404);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var deal = await db.Set<CustomerDeal>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == dealId && d.DeletedAt == null && d.TenantId == ctx!.TenantId);
        if (deal is null) return CustomersHttp.Json(new { error = "Deal not found" }, 404);
        if (ctx!.OrganizationIds is { Count: > 0 } orgIds && !orgIds.Contains(deal.OrganizationId))
            return CustomersHttp.Json(new { error = "Access denied" }, 403);
        if (string.IsNullOrEmpty(deal.ClosureOutcome))
            return CustomersHttp.Json(new { error = "Deal is not closed", code = "DEAL_NOT_CLOSED" }, 400);

        var now = DateTime.UtcNow;
        var weekStart = StartOfIsoWeek(now);
        var quarterStart = new DateTime(now.Year, ((now.Month - 1) / 3) * 3 + 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var dealsClosedThisPeriod = await db.Set<CustomerDeal>().CountAsync(d =>
            d.OrganizationId == deal.OrganizationId && d.TenantId == deal.TenantId &&
            d.ClosureOutcome == deal.ClosureOutcome && d.DeletedAt == null && d.UpdatedAt >= weekStart);

        int? dealRankInQuarter = null;
        if (deal.ClosureOutcome == "won" && deal.ValueAmount is { } amount)
        {
            var higher = await db.Set<CustomerDeal>().CountAsync(d =>
                d.OrganizationId == deal.OrganizationId && d.TenantId == deal.TenantId && d.ClosureOutcome == "won" &&
                d.DeletedAt == null && d.UpdatedAt >= quarterStart && d.ValueAmount > amount);
            dealRankInQuarter = higher + 1;
        }

        var pipeline = deal.PipelineId is { } pipeId
            ? await db.Set<CustomerPipeline>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == pipeId && p.TenantId == deal.TenantId && p.OrganizationId == deal.OrganizationId)
            : null;

        string? lossReason = null;
        if (deal.LossReasonId is { } lossId)
        {
            // Loss reason resolves against the generic dictionaries module (key sales.deal_loss_reason).
            lossReason = await ResolveLossReasonAsync(db, lossId, deal.OrganizationId, deal.TenantId);
        }

        var salesCycleDays = Math.Max(0, (int)Math.Floor((deal.UpdatedAt - deal.CreatedAt).TotalMilliseconds / 86400000d));

        return CustomersHttp.Json(new
        {
            dealValue = deal.ValueAmount,
            dealCurrency = deal.ValueCurrency,
            closureOutcome = deal.ClosureOutcome,
            closedAt = CustomersHttp.Iso(deal.UpdatedAt),
            pipelineName = pipeline?.Name,
            dealsClosedThisPeriod,
            salesCycleDays,
            dealRankInQuarter,
            lossReason,
        }, 200);
    }

    private static async Task<string?> ResolveLossReasonAsync(AppDbContext db, Guid entryId, Guid orgId, Guid tenantId)
    {
        try
        {
            var entry = await db.Set<OpenMercato.Modules.Dictionaries.Data.DictionaryEntry>().AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == entryId && e.OrganizationId == orgId && e.TenantId == tenantId);
            if (entry is null) return null;
            var dict = await db.Set<OpenMercato.Modules.Dictionaries.Data.Dictionary>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == entry.DictionaryId);
            if (dict?.Key != "sales.deal_loss_reason") return null;
            return !string.IsNullOrEmpty(entry.Label) ? entry.Label : entry.Value;
        }
        catch { return null; }
    }

    private static DateTime StartOfIsoWeek(DateTime date)
    {
        var day = (int)date.DayOfWeek;
        var diff = day == 0 ? -6 : 1 - day;
        return date.Date.AddDays(diff);
    }

    // ---- GET /deals/aggregate (kanban lane headers) -------------------------------------------

    private static async Task<IResult> AggregateAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        if (ctx!.TenantId is not { } tenantId) return CustomersHttp.Json(new { error = "Unauthorized" }, 401);
        var orgIds = ResolveOrgFilterIds(ctx);
        if (orgIds.Count == 0) return CustomersHttp.Json(new { error = "Unauthorized" }, 401);

        var q = http.Request.Query;
        var statuses = ReadArrayParam(q, "status");
        foreach (var s in statuses)
            if (s is not ("open" or "closed" or "win" or "loose"))
                return CustomersHttp.Json(new { error = "Invalid query parameters" }, 400);
        var pipelineIdParam = q["pipelineId"].ToString();
        Guid? pipelineId = Guid.TryParse(pipelineIdParam, out var pg) ? pg : null;
        var ownerIds = ReadArrayParam(q, "ownerUserId").Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();
        var personIds = ReadArrayParam(q, "personId").Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();
        var companyIds = ReadArrayParam(q, "companyId").Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();
        var isOverdue = CrudListQueryParser.ParseBooleanToken(q["isOverdue"].ToString());
        var closeFrom = ParseDate(q["expectedCloseAtFrom"].ToString());
        var closeTo = ParseDate(q["expectedCloseAtTo"].ToString());

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var query = db.Set<CustomerDeal>().AsNoTracking().Where(d => d.TenantId == tenantId && orgIds.Contains(d.OrganizationId) && d.DeletedAt == null);
        if (pipelineId is { } pid) query = query.Where(d => d.PipelineId == pid);
        if (statuses.Count > 0) query = query.Where(d => statuses.Contains(d.Status));
        if (ownerIds.Count > 0) query = query.Where(d => d.OwnerUserId != null && ownerIds.Contains(d.OwnerUserId.Value));
        if (closeFrom is { } cf) query = query.Where(d => d.ExpectedCloseAt >= cf);
        if (closeTo is { } cto) query = query.Where(d => d.ExpectedCloseAt <= cto);
        if (isOverdue) { var today = DateTime.UtcNow.Date; query = query.Where(d => d.Status == "open" && d.ExpectedCloseAt != null && d.ExpectedCloseAt < today); }

        var deals = await query.ToListAsync();
        if (personIds.Count > 0)
        {
            var matched = await db.Set<CustomerDealPersonLink>().AsNoTracking().Where(l => personIds.Contains(l.PersonEntityId)).Select(l => l.DealId).Distinct().ToListAsync();
            var set = matched.ToHashSet();
            deals = deals.Where(d => set.Contains(d.Id)).ToList();
        }
        if (companyIds.Count > 0)
        {
            var matched = await db.Set<CustomerDealCompanyLink>().AsNoTracking().Where(l => companyIds.Contains(l.CompanyEntityId)).Select(l => l.DealId).Distinct().ToListAsync();
            var set = matched.ToHashSet();
            deals = deals.Where(d => set.Contains(d.Id)).ToList();
        }

        var exchange = http.RequestServices.GetService<IExchangeRateService>();
        var scope = new CurrencyScope(tenantId, orgIds[0]);
        var baseCurrencyCode = exchange is not null ? await exchange.GetBaseCurrencyCodeAsync(scope) : null;

        // Group by (stageId ?? __unassigned, UPPER(currency)).
        var stageMap = new Dictionary<string, StageAgg>();
        foreach (var d in deals)
        {
            var stageId = d.PipelineStageId?.ToString() ?? "__unassigned";
            if (!stageMap.TryGetValue(stageId, out var agg)) { agg = new StageAgg(stageId); stageMap[stageId] = agg; }
            agg.Count += 1;
            if (d.Status == "open") agg.OpenCount += 1;
            var currency = (d.ValueCurrency ?? string.Empty).Trim().ToUpperInvariant();
            if (currency.Length > 0)
            {
                if (!agg.ByCurrency.TryGetValue(currency, out var cur)) { cur = new CurrencyAgg(); agg.ByCurrency[currency] = cur; }
                cur.Total += d.ValueAmount ?? 0m;
                cur.Count += 1;
            }
        }

        var rateCache = await BuildRateCacheAsync(exchange, scope, baseCurrencyCode, stageMap.Values.SelectMany(a => a.ByCurrency.Keys));

        var perStage = stageMap.Values.Select(agg =>
        {
            decimal totalBase = 0m;
            var convertedAll = true;
            var missing = new List<string>();
            foreach (var (currency, cur) in agg.ByCurrency)
            {
                if (baseCurrencyCode is null) continue;
                if (currency == baseCurrencyCode) { totalBase += cur.Total; continue; }
                if (rateCache.TryGetValue($"{currency}/{baseCurrencyCode}", out var rate)) totalBase += cur.Total * rate;
                else { convertedAll = false; if (!missing.Contains(currency)) missing.Add(currency); }
            }
            if (baseCurrencyCode is null)
            {
                var present = agg.ByCurrency.Keys.ToList();
                if (present.Count > 0) { convertedAll = false; missing = present; }
            }
            var byCurrency = agg.ByCurrency.OrderByDescending(kv => kv.Value.Total)
                .Select(kv => new { currency = kv.Key, total = kv.Value.Total, count = kv.Value.Count }).ToList();
            return new
            {
                stageId = agg.StageId, count = agg.Count, openCount = agg.OpenCount,
                totalInBaseCurrency = baseCurrencyCode is null ? 0m : Math.Round(totalBase),
                byCurrency, convertedAll, missingRateCurrencies = missing,
            };
        }).ToList();

        return CustomersHttp.Json(new { baseCurrencyCode, perStage }, 200);
    }

    private sealed class StageAgg
    {
        public StageAgg(string stageId) => StageId = stageId;
        public string StageId { get; }
        public int Count;
        public int OpenCount;
        public Dictionary<string, CurrencyAgg> ByCurrency = new();
    }

    private sealed class CurrencyAgg { public decimal Total; public int Count; }

    private static async Task<Dictionary<string, decimal>> BuildRateCacheAsync(
        IExchangeRateService? exchange, CurrencyScope scope, string? baseCurrencyCode, IEnumerable<string> currencies)
    {
        var cache = new Dictionary<string, decimal>();
        if (exchange is null || baseCurrencyCode is null) return cache;
        string baseCode = baseCurrencyCode;
        var distinct = currencies.Where(c => c.Length > 0 && c != baseCode).Distinct().ToList();
        if (distinct.Count == 0) return cache;
        var pairs = distinct.Select(c => (From: c, To: baseCode)).ToList();
        try
        {
            var results = await exchange.GetRatesAsync(pairs, DateTimeOffset.UtcNow, scope, 60);
            foreach (var (key, res) in results)
                if (res.Rates.Count > 0 && res.Rates[0].Rate > 0) cache[key] = res.Rates[0].Rate;
        }
        catch { /* partial totals still useful */ }
        return cache;
    }

    // ---- GET /deals/summary (4 KPI cards) -----------------------------------------------------

    private static async Task<IResult> SummaryAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        if (ctx!.TenantId is not { } tenantId) return CustomersHttp.Json(new { error = "Unauthorized" }, 401);
        var orgIds = ResolveOrgFilterIds(ctx);
        if (orgIds.Count == 0) return CustomersHttp.Json(new { error = "Unauthorized" }, 401);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var deals = await db.Set<CustomerDeal>().AsNoTracking()
            .Where(d => d.TenantId == tenantId && orgIds.Contains(d.OrganizationId) && d.DeletedAt == null).ToListAsync();

        var now = DateTime.UtcNow;
        var (qStart, qEnd) = QuarterWindow(now);
        var (pqStart, pqEnd) = (QuarterWindow(qStart.AddDays(-1)).Start, qStart);
        var openStatuses = new[] { "open", "in_progress" };

        var exchange = http.RequestServices.GetService<IExchangeRateService>();
        var scope = new CurrencyScope(tenantId, orgIds[0]);
        var baseCurrencyCode = exchange is not null ? await exchange.GetBaseCurrencyCodeAsync(scope) : null;

        var openDeals = deals.Where(d => openStatuses.Contains(d.Status)).ToList();
        bool IsWon(CustomerDeal d) => d.Status == "win" || d.ClosureOutcome == "won";
        bool IsLost(CustomerDeal d) => d.Status == "loose" || d.ClosureOutcome == "lost";

        var allCurrencies = deals.Select(d => (d.ValueCurrency ?? string.Empty).Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct().ToList();
        var rateCache = await BuildRateCacheAsync(exchange, scope, baseCurrencyCode, allCurrencies);
        var missing = new HashSet<string>();
        var convertedAll = true;
        decimal Convert(IEnumerable<CustomerDeal> src)
        {
            decimal total = 0m;
            var byCur = src.GroupBy(d => (d.ValueCurrency ?? string.Empty).Trim().ToUpperInvariant())
                .Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.Sum(d => d.ValueAmount ?? 0m));
            if (baseCurrencyCode is null)
            {
                convertedAll = false;
                foreach (var c in byCur.Keys) missing.Add(c);
                decimal best = 0m;
                foreach (var v in byCur.Values) if (Math.Abs(v) > Math.Abs(best)) best = v;
                return Math.Round(best);
            }
            foreach (var (cur, sum) in byCur)
            {
                if (cur == baseCurrencyCode) { total += sum; continue; }
                if (rateCache.TryGetValue($"{cur}/{baseCurrencyCode}", out var rate)) total += sum * rate;
                else { convertedAll = false; missing.Add(cur); }
            }
            return Math.Round(total);
        }

        // Pipeline value (open) + per-stage breakdown.
        var pipelineValue = Convert(openDeals);
        var stages = openDeals.GroupBy(d => d.PipelineStage).Select(g => new
        {
            stage = g.Key, count = g.Count(), value = Convert(g),
        }).ToList();
        var inflowCurrent = openDeals.Where(d => d.CreatedAt >= qStart && d.CreatedAt < qEnd).ToList();
        var inflowPrevious = openDeals.Where(d => d.CreatedAt >= pqStart && d.CreatedAt < pqEnd).ToList();
        var pipelineDelta = ComputeDelta(Convert(inflowCurrent), Convert(inflowPrevious));

        // Active deals.
        var ownerCounts = openDeals.Where(d => d.OwnerUserId != null).GroupBy(d => d.OwnerUserId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        var activeDelta = ComputeDelta(inflowCurrent.Count, inflowPrevious.Count);
        var sortedOwners = ownerCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.ToString()).ToList();
        var topOwners = sortedOwners.Take(5).Select(kv => new { id = kv.Key.ToString(), count = kv.Value }).ToList();
        var needAttention = deals.Count(d => d.Status == "open" && d.ExpectedCloseAt != null && d.ExpectedCloseAt < now.Date);

        // Won this quarter.
        var wonCurrentDeals = deals.Where(d => IsWon(d) && d.UpdatedAt >= qStart && d.UpdatedAt < qEnd).ToList();
        var wonPreviousDeals = deals.Where(d => IsWon(d) && d.UpdatedAt >= pqStart && d.UpdatedAt < pqEnd).ToList();
        var wonCurrent = Convert(wonCurrentDeals);
        var wonDelta = ComputeDelta(wonCurrent, Convert(wonPreviousDeals));
        var dealsClosed = wonCurrentDeals.Count;
        var avgDeal = dealsClosed > 0 ? Math.Round(wonCurrent / dealsClosed) : 0m;

        // Win rate.
        int WinRate(int won, int lost) => (won + lost) <= 0 ? 0 : (int)Math.Round(100.0 * won / (won + lost));
        var currentWon = deals.Count(d => IsWon(d) && d.UpdatedAt >= qStart && d.UpdatedAt < qEnd);
        var currentLost = deals.Count(d => IsLost(d) && d.UpdatedAt >= qStart && d.UpdatedAt < qEnd);
        var previousWon = deals.Count(d => IsWon(d) && d.UpdatedAt >= pqStart && d.UpdatedAt < pqEnd);
        var previousLost = deals.Count(d => IsLost(d) && d.UpdatedAt >= pqStart && d.UpdatedAt < pqEnd);
        var winRateValue = WinRate(currentWon, currentLost);
        var winRatePrevious = WinRate(previousWon, previousLost);
        var deltaPp = winRateValue - winRatePrevious;
        var winDir = deltaPp > 0 ? "up" : deltaPp < 0 ? "down" : "unchanged";

        var series = TrailingMonths(now, 6).Select(m =>
        {
            var won = deals.Count(d => IsWon(d) && d.UpdatedAt >= m.Start && d.UpdatedAt < m.Start.AddMonths(1));
            var lost = deals.Count(d => IsLost(d) && d.UpdatedAt >= m.Start && d.UpdatedAt < m.Start.AddMonths(1));
            var denom = won + lost;
            return new { period = m.Label, rate = denom > 0 ? (double)won / denom : 0d };
        }).ToList();

        return CustomersHttp.Json(new
        {
            baseCurrencyCode,
            convertedAll,
            missingRateCurrencies = missing.ToList(),
            pipelineValue = new { value = pipelineValue, delta = pipelineDelta, stages },
            activeDeals = new
            {
                value = openDeals.Count, delta = activeDelta, ownersCount = ownerCounts.Count,
                needAttention, owners = topOwners, ownersOverflow = Math.Max(0, ownerCounts.Count - topOwners.Count),
            },
            wonThisQuarter = new { value = wonCurrent, delta = wonDelta, dealsClosed, avgDeal },
            winRate = new { value = winRateValue, deltaPp, direction = winDir, previousValue = winRatePrevious, series },
        }, 200);
    }

    private static object ComputeDelta(decimal current, decimal previous)
    {
        if (previous == 0m) return new { value = 0, direction = "unchanged" };
        var change = (double)((current - previous) / Math.Abs(previous)) * 100.0;
        var value = (int)Math.Round(change);
        return new { value, direction = value > 0 ? "up" : value < 0 ? "down" : "unchanged" };
    }

    private static (DateTime Start, DateTime End) QuarterWindow(DateTime now)
    {
        var qi = (now.Month - 1) / 3;
        var start = new DateTime(now.Year, qi * 3 + 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return (start, start.AddMonths(3));
    }

    private static List<(DateTime Start, string Label)> TrailingMonths(DateTime now, int count)
    {
        var buckets = new List<(DateTime, string)>();
        var baseStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var offset = count - 1; offset >= 0; offset--)
        {
            var start = baseStart.AddMonths(-offset);
            buckets.Add((start, $"{start.Year}-{start.Month:D2}"));
        }
        return buckets;
    }

    // ---- POST /deals/bulk-update-owner | -stage -----------------------------------------------

    private static async Task<IResult> BulkUpdateAsync(HttpContext http, bool stage)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null)
            return CustomersHttp.Json(new { ok = false, progressJobId = (string?)null, message = "Unauthorized" }, 401);

        var body = await CustomersHttp.ReadBodyAsync(http);
        var ids = DealWriteHelpers.ReadGuidArray(body, "ids");
        if (ids is null || ids.Count == 0 || ids.Count > 10000)
            return CustomersHttp.Json(new { ok = false, progressJobId = (string?)null, message = "Invalid payload" }, 400);
        Guid? ownerUserId = null;
        Guid? pipelineStageId = null;
        if (stage)
        {
            pipelineStageId = CustomersHttp.GuidOf(body, "pipelineStageId");
            if (pipelineStageId is null)
                return CustomersHttp.Json(new { ok = false, progressJobId = (string?)null, message = "Invalid payload" }, 400);
        }
        else
        {
            // ownerUserId is nullable (null clears ownership); it must be PRESENT.
            if (!CustomersHttp.Has(body, "ownerUserId"))
                return CustomersHttp.Json(new { ok = false, progressJobId = (string?)null, message = "Invalid payload" }, 400);
            ownerUserId = CustomersHttp.GuidOf(body, "ownerUserId");
        }

        var deduped = ids.Distinct().ToList();
        var bus = http.RequestServices.GetRequiredService<CommandBus>();
        // PARITY-TODO: upstream enqueues a progress job + worker; here the per-id updates run
        // synchronously through the command bus and the progress-job id is a synthetic no-op.
        foreach (var dealId in deduped)
        {
            var updateBody = stage
                ? JsonSerializer.SerializeToElement(new { id = dealId, pipelineStageId })
                : JsonSerializer.SerializeToElement(new { id = dealId, ownerUserId });
            try { await bus.ExecuteWithLog<DealUpdateInput, DealResult>("customers.deals.update", new DealUpdateInput(dealId, updateBody), ctx!); }
            catch (CommandHttpException) { /* best-effort per-id (mirrors worker) */ }
        }

        var progressJobId = Guid.NewGuid().ToString();
        var message = stage
            ? $"Bulk stage update started ({deduped.Count} deals)."
            : $"Bulk owner update started ({deduped.Count} deals).";
        return CustomersHttp.Json(new { ok = true, progressJobId, message }, 202);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static List<Guid> ResolveOrgFilterIds(CommandContext ctx)
    {
        if (ctx.OrganizationIds is { Count: > 0 } ids) return ids.ToList();
        if (ctx.OrganizationId is { } org && org != Guid.Empty) return new List<Guid> { org };
        return new List<Guid>();
    }

    private static List<string> ReadArrayParam(IQueryCollection q, string key)
    {
        var result = new List<string>();
        foreach (var raw in q[key])
            if (raw is not null)
                foreach (var token in raw.Split(','))
                    if (!string.IsNullOrWhiteSpace(token)) result.Add(token.Trim());
        return result;
    }

    private static DateTime? ParseDate(string? raw) =>
        DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
}
