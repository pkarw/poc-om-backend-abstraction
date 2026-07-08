namespace OpenMercato.Modules.Dictionaries.Commands;

// ---- Dictionary write commands -------------------------------------------------------------------

public sealed record DictionaryCreateInput(
    string Key,
    string Name,
    string? Description,
    bool? IsSystem,
    bool? IsActive,
    string? EntrySortMode);

public sealed record DictionaryUpdateInput(
    Guid Id,
    string? Key,
    string? Name,
    string? Description,
    bool? IsActive,
    string? EntrySortMode,
    IReadOnlySet<string> Provided);

public sealed record DictionaryDeleteInput(Guid Id);

public sealed record DictionaryResult(string Id);

// ---- Entry write commands ------------------------------------------------------------------------

public sealed record DictionaryEntryCreateInput(
    Guid DictionaryId,
    string Value,
    string? Label,
    string? Color,
    string? Icon,
    int? Position);

public sealed record DictionaryEntryUpdateInput(
    Guid Id,
    string? Value,
    string? Label,
    string? Color,
    string? Icon,
    int? Position,
    bool? IsDefault,
    IReadOnlySet<string> Provided);

public sealed record DictionaryEntryDeleteInput(Guid Id);

public sealed record DictionaryEntryResult(string EntryId);

// ---- Entry operations ----------------------------------------------------------------------------

public sealed record ReorderEntryPosition(Guid Id, int Position);

public sealed record ReorderDictionaryEntriesInput(
    Guid DictionaryId,
    Guid TenantId,
    Guid OrganizationId,
    IReadOnlyList<ReorderEntryPosition> Entries);

public sealed record ReorderDictionaryEntriesResult(string DictionaryId, IReadOnlyList<string> UpdatedIds);

public sealed record SetDefaultDictionaryEntryInput(
    Guid DictionaryId,
    Guid TenantId,
    Guid OrganizationId,
    Guid EntryId);

public sealed record SetDefaultDictionaryEntryResult(string DictionaryId, string EntryId, IReadOnlyList<string> ClearedIds);

/// <summary>Snapshot persisted for entry undo/redo (upstream <c>DictionaryEntrySnapshot</c>).</summary>
public sealed record DictionaryEntrySnapshot(
    Guid Id,
    Guid DictionaryId,
    Guid OrganizationId,
    Guid TenantId,
    string Value,
    string Label,
    string? Color,
    string? Icon,
    int Position,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
