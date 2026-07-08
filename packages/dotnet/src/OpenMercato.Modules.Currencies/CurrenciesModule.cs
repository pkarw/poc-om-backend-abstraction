using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Currencies.Api;
using OpenMercato.Modules.Currencies.Commands;
using OpenMercato.Modules.Currencies.Data;
using OpenMercato.Modules.Currencies.Services;

namespace OpenMercato.Modules.Currencies;

/// <summary>
/// The currencies module (upstream packages/core/src/modules/currencies) — currency reference data +
/// exchange rates + conversion. Owns three tables (currencies, exchange_rates, currency_fetch_configs;
/// byte-exact DDL in the raw-SQL migration <c>20260707070000_AddCurrenciesModule</c>). Currencies +
/// exchange-rates get admin CRUD via the CRUD factory + command bus; the conversion service
/// (<see cref="IExchangeRateService"/>) exposes the ConvertToBase-style API the customers deal
/// aggregate/summary endpoints call.
///
/// PARITY-TODO: the rate-fetching providers (NBP / Raiffeisen web scraping), the
/// <c>currency_fetch_configs</c> CRUD routes, the i18n/backend UI, and the exchange-rate service's
/// <c>autoFetch</c> path are out of scope; the table is created for DDL parity only.
/// </summary>
public sealed class CurrenciesModule : IModule
{
    public string Id => "currencies";

    /// <summary>The 6 ACL feature ids (acl.ts). Kept for back-compat.</summary>
    public IReadOnlyList<string> AclFeatures { get; } = new[]
    {
        "currencies.view",
        "currencies.manage",
        "currencies.rates.view",
        "currencies.rates.manage",
        "currencies.fetch.view",
        "currencies.fetch.manage",
    };

    /// <summary>The 6 ACL features with their upstream titles (acl.ts).</summary>
    public IReadOnlyList<AclFeatureDefinition> AclFeatureDefinitions { get; } = new[]
    {
        new AclFeatureDefinition("currencies.view", "View currencies"),
        new AclFeatureDefinition("currencies.manage", "Manage currencies"),
        new AclFeatureDefinition("currencies.rates.view", "View exchange rates"),
        new AclFeatureDefinition("currencies.rates.manage", "Manage exchange rates"),
        new AclFeatureDefinition("currencies.fetch.view", "View currency fetch configuration"),
        new AclFeatureDefinition("currencies.fetch.manage", "Manage currency fetch configuration"),
    };

    /// <summary>Declared CRUD events (events.ts).</summary>
    public IReadOnlyList<EventDeclaration> DeclaredEvents { get; } = new[]
    {
        new EventDeclaration("currencies.currency.created", "{ id, organizationId, tenantId }", Persistent: true),
        new EventDeclaration("currencies.currency.updated", "{ id, organizationId, tenantId }", Persistent: true),
        new EventDeclaration("currencies.currency.deleted", "{ id, organizationId, tenantId }", Persistent: true),
        new EventDeclaration("currencies.exchange_rate.created", "{ id, organizationId, tenantId }", Persistent: true),
        new EventDeclaration("currencies.exchange_rate.updated", "{ id, organizationId, tenantId }", Persistent: true),
        new EventDeclaration("currencies.exchange_rate.deleted", "{ id, organizationId, tenantId }", Persistent: true),
    };

    /// <summary>Admin gets the whole feature family (setup.ts <c>admin: ['currencies.*']</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultRoleFeatures { get; } =
        new Dictionary<string, IReadOnlyList<string>> { ["admin"] = new[] { "currencies.*" } };

    public void ConfigureServices(IServiceCollection services)
    {
        // Write commands (di.ts registerCommand equivalent).
        services.AddScoped<ICommand, CreateCurrencyCommand>();
        services.AddScoped<ICommand, UpdateCurrencyCommand>();
        services.AddScoped<ICommand, DeleteCurrencyCommand>();
        services.AddScoped<ICommand, CreateExchangeRateCommand>();
        services.AddScoped<ICommand, UpdateExchangeRateCommand>();
        services.AddScoped<ICommand, DeleteExchangeRateCommand>();

        // Conversion service (di.ts exchangeRateService). rateFetchingService is PARITY-TODO.
        services.AddScoped<IExchangeRateService, ExchangeRateService>();
    }

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Currency>(e =>
        {
            e.ToTable("currencies");
            e.HasKey(x => x.Id).HasName("currencies_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Code).HasColumnName("code").IsRequired();
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.Symbol).HasColumnName("symbol");
            e.Property(x => x.DecimalPlaces).HasColumnName("decimal_places");
            e.Property(x => x.ThousandsSeparator).HasColumnName("thousands_separator");
            e.Property(x => x.DecimalSeparator).HasColumnName("decimal_separator");
            e.Property(x => x.IsBase).HasColumnName("is_base");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
            e.HasIndex(x => new { x.OrganizationId, x.TenantId, x.Code })
                .IsUnique().HasDatabaseName("currencies_code_scope_unique");
        });

        modelBuilder.Entity<ExchangeRate>(e =>
        {
            e.ToTable("exchange_rates");
            e.HasKey(x => x.Id).HasName("exchange_rates_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.FromCurrencyCode).HasColumnName("from_currency_code").IsRequired();
            e.Property(x => x.ToCurrencyCode).HasColumnName("to_currency_code").IsRequired();
            e.Property(x => x.Rate).HasColumnName("rate").HasColumnType("numeric(18,8)");
            e.Property(x => x.Date).HasColumnName("date").HasColumnType("timestamptz");
            e.Property(x => x.Source).HasColumnName("source").IsRequired();
            e.Property(x => x.Type).HasColumnName("type");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
            e.HasIndex(x => new { x.OrganizationId, x.TenantId, x.FromCurrencyCode, x.ToCurrencyCode, x.Date, x.Source })
                .IsUnique().HasDatabaseName("exchange_rates_pair_datetime_source_unique");
        });

        modelBuilder.Entity<CurrencyFetchConfig>(e =>
        {
            e.ToTable("currency_fetch_configs");
            e.HasKey(x => x.Id).HasName("currency_fetch_configs_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Provider).HasColumnName("provider").IsRequired();
            e.Property(x => x.IsEnabled).HasColumnName("is_enabled");
            e.Property(x => x.SyncTime).HasColumnName("sync_time");
            e.Property(x => x.LastSyncAt).HasColumnName("last_sync_at").HasColumnType("timestamptz");
            e.Property(x => x.LastSyncStatus).HasColumnName("last_sync_status");
            e.Property(x => x.LastSyncMessage).HasColumnName("last_sync_message");
            e.Property(x => x.LastSyncCount).HasColumnName("last_sync_count");
            e.Property(x => x.Config).HasColumnName("config").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.HasIndex(x => new { x.OrganizationId, x.TenantId, x.Provider })
                .IsUnique().HasDatabaseName("currency_fetch_configs_provider_scope_unique");
        });
    }

    public void MapRoutes(IEndpointRouteBuilder routes) => CurrenciesRoutes.Map(routes);
}
