using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Currencies.Data;

namespace OpenMercato.Modules.Currencies.Seeding;

/// <summary>
/// Default-currency seeding — the .NET port of currencies/lib/seeds.ts + setup.ts <c>seedDefaults</c>.
/// Idempotently upserts the 10 example currencies (USD base) for a tenant+org scope: existing rows only
/// have their <c>name</c> refreshed, missing rows are inserted.
/// </summary>
public static class CurrenciesSeeder
{
    private sealed record CurrencySeed(
        string Code, string Name, int DecimalPlaces, string Symbol,
        string DecimalSeparator, string ThousandsSeparator, bool IsBase);

    // Matches upstream SEED_CURRENCIES order + values exactly.
    private static readonly CurrencySeed[] Seeds =
    {
        new("USD", "US Dollar", 2, "$", ".", ",", true),
        new("EUR", "Euro", 2, "€", ",", ".", false),
        new("JPY", "Japanese Yen", 0, "¥", ".", ",", false),
        new("GBP", "British Pound", 2, "£", ".", ",", false),
        new("CHF", "Swiss Franc", 2, "Fr", ".", "'", false),
        new("CAD", "Canadian Dollar", 2, "C$", ".", ",", false),
        new("AUD", "Australian Dollar", 2, "A$", ".", ",", false),
        new("CNY", "Chinese Yuan", 2, "¥", ".", ",", false),
        new("CNH", "Chinese Yuan (Offshore)", 2, "¥", ".", ",", false),
        new("PLN", "Polish Zloty", 2, "zł", ",", " ", false),
    };

    /// <summary>Seed the example currencies for one tenant+org (idempotent). Returns true if anything changed.</summary>
    public static async Task<bool> SeedExampleCurrenciesAsync(AppDbContext db, Guid tenantId, Guid organizationId, CancellationToken ct = default)
    {
        var existing = await db.Set<Currency>()
            .Where(c => c.TenantId == tenantId && c.OrganizationId == organizationId)
            .ToListAsync(ct);
        var byCode = existing.ToDictionary(c => c.Code, StringComparer.Ordinal);

        var touched = false;
        var now = DateTimeOffset.UtcNow;
        foreach (var seed in Seeds)
        {
            if (byCode.TryGetValue(seed.Code, out var current))
            {
                if (current.Name != seed.Name)
                {
                    current.Name = seed.Name;
                    current.UpdatedAt = now;
                    touched = true;
                }
                continue;
            }
            db.Set<Currency>().Add(new Currency
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                OrganizationId = organizationId,
                Code = seed.Code,
                Name = seed.Name,
                DecimalPlaces = seed.DecimalPlaces,
                Symbol = seed.Symbol,
                DecimalSeparator = seed.DecimalSeparator,
                ThousandsSeparator = seed.ThousandsSeparator,
                IsBase = seed.IsBase,
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            touched = true;
        }

        if (touched) await db.SaveChangesAsync(ct);
        return touched;
    }

    /// <summary>
    /// Boot seeding (runs from the API host after migrations): seeds the example currencies for every
    /// existing organization scope. Idempotent — safe to run on every start.
    /// </summary>
    public static async Task RunBootAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // (tenantId, organizationId) pairs from the directory organizations table.
        var scopes = await db.Database.SqlQueryRaw<OrgScopeRow>(
            "SELECT tenant_id AS \"TenantId\", id AS \"OrganizationId\" FROM organizations WHERE deleted_at IS NULL").ToListAsync(ct);

        var seededScopes = 0;
        foreach (var s in scopes)
            if (await SeedExampleCurrenciesAsync(db, s.TenantId, s.OrganizationId, ct))
                seededScopes++;

        if (seededScopes > 0)
            logger.LogInformation("Currencies seed: default currencies ensured for {Count} organization scope(s).", seededScopes);
    }

    private sealed record OrgScopeRow(Guid TenantId, Guid OrganizationId);
}
