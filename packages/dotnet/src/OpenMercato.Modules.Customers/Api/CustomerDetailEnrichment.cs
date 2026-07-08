using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Best-effort enrichment of the people/company detail projections with their related timeline
/// collections — the completion of the Phase-1 PARITY-TODO now that the Phase-3 entities exist.
/// Mirrors upstream <c>api/people/[id]/route.ts</c> + <c>api/companies/[id]/route.ts</c>: each of
/// <c>comments/activities/interactions/deals/todos</c> is gated behind an <c>?include=</c> token
/// (absent ⇒ empty array), while the <c>counts</c> block is always computed. Author name/email
/// hydration, private-email visibility filtering, the canonical/legacy interaction merge and the
/// legacy todo detail resolution remain deferred (see ADR) — the response SHAPE is preserved.
/// </summary>
internal static class CustomerDetailEnrichment
{
    public sealed record Result(
        List<object> Comments,
        List<object> Activities,
        List<object> Interactions,
        List<object> Deals,
        List<object> Todos,
        int CommentsCount,
        int ActivitiesCount,
        int InteractionsCount,
        int TodosCount,
        int DealsCount);

    /// <summary>Parse the repeatable, comma-separated <c>include</c> query param into a token set.</summary>
    public static HashSet<string> ParseIncludeTokens(HttpContext http)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in http.Request.Query["include"])
        {
            if (string.IsNullOrEmpty(raw)) continue;
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                tokens.Add(part.ToLowerInvariant());
        }
        return tokens;
    }

    public static async Task<Result> LoadAsync(AppDbContext db, CustomerEntity entity, bool isCompany, HashSet<string> tokens)
    {
        var id = entity.Id;
        var includeComments = tokens.Contains("comments") || tokens.Contains("notes");
        var includeActivities = tokens.Contains("activities");
        var includeInteractions = tokens.Contains("interactions");
        var includeDeals = tokens.Contains("deals");
        var includeTodos = tokens.Contains("todos") || tokens.Contains("tasks");

        // ---- comments ----------------------------------------------------------------------------
        var comments = new List<object>();
        if (includeComments)
        {
            var rows = await db.Set<CustomerComment>().AsNoTracking()
                .Where(c => c.EntityId == id && c.DeletedAt == null)
                .OrderByDescending(c => c.CreatedAt).Take(50).ToListAsync();
            comments = rows.Select(c => (object)new
            {
                id = c.Id.ToString(),
                body = c.Body,
                authorUserId = c.AuthorUserId?.ToString(),
                authorName = (string?)null,
                authorEmail = (string?)null,
                dealId = c.DealId?.ToString(),
                createdAt = CustomersHttp.Iso(c.CreatedAt),
                appearanceIcon = c.AppearanceIcon,
                appearanceColor = c.AppearanceColor,
            }).ToList();
        }

        // ---- activities (legacy table; non-unified default) --------------------------------------
        var activities = new List<object>();
        if (includeActivities)
        {
            var rows = await db.Set<CustomerActivity>().AsNoTracking()
                .Where(a => a.EntityId == id)
                .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.CreatedAt).Take(50).ToListAsync();
            activities = rows.Select(a => (object)new
            {
                id = a.Id.ToString(),
                activityType = a.ActivityType,
                subject = a.Subject,
                body = a.Body,
                occurredAt = CustomersHttp.Iso(a.OccurredAt),
                dealId = a.DealId?.ToString(),
                authorUserId = a.AuthorUserId?.ToString(),
                authorName = (string?)null,
                authorEmail = (string?)null,
                createdAt = CustomersHttp.Iso(a.CreatedAt),
                appearanceIcon = a.AppearanceIcon,
                appearanceColor = a.AppearanceColor,
            }).ToList();
        }

        // ---- interactions ------------------------------------------------------------------------
        var interactions = new List<object>();
        if (includeInteractions)
        {
            var rows = await db.Set<CustomerInteraction>().AsNoTracking()
                .Where(i => i.EntityId == id && i.DeletedAt == null)
                .OrderByDescending(i => i.CreatedAt).Take(50).ToListAsync();
            interactions = rows.Select(i => (object)new
            {
                id = i.Id.ToString(),
                entityId = i.EntityId.ToString(),
                interactionType = i.InteractionType,
                title = i.Title,
                body = i.Body,
                status = i.Status,
                scheduledAt = CustomersHttp.Iso(i.ScheduledAt),
                occurredAt = CustomersHttp.Iso(i.OccurredAt),
                priority = i.Priority,
                authorUserId = i.AuthorUserId?.ToString(),
                ownerUserId = i.OwnerUserId?.ToString(),
                dealId = i.DealId?.ToString(),
                organizationId = i.OrganizationId.ToString(),
                tenantId = i.TenantId.ToString(),
                authorName = (string?)null,
                authorEmail = (string?)null,
                dealTitle = (string?)null,
                customValues = (object?)null,
                appearanceIcon = i.AppearanceIcon,
                appearanceColor = i.AppearanceColor,
                source = i.Source,
                createdAt = CustomersHttp.Iso(i.CreatedAt),
                updatedAt = CustomersHttp.Iso(i.UpdatedAt),
            }).ToList();
        }

        // ---- deals (via link table; person vs company) -------------------------------------------
        var deals = new List<object>();
        List<Guid> dealIds;
        if (isCompany)
            dealIds = await db.Set<CustomerDealCompanyLink>().AsNoTracking().Where(l => l.CompanyEntityId == id).Select(l => l.DealId).ToListAsync();
        else
            dealIds = await db.Set<CustomerDealPersonLink>().AsNoTracking().Where(l => l.PersonEntityId == id).Select(l => l.DealId).ToListAsync();
        var dealsCount = dealIds.Count;
        if (includeDeals && dealIds.Count > 0)
        {
            var rows = await db.Set<CustomerDeal>().AsNoTracking()
                .Where(d => dealIds.Contains(d.Id) && d.DeletedAt == null)
                .OrderByDescending(d => d.CreatedAt).ToListAsync();
            deals = rows.Select(d => (object)new
            {
                id = d.Id.ToString(),
                title = d.Title,
                status = d.Status,
                pipelineStage = d.PipelineStage,
                pipelineId = d.PipelineId?.ToString(),
                pipelineStageId = d.PipelineStageId?.ToString(),
                valueAmount = d.ValueAmount,
                valueCurrency = d.ValueCurrency,
                probability = d.Probability,
                expectedCloseAt = CustomersHttp.Iso(d.ExpectedCloseAt),
                ownerUserId = d.OwnerUserId?.ToString(),
                source = d.Source,
                closureOutcome = d.ClosureOutcome,
                lossReasonId = d.LossReasonId?.ToString(),
                lossNotes = d.LossNotes,
                createdAt = CustomersHttp.Iso(d.CreatedAt),
                updatedAt = CustomersHttp.Iso(d.UpdatedAt),
            }).ToList();
        }

        // ---- todos (legacy bridge links; detail resolution deferred) -----------------------------
        var todos = new List<object>();
        if (includeTodos)
        {
            var rows = await db.Set<CustomerTodoLink>().AsNoTracking()
                .Where(l => l.EntityId == id)
                .OrderByDescending(l => l.CreatedAt).Take(50).ToListAsync();
            todos = rows.Select(l => (object)new
            {
                id = l.Id.ToString(),
                todoId = l.TodoId.ToString(),
                todoSource = string.IsNullOrWhiteSpace(l.TodoSource) ? "example:todo" : l.TodoSource,
                createdAt = CustomersHttp.Iso(l.CreatedAt),
                createdByUserId = l.CreatedByUserId?.ToString(),
                title = (string?)null,
                isDone = (bool?)null,
                priority = (int?)null,
                severity = (string?)null,
                description = (string?)null,
                dueAt = (string?)null,
                todoOrganizationId = (string?)null,
                customValues = (object?)null,
            }).ToList();
        }

        // ---- counts (always computed) ------------------------------------------------------------
        var commentsCount = includeComments
            ? comments.Count
            : await db.Set<CustomerComment>().AsNoTracking().CountAsync(c => c.EntityId == id && c.DeletedAt == null);
        var activitiesCount = await db.Set<CustomerInteraction>().AsNoTracking()
            .CountAsync(i => i.EntityId == id && i.DeletedAt == null && i.InteractionType != "task");
        var interactionsCount = await db.Set<CustomerInteraction>().AsNoTracking()
            .CountAsync(i => i.EntityId == id && i.DeletedAt == null);
        var todosCount = await db.Set<CustomerTodoLink>().AsNoTracking().CountAsync(l => l.EntityId == id);

        return new Result(comments, activities, interactions, deals, todos,
            commentsCount, activitiesCount, interactionsCount, todosCount, dealsCount);
    }
}
