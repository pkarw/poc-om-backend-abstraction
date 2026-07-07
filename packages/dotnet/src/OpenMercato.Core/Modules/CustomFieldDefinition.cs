namespace OpenMercato.Core.Modules;

/// <summary>
/// A single custom (EAV) field a module attaches to an entity, mirroring upstream
/// <c>ce.ts</c> / <c>data/fields.ts</c> field-set declarations.
/// </summary>
/// <param name="Key">Field key, unique within its <see cref="CustomFieldSet"/> (e.g. <c>"vat_id"</c>).</param>
/// <param name="Kind">
/// Field kind: <c>"text"</c>, <c>"integer"</c>, <c>"boolean"</c>, <c>"select"</c>,
/// <c>"multiline"</c>, ... (mirrors upstream field kinds).
/// </param>
/// <param name="Label">Human-readable label.</param>
/// <param name="Required">Whether a value is required.</param>
/// <param name="Multi">Whether the field holds multiple values.</param>
/// <param name="Options">Allowed options for <c>"select"</c>-like kinds; null otherwise.</param>
public record CustomFieldDefinition(
    string Key,
    string Kind,
    string Label,
    bool Required = false,
    bool Multi = false,
    string[]? Options = null);

/// <summary>
/// A set of custom fields attached to one entity, mirroring upstream
/// <c>ce.ts</c> / <c>data/fields.ts</c>. A module may declare zero or more sets.
///
/// Declaring a set is the "supported surface": the values storage engine (EAV)
/// ships with the entities module port later (PORT-TODO). At this stage the
/// declaration only feeds <see cref="ICustomFieldRegistry"/>.
/// </summary>
/// <param name="EntityId">Target entity id, e.g. <c>"auth:user"</c>.</param>
/// <param name="Fields">The fields attached to that entity.</param>
public record CustomFieldSet(
    string EntityId,
    IReadOnlyList<CustomFieldDefinition> Fields);
