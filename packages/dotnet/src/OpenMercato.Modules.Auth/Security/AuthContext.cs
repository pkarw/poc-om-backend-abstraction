namespace OpenMercato.Modules.Auth.Security;

/// <summary>
/// The authenticated staff principal resolved for a request (spec 05 "AuthContext").
/// Roles come from the verified JWT claim. Foundation slice keeps this minimal;
/// domain slices may recompute roles/superadmin from the DB per spec 05 R17.
/// </summary>
public sealed record AuthContext(
    Guid UserId,
    Guid? Sid,
    Guid? TenantId,
    Guid? OrganizationId,
    string Email,
    string[] Roles);
