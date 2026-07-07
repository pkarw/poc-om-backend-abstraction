using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Directory.Seeding;

namespace OpenMercato.Cli.Commands;

/// <summary>Runs the OM-parity <see cref="InitialTenantSeeder"/> and prints a human summary.</summary>
internal static class SeedRunner
{
    public static async Task<int> RunAsync(IServiceProvider services, SetupInitialTenantOptions options)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<ModuleRegistry>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var encryption = scope.ServiceProvider.GetRequiredService<EncryptionService>();

        var result = await InitialTenantSeeder.SetupInitialTenantAsync(db, registry, hasher, encryption, options);

        if (result.ReusedExistingUser)
        {
            Console.WriteLine("Existing initial user detected — reused tenant/organization and ensured roles/ACLs.");
            Console.WriteLine($"Tenant {result.TenantId}, organization {result.OrganizationId}.");
            return 0;
        }

        foreach (var user in result.Users)
            Console.WriteLine($"Created user {user.Email} [{string.Join(",", user.Roles)}]");
        Console.WriteLine($"Setup complete: tenant {result.TenantId}, organization {result.OrganizationId}.");
        return 0;
    }
}

/// <summary>Built-in <c>seed</c> — runs the OM-parity seeder only (idempotent; no migration).</summary>
public sealed class SeedCommand : ICliCommand
{
    public string Name => "seed";
    public string Description => "Seed the initial Acme tenant/org/roles/users/ACLs (idempotent)";

    public Task<int> RunAsync(string[] args, IServiceProvider services) =>
        SeedRunner.RunAsync(services, SeedOptions.Resolve(args));
}
