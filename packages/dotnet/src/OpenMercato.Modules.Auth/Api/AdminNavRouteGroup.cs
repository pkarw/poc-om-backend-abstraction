using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenMercato.Modules.Auth.Security;

namespace OpenMercato.Modules.Auth.Api;

/// <summary>
/// GET /api/auth/admin/nav — 1:1 port of the response contract of upstream api/admin/nav.ts (requires
/// auth). The full sidebar chrome (groups/sections) is UI-derived (lib/backendChrome.tsx) and out of
/// scope; this returns the faithful envelope with empty chrome plus the actor's real
/// <c>grantedFeatures</c> (via RBAC) and JWT <c>roles</c>. // PARITY-TODO: populate chrome once the
/// backend route/entity registries are ported.
/// </summary>
public sealed class AdminNavRouteGroup : IAuthRouteGroup
{
    public void Map(IEndpointRouteBuilder routes)
    {
        routes.MapGet("/api/auth/admin/nav", async (HttpContext http, IRbacService rbac) =>
        {
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
}
