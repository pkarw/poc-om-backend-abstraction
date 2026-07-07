using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;

namespace OpenMercato.Modules.Auth.Services;

/// <summary>
/// Login / password / session / reset business logic, mirroring upstream
/// services/authService.ts. Constant-time password verify is delegated to <see cref="PasswordHasher"/>
/// (fixed dummy hash, issue #2242). Opaque tokens are HMAC-hashed via <see cref="TokenHasher"/> and
/// only the hash is persisted. Password-reset confirm is an atomic compare-and-set (replay-safe) and
/// deletes all of the user's sessions.
///
/// Email lookup keys on the deterministic <c>email_hash</c> (not on the non-deterministic ciphertext):
/// <see cref="EncryptionService.EmailHashLookupValues"/> yields both the v2 (HMAC) and legacy
/// candidates so reads match either format (spec 05 R47). Returned users have <see cref="User.Email"/>
/// decrypted in-place; they are read <c>AsNoTracking</c> so this never writes plaintext back.
///
/// This class is constructed directly by the route handlers from DI primitives (AppDbContext +
/// the singleton crypto services), so it does not require a DI registration of its own.
/// </summary>
public sealed class AuthService
{
    private readonly AppDbContext _db;
    private readonly PasswordHasher _passwords;
    private readonly TokenHasher _tokens;
    private readonly EncryptionService _encryption;

    public AuthService(AppDbContext db, PasswordHasher passwords, TokenHasher tokens, EncryptionService encryption)
    {
        _db = db;
        _passwords = passwords;
        _tokens = tokens;
        _encryption = encryption;
    }

    /// <summary>Single user matched by email (any tenant), or null. Email is decrypted in-place.</summary>
    public async Task<User?> FindUserByEmailAsync(string email, CancellationToken ct = default)
    {
        var lookups = _encryption.EmailHashLookupValues(email);
        var user = await _db.Set<User>().AsNoTracking()
            .Where(u => u.DeletedAt == null && u.EmailHash != null && lookups.Contains(u.EmailHash!))
            .FirstOrDefaultAsync(ct);
        return Decrypt(user);
    }

    /// <summary>All users matched by email (across tenants). Emails decrypted in-place.</summary>
    public async Task<List<User>> FindUsersByEmailAsync(string email, CancellationToken ct = default)
    {
        var lookups = _encryption.EmailHashLookupValues(email);
        var users = await _db.Set<User>().AsNoTracking()
            .Where(u => u.DeletedAt == null && u.EmailHash != null && lookups.Contains(u.EmailHash!))
            .ToListAsync(ct);
        foreach (var u in users) Decrypt(u);
        return users;
    }

    /// <summary>Single user matched by email within a specific tenant, or null.</summary>
    public async Task<User?> FindUserByEmailAndTenantAsync(string email, Guid tenantId, CancellationToken ct = default)
    {
        var lookups = _encryption.EmailHashLookupValues(email);
        var user = await _db.Set<User>().AsNoTracking()
            .Where(u => u.TenantId == tenantId && u.DeletedAt == null && u.EmailHash != null && lookups.Contains(u.EmailHash!))
            .FirstOrDefaultAsync(ct);
        return Decrypt(user);
    }

    /// <summary>Constant-time password verification (false when user/hash absent, still runs a compare).</summary>
    public bool VerifyPassword(User? user, string password) => _passwords.Verify(user, password);

    /// <summary>Set last_login_at without flushing other tracked entities.</summary>
    public async Task UpdateLastLoginAtAsync(User user, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        await _db.Set<User>().Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastLoginAt, now), ct);
        user.LastLoginAt = now;
    }

    /// <summary>Names of the user's non-deleted roles within the resolved tenant (soft-deleted roles dropped).</summary>
    public async Task<string[]> GetUserRolesAsync(User user, Guid? tenantId, CancellationToken ct = default)
    {
        var resolved = tenantId ?? user.TenantId;
        if (resolved is null) return Array.Empty<string>();
        var names = await (
            from ur in _db.Set<UserRole>().AsNoTracking()
            where ur.UserId == user.Id && ur.DeletedAt == null
            join r in _db.Set<Role>().AsNoTracking() on ur.RoleId equals r.Id
            where r.TenantId == resolved && r.DeletedAt == null
            select r.Name).ToListAsync(ct);
        return names.Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
    }

    /// <summary>Create a session row (stored token = HMAC hash); returns the entity + raw refresh token.</summary>
    public async Task<(Session session, string rawToken)> CreateSessionAsync(User user, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        var raw = _tokens.Generate();
        var session = new Session
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = _tokens.Hash(raw),
            ExpiresAt = expiresAt,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Set<Session>().Add(session);
        await _db.SaveChangesAsync(ct);
        return (session, raw);
    }

    public async Task DeleteSessionByTokenAsync(string rawToken, CancellationToken ct = default)
    {
        var hashed = _tokens.Hash(rawToken);
        await _db.Set<Session>().Where(s => s.Token == hashed).ExecuteDeleteAsync(ct);
    }

    public async Task DeleteSessionByIdAsync(Guid sessionId, CancellationToken ct = default)
    {
        await _db.Set<Session>().Where(s => s.Id == sessionId).ExecuteDeleteAsync(ct);
    }

    public async Task DeleteAllUserSessionsAsync(Guid userId, CancellationToken ct = default)
    {
        await _db.Set<Session>().Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
    }

    /// <summary>Exchange a raw session/refresh token for (user, roles, session), or null when invalid/expired.</summary>
    public async Task<(User user, string[] roles, Session session)?> RefreshFromSessionTokenAsync(string rawToken, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var hashed = _tokens.Hash(rawToken);
        var session = await _db.Set<Session>().AsNoTracking().FirstOrDefaultAsync(s => s.Token == hashed, ct);
        if (session is null || session.ExpiresAt <= now) return null;
        var user = await _db.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == session.UserId && u.DeletedAt == null, ct);
        if (user is null) return null;
        Decrypt(user);
        var roles = await GetUserRolesAsync(user, user.TenantId, ct);
        return (user, roles, session);
    }

    /// <summary>Create a reset token for a known user (existence-hiding is the caller's concern). Null when unknown.</summary>
    public async Task<(User user, string rawToken)?> RequestPasswordResetAsync(string email, CancellationToken ct = default)
    {
        var user = await FindUserByEmailAsync(email, ct);
        if (user is null) return null;
        var raw = _tokens.Generate();
        var row = new PasswordReset
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Token = _tokens.Hash(raw),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(60),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Set<PasswordReset>().Add(row);
        await _db.SaveChangesAsync(ct);
        return (user, raw);
    }

    /// <summary>Atomic compare-and-set confirm: consume the token, set the new password, revoke all sessions.</summary>
    public async Task<User?> ConfirmPasswordResetAsync(string rawToken, string newPassword, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var hashed = _tokens.Hash(rawToken);
        var row = await _db.Set<PasswordReset>().AsNoTracking().FirstOrDefaultAsync(r => r.Token == hashed, ct);
        if (row is null || (row.UsedAt != null && row.UsedAt <= now) || row.ExpiresAt <= now) return null;

        // Atomic compare-and-set: only mark used when still unused — prevents token replay under concurrency.
        var affected = await _db.Set<PasswordReset>()
            .Where(r => r.Id == row.Id && r.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.UsedAt, now), ct);
        if (affected == 0) return null;

        var user = await _db.Set<User>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == row.UserId && u.DeletedAt == null, ct);
        if (user is null) return null;
        Decrypt(user);

        var passwordHash = _passwords.Hash(newPassword);
        await _db.Set<User>().Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.PasswordHash, passwordHash)
                .SetProperty(u => u.UpdatedAt, now), ct);
        user.PasswordHash = passwordHash;

        await DeleteAllUserSessionsAsync(user.Id, ct);
        return user;
    }

    /// <summary>Decrypt the stored email ciphertext in-place; falls back to the raw value if not decryptable.</summary>
    private User? Decrypt(User? user)
    {
        if (user is null) return null;
        user.Email = _encryption.Decrypt(user.Email) ?? user.Email;
        return user;
    }
}
