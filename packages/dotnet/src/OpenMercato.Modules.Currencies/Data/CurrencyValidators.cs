using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenMercato.Core.Crud;

namespace OpenMercato.Modules.Currencies.Data;

// Typed command inputs + validation helpers — the .NET port of currencies/data/validators.ts.
// Validation runs in the CRUD factory's ValidateCreate/ValidateUpdate hook (returns CrudValidationIssue
// list → 400 { error, details }); org/tenant scope is injected from the CommandContext, not the body.

public sealed record CurrencyCreateInput
{
    public Guid OrganizationId { get; init; }
    public Guid TenantId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Symbol { get; init; }
    public int? DecimalPlaces { get; init; }
    public string? ThousandsSeparator { get; init; }
    public string? DecimalSeparator { get; init; }
    public bool? IsBase { get; init; }
    public bool? IsActive { get; init; }
}

public sealed record CurrencyUpdateInput
{
    public Guid Id { get; init; }
    public string? Code { get; init; }
    public string? Name { get; init; }
    public string? Symbol { get; init; }
    public bool SymbolSet { get; init; }
    public int? DecimalPlaces { get; init; }
    public string? ThousandsSeparator { get; init; }
    public bool ThousandsSeparatorSet { get; init; }
    public string? DecimalSeparator { get; init; }
    public bool DecimalSeparatorSet { get; init; }
    public bool? IsBase { get; init; }
    public bool? IsActive { get; init; }
}

public sealed record CurrencyDeleteInput
{
    public Guid Id { get; init; }
    public Guid OrganizationId { get; init; }
    public Guid TenantId { get; init; }
}

public sealed record ExchangeRateCreateInput
{
    public Guid OrganizationId { get; init; }
    public Guid TenantId { get; init; }
    public string FromCurrencyCode { get; init; } = string.Empty;
    public string ToCurrencyCode { get; init; } = string.Empty;
    public string Rate { get; init; } = string.Empty;
    public DateTimeOffset Date { get; init; }
    public string Source { get; init; } = string.Empty;
    public string? Type { get; init; }
    public bool? IsActive { get; init; }
}

public sealed record ExchangeRateUpdateInput
{
    public Guid Id { get; init; }
    public string? FromCurrencyCode { get; init; }
    public string? ToCurrencyCode { get; init; }
    public string? Rate { get; init; }
    public DateTimeOffset? Date { get; init; }
    public string? Source { get; init; }
    public string? Type { get; init; }
    public bool TypeSet { get; init; }
    public bool? IsActive { get; init; }
}

public sealed record ExchangeRateDeleteInput
{
    public Guid Id { get; init; }
    public Guid OrganizationId { get; init; }
    public Guid TenantId { get; init; }
}

/// <summary>Validation + parsing helpers mirroring the Zod schemas in validators.ts.</summary>
public static class CurrencyValidators
{
    private static readonly Regex CodeRegex = new("^[A-Z]{3}$", RegexOptions.Compiled);
    private static readonly Regex RateRegex = new(@"^\d+(\.\d{1,8})?$", RegexOptions.Compiled);
    private static readonly Regex SourceRegex = new(@"^[a-zA-Z0-9\s\-_]+$", RegexOptions.Compiled);

    private static CrudValidationIssue Issue(string path, string message, string code = "custom") =>
        new(new[] { path }, message, code);

    // ---- Currency code -----------------------------------------------------------------------

    /// <summary>Normalize a code (trim + upper) and validate ISO 4217. Returns null when valid.</summary>
    public static string? NormalizeCode(string? raw, out string normalized)
    {
        normalized = (raw ?? string.Empty).Trim().ToUpperInvariant();
        return CodeRegex.IsMatch(normalized)
            ? null
            : "Currency code must be a three-letter ISO code (e.g., USD, EUR)";
    }

    // ---- Currency create/update --------------------------------------------------------------

    public static IReadOnlyList<CrudValidationIssue> ValidateCurrencyCreate(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();

        var codeErr = NormalizeCode(GetString(body, "code"), out _);
        if (codeErr is not null) issues.Add(Issue("code", codeErr));

        var name = GetString(body, "name");
        if (string.IsNullOrEmpty(name) || name.Length > 200)
            issues.Add(Issue("name", "Name must be between 1 and 200 characters"));

        var symbol = GetString(body, "symbol");
        if (symbol is { Length: > 10 }) issues.Add(Issue("symbol", "Symbol must be at most 10 characters"));

        if (TryGetInt(body, "decimalPlaces", out var dp) && (dp < 0 || dp > 8))
            issues.Add(Issue("decimalPlaces", "decimalPlaces must be between 0 and 8"));

        return issues;
    }

    public static IReadOnlyList<CrudValidationIssue> ValidateCurrencyUpdate(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();
        if (!TryGetGuid(body, "id", out _)) issues.Add(Issue("id", "Currency ID is required"));

        if (HasProp(body, "code"))
        {
            var codeErr = NormalizeCode(GetString(body, "code"), out _);
            if (codeErr is not null) issues.Add(Issue("code", codeErr));
        }
        if (HasProp(body, "name"))
        {
            var name = GetString(body, "name");
            if (string.IsNullOrEmpty(name) || name.Length > 200)
                issues.Add(Issue("name", "Name must be between 1 and 200 characters"));
        }
        if (TryGetInt(body, "decimalPlaces", out var dp) && (dp < 0 || dp > 8))
            issues.Add(Issue("decimalPlaces", "decimalPlaces must be between 0 and 8"));
        return issues;
    }

    // ---- Exchange rate create/update ---------------------------------------------------------

    public static IReadOnlyList<CrudValidationIssue> ValidateExchangeRateCreate(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();

        var fromErr = NormalizeCode(GetString(body, "fromCurrencyCode"), out var from);
        if (fromErr is not null) issues.Add(Issue("fromCurrencyCode", fromErr));
        var toErr = NormalizeCode(GetString(body, "toCurrencyCode"), out var to);
        if (toErr is not null) issues.Add(Issue("toCurrencyCode", toErr));
        if (fromErr is null && toErr is null && from == to)
            issues.Add(Issue("toCurrencyCode", "From and To currencies must be different"));

        ValidateRate(GetString(body, "rate"), issues);
        ValidateSource(GetString(body, "source"), issues);
        ValidateType(body, issues);

        if (!HasProp(body, "date") || !TryParseDate(body, "date", out _))
            issues.Add(Issue("date", "A valid date is required"));

        return issues;
    }

    public static IReadOnlyList<CrudValidationIssue> ValidateExchangeRateUpdate(JsonElement body)
    {
        var issues = new List<CrudValidationIssue>();
        if (!TryGetGuid(body, "id", out _)) issues.Add(Issue("id", "Exchange rate ID is required"));

        string? from = null, to = null;
        if (HasProp(body, "fromCurrencyCode"))
        {
            var e = NormalizeCode(GetString(body, "fromCurrencyCode"), out from);
            if (e is not null) issues.Add(Issue("fromCurrencyCode", e));
        }
        if (HasProp(body, "toCurrencyCode"))
        {
            var e = NormalizeCode(GetString(body, "toCurrencyCode"), out to);
            if (e is not null) issues.Add(Issue("toCurrencyCode", e));
        }
        if (from is not null && to is not null && from == to)
            issues.Add(Issue("toCurrencyCode", "From and To currencies must be different"));

        if (HasProp(body, "rate")) ValidateRate(GetString(body, "rate"), issues);
        if (HasProp(body, "source")) ValidateSource(GetString(body, "source"), issues);
        ValidateType(body, issues);
        if (HasProp(body, "date") && !TryParseDate(body, "date", out _))
            issues.Add(Issue("date", "A valid date is required"));
        return issues;
    }

    private static void ValidateRate(string? rate, List<CrudValidationIssue> issues)
    {
        if (rate is null || !RateRegex.IsMatch(rate))
        {
            issues.Add(Issue("rate", "Rate must be a positive decimal number"));
            return;
        }
        if (decimal.TryParse(rate, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v <= 0)
            issues.Add(Issue("rate", "Rate must be greater than zero"));
    }

    private static void ValidateSource(string? source, List<CrudValidationIssue> issues)
    {
        var s = (source ?? string.Empty).Trim();
        if (s.Length < 2 || s.Length > 50 || !SourceRegex.IsMatch(s))
            issues.Add(Issue("source", "Source must be 2-50 chars (letters, numbers, spaces, - _)"));
    }

    private static void ValidateType(JsonElement body, List<CrudValidationIssue> issues)
    {
        if (!HasProp(body, "type")) return;
        var t = GetString(body, "type");
        if (t is null) return; // null is allowed
        if (t is not ("buy" or "sell")) issues.Add(Issue("type", "type must be 'buy' or 'sell'"));
    }

    // ---- Date truncation (validators.ts truncateToMinute) ------------------------------------

    /// <summary>Zero seconds + milliseconds, preserving offset (upstream <c>truncateToMinute</c>).</summary>
    public static DateTimeOffset TruncateToMinute(DateTimeOffset date)
    {
        var utc = date.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }

    // ---- JSON helpers ------------------------------------------------------------------------

    public static bool HasProp(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Undefined;

    public static string? GetString(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Null => null,
            _ => p.ToString(),
        };
    }

    public static bool? GetBool(JsonElement body, string name)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    public static bool TryGetInt(JsonElement body, string name, out int value)
    {
        value = 0;
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out value)) return true;
        if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out value)) return true;
        return false;
    }

    public static bool TryGetGuid(JsonElement body, string name, out Guid value)
    {
        value = Guid.Empty;
        var s = GetString(body, name);
        return s is not null && Guid.TryParse(s, out value);
    }

    public static bool TryParseDate(JsonElement body, string name, out DateTimeOffset value)
    {
        value = default;
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value))
            return true;
        return false;
    }
}
