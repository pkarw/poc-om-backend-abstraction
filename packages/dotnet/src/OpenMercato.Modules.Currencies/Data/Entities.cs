namespace OpenMercato.Modules.Currencies.Data;

// Plain POCO entities mirroring upstream packages/core/src/modules/currencies/data/entities.ts.
// Byte-exact table/column/index/constraint mapping lives in CurrenciesModule.ConfigureModel; the DDL
// is created by the raw-SQL migration OpenMercato.Api/Migrations/20260707070000_AddCurrenciesModule.
// All three tables are org+tenant scoped; currencies/exchange_rates soft-delete via nullable DeletedAt.

/// <summary>Table <c>currencies</c> — ISO 4217 currency reference data (org+tenant scoped).</summary>
public sealed class Currency
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    /// <summary>ISO 4217 three-letter code (unique per org+tenant).</summary>
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Symbol { get; set; }
    public int DecimalPlaces { get; set; } = 2;
    public string? ThousandsSeparator { get; set; }
    public string? DecimalSeparator { get; set; }
    /// <summary>Base-currency flag; exactly one base per scope (enforced in the create/update command).</summary>
    public bool IsBase { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Table <c>exchange_rates</c> — a rate for a currency pair on a date from a source.</summary>
public sealed class ExchangeRate
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string FromCurrencyCode { get; set; } = string.Empty;
    public string ToCurrencyCode { get; set; } = string.Empty;
    /// <summary>numeric(18,8); projected to string on the wire (upstream stores/returns a string).</summary>
    public decimal Rate { get; set; }
    /// <summary>Datetime the rate applies (truncated to minute on write).</summary>
    public DateTimeOffset Date { get; set; }
    public string Source { get; set; } = string.Empty;
    /// <summary>Rate type from the bank's perspective: <c>buy</c> | <c>sell</c> | null.</summary>
    public string? Type { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>
/// Table <c>currency_fetch_configs</c> — provider fetch scheduling. Mapped + created for DDL parity;
/// the CRUD routes + rate-fetching providers (NBP / Raiffeisen scraping) are PARITY-TODO.
/// </summary>
public sealed class CurrencyFetchConfig
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string? SyncTime { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? LastSyncStatus { get; set; }
    public string? LastSyncMessage { get; set; }
    public int? LastSyncCount { get; set; }
    public string? Config { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
