using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Customers.Data;
using OpenMercato.Modules.Customers.Lib;

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

    private static string? NameOf(IReadOnlyDictionary<Guid, CustomerUserDirectory.UserIdentity> m, Guid? id) =>
        id is { } g && m.TryGetValue(g, out var u) ? u.Name : null;
    private static string? EmailOf(IReadOnlyDictionary<Guid, CustomerUserDirectory.UserIdentity> m, Guid? id) =>
        id is { } g && m.TryGetValue(g, out var u) ? u.Email : null;

    public static async Task<Result> LoadAsync(
        AppDbContext db, TenantDataEncryptionService encryption, CustomerEntity entity, bool isCompany, HashSet<string> tokens)
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
            var au = await CustomerUserDirectory.ResolveAsync(db, encryption, CustomerUserDirectory.Ids(rows.Select(c => c.AuthorUserId)));
            comments = rows.Select(c => (object)new
            {
                id = c.Id.ToString(),
                body = c.Body,
                authorUserId = c.AuthorUserId?.ToString(),
                authorName = NameOf(au, c.AuthorUserId),
                authorEmail = EmailOf(au, c.AuthorUserId),
                dealId = c.DealId?.ToString(),
                createdAt = CustomersHttp.Iso(c.CreatedAt),
                appearanceIcon = c.AppearanceIcon,
                appearanceColor = c.AppearanceColor,
            }).ToList();
        }

        // ---- activities (legacy table + canonical adapter:activity interactions) -----------------
        // Upstream (legacy mode default): activities = legacy customer_activities (deduped vs bridged) +
        // canonical NON-task interactions whose source is adapter:activity, mapped to activity summaries
        // (id = interaction id). (OM integration test TC-CRM-025.)
        var activities = new List<object>();
        if (includeActivities)
        {
            var canonicalActs = await db.Set<CustomerInteraction>().AsNoTracking()
                .Where(i => i.EntityId == id && i.DeletedAt == null && i.InteractionType != InteractionCompat.TaskType
                            && i.Source == InteractionCompat.ActivityAdapterSource)
                .ToListAsync();
            var bridgedActIds = canonicalActs.Select(i => i.Id).ToHashSet();

            var legacyActs = await db.Set<CustomerActivity>().AsNoTracking()
                .Where(a => a.EntityId == id).ToListAsync();

            var actAuthors = await CustomerUserDirectory.ResolveAsync(db, encryption,
                CustomerUserDirectory.Ids(legacyActs.Select(a => a.AuthorUserId), canonicalActs.Select(i => i.AuthorUserId)));

            var actRows = new List<(DateTimeOffset Sort, string Id, object Item)>();
            foreach (var a in legacyActs.Where(a => !bridgedActIds.Contains(a.Id)))
                actRows.Add((a.OccurredAt ?? a.CreatedAt, a.Id.ToString(), new
                {
                    id = a.Id.ToString(),
                    activityType = a.ActivityType,
                    subject = a.Subject,
                    body = a.Body,
                    occurredAt = CustomersHttp.Iso(a.OccurredAt),
                    dealId = a.DealId?.ToString(),
                    authorUserId = a.AuthorUserId?.ToString(),
                    authorName = NameOf(actAuthors, a.AuthorUserId),
                    authorEmail = EmailOf(actAuthors, a.AuthorUserId),
                    createdAt = CustomersHttp.Iso(a.CreatedAt),
                    appearanceIcon = a.AppearanceIcon,
                    appearanceColor = a.AppearanceColor,
                }));
            foreach (var i in canonicalActs)
                actRows.Add((i.OccurredAt ?? i.ScheduledAt ?? i.CreatedAt, i.Id.ToString(), new
                {
                    id = i.Id.ToString(),
                    activityType = i.InteractionType,
                    subject = i.Title,
                    body = i.Body,
                    occurredAt = CustomersHttp.Iso(i.OccurredAt ?? i.ScheduledAt),
                    dealId = i.DealId?.ToString(),
                    authorUserId = i.AuthorUserId?.ToString(),
                    authorName = NameOf(actAuthors, i.AuthorUserId),
                    authorEmail = EmailOf(actAuthors, i.AuthorUserId),
                    createdAt = CustomersHttp.Iso(i.CreatedAt),
                    appearanceIcon = i.AppearanceIcon,
                    appearanceColor = i.AppearanceColor,
                }));
            activities = actRows
                .OrderByDescending(r => r.Sort).ThenByDescending(r => r.Id, StringComparer.Ordinal)
                .Take(50).Select(r => r.Item).ToList();
        }

        // ---- interactions ------------------------------------------------------------------------
        var interactions = new List<object>();
        if (includeInteractions)
        {
            var rows = await db.Set<CustomerInteraction>().AsNoTracking()
                .Where(i => i.EntityId == id && i.DeletedAt == null)
                .OrderByDescending(i => i.CreatedAt).Take(50).ToListAsync();
            var ixAuthors = await CustomerUserDirectory.ResolveAsync(db, encryption, CustomerUserDirectory.Ids(rows.Select(i => i.AuthorUserId)));
            var ixDealIds = rows.Where(i => i.DealId is not null).Select(i => i.DealId!.Value).Distinct().ToList();
            var ixDealTitles = ixDealIds.Count == 0
                ? new Dictionary<Guid, string>()
                : (await db.Set<CustomerDeal>().AsNoTracking().Where(d => ixDealIds.Contains(d.Id)).ToListAsync()).ToDictionary(d => d.Id, d => d.Title);
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
                authorName = NameOf(ixAuthors, i.AuthorUserId),
                authorEmail = EmailOf(ixAuthors, i.AuthorUserId),
                dealTitle = i.DealId is { } dd && ixDealTitles.TryGetValue(dd, out var dt) ? dt : null,
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

        // ---- todos (legacy links + canonical adapter:todo task interactions) ---------------------
        // Upstream (legacy interaction mode, the default): todos = legacy customer_todo_links (deduped
        // against bridged ids) + canonical task interactions whose source is adapter:todo, each mapped to
        // a todo summary (todoId = interaction id). (OM integration test TC-CRM-025.)
        var todos = new List<object>();
        if (includeTodos)
        {
            var canonicalTodos = await db.Set<CustomerInteraction>().AsNoTracking()
                .Where(i => i.EntityId == id && i.DeletedAt == null && i.InteractionType == InteractionCompat.TaskType
                            && i.Source == InteractionCompat.TodoAdapterSource)
                .ToListAsync();
            var bridgedTodoIds = canonicalTodos.Select(i => i.Id).ToHashSet();

            var links = await db.Set<CustomerTodoLink>().AsNoTracking()
                .Where(l => l.EntityId == id).ToListAsync();

            var todoRows = new List<(DateTimeOffset Created, string Id, object Item)>();
            foreach (var l in links.Where(l => !bridgedTodoIds.Contains(l.TodoId)))
                todoRows.Add((l.CreatedAt, l.Id.ToString(), new
                {
                    id = l.Id.ToString(),
                    todoId = l.TodoId.ToString(),
                    todoSource = string.IsNullOrWhiteSpace(l.TodoSource) ? InteractionCompat.ExampleTodoSource : l.TodoSource,
                    createdAt = CustomersHttp.Iso(l.CreatedAt),
                    createdByUserId = l.CreatedByUserId?.ToString(),
                    title = (string?)null,
                    isDone = (bool?)null,
                    status = (string?)null,
                    priority = (int?)null,
                    severity = (string?)null,
                    description = (string?)null,
                    dueAt = (string?)null,
                    todoOrganizationId = (string?)null,
                    customValues = (object?)null,
                }));
            foreach (var i in canonicalTodos)
                todoRows.Add((i.CreatedAt, i.Id.ToString(), new
                {
                    id = i.Id.ToString(),
                    todoId = i.Id.ToString(),
                    todoSource = InteractionCompat.TaskSource,
                    createdAt = CustomersHttp.Iso(i.CreatedAt),
                    createdByUserId = (string?)null,
                    title = i.Title,
                    isDone = (bool?)(i.Status == "done"),
                    status = i.Status,
                    priority = i.Priority,
                    severity = (string?)null,
                    description = i.Body,
                    dueAt = CustomersHttp.Iso(i.ScheduledAt),
                    todoOrganizationId = (string?)null,
                    customValues = (object?)null,
                }));
            todos = todoRows
                .OrderByDescending(r => r.Created).ThenByDescending(r => r.Id, StringComparer.Ordinal)
                .Take(50).Select(r => r.Item).ToList();
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
