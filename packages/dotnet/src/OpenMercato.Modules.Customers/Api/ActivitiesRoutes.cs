using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Commands;
using OpenMercato.Modules.Customers.Data;
using OpenMercato.Modules.Customers.Lib;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Activities routes — DEPRECATED compatibility bridge (sunset 2026-06-30), port of upstream
/// <c>api/activities/*</c>. All responses carry <c>Deprecation</c>/<c>Sunset</c>/<c>Link</c> headers; when
/// the <c>legacy-adapters</c> flag is off the whole surface returns 410 (flag defaults ON, so 410 is
/// dormant here — feature_toggles is unported). Writes delegate to the canonical
/// <c>customers.interactions.*</c> commands (interactionType=activityType, source <c>adapter:activity</c>,
/// create status = occurredAt ? 'done' : 'planned'). GET is a paged merge of legacy + bridged rows.
/// </summary>
public sealed class ActivitiesRoutes : ICustomersRouteGroup
{
    private static readonly string[] View = { "customers.activities.view" };
    private static readonly string[] Manage = { "customers.activities.manage" };

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/customers/activities", (Func<HttpContext, Task<IResult>>)ListAsync);
        routes.MapPost("/api/customers/activities", (Func<HttpContext, Task<IResult>>)CreateAsync);
        routes.MapPut("/api/customers/activities", (Func<HttpContext, Task<IResult>>)UpdateAsync);
        routes.MapDelete("/api/customers/activities", (Func<HttpContext, Task<IResult>>)DeleteAsync);
    }

    internal static void SetDeprecationHeaders(HttpContext http)
    {
        http.Response.Headers["Deprecation"] = "true";
        http.Response.Headers["Sunset"] = "Tue, 30 Jun 2026 00:00:00 GMT";
        http.Response.Headers["Link"] = "</api/customers/interactions>; rel=\"successor-version\"";
    }

    private static IResult Deprecated(HttpContext http, object body, int status)
    {
        SetDeprecationHeaders(http);
        return CustomersHttp.Json(body, status);
    }

    private static async Task<IResult> ListAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) { SetDeprecationHeaders(http); return denied; }
        var flags = InteractionCompat.ResolveFlags(http.RequestServices, ctx!.TenantId);
        if (!flags.LegacyAdapters) return Deprecated(http, new { error = "This legacy adapter has been disabled. Use /api/customers/interactions instead." }, 410);

        var qp = http.Request.Query;
        var page = int.TryParse(qp["page"], out var pg) && pg >= 1 ? pg : 1;
        var pageSize = int.TryParse(qp["pageSize"], out var ps) ? Math.Clamp(ps, 1, 100) : 50;
        Guid? entityId = Guid.TryParse(qp["entityId"], out var e) ? e : null;
        Guid? dealId = Guid.TryParse(qp["dealId"], out var d) ? d : null;
        var activityType = qp["activityType"].ToString();

        var db = http.RequestServices.GetRequiredService<AppDbContext>();

        // Canonical interactions mapped to activity summaries.
        var canonicalQ = db.Set<CustomerInteraction>().AsNoTracking().Where(i => i.TenantId == ctx.TenantId && i.DeletedAt == null);
        if (ctx.OrganizationIds is { Count: > 0 } orgIds) canonicalQ = canonicalQ.Where(i => orgIds.Contains(i.OrganizationId));
        if (entityId is { } eid) canonicalQ = canonicalQ.Where(i => i.EntityId == eid);
        if (dealId is { } did) canonicalQ = canonicalQ.Where(i => i.DealId == did);
        if (!string.IsNullOrEmpty(activityType)) canonicalQ = canonicalQ.Where(i => i.InteractionType == activityType);
        var canonical = await canonicalQ.ToListAsync();
        var bridgedActivityIds = canonical.Where(i => i.Source == InteractionCompat.ActivityAdapterSource).Select(i => i.Id).ToHashSet();

        // Legacy customer_activities not superseded by a bridged canonical row.
        var legacyQ = db.Set<CustomerActivity>().AsNoTracking().Where(a => a.TenantId == ctx.TenantId);
        if (ctx.OrganizationIds is { Count: > 0 } orgIds2) legacyQ = legacyQ.Where(a => orgIds2.Contains(a.OrganizationId));
        if (entityId is { } eid2) legacyQ = legacyQ.Where(a => a.EntityId == eid2);
        if (dealId is { } did2) legacyQ = legacyQ.Where(a => a.DealId == did2);
        if (!string.IsNullOrEmpty(activityType)) legacyQ = legacyQ.Where(a => a.ActivityType == activityType);
        var legacy = await legacyQ.ToListAsync();

        var enc = http.RequestServices.GetService<OpenMercato.Modules.Auth.Security.TenantDataEncryptionService>();
        var authors = await CustomerUserDirectory.ResolveAsync(db, enc,
            CustomerUserDirectory.Ids(canonical.Select(i => i.AuthorUserId), legacy.Select(a => a.AuthorUserId)));

        var rows = new List<(string? CreatedAt, object Item)>();
        rows.AddRange(canonical.Select(i => (CustomersHttp.Iso(i.CreatedAt), MapCanonical(i, authors))));
        rows.AddRange(legacy.Where(a => !bridgedActivityIds.Contains(a.Id)).Select(a => (CustomersHttp.Iso(a.CreatedAt), MapLegacy(a, authors))));

        var ordered = rows.OrderByDescending(r => r.CreatedAt, StringComparer.Ordinal).Select(r => r.Item).ToList();
        var total = ordered.Count;
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Deprecated(http, new { items, total, page, pageSize, totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)) }, 200);
        // PARITY-TODO: author name/email + custom-value hydration (interactionReadModel) deferred.
    }

    private static object MapCanonical(CustomerInteraction i, IReadOnlyDictionary<Guid, CustomerUserDirectory.UserIdentity> authors) => new
    {
        id = i.Id.ToString(),
        activityType = i.InteractionType,
        subject = i.Title,
        body = i.Body,
        occurredAt = CustomersHttp.Iso(i.OccurredAt ?? i.ScheduledAt),
        createdAt = CustomersHttp.Iso(i.CreatedAt),
        appearanceIcon = i.AppearanceIcon,
        appearanceColor = i.AppearanceColor,
        entityId = i.EntityId.ToString(),
        authorUserId = i.AuthorUserId?.ToString(),
        authorName = i.AuthorUserId is { } au && authors.TryGetValue(au, out var ai) ? ai.Name : null,
        authorEmail = i.AuthorUserId is { } au2 && authors.TryGetValue(au2, out var ai2) ? ai2.Email : null,
        dealId = i.DealId?.ToString(),
        dealTitle = (string?)null,
        customValues = (object?)null,
    };

    private static object MapLegacy(CustomerActivity a, IReadOnlyDictionary<Guid, CustomerUserDirectory.UserIdentity> authors) => new
    {
        id = a.Id.ToString(),
        activityType = a.ActivityType,
        subject = a.Subject,
        body = a.Body,
        occurredAt = CustomersHttp.Iso(a.OccurredAt),
        createdAt = CustomersHttp.Iso(a.CreatedAt),
        appearanceIcon = a.AppearanceIcon,
        appearanceColor = a.AppearanceColor,
        entityId = a.EntityId.ToString(),
        authorUserId = a.AuthorUserId?.ToString(),
        authorName = a.AuthorUserId is { } au && authors.TryGetValue(au, out var ai) ? ai.Name : null,
        authorEmail = a.AuthorUserId is { } au2 && authors.TryGetValue(au2, out var ai2) ? ai2.Email : null,
        dealId = a.DealId?.ToString(),
        dealTitle = (string?)null,
        customValues = (object?)null,
    };

    private static async Task<IResult> CreateAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) { SetDeprecationHeaders(http); return denied; }
        var flags = InteractionCompat.ResolveFlags(http.RequestServices, ctx!.TenantId);
        if (!flags.LegacyAdapters) return Deprecated(http, new { error = "This legacy adapter has been disabled. Use /api/customers/interactions instead." }, 410);

        var body = await CustomersHttp.ReadBodyAsync(http);
        if (CustomersHttp.GuidOf(body, "entityId") is null || string.IsNullOrEmpty(CustomersHttp.Str(body, "activityType")?.Trim()))
            return Deprecated(http, new { error = "Validation failed" }, 400);

        var occurredAt = CustomersHttp.Date(body, "occurredAt");
        var payload = JsonSerializer.Serialize(new
        {
            entityId = CustomersHttp.Str(body, "entityId"),
            interactionType = CustomersHttp.Str(body, "activityType"),
            title = CustomersHttp.Str(body, "subject"),
            body = CustomersHttp.Str(body, "body"),
            date = CustomersHttp.Str(body, "date"),
            time = CustomersHttp.Str(body, "time"),
            phoneNumber = CustomersHttp.Str(body, "phoneNumber"),
            occurredAt = CustomersHttp.Iso(occurredAt),
            dealId = CustomersHttp.Str(body, "dealId"),
            authorUserId = CustomersHttp.Str(body, "authorUserId"),
            appearanceIcon = CustomersHttp.Str(body, "appearanceIcon"),
            appearanceColor = CustomersHttp.Str(body, "appearanceColor"),
            source = InteractionCompat.ActivityAdapterSource,
            status = occurredAt is not null ? "done" : "planned",
        }, CustomersHttp.Web);
        var input = JsonDocument.Parse(payload).RootElement.Clone();
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<InteractionCreateInput, InteractionResult>(
                "customers.interactions.create", new InteractionCreateInput(ctx.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, input), ctx);
            await InteractionCompat.EmitInteractionSideEffectsAsync(http.RequestServices, r.Result, "created");
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return Deprecated(http, new { id = r.Result.InteractionId }, 201);
        }
        catch (CommandHttpException ex) { return Deprecated(http, ex.Body, ex.Status); }
    }

    private static async Task<IResult> UpdateAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) { SetDeprecationHeaders(http); return denied; }
        var flags = InteractionCompat.ResolveFlags(http.RequestServices, ctx!.TenantId);
        if (!flags.LegacyAdapters) return Deprecated(http, new { error = "This legacy adapter has been disabled. Use /api/customers/interactions instead." }, 410);

        var body = await CustomersHttp.ReadBodyAsync(http);
        var id = CustomersHttp.GuidOf(body, "id") ?? Guid.Empty;
        // Map activity fields → interaction fields on the update body.
        var patch = JsonSerializer.Serialize(new
        {
            id = id.ToString(),
            interactionType = CustomersHttp.Str(body, "activityType"),
            title = CustomersHttp.Str(body, "subject"),
            body = CustomersHttp.Has(body, "body") ? CustomersHttp.Str(body, "body") : null,
            occurredAt = CustomersHttp.Has(body, "occurredAt") ? CustomersHttp.Iso(CustomersHttp.Date(body, "occurredAt")) : null,
            appearanceIcon = CustomersHttp.Str(body, "appearanceIcon"),
            appearanceColor = CustomersHttp.Str(body, "appearanceColor"),
        }, CustomersHttp.Web);
        var input = JsonDocument.Parse(patch).RootElement.Clone();
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<InteractionUpdateInput, InteractionResult>(
                "customers.interactions.update", new InteractionUpdateInput(id, input), ctx);
            await InteractionCompat.EmitInteractionSideEffectsAsync(http.RequestServices, r.Result, "updated");
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return Deprecated(http, new { ok = true }, 200);
        }
        catch (CommandHttpException ex) { return Deprecated(http, ex.Body, ex.Status); }
    }

    private static async Task<IResult> DeleteAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) { SetDeprecationHeaders(http); return denied; }
        var flags = InteractionCompat.ResolveFlags(http.RequestServices, ctx!.TenantId);
        if (!flags.LegacyAdapters) return Deprecated(http, new { error = "This legacy adapter has been disabled. Use /api/customers/interactions instead." }, 410);

        var body = await CustomersHttp.ReadBodyAsync(http);
        var id = CustomersHttp.GuidOf(body, "id");
        if (id is null) return Deprecated(http, new { error = "Validation failed" }, 400);
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<InteractionDeleteInput, InteractionResult>(
                "customers.interactions.delete", new InteractionDeleteInput(id.Value), ctx);
            await InteractionCompat.EmitInteractionSideEffectsAsync(http.RequestServices, r.Result, "deleted");
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return Deprecated(http, new { ok = true }, 200);
        }
        catch (CommandHttpException ex) { return Deprecated(http, ex.Body, ex.Status); }
    }
}
