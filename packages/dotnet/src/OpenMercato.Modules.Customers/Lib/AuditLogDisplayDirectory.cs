using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Directory.Data;

namespace OpenMercato.Modules.Customers.Lib;

/// <summary>
/// The <see cref="IAuditLogDisplayDirectory"/> implementation (registered from Customers, which already
/// references Auth + Directory). Resolves actor names via <see cref="CustomerUserDirectory"/> (decrypting
/// user name/email, display = name ?? email) and tenant/org names via the Directory tables. Backs the
/// audit-log changelog's actor/tenant/org name columns.
/// </summary>
public sealed class AuditLogDisplayDirectory : IAuditLogDisplayDirectory
{
    private readonly AppDbContext _db;
    private readonly TenantDataEncryptionService _encryption;
    public AuditLogDisplayDirectory(AppDbContext db, TenantDataEncryptionService encryption)
    {
        _db = db;
        _encryption = encryption;
    }

    public async Task<AuditLogDisplayMaps> ResolveAsync(
        IReadOnlyCollection<Guid> userIds, IReadOnlyCollection<Guid> tenantIds, IReadOnlyCollection<Guid> organizationIds,
        CancellationToken ct = default)
    {
        var users = new Dictionary<Guid, string>();
        if (userIds.Count > 0)
        {
            var idents = await CustomerUserDirectory.ResolveAsync(_db, _encryption, userIds, ct);
            foreach (var (id, ident) in idents)
            {
                var display = !string.IsNullOrWhiteSpace(ident.Name) ? ident.Name : ident.Email;
                if (!string.IsNullOrWhiteSpace(display)) users[id] = display!;
            }
        }

        var tenants = tenantIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Set<Tenant>().AsNoTracking().Where(t => tenantIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var orgs = organizationIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Set<Organization>().AsNoTracking().Where(o => organizationIds.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id, o => o.Name, ct);

        return new AuditLogDisplayMaps(users, tenants, orgs);
    }
}
