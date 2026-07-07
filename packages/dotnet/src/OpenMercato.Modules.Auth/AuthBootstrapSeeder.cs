using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;

namespace OpenMercato.Modules.Auth;

/// <summary>
/// Env-gated, idempotent superadmin bootstrap. When OM_INIT_SUPERADMIN_EMAIL and
/// OM_INIT_SUPERADMIN_PASSWORD are set AND no users exist, seeds a minimal, login-ready
/// superadmin: a User (encrypted email + email_hash + bcrypt password, is_confirmed=true),
/// a tenant-scoped 'superadmin' Role, a UserRole link, and a RoleAcl with is_super_admin=true.
///
/// SCOPING: the directory module (tenants/organizations tables) is not ported yet, so this
/// seeds only into auth tables. A tenant id is generated and shared by the user + role + acl,
/// which is sufficient for login and superadmin RBAC. Runs from OpenMercato.Api/Program.cs after
/// MigrateAsync.
/// </summary>
public static class AuthBootstrapSeeder
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        var email = Environment.GetEnvironmentVariable("OM_INIT_SUPERADMIN_EMAIL");
        var password = Environment.GetEnvironmentVariable("OM_INIT_SUPERADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return; // not configured — nothing to do

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var encryption = scope.ServiceProvider.GetRequiredService<EncryptionService>();

        if (await db.Set<User>().AnyAsync(ct))
            return; // idempotent — users already exist

        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();

        var user = new User
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OrganizationId = null,
            Email = encryption.Encrypt(email)!,
            EmailHash = encryption.ComputeEmailHash(email),
            Name = null,
            PasswordHash = hasher.Hash(password),
            IsConfirmed = true,
            CreatedAt = now,
        };

        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "superadmin",
            TenantId = tenantId,
            CreatedAt = now,
        };

        var userRole = new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            RoleId = role.Id,
            CreatedAt = now,
        };

        var roleAcl = new RoleAcl
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            TenantId = tenantId,
            FeaturesJson = null,
            IsSuperAdmin = true,
            OrganizationsJson = null,
            CreatedAt = now,
        };

        // ConfigureModel maps columns only (no FK relationships), so EF cannot order inserts by
        // dependency. Persist parents (user, role) before children (user_role, role_acl) to satisfy
        // the DB-level FKs created by the raw-SQL migration.
        db.Set<User>().Add(user);
        db.Set<Role>().Add(role);
        await db.SaveChangesAsync(ct);

        db.Set<UserRole>().Add(userRole);
        db.Set<RoleAcl>().Add(roleAcl);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Auth bootstrap: seeded superadmin user {UserId} (email {Email}), role 'superadmin' {RoleId}, tenant {TenantId}.",
            user.Id, email, role.Id, tenantId);
    }
}
