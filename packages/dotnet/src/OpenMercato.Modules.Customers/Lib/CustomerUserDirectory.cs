using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;

namespace OpenMercato.Modules.Customers.Lib;

/// <summary>
/// Resolves author/owner display identity (name + email) for a set of user ids — the .NET stand-in for
/// upstream's <c>interactionReadModel</c> author hydration. Auth user name + email are encrypted at rest,
/// so this reuses the auth <see cref="TenantDataEncryptionService"/> (the same decrypt-for-display path
/// the dashboards viewer chip uses). Customers already references the Auth module, so this is an in-process
/// join, not a cross-service call.
/// </summary>
public static class CustomerUserDirectory
{
    public readonly record struct UserIdentity(string? Name, string? Email);

    private static readonly IReadOnlyDictionary<Guid, UserIdentity> Empty = new Dictionary<Guid, UserIdentity>();

    /// <summary>
    /// Load + decrypt the given users, returning id → (name, email). Missing/empty ids are skipped.
    /// Best-effort: a null <paramref name="encryption"/> (e.g. a minimal test host that doesn't register
    /// the auth encryption service) yields an empty map, so author hydration degrades to null rather than
    /// failing the request.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, UserIdentity>> ResolveAsync(
        AppDbContext db, TenantDataEncryptionService? encryption, IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        if (encryption is null) return Empty;
        var ids = userIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0) return Empty;

        var users = await db.Set<User>().AsNoTracking()
            .Where(u => ids.Contains(u.Id) && u.DeletedAt == null)
            .ToListAsync(ct);

        var map = new Dictionary<Guid, UserIdentity>(users.Count);
        foreach (var u in users)
        {
            encryption.DecryptUserInPlace(db, u); // AsNoTracking — safe to mutate for display, never persisted
            var name = string.IsNullOrWhiteSpace(u.Name) ? null : u.Name!.Trim();
            var email = string.IsNullOrWhiteSpace(u.Email) ? null : u.Email!.Trim();
            map[u.Id] = new UserIdentity(name, email);
        }
        return map;
    }

    /// <summary>Collect the distinct non-null user ids from a set of nullable guids (author/owner columns).</summary>
    public static IEnumerable<Guid> Ids(params IEnumerable<Guid?>[] sources) =>
        sources.SelectMany(s => s).Where(g => g is { } && g.Value != Guid.Empty).Select(g => g!.Value);
}
