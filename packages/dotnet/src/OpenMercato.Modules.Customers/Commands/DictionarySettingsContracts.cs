using System.Text.Json;

namespace OpenMercato.Modules.Customers.Commands;

// Command input/result contracts for the customers Phase-2 dictionaries & settings write surface.
// Dictionary-entry create/update carry the raw request body (JsonElement) so the handler can honour
// the upstream tri-state semantics for color/icon (absent = keep, null = clear, value = set) exactly.

// ---- Dictionary entries ----------------------------------------------------------------------
public sealed record DictionaryEntryCreateInput(Guid OrganizationId, Guid TenantId, string Kind, JsonElement Body);
public sealed record DictionaryEntryUpdateInput(Guid Id, Guid OrganizationId, Guid TenantId, string Kind, JsonElement Body);
public sealed record DictionaryEntryDeleteInput(Guid Id, Guid OrganizationId, Guid TenantId, string Kind);

/// <summary>Result of create/upsert: mode is <c>created|updated|unchanged</c> (drives 201-vs-200).</summary>
public sealed record DictionaryEntryWriteResult(
    string EntryId, Guid OrganizationId, string Mode, string Value, string Label, string? Color, string? Icon);

public sealed record DictionaryEntryUpdateResult(
    string EntryId, bool Changed, string Value, string Label, string? Color, string? Icon);

public sealed record DictionaryEntryDeleteResult(string EntryId);

/// <summary>Undo/redo snapshot for a customer dictionary entry.</summary>
public sealed record DictionaryEntrySnapshot(
    Guid Id, Guid OrganizationId, Guid TenantId, string Kind, string Value, string NormalizedValue,
    string Label, string? Color, string? Icon, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

// ---- Dictionary kind settings ----------------------------------------------------------------
public sealed record KindSettingsUpsertInput(
    Guid OrganizationId, Guid TenantId, string Kind, string? SelectionMode, bool? VisibleInTags, int? SortOrder);

public sealed record KindSettingsUpsertResult(
    string SettingId, bool Created, string Kind, string SelectionMode, bool VisibleInTags, int SortOrder);

public sealed record KindSettingSnapshot(
    Guid Id, Guid OrganizationId, Guid TenantId, string Kind, string SelectionMode, bool VisibleInTags, int SortOrder);

// ---- Settings --------------------------------------------------------------------------------
public sealed record SettingsSaveInput(Guid OrganizationId, Guid TenantId, string AddressFormat);
public sealed record SettingsSaveResult(string SettingsId, string AddressFormat);

public sealed record StuckThresholdSaveInput(Guid OrganizationId, Guid TenantId, int StuckThresholdDays);
public sealed record StuckThresholdSaveResult(string SettingsId, int StuckThresholdDays);

public sealed record DictionarySortModesSaveInput(Guid OrganizationId, Guid TenantId, IReadOnlyDictionary<string, string> DictionarySortModes);
public sealed record DictionarySortModesSaveResult(string SettingsId, IReadOnlyDictionary<string, string> DictionarySortModes);
