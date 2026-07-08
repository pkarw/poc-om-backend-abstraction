using System.Text.Json;

namespace OpenMercato.Modules.Customers.Commands;

// Command input/result contracts for the customers Phase-1 write surface. Create/update inputs carry
// the raw request body (JsonElement) so the handler can read fields AND persist cf_* custom-field
// values in the same transaction (the .NET CRUD factory does not persist custom fields itself — the
// command owns that, keeping base + satellite + EAV writes atomic). Ids are strings for wire parity.

// ---- People ----------------------------------------------------------------------------------
public sealed record PersonCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record PersonUpdateInput(Guid Id, JsonElement Body);
public sealed record PersonDeleteInput(Guid Id);
public sealed record PersonResult(string? EntityId, string? PersonId, DateTimeOffset? UpdatedAt = null);

// ---- Companies -------------------------------------------------------------------------------
public sealed record CompanyCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record CompanyUpdateInput(Guid Id, JsonElement Body);
public sealed record CompanyDeleteInput(Guid Id);
public sealed record CompanyResult(string? EntityId, string? CompanyId, DateTimeOffset? UpdatedAt = null);

// ---- Addresses -------------------------------------------------------------------------------
public sealed record AddressCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record AddressUpdateInput(Guid Id, JsonElement Body);
public sealed record AddressDeleteInput(Guid Id);
public sealed record AddressResult(string? AddressId);

// ---- Tags ------------------------------------------------------------------------------------
public sealed record TagCreateInput(Guid OrganizationId, Guid TenantId, JsonElement Body);
public sealed record TagUpdateInput(Guid Id, JsonElement Body);
public sealed record TagDeleteInput(Guid Id);
public sealed record TagResult(string? TagId);

public sealed record TagAssignInput(Guid OrganizationId, Guid TenantId, Guid TagId, Guid EntityId);
public sealed record TagAssignResult(string? AssignmentId);

// ---- Labels ----------------------------------------------------------------------------------
public sealed record LabelCreateInput(Guid OrganizationId, Guid TenantId, Guid UserId, string Slug, string Label);
public sealed record LabelResult(string Id, string Slug, string Label);

public sealed record LabelAssignInput(Guid OrganizationId, Guid TenantId, Guid UserId, Guid LabelId, Guid EntityId);
public sealed record LabelAssignResult(string? AssignmentId, bool Created);

// ---- Entity roles ----------------------------------------------------------------------------
public sealed record EntityRoleCreateInput(Guid OrganizationId, Guid TenantId, string EntityType, Guid EntityId, string RoleType, Guid UserId);
public sealed record EntityRoleUpdateInput(Guid OrganizationId, Guid TenantId, Guid Id, Guid UserId);
public sealed record EntityRoleDeleteInput(Guid OrganizationId, Guid TenantId, Guid Id);
public sealed record EntityRoleResult(string? RoleId);

// ---- Person↔company links --------------------------------------------------------------------
public sealed record PersonCompanyLinkCreateInput(Guid OrganizationId, Guid TenantId, Guid PersonEntityId, Guid CompanyEntityId, bool? IsPrimary);
public sealed record PersonCompanyLinkUpdateInput(Guid OrganizationId, Guid TenantId, Guid LinkId, bool IsPrimary);
public sealed record PersonCompanyLinkDeleteInput(Guid OrganizationId, Guid TenantId, Guid LinkId);
public sealed record PersonCompanyLinkResult(string? Id, string? CompanyId, string? DisplayName, bool IsPrimary);

// ---- Undo/redo snapshots ---------------------------------------------------------------------
public sealed record CustomerEntitySnapshot(
    Guid Id, Guid OrganizationId, Guid TenantId, string Kind, string DisplayName, string? Description,
    Guid? OwnerUserId, string? PrimaryEmail, string? PrimaryPhone, string? Status, string? LifecycleStage,
    string? Source, string? Temperature, string? RenewalQuarter, bool IsActive,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt);

public sealed record PersonProfileSnapshot(
    Guid Id, Guid OrganizationId, Guid TenantId, string? FirstName, string? LastName, string? PreferredName,
    string? JobTitle, string? Department, string? Seniority, string? Timezone, string? LinkedInUrl,
    string? TwitterUrl, Guid EntityId, Guid? CompanyEntityId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record CompanyProfileSnapshot(
    Guid Id, Guid OrganizationId, Guid TenantId, string? LegalName, string? BrandName, string? Domain,
    string? WebsiteUrl, string? Industry, string? SizeBucket, decimal? AnnualRevenue, Guid EntityId,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
