using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Validators;

namespace OpenMercato.Modules.Auth.Cli;

/// <summary>
/// <c>add-user</c> — port of upstream auth cli.ts addUser. Creates a confirmed user in an
/// organization (resolving the tenant from the org row) with a bcrypt password and optional roles.
/// Usage: add-user --email &lt;email&gt; --password &lt;password&gt; --organizationId &lt;id&gt; [--roles a,b]
/// </summary>
public sealed class AddUserCommand : ICliCommand
{
    public string Name => "add-user";
    public string Description => "Create a user in an organization: --email --password --organizationId [--roles a,b]";

    public async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var parsed = CliArgs.Parse(args);
        var email = parsed.Get("email");
        var password = parsed.Get("password");
        var orgIdRaw = parsed.Get("organizationId", "orgId", "org");
        var rolesCsv = parsed.Get("roles") ?? string.Empty;

        if (email is null || password is null || orgIdRaw is null)
        {
            Console.Error.WriteLine("Usage: add-user --email <email> --password <password> --organizationId <id> [--roles customer,employee]");
            return 1;
        }
        if (!Guid.TryParse(orgIdRaw, out var organizationId))
        {
            Console.Error.WriteLine($"Invalid --organizationId: {orgIdRaw}");
            return 1;
        }
        if (!PasswordPolicy.IsValid(password))
        {
            Console.Error.WriteLine(PasswordPolicy.Message);
            return 1;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var encryption = scope.ServiceProvider.GetRequiredService<EncryptionService>();

        // Resolve the tenant from the organizations table via raw SQL (auth does not reference the
        // directory entity type; the shared DB owns the organizations table).
        var tenantIds = await db.Database
            .SqlQueryRaw<Guid>("SELECT tenant_id AS \"Value\" FROM organizations WHERE id = {0}", organizationId)
            .ToListAsync();
        if (tenantIds.Count == 0)
        {
            Console.Error.WriteLine("Organization not found");
            return 1;
        }
        var tenantId = tenantIds[0];
        var now = DateTimeOffset.UtcNow;

        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OrganizationId = organizationId,
            Email = encryption.Encrypt(email)!,
            EmailHash = encryption.ComputeEmailHash(email),
            PasswordHash = hasher.Hash(password),
            IsConfirmed = true,
            CreatedAt = now,
        };
        db.Set<User>().Add(user);
        await db.SaveChangesAsync();

        var names = rolesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var roleName in names)
        {
            var role = await db.Set<Role>().FirstOrDefaultAsync(r => r.Name == roleName && r.TenantId == tenantId);
            if (role is null)
            {
                role = new Role { Id = Guid.NewGuid(), Name = roleName, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow };
                db.Set<Role>().Add(role);
                await db.SaveChangesAsync();
            }
            db.Set<UserRole>().Add(new UserRole { Id = Guid.NewGuid(), UserId = user.Id, RoleId = role.Id, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        Console.WriteLine($"User created with id {user.Id}");
        return 0;
    }
}
