using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;

namespace OpenMercato.Modules.Auth.Security;

/// <summary>
/// The Auth-backed <see cref="ICrudRequestContext"/> the CRUD factory resolves at request time — the
/// port of upstream <c>withCtx</c> (crud/factory.ts): turn the HTTP request's staff JWT into the
/// <see cref="CommandContext"/> (with resolved org scope + request headers for optimistic locking), and
/// run the RBAC feature check.
///
/// Org-scope resolution here is the single-tenant common path: the selected org comes from the JWT and
/// the allowed set from the resolved ACL (<c>null</c> = unrestricted / super admin). PARITY-TODO:
/// org-tree descendant expansion + the <c>om_selected_org</c>/<c>om_selected_tenant</c> cookies land with
/// the directory scope resolver.
/// </summary>
public sealed class AuthCrudRequestContext : ICrudRequestContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly AppDbContext _db;
    private readonly JwtService _jwt;
    private readonly IRbacService _rbac;

    public AuthCrudRequestContext(IHttpContextAccessor httpContextAccessor, AppDbContext db, JwtService jwt, IRbacService rbac)
    {
        _httpContextAccessor = httpContextAccessor;
        _db = db;
        _jwt = jwt;
        _rbac = rbac;
    }

    public async Task<CommandContext?> ResolveAsync(HttpContext http)
    {
        var auth = HttpContextAuth.Current(http) ?? await HttpContextAuth.ResolveAsync(http, _db, _jwt);
        if (auth is null) return null;

        Acl acl;
        try { acl = await _rbac.LoadAcl(auth.UserId, auth.TenantId, auth.OrganizationId); }
        catch { acl = new Acl(); }

        // allowedIds: null = unrestricted (super admin / all orgs).
        IReadOnlyList<Guid>? allowedIds = acl.IsSuperAdmin || acl.Organizations is null
            ? null
            : acl.Organizations.Select(s => Guid.TryParse(s, out var g) ? (Guid?)g : null)
                .Where(g => g is not null).Select(g => g!.Value).ToList();

        // filterIds: a selected org narrows to it; otherwise fall back to the allowed set (null = all).
        IReadOnlyList<Guid>? filterIds = auth.OrganizationId is { } selected
            ? new[] { selected }
            : allowedIds;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in http.Request.Headers)
            headers[header.Key] = header.Value.ToString();

        return new CommandContext
        {
            TenantId = auth.TenantId,
            OrganizationId = auth.OrganizationId,
            UserId = auth.UserId,
            OrganizationIds = filterIds,
            AllowedOrganizationIds = allowedIds,
            IsSuperAdmin = acl.IsSuperAdmin,
            Headers = headers,
        };
    }

    public async Task<bool> HasAllFeaturesAsync(CommandContext ctx, IReadOnlyList<string> features)
    {
        if (features.Count == 0) return true;
        if (ctx.UserId is not { } userId) return false;
        return await _rbac.UserHasAllFeatures(userId, features, ctx.TenantId, ctx.OrganizationId);
    }
}
