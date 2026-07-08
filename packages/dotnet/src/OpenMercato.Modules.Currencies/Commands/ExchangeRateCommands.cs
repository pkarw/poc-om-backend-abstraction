using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Currencies.Data;

namespace OpenMercato.Modules.Currencies.Commands;

// Exchange-rate write commands — the .NET port of currencies/commands/exchange-rates.ts.

/// <summary>currencies.exchange_rates.create — both currencies must exist; reject duplicate (pair+date+source).</summary>
public sealed class CreateExchangeRateCommand :
    ICommand<ExchangeRateCreateInput, ExchangeRateResult>,
    IUndoableCommand,
    ICommandLogMetadataBuilder<ExchangeRateCreateInput, ExchangeRateResult>
{
    public string CommandId => "currencies.exchange_rates.create";

    public async Task<ExchangeRateResult> ExecuteAsync(ExchangeRateCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        CurrencyValidators.NormalizeCode(input.FromCurrencyCode, out var from);
        CurrencyValidators.NormalizeCode(input.ToCurrencyCode, out var to);
        var date = CurrencyValidators.TruncateToMinute(input.Date);

        await ValidateCurrenciesExistAsync(db, from, to, input.OrganizationId, input.TenantId);

        var dup = await db.Set<ExchangeRate>().AnyAsync(r =>
            r.FromCurrencyCode == from && r.ToCurrencyCode == to && r.Date == date && r.Source == input.Source &&
            r.OrganizationId == input.OrganizationId && r.TenantId == input.TenantId && r.DeletedAt == null);
        if (dup) throw CommandHttpException.Conflict("Exchange rate for this currency pair, date, and source already exists");

        var now = DateTimeOffset.UtcNow;
        var record = new ExchangeRate
        {
            Id = Guid.NewGuid(),
            OrganizationId = input.OrganizationId,
            TenantId = input.TenantId,
            FromCurrencyCode = from,
            ToCurrencyCode = to,
            Rate = decimal.Parse(input.Rate, NumberStyles.Number, CultureInfo.InvariantCulture),
            Date = date,
            Source = input.Source.Trim(),
            Type = input.Type,
            IsActive = input.IsActive != false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Set<ExchangeRate>().Add(record);
        return new ExchangeRateResult(record.Id.ToString());
    }

    public CommandLogMetadata BuildLog(ExchangeRateCreateInput input, ExchangeRateResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Create exchange rate",
        ResourceKind = "currencies.exchange_rate",
        ResourceId = result.ExchangeRateId,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var record = await db.Set<ExchangeRate>().FirstOrDefaultAsync(r => r.Id == Guid.Parse(log.ResourceId!));
        if (record is null) return;
        record.DeletedAt = DateTimeOffset.UtcNow;
        record.IsActive = false;
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var record = await db.Set<ExchangeRate>().FirstOrDefaultAsync(r => r.Id == Guid.Parse(log.ResourceId!));
        if (record is null) return;
        record.DeletedAt = null;
        record.IsActive = true;
        record.UpdatedAt = DateTimeOffset.UtcNow;
    }

    internal static async Task ValidateCurrenciesExistAsync(AppDbContext db, string from, string to, Guid orgId, Guid tenantId)
    {
        var fromOk = await db.Set<Currency>().AnyAsync(c => c.Code == from && c.OrganizationId == orgId && c.TenantId == tenantId && c.DeletedAt == null);
        if (!fromOk) throw CommandHttpException.BadRequest($"From currency {from} does not exist or is inactive");
        var toOk = await db.Set<Currency>().AnyAsync(c => c.Code == to && c.OrganizationId == orgId && c.TenantId == tenantId && c.DeletedAt == null);
        if (!toOk) throw CommandHttpException.BadRequest($"To currency {to} does not exist or is inactive");
    }
}

/// <summary>currencies.exchange_rates.update — 404 if missing; currency + duplicate checks; final-state guards.</summary>
public sealed class UpdateExchangeRateCommand :
    ICommand<ExchangeRateUpdateInput, ExchangeRateResult>,
    IUndoableCommand,
    ICommandLogMetadataBuilder<ExchangeRateUpdateInput, ExchangeRateResult>
{
    public string CommandId => "currencies.exchange_rates.update";

    private ExchangeRateSnapshot? _before;

    public async Task<ExchangeRateResult> ExecuteAsync(ExchangeRateUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var record = await db.Set<ExchangeRate>().FirstOrDefaultAsync(r => r.Id == input.Id && r.DeletedAt == null)
            ?? throw CommandHttpException.NotFound("Exchange rate not found");
        _before = Snapshot(record);

        string from = record.FromCurrencyCode, to = record.ToCurrencyCode;
        if (input.FromCurrencyCode is not null) CurrencyValidators.NormalizeCode(input.FromCurrencyCode, out from);
        if (input.ToCurrencyCode is not null) CurrencyValidators.NormalizeCode(input.ToCurrencyCode, out to);
        if (input.FromCurrencyCode is not null || input.ToCurrencyCode is not null)
            await CreateExchangeRateCommand.ValidateCurrenciesExistAsync(db, from, to, record.OrganizationId, record.TenantId);

        var date = input.Date is { } d ? CurrencyValidators.TruncateToMinute(d) : record.Date;
        var source = input.Source?.Trim() ?? record.Source;
        if (input.FromCurrencyCode is not null || input.ToCurrencyCode is not null || input.Date is not null || input.Source is not null)
        {
            var dup = await db.Set<ExchangeRate>().AnyAsync(r =>
                r.FromCurrencyCode == from && r.ToCurrencyCode == to && r.Date == date && r.Source == source &&
                r.OrganizationId == record.OrganizationId && r.TenantId == record.TenantId &&
                r.Id != record.Id && r.DeletedAt == null);
            if (dup) throw CommandHttpException.Conflict("Exchange rate for this currency pair, date, and source already exists");
        }

        record.FromCurrencyCode = from;
        record.ToCurrencyCode = to;
        record.Date = date;
        record.Source = source;
        if (input.Rate is not null) record.Rate = decimal.Parse(input.Rate, NumberStyles.Number, CultureInfo.InvariantCulture);
        if (input.TypeSet) record.Type = input.Type;
        if (input.IsActive is { } active) record.IsActive = active;
        record.UpdatedAt = DateTimeOffset.UtcNow;

        if (record.FromCurrencyCode == record.ToCurrencyCode)
            throw CommandHttpException.BadRequest("From and To currencies must be different");
        if (record.Rate <= 0)
            throw CommandHttpException.BadRequest("Rate must be greater than zero");

        return new ExchangeRateResult(record.Id.ToString());
    }

    public CommandLogMetadata BuildLog(ExchangeRateUpdateInput input, ExchangeRateResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Update exchange rate",
        ResourceKind = "currencies.exchange_rate",
        ResourceId = result.ExchangeRateId,
        SnapshotBefore = _before,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var before = log.GetSnapshotBefore<ExchangeRateSnapshot>();
        if (before is null) return;
        var db = services.GetRequiredService<AppDbContext>();
        var record = await db.Set<ExchangeRate>().FirstOrDefaultAsync(r => r.Id == Guid.Parse(before.Id));
        if (record is null) return;
        ApplySnapshot(record, before);
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var input = log.GetRedoInput<ExchangeRateUpdateInput>();
        if (input is null) return;
        await ExecuteAsync(input, ctx, services);
    }

    internal static ExchangeRateSnapshot Snapshot(ExchangeRate r) => new(
        r.Id.ToString(), r.OrganizationId.ToString(), r.TenantId.ToString(), r.FromCurrencyCode, r.ToCurrencyCode,
        r.Rate.ToString(CultureInfo.InvariantCulture), r.Date.ToUniversalTime().ToString("o"), r.Source, r.Type, r.IsActive);

    internal static void ApplySnapshot(ExchangeRate record, ExchangeRateSnapshot s)
    {
        record.FromCurrencyCode = s.FromCurrencyCode;
        record.ToCurrencyCode = s.ToCurrencyCode;
        record.Rate = decimal.Parse(s.Rate, NumberStyles.Number, CultureInfo.InvariantCulture);
        record.Date = DateTimeOffset.Parse(s.Date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        record.Source = s.Source;
        record.Type = s.Type;
        record.IsActive = s.IsActive;
        record.DeletedAt = null;
        record.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>currencies.exchange_rates.delete — 404 if missing; soft delete.</summary>
public sealed class DeleteExchangeRateCommand :
    ICommand<ExchangeRateDeleteInput, ExchangeRateResult>,
    IUndoableCommand,
    ICommandLogMetadataBuilder<ExchangeRateDeleteInput, ExchangeRateResult>
{
    public string CommandId => "currencies.exchange_rates.delete";

    private ExchangeRateSnapshot? _before;

    public async Task<ExchangeRateResult> ExecuteAsync(ExchangeRateDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var record = await db.Set<ExchangeRate>().FirstOrDefaultAsync(r => r.Id == input.Id && r.DeletedAt == null)
            ?? throw CommandHttpException.NotFound("Exchange rate not found");
        _before = UpdateExchangeRateCommand.Snapshot(record);
        record.DeletedAt = DateTimeOffset.UtcNow;
        record.IsActive = false;
        return new ExchangeRateResult(record.Id.ToString());
    }

    public CommandLogMetadata BuildLog(ExchangeRateDeleteInput input, ExchangeRateResult result, CommandContext ctx) => new()
    {
        ActionLabel = "Delete exchange rate",
        ResourceKind = "currencies.exchange_rate",
        ResourceId = result.ExchangeRateId,
        SnapshotBefore = _before,
    };

    public async Task UndoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var before = log.GetSnapshotBefore<ExchangeRateSnapshot>();
        if (before is null) return;
        var db = services.GetRequiredService<AppDbContext>();
        var record = await db.Set<ExchangeRate>().FirstOrDefaultAsync(r => r.Id == Guid.Parse(before.Id));
        if (record is null) return;
        record.DeletedAt = null;
        record.IsActive = before.IsActive;
        record.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task RedoAsync(ActionLog log, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var record = await db.Set<ExchangeRate>().FirstOrDefaultAsync(r => r.Id == Guid.Parse(log.ResourceId!));
        if (record is null) return;
        record.DeletedAt = DateTimeOffset.UtcNow;
        record.IsActive = false;
    }
}
