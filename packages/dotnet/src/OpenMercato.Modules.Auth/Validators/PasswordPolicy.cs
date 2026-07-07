using System.Text.RegularExpressions;

namespace OpenMercato.Modules.Auth.Validators;

/// <summary>
/// Password policy, mirroring packages/shared/src/lib/auth/passwordPolicy.ts. Defaults:
/// minLength 6 + require digit/uppercase/special, each toggleable via
/// OM_PASSWORD_MIN_LENGTH / OM_PASSWORD_REQUIRE_{DIGIT,UPPERCASE,SPECIAL} (or their NEXT_PUBLIC_
/// variants). Violation message is the exact upstream string.
/// </summary>
public static class PasswordPolicy
{
    public const string Message = "Password does not meet the requirements.";

    public sealed record Policy(int MinLength, bool RequireDigit, bool RequireUppercase, bool RequireSpecial);

    public static Policy Current() => new(
        MinLength: ReadInt("OM_PASSWORD_MIN_LENGTH", 6, 1),
        RequireDigit: ReadBool("OM_PASSWORD_REQUIRE_DIGIT", true),
        RequireUppercase: ReadBool("OM_PASSWORD_REQUIRE_UPPERCASE", true),
        RequireSpecial: ReadBool("OM_PASSWORD_REQUIRE_SPECIAL", true));

    /// <summary>True when the password satisfies the active policy.</summary>
    public static bool IsValid(string password, Policy? policy = null)
    {
        var p = policy ?? Current();
        if (password.Length < p.MinLength) return false;
        if (p.RequireDigit && !Regex.IsMatch(password, "[0-9]")) return false;
        if (p.RequireUppercase && !Regex.IsMatch(password, "[A-Z]")) return false;
        if (p.RequireSpecial && !Regex.IsMatch(password, "[^A-Za-z0-9]")) return false;
        return true;
    }

    private static int ReadInt(string key, int fallback, int min)
    {
        var raw = ReadEnv(key);
        if (raw is null || !int.TryParse(raw, out var parsed)) return fallback;
        return Math.Max(min, parsed);
    }

    private static bool ReadBool(string key, bool fallback)
    {
        var raw = ReadEnv(key);
        if (raw is null) return fallback;
        var token = AuthApiBooleanToken(raw);
        return token ?? fallback;
    }

    private static bool? AuthApiBooleanToken(string raw)
    {
        var t = raw.Trim().ToLowerInvariant();
        if (t is "1" or "true" or "yes" or "y" or "on" or "enable" or "enabled") return true;
        if (t is "0" or "false" or "no" or "n" or "off" or "disable" or "disabled") return false;
        return null;
    }

    private static string? ReadEnv(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        value = Environment.GetEnvironmentVariable("NEXT_PUBLIC_" + key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
