using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth.Api;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;

namespace OpenMercato.Modules.Auth;

/// <summary>
/// The auth module (upstream packages/core/src/modules/auth). Owns users, roles, sessions,
/// ACLs, password resets, consents and sidebar customization. The byte-exact DDL is created by
/// the raw-SQL migration in OpenMercato.Api/Migrations (AddAuthModule); ConfigureModel here only
/// wires the runtime EF model (table/column names) for querying.
/// </summary>
public sealed class AuthModule : IModule
{
    public string Id => "auth";

    /// <summary>The 8 ACL feature ids from the contract (acl.ts).</summary>
    public IReadOnlyList<string> AclFeatures { get; } = new[]
    {
        "auth.users.list",
        "auth.users.create",
        "auth.users.edit",
        "auth.users.delete",
        "auth.roles.list",
        "auth.roles.manage",
        "auth.acl.manage",
        "auth.sidebar.manage",
    };

    public void ConfigureServices(IServiceCollection services)
    {
        // Foundation crypto/JWT/session primitives (stateless, read env/AppConfig at construction).
        services.AddSingleton<PasswordHasher>();
        services.AddSingleton<TokenHasher>();
        services.AddSingleton<JwtService>();
        services.AddSingleton<EncryptionService>();

        // Domain services provided by the domain slices.
        services.AddScoped<IRbacService, OpenMercato.Modules.Auth.Services.RbacService>();
        services.AddScoped<OpenMercato.Modules.Auth.Services.AuthService>();
        services.AddScoped<OpenMercato.Modules.Auth.Services.SidebarPreferencesService>();
    }

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id).HasName("users_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.Email).HasColumnName("email").IsRequired();
            e.Property(x => x.EmailHash).HasColumnName("email_hash");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.PasswordHash).HasColumnName("password_hash");
            e.Property(x => x.IsConfirmed).HasColumnName("is_confirmed");
            e.Property(x => x.LastLoginAt).HasColumnName("last_login_at").HasColumnType("timestamptz");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasKey(x => x.Id).HasName("roles_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<UserRole>(e =>
        {
            e.ToTable("user_roles");
            e.HasKey(x => x.Id).HasName("user_roles_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.RoleId).HasColumnName("role_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<Session>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id).HasName("sessions_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Token).HasColumnName("token").IsRequired();
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.LastUsedAt).HasColumnName("last_used_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<PasswordReset>(e =>
        {
            e.ToTable("password_resets");
            e.HasKey(x => x.Id).HasName("password_resets_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Token).HasColumnName("token").IsRequired();
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamptz");
            e.Property(x => x.UsedAt).HasColumnName("used_at").HasColumnType("timestamptz");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<RoleAcl>(e =>
        {
            e.ToTable("role_acls");
            e.HasKey(x => x.Id).HasName("role_acls_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.RoleId).HasColumnName("role_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.FeaturesJson).HasColumnName("features_json").HasColumnType("jsonb");
            e.Property(x => x.IsSuperAdmin).HasColumnName("is_super_admin");
            e.Property(x => x.OrganizationsJson).HasColumnName("organizations_json").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<UserAcl>(e =>
        {
            e.ToTable("user_acls");
            e.HasKey(x => x.Id).HasName("user_acls_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(x => x.FeaturesJson).HasColumnName("features_json").HasColumnType("jsonb");
            e.Property(x => x.IsSuperAdmin).HasColumnName("is_super_admin");
            e.Property(x => x.OrganizationsJson).HasColumnName("organizations_json").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<UserSidebarPreference>(e =>
        {
            e.ToTable("user_sidebar_preferences");
            e.HasKey(x => x.Id).HasName("user_sidebar_preferences_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.Locale).HasColumnName("locale").IsRequired();
            e.Property(x => x.SettingsJson).HasColumnName("settings_json").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<RoleSidebarPreference>(e =>
        {
            e.ToTable("role_sidebar_preferences");
            e.HasKey(x => x.Id).HasName("role_sidebar_preferences_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.RoleId).HasColumnName("role_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Locale).HasColumnName("locale").IsRequired();
            e.Property(x => x.SettingsJson).HasColumnName("settings_json").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<SidebarVariant>(e =>
        {
            e.ToTable("sidebar_variants");
            e.HasKey(x => x.Id).HasName("sidebar_variants_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.Locale).HasColumnName("locale").IsRequired();
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.SettingsJson).HasColumnName("settings_json").HasColumnType("jsonb");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<UserConsent>(e =>
        {
            e.ToTable("user_consents");
            e.HasKey(x => x.Id).HasName("user_consents_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.ConsentType).HasColumnName("consent_type").IsRequired();
            e.Property(x => x.IsGranted).HasColumnName("is_granted");
            e.Property(x => x.GrantedAt).HasColumnName("granted_at").HasColumnType("timestamptz");
            e.Property(x => x.WithdrawnAt).HasColumnName("withdrawn_at").HasColumnType("timestamptz");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.IpAddress).HasColumnName("ip_address");
            e.Property(x => x.IntegrityHash).HasColumnName("integrity_hash");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });
    }

    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        // Discover every IAuthRouteGroup in the Auth assembly and let each map its routes.
        // Domain slices add route files without editing this method.
        var groupType = typeof(IAuthRouteGroup);
        var implementations = typeof(AuthModule).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && groupType.IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in implementations)
        {
            if (Activator.CreateInstance(type) is IAuthRouteGroup group)
                group.Map(routes);
        }
    }
}
