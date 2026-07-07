using OpenMercato.Modules.Auth.Data;

namespace OpenMercato.Modules.Auth.Security;

/// <summary>
/// bcrypt (cost 10) password hashing, mirroring upstream AuthService.
/// Verification is constant-time even when the user or the stored hash is missing:
/// it always runs a bcrypt compare against a fixed dummy hash so unknown-email and
/// wrong-password latencies are indistinguishable (contract #2242 / spec 05 R8).
/// </summary>
public sealed class PasswordHasher
{
    /// <summary>Fixed dummy hash from the contract — used for the anti-timing-oracle compare.</summary>
    public const string DummyHash = "$2b$10$OcZrhmZpIzJOjkfwUrk7d.Nl0eHNzOvalBcBlt5Ran.4lj8R3HZg6";

    private const int WorkFactor = 10;

    /// <summary>Hash a password with bcrypt cost 10 in the <c>$2b$</c> format.</summary>
    public string Hash(string password) =>
        BCrypt.Net.BCrypt.HashPassword(password, BCrypt.Net.BCrypt.GenerateSalt(WorkFactor, 'b'));

    /// <summary>
    /// Verify a candidate password against a user's stored hash. When the user is null
    /// or has no hash, the compare still runs against <see cref="DummyHash"/> and returns
    /// false, keeping timing uniform.
    /// </summary>
    public bool Verify(User? user, string password) => Verify(user?.PasswordHash, password);

    /// <summary>Verify against a raw stored hash (null-safe, constant-time).</summary>
    public bool Verify(string? storedHash, string password)
    {
        var hasHash = !string.IsNullOrEmpty(storedHash);
        var hashToCheck = hasHash ? storedHash! : DummyHash;
        bool matches;
        try
        {
            matches = BCrypt.Net.BCrypt.Verify(password, hashToCheck);
        }
        catch
        {
            matches = false;
        }
        return hasHash && matches;
    }
}
