using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Currencies.Data;

namespace OpenMercato.Modules.Currencies.Services;

/// <summary>Tenant+org scope for a rate lookup (upstream <c>{ tenantId, organizationId }</c>).</summary>
public readonly record struct CurrencyScope(Guid TenantId, Guid OrganizationId);

/// <summary>
/// Result of a single-pair rate lookup — the port of upstream <c>RateResult</c>. <see cref="Rates"/>
/// holds every provider's row for the matched date (empty when none found within the fallback window).
/// </summary>
public sealed record RateResult(
    IReadOnlyList<ExchangeRate> Rates,
    string FromCurrencyCode,
    string ToCurrencyCode,
    DateTimeOffset RequestedDate,
    DateTimeOffset? ActualDate,
    string? Error = null);

/// <summary>
/// Result of <see cref="IExchangeRateService.ConvertToBaseAsync"/> — the base-currency conversion the
/// customers deal aggregate/summary endpoints consume. <see cref="Converted"/> is false when no base
/// currency is configured or no rate is available (caller falls back to the raw amount).
/// </summary>
public sealed record ConvertToBaseResult(
    decimal Amount,
    string? BaseCurrencyCode,
    decimal? Rate,
    bool Converted);

/// <summary>
/// The exchange-rate + conversion service — the .NET port of currencies/services/exchangeRateService.ts.
/// Provides currency reference lookups over the DB with the day-by-day fallback (up to
/// <c>maxDaysBack</c>), plus the ConvertToBase-style helper customers call.
///
/// PARITY-TODO: <c>autoFetch</c> (fetching from the NBP/Raiffeisen providers when a rate is missing) is
/// a no-op seam here — the providers/rate-fetching service are out of scope, so lookups only read the
/// <c>exchange_rates</c> table.
/// </summary>
public interface IExchangeRateService
{
    Task<RateResult> GetRateAsync(string fromCurrencyCode, string toCurrencyCode, DateTimeOffset date,
        CurrencyScope scope, int maxDaysBack = 30, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, RateResult>> GetRatesAsync(
        IReadOnlyList<(string From, string To)> pairs, DateTimeOffset date, CurrencyScope scope,
        int maxDaysBack = 30, CancellationToken ct = default);

    /// <summary>
    /// Convert <paramref name="amount"/> from <paramref name="fromCurrencyCode"/> into the scope's base
    /// currency (<c>is_base = true</c>). Returns the amount unchanged (converted=true, rate=1) when the
    /// source already is the base; converted=false when there is no base or no available rate.
    /// </summary>
    Task<ConvertToBaseResult> ConvertToBaseAsync(decimal amount, string fromCurrencyCode, DateTimeOffset date,
        CurrencyScope scope, int maxDaysBack = 30, CancellationToken ct = default);

    /// <summary>Resolve the active base currency code for a scope (<c>is_base = true</c>), or null.</summary>
    Task<string?> GetBaseCurrencyCodeAsync(CurrencyScope scope, CancellationToken ct = default);
}

public sealed class ExchangeRateService : IExchangeRateService
{
    private readonly AppDbContext _db;

    public ExchangeRateService(AppDbContext db) => _db = db;

    public async Task<RateResult> GetRateAsync(string fromCurrencyCode, string toCurrencyCode, DateTimeOffset date,
        CurrencyScope scope, int maxDaysBack = 30, CancellationToken ct = default)
    {
        ValidateNotFuture(date);
        var from = fromCurrencyCode.Trim().ToUpperInvariant();
        var to = toCurrencyCode.Trim().ToUpperInvariant();
        if (from == to) throw new InvalidOperationException("Cannot get exchange rate for the same currency");

        // Recurse day-by-day up to maxDaysBack (total maxDaysBack + 1 checks).
        for (var daysBack = 0; daysBack <= maxDaysBack; daysBack++)
        {
            var checkDate = NormalizeDate(date.AddDays(-daysBack));
            var rates = await FindExactRatesAsync(from, to, checkDate, scope, ct);
            if (rates.Count > 0)
                return new RateResult(rates, from, to, date, checkDate);
            // PARITY-TODO: autoFetch from providers would run here before falling back a day.
        }
        return new RateResult(Array.Empty<ExchangeRate>(), from, to, date, null);
    }

    public async Task<IReadOnlyDictionary<string, RateResult>> GetRatesAsync(
        IReadOnlyList<(string From, string To)> pairs, DateTimeOffset date, CurrencyScope scope,
        int maxDaysBack = 30, CancellationToken ct = default)
    {
        var results = new Dictionary<string, RateResult>();
        foreach (var (from, to) in pairs)
        {
            var key = $"{from}/{to}";
            try
            {
                results[key] = await GetRateAsync(from, to, date, scope, maxDaysBack, ct);
            }
            catch (Exception ex)
            {
                results[key] = new RateResult(Array.Empty<ExchangeRate>(), from, to, date, null, ex.Message);
            }
        }
        return results;
    }

    public async Task<ConvertToBaseResult> ConvertToBaseAsync(decimal amount, string fromCurrencyCode, DateTimeOffset date,
        CurrencyScope scope, int maxDaysBack = 30, CancellationToken ct = default)
    {
        var from = fromCurrencyCode.Trim().ToUpperInvariant();
        var baseCode = await GetBaseCurrencyCodeAsync(scope, ct);
        if (baseCode is null) return new ConvertToBaseResult(amount, null, null, false);
        if (from == baseCode) return new ConvertToBaseResult(amount, baseCode, 1m, true);

        var result = await GetRateAsync(from, baseCode, date, scope, maxDaysBack, ct);
        if (result.Rates.Count == 0) return new ConvertToBaseResult(amount, baseCode, null, false);

        // Use the first provider's rate (upstream README convertAmount pattern).
        var rate = result.Rates[0].Rate;
        return new ConvertToBaseResult(amount * rate, baseCode, rate, true);
    }

    public Task<string?> GetBaseCurrencyCodeAsync(CurrencyScope scope, CancellationToken ct = default) =>
        _db.Set<Currency>()
            .Where(c => c.TenantId == scope.TenantId && c.OrganizationId == scope.OrganizationId && c.IsBase && c.DeletedAt == null)
            .Select(c => (string?)c.Code)
            .FirstOrDefaultAsync(ct);

    private async Task<IReadOnlyList<ExchangeRate>> FindExactRatesAsync(
        string from, string to, DateTimeOffset date, CurrencyScope scope, CancellationToken ct)
    {
        var normalized = NormalizeDate(date);
        return await _db.Set<ExchangeRate>()
            .Where(r => r.OrganizationId == scope.OrganizationId && r.TenantId == scope.TenantId &&
                        r.FromCurrencyCode == from && r.ToCurrencyCode == to && r.Date == normalized &&
                        r.DeletedAt == null && r.IsActive)
            .ToListAsync(ct);
    }

    private static DateTimeOffset NormalizeDate(DateTimeOffset date)
    {
        var utc = date.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
    }

    private static void ValidateNotFuture(DateTimeOffset date)
    {
        if (NormalizeDate(date) > NormalizeDate(DateTimeOffset.UtcNow))
            throw new InvalidOperationException("Cannot get exchange rate for a future date");
    }
}
