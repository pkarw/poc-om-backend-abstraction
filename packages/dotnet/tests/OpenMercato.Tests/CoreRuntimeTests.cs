using OpenMercato.Core.Configuration;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.HealthCheck;
using Xunit;

namespace OpenMercato.Tests;

public class ModuleRegistryTests
{
    [Fact]
    public void Registers_health_check_module_with_acl_features()
    {
        var registry = new ModuleRegistry(new IModule[] { new HealthCheckModule() });

        Assert.Single(registry.Modules);
        Assert.Equal("health_check", registry.Modules[0].Id);
        Assert.Contains("health_check.view", registry.AclFeatures);
    }

    [Fact]
    public void Rejects_duplicate_module_ids()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ModuleRegistry(new IModule[] { new HealthCheckModule(), new HealthCheckModule() }));
    }
}

public class ConnectionStringTests
{
    [Fact]
    public void Converts_database_url_to_npgsql_keywords()
    {
        var result = ConnectionStrings.FromDatabaseUrl("postgres://mercato:s3cret@db.example.com:5433/open_mercato");

        Assert.Equal("Host=db.example.com;Port=5433;Database=open_mercato;Username=mercato;Password=s3cret", result);
    }

    [Fact]
    public void Defaults_postgres_port_to_5432()
    {
        var result = ConnectionStrings.FromDatabaseUrl("postgres://u:p@localhost/db");

        Assert.Contains("Port=5432", result);
    }

    [Fact]
    public void Passes_keyword_connection_strings_through()
    {
        const string keyword = "Host=localhost;Database=db;Username=u;Password=p";

        Assert.Equal(keyword, ConnectionStrings.FromDatabaseUrl(keyword));
    }

    [Fact]
    public void Parses_redis_url_with_password()
    {
        var options = ConnectionStrings.FromRedisUrl("redis://:secret@cache.example.com:6380");

        Assert.False(options.AbortOnConnectFail);
        Assert.Equal("secret", options.Password);
        Assert.Single(options.EndPoints);
    }
}
