using OpenMercato.Core.Configuration;
using OpenMercato.Modules.Auth.Api;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Validators;
using Xunit;

namespace OpenMercato.Tests;

/// <summary>Focused tests for the core auth flow slice (JWT round-trip, password policy,
/// feature aggregation, locale set) — the pieces observable through login/profile/features/locale.</summary>
public class AuthJwtRoundTripTests
{
    private static JwtService NewService() =>
        new(new AppConfig("postgres://x", "redis://x", "redis", "redis://x", "test-secret-abc", 8080));

    [Fact]
    public void Sign_then_verify_round_trips_claims()
    {
        var jwt = NewService();
        var sid = Guid.NewGuid().ToString();
        var token = jwt.Sign(new StaffJwtClaims
        {
            Sub = "11111111-1111-1111-1111-111111111111",
            Sid = sid,
            TenantId = "22222222-2222-2222-2222-222222222222",
            OrgId = "33333333-3333-3333-3333-333333333333",
            Email = "user@example.com",
            Roles = new[] { "admin", "employee" },
        });

        Assert.True(jwt.TryVerify(token, out var claims));
        Assert.Equal("11111111-1111-1111-1111-111111111111", claims.Sub);
        Assert.Equal(sid, claims.Sid);
        Assert.Equal("22222222-2222-2222-2222-222222222222", claims.TenantId);
        Assert.Equal("33333333-3333-3333-3333-333333333333", claims.OrgId);
        Assert.Equal("user@example.com", claims.Email);
        Assert.Equal(new[] { "admin", "employee" }, claims.Roles);
    }

    [Fact]
    public void Tampered_token_fails_verification()
    {
        var jwt = NewService();
        var token = jwt.Sign(new StaffJwtClaims { Sub = "abc" });
        var tampered = token[..^2] + (token[^1] == 'a' ? "bb" : "aa");
        Assert.False(jwt.TryVerify(tampered, out _));
    }

    [Fact]
    public void Different_secret_cannot_verify()
    {
        var signed = NewService().Sign(new StaffJwtClaims { Sub = "abc", Sid = "s" });
        var other = new JwtService(new AppConfig("p", "r", "redis", "r", "a-totally-different-secret", 8080));
        Assert.False(other.TryVerify(signed, out _));
    }
}

public class AuthPasswordPolicyTests
{
    [Theory]
    [InlineData("Abcd1!")]      // 6 chars, upper, digit, special
    [InlineData("Str0ng#Pass")]
    public void Accepts_compliant_passwords(string password)
    {
        Assert.True(PasswordPolicy.IsValid(password));
    }

    [Theory]
    [InlineData("abc12")]        // too short + no upper/special
    [InlineData("abcdef")]       // no upper/digit/special
    [InlineData("Abcdef")]       // no digit/special
    [InlineData("Abcdef1")]      // no special
    [InlineData("abcdef1!")]     // no uppercase
    public void Rejects_noncompliant_passwords(string password)
    {
        Assert.False(PasswordPolicy.IsValid(password));
    }
}

public class FeatureCatalogTests
{
    [Fact]
    public void Dedupes_by_id_derives_module_and_sorts()
    {
        var (items, modules) = FeatureCatalog.Build(
            new[] { "auth.users.list", "directory.tenants.view", "auth.users.list", "auth.acl.manage" },
            new[] { ("auth", "auth"), ("directory", "directory") });

        Assert.Equal(3, items.Count); // duplicate auth.users.list collapsed
        // sorted by (module, id): auth.acl.manage, auth.users.list, directory.tenants.view
        Assert.Equal("auth.acl.manage", items[0].Id);
        Assert.Equal("auth", items[0].Module);
        Assert.Equal("auth.users.list", items[1].Id);
        Assert.Equal("directory.tenants.view", items[2].Id);
        Assert.Equal("directory", items[2].Module);
        // title falls back to id
        Assert.Equal("auth.acl.manage", items[0].Title);
        Assert.Equal(2, modules.Count);
    }
}

public class LocaleSetTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("pl")]
    public void Supports_configured_locales(string locale)
    {
        Assert.Contains(locale, LocaleRoutes.SupportedLocales);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("EN")]
    [InlineData("")]
    public void Rejects_other_locales(string locale)
    {
        Assert.DoesNotContain(locale, LocaleRoutes.SupportedLocales);
    }
}
