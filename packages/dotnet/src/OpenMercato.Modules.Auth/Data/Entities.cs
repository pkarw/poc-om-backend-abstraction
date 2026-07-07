namespace OpenMercato.Modules.Auth.Data;

// Plain POCO entities mirroring upstream packages/core/src/modules/auth/data/entities.ts.
// Byte-exact table/column/index/constraint mapping lives in AuthModule.ConfigureModel;
// the DDL is created by the raw-SQL migration OpenMercato.Api/Migrations AddAuthModule.
// All tables soft-delete via nullable DeletedAt; tenant_id/organization_id are bare uuids
// (tenancy by convention, no cross-module FK). jsonb columns are stored as raw JSON strings.

/// <summary>Table <c>users</c>. Email + name are encrypted at rest; EmailHash is the lookup hash.</summary>
public sealed class User
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OrganizationId { get; set; }
    /// <summary>Encrypted (AES-256-GCM per-row IV) ciphertext of the email.</summary>
    public string Email { get; set; } = string.Empty;
    /// <summary>Deterministic lookup hash (see EncryptionService.ComputeEmailHash).</summary>
    public string? EmailHash { get; set; }
    /// <summary>Encrypted display name.</summary>
    public string? Name { get; set; }
    /// <summary>bcrypt cost 10 hash; null for invite-pending users.</summary>
    public string? PasswordHash { get; set; }
    public bool IsConfirmed { get; set; } = true;
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Table <c>roles</c>. Strictly tenant-scoped (tenant_id NOT NULL).</summary>
public sealed class Role
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Table <c>user_roles</c>. Link table; no updated_at, no (user_id, role_id) uniqueness.</summary>
public sealed class UserRole
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Table <c>sessions</c>. Token is HMAC-SHA256(rawToken) hex; binds an issued JWT (sid).</summary>
public sealed class Session
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Token { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Table <c>password_resets</c>. Token is HMAC hash; UsedAt is atomic compare-and-set on confirm.</summary>
public sealed class PasswordReset
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Token { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Table <c>role_acls</c>. FeaturesJson/OrganizationsJson are raw jsonb string arrays.</summary>
public sealed class RoleAcl
{
    public Guid Id { get; set; }
    public Guid RoleId { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>jsonb: string[] with wildcards (e.g. ["auth.*"]). Null allowed.</summary>
    public string? FeaturesJson { get; set; }
    public bool IsSuperAdmin { get; set; }
    /// <summary>jsonb: string[]; null/empty = all orgs.</summary>
    public string? OrganizationsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Table <c>user_acls</c>. Same shape as role_acls but keyed by user_id.</summary>
public sealed class UserAcl
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid TenantId { get; set; }
    public string? FeaturesJson { get; set; }
    public bool IsSuperAdmin { get; set; }
    public string? OrganizationsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Table <c>user_sidebar_preferences</c>. SettingsJson is raw jsonb.</summary>
public sealed class UserSidebarPreference
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OrganizationId { get; set; }
    public string Locale { get; set; } = string.Empty;
    public string? SettingsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Table <c>role_sidebar_preferences</c>. No organization_id column.</summary>
public sealed class RoleSidebarPreference
{
    public Guid Id { get; set; }
    public Guid RoleId { get; set; }
    public Guid? TenantId { get; set; }
    public string Locale { get; set; } = string.Empty;
    public string? SettingsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Table <c>sidebar_variants</c>. Named sidebar presets; unique (user_id, tenant_id, name) where not deleted.</summary>
public sealed class SidebarVariant
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OrganizationId { get; set; }
    public string Locale { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? SettingsJson { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Table <c>user_consents</c>. UserId has NO FK. Source/IpAddress encrypted; IntegrityHash HMAC.</summary>
public sealed class UserConsent
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? TenantId { get; set; }
    public Guid? OrganizationId { get; set; }
    public string ConsentType { get; set; } = string.Empty;
    public bool IsGranted { get; set; }
    public DateTimeOffset? GrantedAt { get; set; }
    public DateTimeOffset? WithdrawnAt { get; set; }
    /// <summary>Encrypted.</summary>
    public string? Source { get; set; }
    /// <summary>Encrypted.</summary>
    public string? IpAddress { get; set; }
    public string? IntegrityHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
