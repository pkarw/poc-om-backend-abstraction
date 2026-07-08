using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Api;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Commands;

/// <summary>
/// Customer-settings write commands — the port of upstream <c>commands/settings.ts</c>. Each upserts
/// the single per-(tenant,org) <c>customer_settings</c> row for one facet (address format, stuck
/// threshold, dictionary sort modes). Upstream registers no <c>buildLog</c>/undo for these, so they
/// write no action-log row (not undoable).
/// </summary>
internal static class SettingsWriter
{
    public static async Task<CustomerSettings> UpsertAsync(AppDbContext db, Guid tenantId, Guid organizationId)
    {
        var settings = await db.Set<CustomerSettings>().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.OrganizationId == organizationId);
        if (settings is null)
        {
            var now = DateTimeOffset.UtcNow;
            settings = new CustomerSettings { Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId, CreatedAt = now, UpdatedAt = now };
            db.Set<CustomerSettings>().Add(settings);
        }
        return settings;
    }
}

/// <summary><c>customers.settings.save</c> — persist the address-format preference.</summary>
public sealed class SaveCustomerSettingsCommand : ICommand<SettingsSaveInput, SettingsSaveResult>
{
    public string CommandId => "customers.settings.save";

    public async Task<SettingsSaveResult> ExecuteAsync(SettingsSaveInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var settings = await SettingsWriter.UpsertAsync(db, input.TenantId, input.OrganizationId);
        if (settings.AddressFormat != input.AddressFormat) { settings.AddressFormat = input.AddressFormat; settings.UpdatedAt = DateTimeOffset.UtcNow; }
        return new SettingsSaveResult(settings.Id.ToString(), settings.AddressFormat);
    }
}

/// <summary><c>customers.settings.save_stuck_threshold</c> — persist the deals stuck-threshold days.</summary>
public sealed class SaveStuckThresholdCommand : ICommand<StuckThresholdSaveInput, StuckThresholdSaveResult>
{
    public string CommandId => "customers.settings.save_stuck_threshold";

    public async Task<StuckThresholdSaveResult> ExecuteAsync(StuckThresholdSaveInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var settings = await SettingsWriter.UpsertAsync(db, input.TenantId, input.OrganizationId);
        if (settings.StuckThresholdDays != input.StuckThresholdDays) { settings.StuckThresholdDays = input.StuckThresholdDays; settings.UpdatedAt = DateTimeOffset.UtcNow; }
        return new StuckThresholdSaveResult(settings.Id.ToString(), settings.StuckThresholdDays);
    }
}

/// <summary><c>customers.settings.save_dictionary_sort_modes</c> — persist the merged sort-mode map.</summary>
public sealed class SaveDictionarySortModesCommand : ICommand<DictionarySortModesSaveInput, DictionarySortModesSaveResult>
{
    public string CommandId => "customers.settings.save_dictionary_sort_modes";

    public async Task<DictionarySortModesSaveResult> ExecuteAsync(DictionarySortModesSaveInput input, CommandContext ctx, IServiceProvider services)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var settings = await SettingsWriter.UpsertAsync(db, input.TenantId, input.OrganizationId);
        settings.DictionarySortModes = DictionaryContext.SerializeSortModes(input.DictionarySortModes);
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        return new DictionarySortModesSaveResult(settings.Id.ToString(), input.DictionarySortModes);
    }
}
