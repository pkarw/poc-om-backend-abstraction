namespace OpenMercato.Core.Commands;

/// <summary>Resolved display names for an audit-log page: user id → name/email, tenant id → name, org id → name.</summary>
public sealed record AuditLogDisplayMaps(
    IReadOnlyDictionary<Guid, string> Users,
    IReadOnlyDictionary<Guid, string> Tenants,
    IReadOnlyDictionary<Guid, string> Organizations)
{
    public static readonly AuditLogDisplayMaps Empty = new(
        new Dictionary<Guid, string>(), new Dictionary<Guid, string>(), new Dictionary<Guid, string>());
}

/// <summary>
/// Hydrates actor/tenant/org display names for the audit-log read routes (upstream
/// <c>loadAuditLogDisplayMaps</c>). Defined in Core because the routes live in <see cref="AuditLogsModule"/>
/// (Core), but the implementation lives in a module that can reach Auth (user name/email decryption) +
/// Directory (tenant/org names) — Core cannot reference those. The routes resolve this OPTIONALLY from DI
/// and degrade to un-hydrated ids when no implementation is registered.
/// </summary>
public interface IAuditLogDisplayDirectory
{
    Task<AuditLogDisplayMaps> ResolveAsync(
        IReadOnlyCollection<Guid> userIds,
        IReadOnlyCollection<Guid> tenantIds,
        IReadOnlyCollection<Guid> organizationIds,
        CancellationToken ct = default);
}
