using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;

namespace OpenMercato.Modules.Auth.Cli;

/// <summary>
/// <c>list-users</c> — port of upstream auth cli.ts listUsers. Lists users (optionally filtered by
/// organization/tenant) with decrypted email/name, resolved role names and org/tenant names.
/// Usage: list-users [--organizationId &lt;id&gt;] [--tenantId &lt;id&gt;]
/// </summary>
public sealed class ListUsersCommand : ICliCommand
{
    public string Name => "list-users";
    public string Description => "List users: [--organizationId <id>] [--tenantId <id>]";

    private sealed record NameRow(Guid Id, string Name);

    public async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        var parsed = CliArgs.Parse(args);

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<EncryptionService>();

        var query = db.Set<User>().AsNoTracking().AsQueryable();
        if (parsed.Get("organizationId", "orgId", "org") is { } orgRaw && Guid.TryParse(orgRaw, out var orgId))
            query = query.Where(u => u.OrganizationId == orgId);
        if (parsed.Get("tenantId", "tenant") is { } tenantRaw && Guid.TryParse(tenantRaw, out var tenantId))
            query = query.Where(u => u.TenantId == tenantId);

        var users = await query.ToListAsync();
        if (users.Count == 0)
        {
            Console.WriteLine("No users found");
            return 0;
        }

        // Best-effort name lookups from the (plaintext) directory tables via raw SQL.
        var orgNames = (await SafeNamesAsync(db, "organizations")).ToDictionary(r => r.Id, r => r.Name);
        var tenantNames = (await SafeNamesAsync(db, "tenants")).ToDictionary(r => r.Id, r => r.Name);

        Console.WriteLine($"Found {users.Count} user(s):");
        Console.WriteLine();
        Console.WriteLine("ID                                   | Email                     | Name                | Organization         | Tenant               | Roles");
        Console.WriteLine("-------------------------------------|---------------------------|---------------------|----------------------|----------------------|------");

        foreach (var user in users)
        {
            var roleNames = await (from ur in db.Set<UserRole>()
                                   join r in db.Set<Role>() on ur.RoleId equals r.Id
                                   where ur.UserId == user.Id
                                   select r.Name).ToListAsync();
            var roles = roleNames.Count > 0 ? string.Join(", ", roleNames) : "None";
            var email = encryption.Decrypt(user.Email) ?? user.Email;
            var name = (user.Name is null ? null : encryption.Decrypt(user.Name)) ?? user.Name ?? "Unnamed";
            var org = user.OrganizationId is { } oid && orgNames.TryGetValue(oid, out var on) ? on : (user.OrganizationId?.ToString() ?? "N/A");
            var tenant = user.TenantId is { } tid && tenantNames.TryGetValue(tid, out var tn) ? tn : (user.TenantId?.ToString() ?? "N/A");

            Console.WriteLine($"{user.Id,-36} | {Trunc(email, 25),-25} | {Trunc(name, 19),-19} | {Trunc(org, 20),-20} | {Trunc(tenant, 20),-20} | {roles}");
        }
        return 0;
    }

    private static async Task<List<NameRow>> SafeNamesAsync(AppDbContext db, string table)
    {
        try
        {
            return await db.Database.SqlQueryRaw<NameRow>($"SELECT id AS \"Id\", name AS \"Name\" FROM {table}").ToListAsync();
        }
        catch
        {
            return new List<NameRow>();
        }
    }

    private static string Trunc(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
