namespace OpenMercato.Modules.Currencies.Commands;

/// <summary>Result of the currency write commands (upstream <c>{ currencyId }</c>).</summary>
public sealed record CurrencyResult(string CurrencyId);

/// <summary>Result of the exchange-rate write commands (upstream <c>{ exchangeRateId }</c>).</summary>
public sealed record ExchangeRateResult(string ExchangeRateId);

/// <summary>Serializable snapshot of a currency row for undo/redo (action-log jsonb).</summary>
public sealed record CurrencySnapshot(
    string Id,
    string OrganizationId,
    string TenantId,
    string Code,
    string Name,
    string? Symbol,
    int DecimalPlaces,
    string? ThousandsSeparator,
    string? DecimalSeparator,
    bool IsBase,
    bool IsActive);

/// <summary>Serializable snapshot of an exchange-rate row for undo/redo (action-log jsonb).</summary>
public sealed record ExchangeRateSnapshot(
    string Id,
    string OrganizationId,
    string TenantId,
    string FromCurrencyCode,
    string ToCurrencyCode,
    string Rate,
    string Date,
    string Source,
    string? Type,
    bool IsActive);
