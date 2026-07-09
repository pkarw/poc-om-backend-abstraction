using System.Text.Json;
using OpenMercato.Core.Configuration;
using OpenMercato.Modules.Auth.Api;
using OpenMercato.Modules.Auth.Data;
using OpenMercato.Modules.Auth.Security;
using OpenMercato.Modules.Auth.Services;
using Xunit;

namespace OpenMercato.Tests.Auth;

public class RolesUsersEnvelopeTests
{
    private static readonly TenantDataEncryptionService Enc =
        new(new DerivedKmsService("test-secret"));

    private static string Json(object value) => JsonSerializer.Serialize(value);

    // ---- roles GET empty-envelope quirk (test-pinned) -----------------------------------------

    [Fact]
    public async Task Roles_unauthenticated_returns_empty_envelope_without_isSuperAdmin()
    {
        using var db = AuthTestDb.Create();
        var rbac = new RbacService(db);
        var result = await RolesRouteGroup.ListAsync(db, rbac, auth: null,
            query: new RolesRouteGroup.RolesQuery(null, 1, 50, null, null));

        Assert.Equal("{\"items\":[],\"total\":0,\"totalPages\":1}", Json(result));
    }

    [Fact]
    public async Task Roles_invalid_query_returns_empty_envelope()
    {
        using var db = AuthTestDb.Create();
        var rbac = new RbacService(db);
        var auth = new AuthContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "a@b.c", Array.Empty<string>());

        // A Zod parse failure is modelled as a null query; the handler must still 200 with the empty envelope.
        var result = await RolesRouteGroup.ListAsync(db, rbac, auth, query: null);
        Assert.Equal("{\"items\":[],\"total\":0,\"totalPages\":1}", Json(result));
    }

    [Fact]
    public async Task Roles_authenticated_lists_tenant_roles_with_isSuperAdmin_flag()
    {
        using var db = AuthTestDb.Create();
        var tenant = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        db.Set<Role>().Add(new Role { Id = roleId, Name = "editor", TenantId = tenant, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var rbac = new RbacService(db);
        var auth = new AuthContext(Guid.NewGuid(), Guid.NewGuid(), tenant, null, "a@b.c", Array.Empty<string>());

        var result = await RolesRouteGroup.ListAsync(db, rbac, auth,
            new RolesRouteGroup.RolesQuery(null, 1, 50, null, null));

        var json = Json(result);
        Assert.Contains("\"isSuperAdmin\":false", json);
        Assert.Contains("\"total\":1", json);
        Assert.Contains("\"name\":\"editor\"", json);
        Assert.Contains($"\"tenantId\":\"{tenant}\"", json);
    }

    [Fact]
    public async Task Roles_non_super_admin_hides_superadmin_named_role()
    {
        using var db = AuthTestDb.Create();
        var tenant = Guid.NewGuid();
        db.Set<Role>().Add(new Role { Id = Guid.NewGuid(), Name = "superadmin", TenantId = tenant, CreatedAt = DateTimeOffset.UtcNow });
        db.Set<Role>().Add(new Role { Id = Guid.NewGuid(), Name = "editor", TenantId = tenant, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var rbac = new RbacService(db);
        var auth = new AuthContext(Guid.NewGuid(), Guid.NewGuid(), tenant, null, "a@b.c", Array.Empty<string>());

        var result = await RolesRouteGroup.ListAsync(db, rbac, auth,
            new RolesRouteGroup.RolesQuery(null, 1, 50, null, null));

        var json = Json(result);
        Assert.Contains("\"total\":1", json);
        Assert.DoesNotContain("superadmin", json);
    }

    // ---- users GET empty-envelope quirk -------------------------------------------------------

    [Fact]
    public async Task Users_unauthenticated_returns_empty_envelope_without_isSuperAdmin()
    {
        using var db = AuthTestDb.Create();
        var rbac = new RbacService(db);
        var result = await UsersRouteGroup.ListAsync(db, rbac, Enc, auth: null,
            query: new UsersRouteGroup.UsersQuery(null, 1, 50, null, null, null, null));

        Assert.Equal("{\"items\":[],\"total\":0,\"totalPages\":1}", Json(result));
    }

    [Fact]
    public async Task Users_invalid_query_returns_empty_envelope()
    {
        using var db = AuthTestDb.Create();
        var rbac = new RbacService(db);
        var auth = new AuthContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "a@b.c", Array.Empty<string>());
        var result = await UsersRouteGroup.ListAsync(db, rbac, Enc, auth, query: null);
        Assert.Equal("{\"items\":[],\"total\":0,\"totalPages\":1}", Json(result));
    }

    [Fact]
    public async Task Users_non_super_admin_without_tenant_returns_empty_envelope_with_flag()
    {
        using var db = AuthTestDb.Create();
        var rbac = new RbacService(db);
        // No tenant on the actor and not a super admin => empty envelope carrying isSuperAdmin:false.
        var auth = new AuthContext(Guid.NewGuid(), Guid.NewGuid(), TenantId: null, null, "a@b.c", Array.Empty<string>());
        var result = await UsersRouteGroup.ListAsync(db, rbac, Enc, auth,
            new UsersRouteGroup.UsersQuery(null, 1, 50, null, null, null, null));

        Assert.Equal("{\"items\":[],\"total\":0,\"totalPages\":1,\"isSuperAdmin\":false}", Json(result));
    }
}
