namespace OpenMercato.Modules.Directory.Data;

// Plain POCO entities mirroring upstream packages/core/src/modules/directory/data/entities.ts.
// Byte-exact table/column/index/constraint mapping lives in DirectoryModule.ConfigureModel; the
// DDL is created by the raw-SQL migration OpenMercato.Api/Migrations AddDirectoryModule. Both
// tables soft-delete via nullable DeletedAt. The organization hierarchy arrays are stored as raw
// jsonb string arrays (default '[]'); tenancy is by convention (tenant_id FK only, parent_id/root_id
// carry NO DB FK — the tree is managed in Lib/OrganizationHierarchy).

/// <summary>Table <c>tenants</c>. The tenant root — no own tenant_id/organization_id.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Table <c>organizations</c>. tenant_id FK → tenants.id (ON UPDATE CASCADE).</summary>
public sealed class Organization
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Slug { get; set; }
    public string? LogoUrl { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Self-reference by convention (no DB FK); hierarchy managed in OrganizationHierarchy.</summary>
    public Guid? ParentId { get; set; }
    public Guid? RootId { get; set; }
    public string? TreePath { get; set; }
    public int Depth { get; set; }
    /// <summary>jsonb string[] of ancestor uuids (root → parent). Default '[]'.</summary>
    public string AncestorIdsJson { get; set; } = "[]";
    /// <summary>jsonb string[] of direct child uuids (name-sorted). Default '[]'.</summary>
    public string ChildIdsJson { get; set; } = "[]";
    /// <summary>jsonb string[] of all descendant uuids (pre-order). Default '[]'.</summary>
    public string DescendantIdsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
