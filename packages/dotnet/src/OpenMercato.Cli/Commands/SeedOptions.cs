using OpenMercato.Core.Modules;
using OpenMercato.Modules.Directory.Seeding;

namespace OpenMercato.Cli.Commands;

/// <summary>
/// Shared resolution of the <see cref="SetupInitialTenantOptions"/> from CLI args + OM_INIT_* env,
/// so <c>init</c>, <c>seed</c> and <c>greenfield</c> all produce the identical Acme dataset.
/// Defaults: Acme Corp / superadmin@acme.com / secret / acme.
/// </summary>
internal static class SeedOptions
{
    public static SetupInitialTenantOptions Resolve(string[] args)
    {
        var parsed = CliArgs.Parse(args);
        return new SetupInitialTenantOptions
        {
            OrgName = parsed.Get("orgName", "name") ?? Env("OM_INIT_ORG_NAME") ?? InitialTenantSeeder.DefaultOrgName,
            Email = parsed.Get("email") ?? Env("OM_INIT_SUPERADMIN_EMAIL") ?? InitialTenantSeeder.DefaultSuperadminEmail,
            Password = parsed.Get("password") ?? Env("OM_INIT_SUPERADMIN_PASSWORD") ?? InitialTenantSeeder.DefaultPassword,
            OrgSlug = parsed.Get("orgSlug", "slug") ?? Env("OM_INIT_ORG_SLUG") ?? InitialTenantSeeder.DefaultOrgSlug,
        };
    }

    private static string? Env(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
