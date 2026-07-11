using System.Text.Json;
using Microsoft.AspNetCore.Http;
using OpenMercato.Core.Commands;

namespace OpenMercato.Core.Crud;

/// <summary>
/// Custom-field wire codec extension point — the port of upstream
/// <c>decorateRecordWithCustomFields</c> / <c>normalizeCustomFieldResponse</c> / <c>splitCustomFieldPayload</c>
/// (packages/shared/src/lib/crud/custom-fields.ts). The CRUD factory calls this on every list/detail
/// read to decorate items with <c>customValues</c>/<c>customFields</c>, and on every create/update to
/// persist <c>cf_*</c> values in the same logical write (spec 02 R44–R47).
///
/// The default <see cref="NoopCrudCustomFields"/> is a no-op so the factory works before the entities
/// module lands; that module later registers a real implementation reading <c>custom_field_defs</c> /
/// <c>custom_field_values</c>. PARITY-TODO: full kind coercion, def-scope precedence, multi-value
/// replace semantics live in the entities port.
/// </summary>
public interface ICrudCustomFields
{
    /// <summary>Decorate a page of list items in place with their custom-field values (batch, N+1-safe).</summary>
    Task MergeIntoListItemsAsync(
        string entityType,
        IReadOnlyList<IDictionary<string, object?>> items,
        CommandContext ctx,
        CancellationToken ct = default);

    /// <summary>Decorate a single detail record in place with its custom-field values.</summary>
    Task MergeIntoDetailAsync(
        string entityType,
        IDictionary<string, object?> item,
        CommandContext ctx,
        CancellationToken ct = default);

    /// <summary>Persist the <c>cf_*</c> custom-field values carried by a create/update body for a record.</summary>
    Task PersistAsync(
        string entityType,
        string recordId,
        JsonElement body,
        CommandContext ctx,
        CancellationToken ct = default);
}

/// <summary>Default no-op custom-field codec (registered until the entities module provides a real one).</summary>
public sealed class NoopCrudCustomFields : ICrudCustomFields
{
    public Task MergeIntoListItemsAsync(string entityType, IReadOnlyList<IDictionary<string, object?>> items, CommandContext ctx, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task MergeIntoDetailAsync(string entityType, IDictionary<string, object?> item, CommandContext ctx, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task PersistAsync(string entityType, string recordId, JsonElement body, CommandContext ctx, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>
/// Query-index projection extension point — the write-side of upstream's <c>query_index.upsert_one</c> /
/// <c>query_index.delete_one</c> maintenance (spec 03 R49–R53). After every command-backed create/update
/// the factory awaits <see cref="UpsertOneAsync"/>, and after every delete <see cref="DeleteOneAsync"/>,
/// so the shared <c>entity_indexes</c> read model stays in sync (read-your-writes, R50).
///
/// The default <see cref="NoopCrudIndexer"/> is a no-op; the query_index module later registers a real
/// implementation. PARITY-TODO: coverage counters, search-token rebuilds, and the "index errors never
/// fail the write" logging live in that port.
/// </summary>
public interface ICrudIndexer
{
    Task UpsertOneAsync(
        string entityType,
        string recordId,
        Guid? organizationId,
        Guid? tenantId,
        string crudAction,
        CancellationToken ct = default);

    Task DeleteOneAsync(
        string entityType,
        string recordId,
        Guid? organizationId,
        Guid? tenantId,
        CancellationToken ct = default);
}

/// <summary>Default no-op indexer (registered until the query_index module provides a real one).</summary>
public sealed class NoopCrudIndexer : ICrudIndexer
{
    public Task UpsertOneAsync(string entityType, string recordId, Guid? organizationId, Guid? tenantId, string crudAction, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DeleteOneAsync(string entityType, string recordId, Guid? organizationId, Guid? tenantId, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>The record ids (already paged, in sort order) + total for an index-backed list (spec 03 R49).</summary>
public sealed record CrudIndexQueryResult(IReadOnlyList<Guid> RecordIds, int Total);

/// <summary>
/// Read-side query-index extension point — the port of upstream's <c>queryEngine</c> list path against
/// <c>entity_indexes</c>. A <see cref="CrudConfig{TEntity}"/> that sets <c>UseIndexList = true</c> lets
/// the CRUD factory resolve matching record ids (filtering/sorting by base fields AND <c>cf:&lt;key&gt;</c>
/// custom fields — the thing base-table SQL cannot do) from the shared read model instead of the base
/// table. The factory then loads those base rows by id, preserving index order.
///
/// The default <see cref="NoopCrudIndexQuery"/> returns <c>null</c> (→ the factory falls back to the base
/// table). The query_index module registers a real implementation adapting <see cref="CrudListQuery"/>
/// (<c>cf_*</c> filters, sort, paging, scope) onto its engine.
/// </summary>
public interface ICrudIndexQuery
{
    /// <summary>Resolve the page of matching record ids + total for a list, or <c>null</c> when not index-backed.</summary>
    Task<CrudIndexQueryResult?> ResolveListAsync(
        string entityType, CrudListQuery query, CommandContext ctx, CancellationToken ct = default);
}

/// <summary>Default: not index-backed (the factory reads the base table). Overridden by query_index.</summary>
public sealed class NoopCrudIndexQuery : ICrudIndexQuery
{
    public Task<CrudIndexQueryResult?> ResolveListAsync(string entityType, CrudListQuery query, CommandContext ctx, CancellationToken ct = default)
        => Task.FromResult<CrudIndexQueryResult?>(null);
}

/// <summary>A request to look up relation-picker option rows (upstream relations/options queryEngine call).</summary>
public sealed record EntityLookupRequest(
    string EntityType,
    Guid? TenantId,
    IReadOnlyList<Guid>? OrganizationIds,
    IReadOnlyList<string> Ids,
    string? Q,
    string LabelField,
    IReadOnlyList<string> Fields,
    int PageSize);

/// <summary>
/// Read-side lookup extension point that returns whole DOC rows (not just ids) for an entity type — the
/// port of upstream's generic <c>queryEngine.query</c> as used by <c>api/relations/options.ts</c> to
/// populate relation-kind custom-field pickers. Distinct from <see cref="ICrudIndexQuery"/> (which returns
/// ids only). The default <see cref="NoopEntityLookupQuery"/> returns nothing; the query_index module
/// registers a real implementation reading the <c>entity_indexes</c> doc store.
/// </summary>
public interface IEntityLookupQuery
{
    /// <summary>Return the projected doc rows (each carries at least <c>id</c> + the requested fields).</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> LookupAsync(EntityLookupRequest req, CancellationToken ct = default);

    /// <summary>Whether any in-scope record of the entity type carries the field (upstream column-probe).</summary>
    Task<bool> FieldExistsAsync(string entityType, string field, Guid? tenantId, IReadOnlyList<Guid>? organizationIds, CancellationToken ct = default);
}

/// <summary>Default: no lookup backing (empty options). Overridden by query_index.</summary>
public sealed class NoopEntityLookupQuery : IEntityLookupQuery
{
    private static readonly IReadOnlyList<IReadOnlyDictionary<string, object?>> Empty = Array.Empty<IReadOnlyDictionary<string, object?>>();
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> LookupAsync(EntityLookupRequest req, CancellationToken ct = default) => Task.FromResult(Empty);
    public Task<bool> FieldExistsAsync(string entityType, string field, Guid? tenantId, IReadOnlyList<Guid>? organizationIds, CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>
/// Bridge that turns an HTTP request into the auth state the CRUD factory needs — the port of upstream
/// <c>withCtx</c> (the request-scoped Awilix container + resolved <c>OrganizationScope</c>). Core owns the
/// factory but MUST NOT depend on the Auth module; Auth (which references Core) registers a real
/// implementation, so the factory resolves this from DI at request time.
///
/// <see cref="ResolveAsync"/> returns the <see cref="CommandContext"/> for an authenticated request
/// (headers populated for optimistic locking) or <c>null</c> when the caller is unauthenticated (→ 401).
/// <see cref="HasAllFeaturesAsync"/> performs the RBAC feature check (→ 403 on failure).
/// </summary>
public interface ICrudRequestContext
{
    Task<CommandContext?> ResolveAsync(HttpContext http);

    Task<bool> HasAllFeaturesAsync(CommandContext ctx, IReadOnlyList<string> features);
}

/// <summary>
/// Fail-closed default: no authenticated principal. Registered in Core so hosts without the Auth module
/// (e.g. the worker) still resolve the service; the Auth module overrides it with the real bridge.
/// </summary>
public sealed class DefaultCrudRequestContext : ICrudRequestContext
{
    public Task<CommandContext?> ResolveAsync(HttpContext http) => Task.FromResult<CommandContext?>(null);

    public Task<bool> HasAllFeaturesAsync(CommandContext ctx, IReadOnlyList<string> features) => Task.FromResult(true);
}
