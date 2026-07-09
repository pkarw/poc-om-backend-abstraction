using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenMercato.Modules.Auth.Security;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// GET /api/auth/admin/nav — the backend sidebar/chrome payload.
///
/// The sidebar is built from OM's FRONTEND page registry (its React <c>page.meta</c> files), which
/// this API-only port cannot know — so standalone this returns the faithful envelope with empty
/// chrome plus the actor's real <c>grantedFeatures</c> + <c>roles</c>.
///
/// In the testbench (<c>OM_NAV_UPSTREAM</c> set, e.g. <c>http://om-app:3000</c>) it instead FETCHES
/// OM's own nav (forwarding the caller's auth cookie/bearer) and FILTERS it to the modules actually
/// ported to .NET — so the shared-DB testbench sidebar shows only ported modules (customers, auth,
/// directory, dashboards, entities, query_index, dictionaries, currencies), not OM's unported ones
/// (catalog, sales, staff, storage, …). Filtering is by each nav item's <c>/backend/&lt;path&gt;</c>
/// prefix (group ids are not cleanly module-scoped). Empty groups/sections are dropped.
/// </summary>
public sealed class AdminNavRouteGroup : IAuthRouteGroup
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Href prefixes of the ported modules' backend pages (allowlist). An item survives the
    /// testbench filter iff its <c>href</c> starts with one of these.</summary>
    private static readonly string[] PortedHrefPrefixes =
    {
        "/backend/customers", "/backend/customer-tasks", "/backend/config/customers", // customers
        "/backend/currencies", "/backend/exchange-rates",                             // currencies
        "/backend/entities", "/backend/query-indexes",                                // entities, query_index
        "/backend/directory",                                                          // directory
        "/backend/users", "/backend/roles", "/backend/api-keys",                       // auth
        "/backend/dictionaries",                                                       // dictionaries
        "/backend/dashboards",                                                          // dashboards
        "/backend/sidebar-customization",                                               // app-shell chrome (core)
    };

    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth/admin/nav", async (HttpContext http, IRbacService rbac) =>
        {
            var upstream = Environment.GetEnvironmentVariable("OM_NAV_UPSTREAM");
            if (!string.IsNullOrWhiteSpace(upstream))
            {
                var filtered = await TryFetchAndFilterAsync(upstream!, http);
                if (filtered is not null) return Results.Content(filtered, "application/json");
                // fall through to the empty envelope if the upstream fetch/parse fails
            }

            var auth = HttpContextAuth.Current(http)!;
            string[] grantedFeatures;
            try { grantedFeatures = (await rbac.LoadAcl(auth.UserId, auth.TenantId, auth.OrganizationId)).Features; }
            catch { grantedFeatures = Array.Empty<string>(); }

            return Results.Json(new
            {
                brand = (object?)null,
                groups = Array.Empty<object>(),
                settingsSections = Array.Empty<object>(),
                settingsPathPrefixes = Array.Empty<string>(),
                profileSections = Array.Empty<object>(),
                profilePathPrefixes = Array.Empty<string>(),
                grantedFeatures,
                roles = auth.Roles,
            });
        }).RequireAuth();
    }

    private static async Task<string?> TryFetchAndFilterAsync(string upstreamBase, HttpContext http)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, upstreamBase.TrimEnd('/') + "/api/auth/admin/nav");
            // Forward the caller's auth so OM resolves the same user (shared JWT_SECRET).
            if (http.Request.Headers.TryGetValue("Cookie", out var cookie))
                req.Headers.TryAddWithoutValidation("Cookie", cookie.ToString());
            if (http.Request.Headers.TryGetValue("Authorization", out var authz))
                req.Headers.TryAddWithoutValidation("Authorization", authz.ToString());

            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            if (JsonNode.Parse(json) is not JsonObject root) return null;

            root["groups"] = FilterGroups(root["groups"] as JsonArray);
            root["settingsSections"] = FilterGroups(root["settingsSections"] as JsonArray);
            return root.ToJsonString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Keep only items whose href is a ported page; drop groups left empty. Deep-clones nodes
    /// (a JsonNode can't be re-parented) so the returned tree is standalone.</summary>
    private static JsonArray FilterGroups(JsonArray? groups)
    {
        var kept = new JsonArray();
        if (groups is null) return kept;
        foreach (var g in groups)
        {
            if (g is not JsonObject group) continue;
            var keptItems = new JsonArray();
            if (group["items"] is JsonArray items)
                foreach (var it in items)
                    if (it is JsonObject io && IsPorted(io["href"]?.GetValue<string>()))
                        keptItems.Add(JsonNode.Parse(io.ToJsonString()));
            if (keptItems.Count == 0) continue;
            var clone = JsonNode.Parse(group.ToJsonString())!.AsObject();
            clone["items"] = keptItems;
            kept.Add(clone);
        }
        return kept;
    }

    private static bool IsPorted(string? href) =>
        href is not null && PortedHrefPrefixes.Any(p => href.StartsWith(p, StringComparison.Ordinal));
}
