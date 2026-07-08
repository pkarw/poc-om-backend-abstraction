namespace OpenMercato.Core.Commands;

/// <summary>
/// Command-level scope guards — the port of upstream <c>ensureTenantScope</c> /
/// <c>ensureOrganizationScope</c> / <c>ensureSameScope</c> (packages/shared/src/lib/commands/scope.ts).
/// Called at the top of a command handler to reject cross-tenant / out-of-scope writes with the exact
/// HTTP 403 bodies from spec 03 R26. <c>null</c> allowed-set means unrestricted; super admins pass.
///
/// Note: these guard against acting on a FORBIDDEN scope; a handler MUST still additionally scope its
/// record lookups by tenant/org/deleted_at and return 404 when a record is out of scope (spec 03 R27).
/// </summary>
public static class CommandScope
{
    /// <summary>Reject when the actor's tenant is set and differs from <paramref name="tenantId"/> (403 Forbidden).</summary>
    public static void EnsureTenantScope(CommandContext ctx, Guid tenantId)
    {
        var current = ctx.TenantId;
        if (current is not null && current.Value != tenantId)
            throw CommandHttpException.Forbidden();
    }

    /// <summary>
    /// Reject when <paramref name="organizationId"/> is outside the actor's allowed org set
    /// (403 Forbidden). Super admins and a null allowed-set pass. When no scope was resolved, falls
    /// back to the selected org (mirrors upstream Pattern C); a fully unscoped call is allowed unless
    /// <c>OM_ENFORCE_ORG_SCOPE_STRICT</c> is truthy.
    /// </summary>
    public static void EnsureOrganizationScope(CommandContext ctx, Guid organizationId)
    {
        if (ctx.IsSuperAdmin) return;

        var allowed = ctx.AllowedOrganizationIds;
        if (allowed is null)
        {
            // No allowed-set resolved: preserve the legacy currentOrg fallback.
            var currentOrg = ctx.OrganizationId;
            if (currentOrg is not null)
            {
                if (currentOrg.Value != organizationId) throw CommandHttpException.Forbidden();
                return;
            }
            // Fully unscoped: allow by default, deny only under strict enforcement.
            if (IsStrictOrgScopeEnforced()) throw CommandHttpException.Forbidden();
            return;
        }

        if (!allowed.Contains(organizationId))
            throw CommandHttpException.Forbidden();
    }

    /// <summary>
    /// Reject a cross-tenant/org entity relation with 403 <c>{ error: "Cross-tenant relation forbidden" }</c>
    /// (upstream <c>ensureSameScope</c>).
    /// </summary>
    public static void EnsureSameScope(Guid entityOrganizationId, Guid entityTenantId, Guid organizationId, Guid tenantId)
    {
        if (entityOrganizationId != organizationId || entityTenantId != tenantId)
            throw new CommandHttpException(403, new { error = "Cross-tenant relation forbidden" });
    }

    internal static bool IsStrictOrgScopeEnforced()
    {
        var raw = Environment.GetEnvironmentVariable("OM_ENFORCE_ORG_SCOPE_STRICT");
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return raw.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
    }
}
