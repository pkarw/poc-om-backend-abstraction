namespace OpenMercato.Modules.Dashboards.Data;

// Plain POCO entities mirroring upstream packages/core/src/modules/dashboards/data/entities.ts.
// Byte-exact table/column/index/constraint mapping lives in DashboardsModule.ConfigureModel; the
// DDL is created by the raw-SQL migration OpenMercato.Api/Migrations AddDashboardsModule. All three
// tables soft-delete via nullable DeletedAt; tenant_id/organization_id are bare nullable uuids
// (tenancy by convention, no cross-module FK). The jsonb columns are stored as raw JSON strings
// (default '[]') and manipulated with System.Text.Json in the route handlers — see Lib/LayoutJson.

/// <summary>Table <c>dashboard_layouts</c>. One row per (user, tenant, org) holding the user's
/// personal widget layout as a jsonb array of layout items.</summary>
public sealed class DashboardLayout
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OrganizationId { get; set; }
    /// <summary>jsonb array of layout items <c>{id,widgetId,order,priority?,size?,settings?}</c>. Default '[]'.</summary>
    public string LayoutJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Table <c>dashboard_role_widgets</c>. The set of widgets made available to a role within
/// a (tenant, org) scope.</summary>
public sealed class DashboardRoleWidgets
{
    public Guid Id { get; set; }
    public Guid RoleId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OrganizationId { get; set; }
    /// <summary>jsonb array of widget id strings. Default '[]'.</summary>
    public string WidgetIdsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Table <c>dashboard_user_widgets</c>. Per-user override of the role-level widget
/// availability. <see cref="Mode"/> is <c>inherit</c> (defer to roles) or <c>override</c>.</summary>
public sealed class DashboardUserWidgets
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OrganizationId { get; set; }
    /// <summary><c>inherit</c> | <c>override</c>. Default 'inherit'.</summary>
    public string Mode { get; set; } = "inherit";
    /// <summary>jsonb array of widget id strings. Default '[]'.</summary>
    public string WidgetIdsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
