using OpenMercato.Modules.Dictionaries.Lib;

namespace OpenMercato.Modules.Dictionaries.Data;

/// <summary>
/// A named, organization-scoped enumeration — the port of upstream <c>Dictionary</c>
/// (dictionaries/data/entities.ts). Owns an ordered set of <see cref="DictionaryEntry"/> rows.
/// Byte-exact table/columns are created by the raw-SQL migration <c>AddDictionariesModule</c>;
/// EF only maps the runtime model (see <see cref="DictionariesModule.ConfigureModel"/>).
/// </summary>
public sealed class Dictionary
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary><c>'default' | 'hidden'</c> (upstream <c>DictionaryManagerVisibility</c>).</summary>
    public string ManagerVisibility { get; set; } = "default";

    /// <summary>One of <see cref="DictionaryEntrySortModes"/>; default <c>label_asc</c>.</summary>
    public string EntrySortMode { get; set; } = DictionaryEntrySortModes.Default;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>
/// A single option within a <see cref="Dictionary"/> — the port of upstream <c>DictionaryEntry</c>.
/// Deduped per dictionary+scope by <see cref="NormalizedValue"/>; carries appearance
/// (color/icon/position) and at most one <see cref="IsDefault"/> per dictionary (partial unique index).
/// </summary>
public sealed class DictionaryEntry
{
    public Guid Id { get; set; }
    public Guid DictionaryId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string Value { get; set; } = string.Empty;
    public string NormalizedValue { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public int Position { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
