using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Auth;
using OpenMercato.Modules.HealthCheck;
using Xunit;

namespace OpenMercato.Tests;

/// <summary>
/// Verifies the extended module contract (declaration surface + registry aggregation +
/// runtime catalogs) against auth's declared surface, mirroring upstream
/// notifications.ts / events.ts / acl.ts.
/// </summary>
public class ModuleContractDeclarationTests
{
    private static ModuleRegistry AuthRegistry() =>
        new(new IModule[] { new HealthCheckModule(), new AuthModule() });

    [Fact]
    public void Auth_declares_six_notification_types()
    {
        var registry = AuthRegistry();

        Assert.Equal(6, registry.AllNotificationTypes.Count);
        var requested = Assert.Single(
            registry.AllNotificationTypes,
            n => n.Type == "auth.password_reset.requested");
        Assert.Equal("info", requested.Severity);
        Assert.Equal(24, requested.ExpiresAfterHours);

        var locked = Assert.Single(registry.AllNotificationTypes, n => n.Type == "auth.account.locked");
        Assert.Equal("warning", locked.Severity);
        Assert.Null(locked.ExpiresAfterHours);
    }

    [Fact]
    public void Auth_declares_twelve_events_with_persistence_flags()
    {
        var registry = AuthRegistry();

        Assert.Equal(12, registry.AllDeclaredEvents.Count);
        Assert.True(Assert.Single(registry.AllDeclaredEvents, e => e.Name == "auth.user.created").Persistent);
        Assert.True(Assert.Single(registry.AllDeclaredEvents, e => e.Name == "auth.role.deleted").Persistent);
        Assert.False(Assert.Single(registry.AllDeclaredEvents, e => e.Name == "auth.login.success").Persistent);
        Assert.False(Assert.Single(registry.AllDeclaredEvents, e => e.Name == "auth.logout").Persistent);
    }

    [Fact]
    public void Auth_declares_eight_acl_feature_definitions_with_titles()
    {
        var registry = AuthRegistry();
        var authFeatures = registry.AllAclFeatureDefinitions.Where(f => f.Id.StartsWith("auth.")).ToList();

        Assert.Equal(8, authFeatures.Count);
        Assert.Equal("List users", Assert.Single(authFeatures, f => f.Id == "auth.users.list").Title);
        Assert.Equal("Manage sidebar presets", Assert.Single(authFeatures, f => f.Id == "auth.sidebar.manage").Title);
    }

    [Fact]
    public void Auth_declares_no_custom_field_sets()
    {
        var registry = AuthRegistry();
        Assert.Empty(registry.AllCustomFieldSets);
    }

    [Fact]
    public void HealthCheck_derives_acl_feature_definitions_from_bare_ids()
    {
        // A module that declares only bare AclFeatures still surfaces rich defs (title == id).
        var registry = new ModuleRegistry(new IModule[] { new HealthCheckModule() });
        var def = Assert.Single(registry.AllAclFeatureDefinitions, f => f.Id == "health_check.view");
        Assert.Equal("health_check.view", def.Title);
    }

    [Fact]
    public void NotificationCatalog_reports_known_and_unknown_types()
    {
        var catalog = new NotificationCatalog(AuthRegistry());

        Assert.True(catalog.IsKnown("auth.password_reset.completed"));
        Assert.False(catalog.IsKnown("auth.does_not_exist"));
        Assert.Equal("success", catalog.Get("auth.password_reset.completed")!.Severity);
        Assert.Null(catalog.Get("auth.does_not_exist"));
        Assert.Equal(6, catalog.All.Count);
    }

    [Fact]
    public void CustomFieldRegistry_returns_declared_sets_by_entity()
    {
        var moduleWithFields = new StubFieldModule();
        var registry = new ModuleRegistry(new IModule[] { moduleWithFields });
        var catalog = new CustomFieldRegistry(registry);

        var set = Assert.Single(catalog.ForEntity("stub:thing"));
        Assert.Equal("color", Assert.Single(set.Fields).Key);
        Assert.Empty(catalog.ForEntity("nope:none"));
    }

    [Fact]
    public void Duplicate_notification_type_across_modules_throws()
    {
        var registry = new ModuleRegistry(new IModule[]
        {
            new StubNotificationModule("a"),
            new StubNotificationModule("b"),
        });

        var ex = Assert.Throws<InvalidOperationException>(() => _ = registry.AllNotificationTypes);
        Assert.Contains("dup.notification", ex.Message);
    }

    [Fact]
    public void Duplicate_declared_event_across_modules_throws()
    {
        var registry = new ModuleRegistry(new IModule[]
        {
            new StubEventModule("a"),
            new StubEventModule("b"),
        });

        Assert.Throws<InvalidOperationException>(() => _ = registry.AllDeclaredEvents);
    }

    // --- stub modules (only override the members under test) ---------------------------

    private abstract class StubModule : IModule
    {
        public abstract string Id { get; }
        public IReadOnlyList<string> AclFeatures => Array.Empty<string>();
        public void ConfigureServices(IServiceCollection services) { }
        public void ConfigureModel(ModelBuilder modelBuilder) { }
        public void MapRoutes(IEndpointRouteBuilder routes) { }
    }

    // Re-listing IModule forces the interface members to re-map to these classes'
    // public properties (otherwise the default interface implementation wins over a
    // member merely inherited-and-shadowed through the abstract base).
    private sealed class StubFieldModule : StubModule, IModule
    {
        public override string Id => "stub";
        public IReadOnlyList<CustomFieldSet> CustomFieldSets { get; } = new[]
        {
            new CustomFieldSet("stub:thing", new[] { new CustomFieldDefinition("color", "select", "Color") }),
        };
    }

    private sealed class StubNotificationModule : StubModule, IModule
    {
        public StubNotificationModule(string id) => Id = id;
        public override string Id { get; }
        public IReadOnlyList<NotificationTypeDefinition> NotificationTypes { get; } = new[]
        {
            new NotificationTypeDefinition("dup.notification", "info"),
        };
    }

    private sealed class StubEventModule : StubModule, IModule
    {
        public StubEventModule(string id) => Id = id;
        public override string Id { get; }
        public IReadOnlyList<EventDeclaration> DeclaredEvents { get; } = new[]
        {
            new EventDeclaration("dup.event"),
        };
    }
}
