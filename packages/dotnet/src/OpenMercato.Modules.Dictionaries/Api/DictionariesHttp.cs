using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Directory.Data;

namespace OpenMercato.Modules.Dictionaries.Api;

/// <summary>
/// Shared HTTP helpers for the dictionaries routes — the port of upstream <c>api/context.ts</c> +
/// <c>crud/errors</c> mapping. Resolves the authenticated <see cref="CommandContext"/> (via the Core
/// CRUD auth bridge), the org-inheritance read scope (selected org + ancestors), reads JSON bodies
/// defensively, and shapes JSON responses.
/// </summary>
internal static class DictionariesHttp
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static IResult Result(object body, int status) => Results.Json(body, Json, statusCode: status);

    internal static async Task<(CommandContext? Ctx, IResult? Denied)> AuthorizeAsync(HttpContext http, params string[] features)
    {
        var bridge = http.RequestServices.GetRequiredService<ICrudRequestContext>();
        var ctx = await bridge.ResolveAsync(http);
        if (ctx is null) return (null, Result(new { error = "Unauthorized" }, 401));
        if (ctx.TenantId is null) return (null, Result(new { error = "Unauthorized" }, 401));
        if (features.Length > 0 && !await bridge.HasAllFeaturesAsync(ctx, features))
            return (null, Result(new { error = "Forbidden", requiredFeatures = features }, 403));
        return (ctx, null);
    }

    /// <summary>
    /// Resolve the set of org ids whose dictionaries the request may READ — the selected org plus its
    /// ancestors (upstream <c>readableOrganizationIds</c>: child orgs inherit parent dictionaries).
    /// PARITY-TODO: the "no selected org ⇒ load all tenant orgs" branch is left as {} here.
    /// </summary>
    internal static async Task<IReadOnlyList<Guid>> ReadableOrganizationIdsAsync(HttpContext http, CommandContext ctx)
    {
        if (ctx.OrganizationId is not { } orgId) return Array.Empty<Guid>();
        var readable = new HashSet<Guid> { orgId };
        try
        {
            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            var org = await db.Set<Organization>().AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == orgId && o.TenantId == ctx.TenantId && o.DeletedAt == null);
            if (org is not null && !string.IsNullOrEmpty(org.AncestorIdsJson))
            {
                var ancestors = JsonSerializer.Deserialize<List<string>>(org.AncestorIdsJson) ?? new();
                foreach (var raw in ancestors)
                    if (Guid.TryParse(raw, out var g)) readable.Add(g);
            }
        }
        catch { /* best-effort; fall back to the selected org only */ }
        return readable.ToList();
    }

    internal static async Task<JsonElement> ReadBodyAsync(HttpContext http)
    {
        try
        {
            if (http.Request.ContentLength is 0) return Empty();
            using var doc = await JsonDocument.ParseAsync(http.Request.Body);
            return doc.RootElement.Clone();
        }
        catch { return Empty(); }
    }

    private static JsonElement Empty()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    internal static bool ParseBooleanToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return raw.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
    }

    internal static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    internal static bool? Bool(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => (bool?)null }
            : null;

    internal static int? Int(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)
            ? i : null;

    internal static bool Has(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out _);

    /// <summary>Build the <c>x-om-operation</c> header from a persisted action-log row (undoable ops only).</summary>
    internal static void SetOperationHeader(HttpContext http, ActionLog? log)
    {
        if (log is null || string.IsNullOrEmpty(log.UndoToken) || string.IsNullOrEmpty(log.CommandId)) return;
        var payload = new
        {
            id = log.Id.ToString(),
            undoToken = log.UndoToken,
            commandId = log.CommandId,
            actionLabel = log.ActionLabel,
            resourceKind = log.ResourceKind,
            resourceId = log.ResourceId,
            executedAt = (log.CreatedAt == default ? DateTime.UtcNow : DateTime.SpecifyKind(log.CreatedAt, DateTimeKind.Utc))
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };
        http.Response.Headers["x-om-operation"] = "omop:" + Uri.EscapeDataString(JsonSerializer.Serialize(payload, Json));
    }
}
