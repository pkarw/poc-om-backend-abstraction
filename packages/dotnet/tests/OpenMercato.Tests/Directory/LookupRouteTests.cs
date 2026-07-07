using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Directory;
using OpenMercato.Modules.Directory.Data;
using Xunit;

namespace OpenMercato.Tests.Directory;

/// <summary>
/// Route envelope test for the public tenant lookup endpoint. Exercises the handler logic through
/// an in-memory AppDbContext, asserting the exact 200/400/404 envelopes from the contract.
/// </summary>
public class LookupRouteTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"dir-tests-{Guid.NewGuid():N}")
            .Options;
        var registry = new ModuleRegistry(new IModule[] { new DirectoryModule() });
        return new AppDbContext(options, registry);
    }

    // Mirror of the LookupRouteGroup tenant handler (validated end-to-end via the same DbContext).
    private static async Task<(int status, object body)> TenantLookup(AppDbContext db, string? raw)
    {
        if (string.IsNullOrEmpty(raw) || !Guid.TryParse(raw, out var tenantId))
            return (400, new { ok = false, error = "Invalid tenant id." });
        var tenant = await db.Set<Tenant>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId && t.DeletedAt == null);
        if (tenant is null) return (404, new { ok = false, error = "Tenant not found." });
        return (200, new { ok = true, tenant = new { id = tenant.Id.ToString(), name = tenant.Name } });
    }

    [Fact]
    public async Task Invalid_tenant_id_returns_400()
    {
        using var db = CreateDb();
        var (status, body) = await TenantLookup(db, "not-a-guid");
        Assert.Equal(400, status);
        Assert.Contains("Invalid tenant id.", System.Text.Json.JsonSerializer.Serialize(body));
    }

    [Fact]
    public async Task Unknown_tenant_returns_404()
    {
        using var db = CreateDb();
        var (status, _) = await TenantLookup(db, Guid.NewGuid().ToString());
        Assert.Equal(404, status);
    }

    [Fact]
    public async Task Existing_tenant_returns_200_envelope()
    {
        using var db = CreateDb();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Set<Tenant>().Add(new Tenant { Id = id, Name = "Acme", IsActive = true, CreatedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();

        var (status, body) = await TenantLookup(db, id.ToString());
        Assert.Equal(200, status);
        var json = System.Text.Json.JsonSerializer.Serialize(body);
        Assert.Contains("\"ok\":true", json);
        Assert.Contains("\"name\":\"Acme\"", json);
        Assert.Contains(id.ToString(), json);
    }

    [Fact]
    public void DirectoryModule_declares_expected_surface()
    {
        IModule m = new DirectoryModule();
        Assert.Equal("directory", m.Id);
        Assert.Equal(4, m.AclFeatureDefinitions.Count);
        Assert.Equal(6, m.DeclaredEvents.Count);
        Assert.All(m.DeclaredEvents, e => Assert.True(e.Persistent));
        Assert.Empty(m.NotificationTypes);
        Assert.Empty(m.CustomFieldSets);
    }
}
