using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Core.Events;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Customers.Commands;
using OpenMercato.Modules.Customers.Data;
using OpenMercato.Modules.Customers.Lib;

namespace OpenMercato.Modules.Customers.Api;

/// <summary>
/// Interactions routes — port of upstream <c>api/interactions/*</c>. GET is a hand-written CURSOR list
/// (<c>{items, nextCursor}</c> — NOT the paged envelope); writes dispatch to the
/// <c>customers.interactions.*</c> commands (create 201 <c>{id}</c>; update/delete <c>{ok:true}</c>) and
/// emit the index projection + <c>customers.interaction.*</c>/<c>next_interaction.updated</c> events.
/// Lifecycle routes: complete, cancel, [id]/visibility, conflicts, counts, tasks.
/// </summary>
public sealed class InteractionsRoutes : ICustomersRouteGroup
{
    private static readonly string[] View = { "customers.interactions.view" };
    private static readonly string[] Manage = { "customers.interactions.manage" };
    private static readonly string[] EmailCompose = { "customers.email.compose" };

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/customers/interactions", (Func<HttpContext, Task<IResult>>)ListAsync);
        routes.MapPost("/api/customers/interactions", (Func<HttpContext, Task<IResult>>)CreateAsync);
        routes.MapPut("/api/customers/interactions", (Func<HttpContext, Task<IResult>>)UpdateAsync);
        routes.MapDelete("/api/customers/interactions", (Func<HttpContext, Task<IResult>>)DeleteAsync);
        routes.MapPost("/api/customers/interactions/complete", (Func<HttpContext, Task<IResult>>)CompleteAsync);
        routes.MapPost("/api/customers/interactions/cancel", (Func<HttpContext, Task<IResult>>)CancelAsync);
        routes.MapPatch("/api/customers/interactions/{id}/visibility", (Func<HttpContext, string, Task<IResult>>)VisibilityAsync);
        routes.MapGet("/api/customers/interactions/conflicts", (Func<HttpContext, Task<IResult>>)ConflictsAsync);
        routes.MapGet("/api/customers/interactions/counts", (Func<HttpContext, Task<IResult>>)CountsAsync);
        routes.MapGet("/api/customers/interactions/tasks", (Func<HttpContext, Task<IResult>>)TasksAsync);
    }

    // ---- sort config (port of interactionSortConfig) ------------------------------------------

    private enum SortType { Date, Number, Text }
    private sealed record SortConfig(string Column, SortType Type, bool DefaultDesc);

    private static readonly IReadOnlyDictionary<string, SortConfig> Sorts = new Dictionary<string, SortConfig>(StringComparer.Ordinal)
    {
        ["scheduledAt"] = new("scheduled_at", SortType.Date, false),
        ["occurredAt"] = new("occurred_at", SortType.Date, true),
        ["createdAt"] = new("created_at", SortType.Date, true),
        ["updatedAt"] = new("updated_at", SortType.Date, true),
        ["status"] = new("status", SortType.Text, false),
        ["priority"] = new("priority", SortType.Number, true),
        ["interactionType"] = new("interaction_type", SortType.Text, false),
        ["title"] = new("title", SortType.Text, false),
    };

    // ---- GET (cursor list) --------------------------------------------------------------------

    private static async Task<IResult> ListAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;

        var qp = http.Request.Query;
        var limit = int.TryParse(qp["limit"], out var l) ? Math.Clamp(l, 1, 100) : 25;
        var sortField = qp["sortField"].ToString();
        if (string.IsNullOrEmpty(sortField)) sortField = "scheduledAt";
        if (!Sorts.TryGetValue(sortField, out var sortConfig))
            return CustomersHttp.Json(new { error = "Validation failed" }, 400);
        var sortDirRaw = qp["sortDir"].ToString();
        var desc = string.IsNullOrEmpty(sortDirRaw) ? sortConfig.DefaultDesc : sortDirRaw == "desc";

        var cursorRaw = qp["cursor"].ToString();
        Cursor? cursor = null;
        if (!string.IsNullOrEmpty(cursorRaw))
        {
            cursor = DecodeCursor(cursorRaw);
            if (cursor is null) return CustomersHttp.Json(new { error = "Invalid cursor" }, 400);
        }

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var query = db.Set<CustomerInteraction>().AsNoTracking()
            .Where(i => i.DeletedAt == null && i.TenantId == ctx!.TenantId);
        if (ctx!.OrganizationIds is { Count: > 0 } orgIds)
            query = query.Where(i => orgIds.Contains(i.OrganizationId));

        if (Guid.TryParse(qp["entityId"], out var eid)) query = query.Where(i => i.EntityId == eid);
        if (Guid.TryParse(qp["dealId"], out var did)) query = query.Where(i => i.DealId == did);
        var status = qp["status"].ToString();
        if (!string.IsNullOrEmpty(status)) query = query.Where(i => i.Status == status);
        var itype = qp["interactionType"].ToString();
        if (!string.IsNullOrEmpty(itype)) query = query.Where(i => i.InteractionType == itype);
        var typeCsv = qp["type"].ToString();
        if (!string.IsNullOrEmpty(typeCsv))
        {
            var types = typeCsv.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
            if (types.Count > 0) query = query.Where(i => types.Contains(i.InteractionType));
        }
        var excludeType = qp["excludeInteractionType"].ToString();
        if (!string.IsNullOrEmpty(excludeType)) query = query.Where(i => i.InteractionType != excludeType);
        var pinned = qp["pinned"].ToString();
        if (pinned == "true") query = query.Where(i => i.Pinned);
        else if (pinned == "false") query = query.Where(i => !i.Pinned);

        // Email visibility (v1 strict owner-only; api-key viewer=null fail-closed).
        var viewerUserId = ctx!.UserId;
        query = InteractionCompat.ApplyEmailVisibility(query, viewerUserId);

        var rows = await query.ToListAsync();

        // In-memory: search (ILIKE title/body), from/to over coalesce(occurred,scheduled,created).
        var search = qp["search"].ToString();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            rows = rows.Where(i =>
                (i.Title ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (i.Body ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (DateTimeOffset.TryParse(qp["from"].ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var from))
            rows = rows.Where(i => Coalesce(i) >= from).ToList();
        if (DateTimeOffset.TryParse(qp["to"].ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var to))
            rows = rows.Where(i => Coalesce(i) <= to).ToList();

        // Sort by (coalesced sort value, id) with the field's direction, then cursor-paginate.
        // PARITY-TODO: upstream keyset-paginates DB-side; here it is faithful in-memory (POC provider parity).
        var dirMul = desc ? -1 : 1;
        rows.Sort((a, b) =>
        {
            var c = CompareSort(a, b, sortConfig, desc);
            if (c == 0) c = string.CompareOrdinal(a.Id.ToString(), b.Id.ToString());
            return dirMul * c;
        });

        if (cursor is not null)
        {
            rows = rows.Where(r =>
            {
                var c = CompareSortToCursor(r, sortConfig, cursor, desc);
                if (c == 0) c = string.CompareOrdinal(r.Id.ToString(), cursor.Id);
                return dirMul * c > 0;
            }).ToList();
        }

        var hasMore = rows.Count > limit;
        var page = rows.Take(limit).ToList();

        // Deal titles + author identity (name/email decrypted from the auth User table).
        var dealIds = page.Where(r => r.DealId is not null).Select(r => r.DealId!.Value).Distinct().ToList();
        var dealTitles = dealIds.Count == 0
            ? new Dictionary<Guid, string>()
            : (await db.Set<CustomerDeal>().AsNoTracking().Where(d => dealIds.Contains(d.Id)).ToListAsync())
                .ToDictionary(d => d.Id, d => d.Title);

        var enc = http.RequestServices.GetService<TenantDataEncryptionService>();
        var authors = await CustomerUserDirectory.ResolveAsync(
            db, enc, CustomerUserDirectory.Ids(page.Select(r => r.AuthorUserId)));

        var items = page.Select(r => Project(r, dealTitles, authors)).ToList();
        var codec = http.RequestServices.GetRequiredService<ICrudCustomFields>();
        await codec.MergeIntoListItemsAsync(InteractionCompat.InteractionEntityType, items, ctx!);

        string? nextCursor = null;
        if (hasMore && page.Count > 0)
            nextCursor = EncodeCursor(page[^1], sortConfig, desc);

        return CustomersHttp.Json(new { items, nextCursor }, 200);
    }

    private static DateTimeOffset Coalesce(CustomerInteraction i) =>
        i.OccurredAt ?? i.ScheduledAt ?? i.CreatedAt;

    private static IDictionary<string, object?> Project(
        CustomerInteraction r, IReadOnlyDictionary<Guid, string> dealTitles,
        IReadOnlyDictionary<Guid, CustomerUserDirectory.UserIdentity> authors) => new Dictionary<string, object?>
    {
        ["id"] = r.Id.ToString(),
        ["entityId"] = r.EntityId.ToString(),
        ["dealId"] = r.DealId?.ToString(),
        ["interactionType"] = r.InteractionType,
        ["title"] = r.Title,
        ["body"] = r.Body,
        ["status"] = r.Status,
        ["scheduledAt"] = CustomersHttp.Iso(r.ScheduledAt),
        ["occurredAt"] = CustomersHttp.Iso(r.OccurredAt),
        ["priority"] = r.Priority,
        ["authorUserId"] = r.AuthorUserId?.ToString(),
        ["ownerUserId"] = r.OwnerUserId?.ToString(),
        ["appearanceIcon"] = r.AppearanceIcon,
        ["appearanceColor"] = r.AppearanceColor,
        ["source"] = r.Source,
        ["duration"] = r.DurationMinutes,
        ["durationMinutes"] = r.DurationMinutes,
        ["location"] = r.Location,
        ["allDay"] = r.AllDay,
        ["recurrenceRule"] = r.RecurrenceRule,
        ["recurrenceEnd"] = CustomersHttp.Iso(r.RecurrenceEnd),
        ["participants"] = InteractionCompat.ParseJson(r.Participants),
        ["reminderMinutes"] = r.ReminderMinutes,
        ["visibility"] = r.Visibility,
        ["linkedEntities"] = InteractionCompat.ParseJson(r.LinkedEntities),
        ["guestPermissions"] = InteractionCompat.ParseJson(r.GuestPermissions),
        ["pinned"] = r.Pinned,
        ["organizationId"] = r.OrganizationId.ToString(),
        ["tenantId"] = r.TenantId.ToString(),
        ["createdAt"] = CustomersHttp.Iso(r.CreatedAt),
        ["updatedAt"] = CustomersHttp.Iso(r.UpdatedAt),
        ["authorName"] = r.AuthorUserId is { } au && authors.TryGetValue(au, out var ai) ? ai.Name : null,
        ["authorEmail"] = r.AuthorUserId is { } au2 && authors.TryGetValue(au2, out var ai2) ? ai2.Email : null,
        ["dealTitle"] = r.DealId is { } d && dealTitles.TryGetValue(d, out var t) ? t : null,
    };

    // ---- cursor coalesce + compare + encode/decode --------------------------------------------

    private sealed record Cursor(string Id, JsonElement SortValue);

    private static Cursor? DecodeCursor(string token)
    {
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return null;
            var id = idEl.GetString();
            if (id is null || !Guid.TryParse(id, out _)) return null;
            var sv = root.TryGetProperty("sortValue", out var svEl) ? svEl.Clone() : default;
            return new Cursor(id, sv);
        }
        catch { return null; }
    }

    private static string EncodeCursor(CustomerInteraction last, SortConfig cfg, bool desc)
    {
        object? sortValue = cfg.Type switch
        {
            SortType.Date => CustomersHttp.Iso(CoalesceDate(last, cfg, desc)),
            SortType.Number => CoalesceNumber(last, cfg, desc),
            _ => CoalesceText(last, cfg, desc),
        };
        var json = JsonSerializer.Serialize(new { id = last.Id.ToString(), sortValue }, CustomersHttp.Web);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static DateTimeOffset? RawDate(CustomerInteraction i, string col) => col switch
    {
        "scheduled_at" => i.ScheduledAt,
        "occurred_at" => i.OccurredAt,
        "updated_at" => i.UpdatedAt,
        _ => i.CreatedAt,
    };

    // Coalesce sentinels mirror the upstream sortSql: asc → high sentinel, desc → low sentinel (nulls last).
    private static DateTimeOffset CoalesceDate(CustomerInteraction i, SortConfig cfg, bool desc)
        => RawDate(i, cfg.Column) ?? (desc ? DateTimeOffset.MinValue : DateTimeOffset.MaxValue);

    private static long CoalesceNumber(CustomerInteraction i, SortConfig cfg, bool desc)
        => i.Priority ?? (desc ? int.MinValue : int.MaxValue);

    private static string CoalesceText(CustomerInteraction i, SortConfig cfg, bool desc)
    {
        var v = cfg.Column switch
        {
            "status" => i.Status,
            "interaction_type" => i.InteractionType,
            "title" => i.Title,
            _ => null,
        };
        return v ?? (desc ? "" : "~~~~~~~~~~");
    }

    private static int CompareSort(CustomerInteraction a, CustomerInteraction b, SortConfig cfg, bool desc) => cfg.Type switch
    {
        SortType.Date => CoalesceDate(a, cfg, desc).CompareTo(CoalesceDate(b, cfg, desc)),
        SortType.Number => CoalesceNumber(a, cfg, desc).CompareTo(CoalesceNumber(b, cfg, desc)),
        _ => string.CompareOrdinal(CoalesceText(a, cfg, desc), CoalesceText(b, cfg, desc)),
    };

    private static int CompareSortToCursor(CustomerInteraction a, SortConfig cfg, Cursor cursor, bool desc)
    {
        switch (cfg.Type)
        {
            case SortType.Date:
                var cd = cursor.SortValue.ValueKind == JsonValueKind.String &&
                         DateTimeOffset.TryParse(cursor.SortValue.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var pd)
                    ? pd : DateTimeOffset.MinValue;
                return CoalesceDate(a, cfg, desc).CompareTo(cd);
            case SortType.Number:
                var cn = cursor.SortValue.ValueKind == JsonValueKind.Number ? cursor.SortValue.GetInt64() : long.MinValue;
                return CoalesceNumber(a, cfg, desc).CompareTo(cn);
            default:
                var cs = cursor.SortValue.ValueKind == JsonValueKind.String ? cursor.SortValue.GetString() ?? "" : "";
                return string.CompareOrdinal(CoalesceText(a, cfg, desc), cs);
        }
    }

    // ---- writes -------------------------------------------------------------------------------

    private static async Task<IResult> CreateAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) return denied;
        if (ctx!.OrganizationIds is { Count: 0 }) return CustomersHttp.Json(new { error = "Forbidden" }, 403);
        var body = await CustomersHttp.ReadBodyAsync(http);
        var issues = ValidateCreate(body);
        if (issues.Count > 0) return CustomersHttp.Json(new { error = "Invalid input", details = issues }, 400);
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<InteractionCreateInput, InteractionResult>(
                "customers.interactions.create",
                new InteractionCreateInput(ctx.OrganizationId ?? Guid.Empty, ctx.TenantId ?? Guid.Empty, body), ctx);
            await InteractionCompat.EmitInteractionSideEffectsAsync(http.RequestServices, r.Result, "created");
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(new { id = r.Result.InteractionId }, 201);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
    }

    private static async Task<IResult> UpdateAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) return denied;
        if (ctx!.OrganizationIds is { Count: 0 }) return CustomersHttp.Json(new { error = "Forbidden" }, 403);
        var body = await CustomersHttp.ReadBodyAsync(http);
        var id = CustomersHttp.GuidOf(body, "id") ?? Guid.Empty;
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<InteractionUpdateInput, InteractionResult>(
                "customers.interactions.update", new InteractionUpdateInput(id, body), ctx);
            await InteractionCompat.EmitInteractionSideEffectsAsync(http.RequestServices, r.Result, "updated");
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(new { ok = true }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
    }

    private static async Task<IResult> DeleteAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) return denied;
        if (ctx!.OrganizationIds is { Count: 0 }) return CustomersHttp.Json(new { error = "Forbidden" }, 403);
        var body = await CustomersHttp.ReadBodyAsync(http);
        var id = CustomersHttp.GuidOf(body, "id");
        if (id is null && Guid.TryParse(http.Request.Query["id"], out var qid)) id = qid;
        if (id is null) return CustomersHttp.Json(new { error = "Interaction id is required" }, 400);
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<InteractionDeleteInput, InteractionResult>(
                "customers.interactions.delete", new InteractionDeleteInput(id.Value), ctx);
            await InteractionCompat.EmitInteractionSideEffectsAsync(http.RequestServices, r.Result, "deleted");
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(new { ok = true }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
    }

    // ---- complete / cancel --------------------------------------------------------------------

    private static async Task<IResult> CompleteAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) return denied;
        var body = await CustomersHttp.ReadBodyAsync(http);
        var id = CustomersHttp.GuidOf(body, "id");
        if (id is null) return CustomersHttp.Json(new { error = "Validation failed" }, 400);
        var occurredAt = CustomersHttp.Date(body, "occurredAt");
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<InteractionCompleteInput, InteractionResult>(
                "customers.interactions.complete", new InteractionCompleteInput(id.Value, occurredAt), ctx!);
            await InteractionCompat.EmitInteractionSideEffectsAsync(http.RequestServices, r.Result, "updated", "customers.interaction.completed");
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(new { ok = true }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
    }

    private static async Task<IResult> CancelAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, Manage);
        if (denied is not null) return denied;
        var body = await CustomersHttp.ReadBodyAsync(http);
        var id = CustomersHttp.GuidOf(body, "id");
        if (id is null) return CustomersHttp.Json(new { error = "Validation failed" }, 400);
        try
        {
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<InteractionCancelInput, InteractionResult>(
                "customers.interactions.cancel", new InteractionCancelInput(id.Value), ctx!);
            await InteractionCompat.EmitInteractionSideEffectsAsync(http.RequestServices, r.Result, "updated", "customers.interaction.canceled");
            PeopleRoutes.SetOperationHeader(http, r.LogEntry);
            return CustomersHttp.Json(new { ok = true }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
    }

    // ---- PATCH [id]/visibility ----------------------------------------------------------------

    private static async Task<IResult> VisibilityAsync(HttpContext http, string id)
    {
        if (!Guid.TryParse(id, out var interactionId))
            return CustomersHttp.Json(new { error = "Invalid interaction id" }, 400);
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, EmailCompose);
        if (denied is not null) return denied;
        if (ctx!.UserId is null) return CustomersHttp.Json(new { error = "Unauthorized" }, 401);

        var body = await CustomersHttp.ReadBodyAsync(http);
        var visibility = CustomersHttp.Str(body, "visibility");
        // strict body: only `visibility` ∈ {private,shared}; anything else → 422.
        var extraKeys = body.ValueKind == JsonValueKind.Object && body.EnumerateObject().Any(p => p.Name != "visibility");
        if (visibility is not ("private" or "shared") || extraKeys)
            return CustomersHttp.Json(new { error = "Invalid request body" }, 422);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var interaction = await db.Set<CustomerInteraction>().AsNoTracking().FirstOrDefaultAsync(i =>
            i.Id == interactionId && i.TenantId == ctx.TenantId && i.DeletedAt == null && i.InteractionType == "email");
        if (interaction is null) return CustomersHttp.Json(new { error = "Email not found" }, 404);

        // Author-only (v1 strict owner-only): non-author → 404 (existence-masking, not 403).
        if (interaction.AuthorUserId is null || interaction.AuthorUserId != ctx.UserId)
            return CustomersHttp.Json(new { error = "Email not found" }, 404);

        if (interaction.Visibility == visibility)
            return CustomersHttp.Json(new { ok = true, changed = false }, 200);

        var previous = interaction.Visibility ?? "private";
        try
        {
            var patch = JsonDocument.Parse(JsonSerializer.Serialize(new { id = interactionId.ToString(), visibility }, CustomersHttp.Web)).RootElement.Clone();
            var bus = http.RequestServices.GetRequiredService<CommandBus>();
            var r = await bus.ExecuteWithLog<InteractionUpdateInput, InteractionResult>(
                "customers.interactions.update", new InteractionUpdateInput(interactionId, patch), ctx);
            await InteractionCompat.EmitInteractionSideEffectsAsync(http.RequestServices, r.Result, "updated");

            var events = http.RequestServices.GetRequiredService<IEventBus>();
            try
            {
                await events.PublishAsync("customers.email.visibility_changed", new
                {
                    interactionId = interaction.Id.ToString(), previousVisibility = previous, nextVisibility = visibility,
                    authorUserId = interaction.AuthorUserId?.ToString(), actorUserId = ctx.UserId?.ToString(),
                    adminBypass = false, tenantId = ctx.TenantId, organizationId = ctx.OrganizationId,
                });
            }
            catch { /* audit emission must not block the response */ }

            return CustomersHttp.Json(new { ok = true, changed = true }, 200);
        }
        catch (CommandHttpException ex) { return CustomersHttp.Json(ex.Body, ex.Status); }
    }

    // ---- GET conflicts ------------------------------------------------------------------------

    private static async Task<IResult> ConflictsAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        var qp = http.Request.Query;
        var date = qp["date"].ToString();
        var startTime = qp["startTime"].ToString();
        if (!System.Text.RegularExpressions.Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(startTime, @"^\d{2}:\d{2}$") ||
            !int.TryParse(qp["duration"], out var duration) || duration < 1 || duration > 1440)
            return CustomersHttp.Json(new { error = "Validation failed" }, 400);

        var offset = int.TryParse(qp["timezoneOffsetMinutes"], out var tz) ? Math.Clamp(tz, -900, 900) : 0;
        var sign = offset >= 0 ? "+" : "-";
        var abs = Math.Abs(offset);
        var suffix = $"{sign}{abs / 60:00}:{abs % 60:00}";
        if (!DateTimeOffset.TryParse($"{date}T{startTime}:00{suffix}", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out var windowStart))
            return CustomersHttp.Json(new { error = "Invalid date/time" }, 400);
        var windowEnd = windowStart.AddMinutes(duration);

        Guid? checkUserId = Guid.TryParse(qp["userId"], out var uid) ? uid : ctx!.UserId;
        Guid? excludeId = Guid.TryParse(qp["excludeId"], out var ex) ? ex : null;

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var q = db.Set<CustomerInteraction>().AsNoTracking()
            .Where(i => i.TenantId == ctx!.TenantId && i.Status == "planned" && i.ScheduledAt != null && i.DeletedAt == null);
        if (ctx!.OrganizationIds is { Count: > 0 } orgIds) q = q.Where(i => orgIds.Contains(i.OrganizationId));
        if (checkUserId is { } cu) q = q.Where(i => i.AuthorUserId == cu || i.OwnerUserId == cu);
        if (excludeId is { } xid) q = q.Where(i => i.Id != xid);
        var candidates = await q.OrderBy(i => i.ScheduledAt).ToListAsync();

        var conflicts = candidates
            .Where(i =>
            {
                var start = i.ScheduledAt!.Value;
                var end = start.AddMinutes(i.DurationMinutes ?? 30);
                return start < windowEnd && end > windowStart;
            })
            .Take(10)
            .Select(i =>
            {
                var start = i.ScheduledAt!.Value;
                var end = start.AddMinutes(i.DurationMinutes ?? 30);
                return new
                {
                    id = i.Id.ToString(), title = i.Title,
                    startTime = start.ToUniversalTime().ToString("HH:mm"),
                    endTime = end.ToUniversalTime().ToString("HH:mm"),
                    type = i.InteractionType,
                };
            })
            .ToList();

        return CustomersHttp.Json(new { ok = true, result = new { hasConflicts = conflicts.Count > 0, conflicts } }, 200);
    }

    // ---- GET counts ---------------------------------------------------------------------------

    private static async Task<IResult> CountsAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        var qp = http.Request.Query;
        if (!Guid.TryParse(qp["entityId"], out var entityId))
            return CustomersHttp.Json(new { error = "Validation failed" }, 400);
        var status = qp["status"].ToString();
        if (!string.IsNullOrEmpty(status) && status is not ("done" or "planned"))
            return CustomersHttp.Json(new { error = "Validation failed" }, 400);

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var q = db.Set<CustomerInteraction>().AsNoTracking()
            .Where(i => i.EntityId == entityId && i.TenantId == ctx!.TenantId && i.DeletedAt == null);
        if (ctx!.OrganizationIds is { Count: > 0 } orgIds) q = q.Where(i => orgIds.Contains(i.OrganizationId));
        if (!string.IsNullOrEmpty(status)) q = q.Where(i => i.Status == status);
        q = InteractionCompat.ApplyEmailVisibility(q, ctx!.UserId);

        var grouped = await q.GroupBy(i => i.InteractionType).Select(g => new { Type = g.Key, Count = g.Count() }).ToListAsync();
        var counts = new Dictionary<string, int> { ["call"] = 0, ["email"] = 0, ["meeting"] = 0, ["note"] = 0, ["task"] = 0 };
        var total = 0;
        foreach (var g in grouped) { if (counts.ContainsKey(g.Type)) counts[g.Type] = g.Count; total += g.Count; }

        return CustomersHttp.Json(new
        {
            ok = true,
            result = new { call = counts["call"], email = counts["email"], meeting = counts["meeting"], note = counts["note"], task = counts["task"], total },
        }, 200);
    }

    // ---- GET tasks (canonical + legacy todo merge) --------------------------------------------

    private static async Task<IResult> TasksAsync(HttpContext http)
    {
        var (ctx, denied) = await CustomersHttp.AuthorizeAsync(http, View);
        if (denied is not null) return denied;
        var qp = http.Request.Query;
        var page = int.TryParse(qp["page"], out var pg) && pg >= 1 ? pg : 1;
        var pageSize = int.TryParse(qp["pageSize"], out var ps) ? Math.Clamp(ps, 1, 100) : 50;
        var exportAll = CrudBool(qp["all"].ToString());
        Guid? entityFilter = Guid.TryParse(qp["entityId"], out var ef) ? ef : null;
        var search = qp["search"].ToString();

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var flags = InteractionCompat.ResolveFlags(http.RequestServices, ctx!.TenantId);

        // Canonical task interactions.
        var canonicalQ = db.Set<CustomerInteraction>().AsNoTracking()
            .Where(i => i.InteractionType == "task" && i.TenantId == ctx.TenantId && i.DeletedAt == null);
        if (ctx.OrganizationIds is { Count: > 0 } orgIds) canonicalQ = canonicalQ.Where(i => orgIds.Contains(i.OrganizationId));
        if (entityFilter is { } efid) canonicalQ = canonicalQ.Where(i => i.EntityId == efid);
        var canonical = await canonicalQ.ToListAsync();

        var rows = new List<Dictionary<string, object?>>();
        var bridgeIds = new HashSet<Guid>();
        foreach (var i in canonical)
        {
            if (i.Source == InteractionCompat.TodoAdapterSource) bridgeIds.Add(i.Id);
            rows.Add(MapCanonicalTodo(i));
        }

        if (!flags.Unified)
        {
            var legacyQ = db.Set<CustomerTodoLink>().AsNoTracking().Where(t => t.TenantId == ctx.TenantId);
            if (ctx.OrganizationIds is { Count: > 0 } orgIds2) legacyQ = legacyQ.Where(t => orgIds2.Contains(t.OrganizationId));
            if (entityFilter is { } efid2) legacyQ = legacyQ.Where(t => t.EntityId == efid2);
            var legacy = await legacyQ.ToListAsync();
            foreach (var l in legacy.Where(l => !bridgeIds.Contains(l.TodoId)))
                rows.Add(MapLegacyTodo(l));
        }

        // Resolve customer summaries.
        var custIds = rows.Select(r => Guid.TryParse(r["_entityId"]?.ToString(), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToList();
        var customers = custIds.Count == 0
            ? new Dictionary<Guid, CustomerEntity>()
            : (await db.Set<CustomerEntity>().AsNoTracking().Where(e => custIds.Contains(e.Id)).ToListAsync()).ToDictionary(e => e.Id);
        foreach (var r in rows)
        {
            var eid = Guid.TryParse(r["_entityId"]?.ToString(), out var g) ? (Guid?)g : null;
            var cust = eid is { } id2 && customers.TryGetValue(id2, out var c) ? c : null;
            r["customer"] = new { id = cust?.Id.ToString(), displayName = cust?.DisplayName, kind = cust?.Kind };
            r.Remove("_entityId");
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            rows = rows.Where(r => (r["todoTitle"]?.ToString() ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        rows = rows.OrderByDescending(r => r["createdAt"]?.ToString()).ToList();

        var total = rows.Count;
        var items = exportAll ? rows : rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return CustomersHttp.Json(new
        {
            items,
            total,
            page = exportAll ? 1 : page,
            pageSize = exportAll ? items.Count : pageSize,
            totalPages = exportAll ? 1 : Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)),
        }, 200);
        // PARITY-TODO: query-engine hydration of legacy todo details (severity/description/dueAt/customValues/externalHref).
    }

    private static Dictionary<string, object?> MapCanonicalTodo(CustomerInteraction i) => new()
    {
        ["id"] = i.Id.ToString(),
        ["todoId"] = i.Id.ToString(),
        ["todoSource"] = InteractionCompat.TaskSource,
        ["todoTitle"] = i.Title,
        ["todoIsDone"] = i.Status == "done",
        ["todoPriority"] = i.Priority,
        ["todoSeverity"] = (string?)null,
        ["todoDescription"] = i.Body,
        ["todoDueAt"] = CustomersHttp.Iso(i.ScheduledAt),
        ["todoOrganizationId"] = (string?)null,
        ["organizationId"] = i.OrganizationId.ToString(),
        ["tenantId"] = i.TenantId.ToString(),
        ["createdAt"] = CustomersHttp.Iso(i.CreatedAt),
        ["_entityId"] = i.EntityId.ToString(),
    };

    private static Dictionary<string, object?> MapLegacyTodo(CustomerTodoLink l) => new()
    {
        ["id"] = l.Id.ToString(),
        ["todoId"] = l.TodoId.ToString(),
        ["todoSource"] = l.TodoSource,
        ["todoTitle"] = (string?)null,
        ["todoIsDone"] = (bool?)null,
        ["todoPriority"] = (int?)null,
        ["todoSeverity"] = (string?)null,
        ["todoDescription"] = (string?)null,
        ["todoDueAt"] = (string?)null,
        ["todoOrganizationId"] = l.OrganizationId.ToString(),
        ["organizationId"] = l.OrganizationId.ToString(),
        ["tenantId"] = l.TenantId.ToString(),
        ["createdAt"] = CustomersHttp.Iso(l.CreatedAt),
        ["_entityId"] = l.EntityId.ToString(),
    };

    // ---- validation ---------------------------------------------------------------------------

    private static IReadOnlyList<CrudValidationIssue> ValidateCreate(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();
        if (CustomersHttp.GuidOf(body, "entityId") is null)
            issues.Add(new CrudValidationIssue(new[] { "entityId" }, "entityId is required", "invalid_uuid"));
        var type = CustomersHttp.Str(body, "interactionType")?.Trim();
        if (string.IsNullOrEmpty(type) || type.Length > 100)
            issues.Add(new CrudValidationIssue(new[] { "interactionType" }, "interactionType is required", "invalid_string"));
        // date/time are optional but, when PRESENT, must be non-empty after trim (upstream
        // `z.string().trim().min(1).optional()`, issue #1806 — OM integration test TC-CRM-053).
        if (CustomersHttp.Has(body, "date") && string.IsNullOrEmpty(CustomersHttp.Str(body, "date")?.Trim()))
            issues.Add(new CrudValidationIssue(new[] { "date" }, "customers.activities.errors.dateRequired", "too_small"));
        if (CustomersHttp.Has(body, "time") && string.IsNullOrEmpty(CustomersHttp.Str(body, "time")?.Trim()))
            issues.Add(new CrudValidationIssue(new[] { "time" }, "customers.activities.errors.timeRequired", "too_small"));
        // superRefine: interactionType='call' requires a valid phoneNumber when one is provided.
        if (type == "call" && CustomersHttp.Has(body, "phoneNumber"))
        {
            var phone = CustomersHttp.Str(body, "phoneNumber")?.Trim();
            if (string.IsNullOrEmpty(phone) || !System.Text.RegularExpressions.Regex.IsMatch(phone, @"^\+?[0-9 ().\-]{6,}$"))
                issues.Add(new CrudValidationIssue(new[] { "phoneNumber" }, "A valid phone number is required for calls", "custom"));
            // PARITY-TODO: full libphonenumber validation (isValidPhoneNumber).
        }
        return issues;
    }

    private static bool CrudBool(string? v) => v is not null && (v == "true" || v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
}
