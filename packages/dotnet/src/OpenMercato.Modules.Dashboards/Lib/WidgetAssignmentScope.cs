namespace OpenMercato.Modules.Dashboards.Lib;

/// <summary>Resolved (tenant, org) scope for a widget-assignment read (upstream ResolvedAssignmentScope).</summary>
public readonly record struct ResolvedAssignmentScope(Guid? TenantId, Guid? OrganizationId);

/// <summary>
/// 1:1 port of upstream <c>lib/widgetAssignmentScope.ts</c>. Only a superadmin may target a scope
/// other than their own session's tenant/org via query parameters; everyone else is pinned to their
/// session scope.
/// </summary>
public static class WidgetAssignmentScope
{
    public static ResolvedAssignmentScope ResolveReadScope(
        Guid? authTenantId,
        Guid? authOrgId,
        bool isSuperAdmin,
        Guid? queryTenantId,
        Guid? queryOrganizationId)
    {
        if (isSuperAdmin)
        {
            return new ResolvedAssignmentScope(
                queryTenantId ?? authTenantId,
                queryOrganizationId ?? authOrgId);
        }
        return new ResolvedAssignmentScope(authTenantId, authOrgId);
    }
}
