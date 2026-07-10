using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Entities.Data;
using OpenMercato.Modules.QueryIndex.Data;
using OpenMercato.Modules.QueryIndex.Lib;

namespace OpenMercato.Modules.QueryIndex.Crud;

/// <summary>
/// The real <see cref="ICrudIndexer"/> — registered by the query_index module (last-wins over Core's
/// <c>NoopCrudIndexer</c>). It is the write side of upstream <c>query_index.upsert_one</c> /
/// <c>query_index.delete_one</c> (indexer.ts): after every command-backed create/update the CRUD
/// factory awaits <see cref="UpsertOneAsync"/>, and after every delete <see cref="DeleteOneAsync"/>, so
/// the shared <c>entity_indexes</c> read model stays read-your-writes consistent (spec 03 R49–R53).
///
/// UpsertOne builds the doc from the base row (<see cref="IIndexBaseRowResolver"/>) + the record's
/// <c>custom_field_values</c> (projected under <c>cf:&lt;key&gt;</c>, value priority
/// <c>bool→int→float→text→multiline</c>, singleton/array) + <c>search_text</c>, then upserts one
/// <c>entity_indexes</c> row for the (type, record, org, tenant) scope. Index errors never fail the
/// write: all work is wrapped and a best-effort <c>indexer_error_logs</c> row is recorded on failure.
/// </summary>
public sealed class QueryIndexCrudIndexer : ICrudIndexer
{
    private readonly AppDbContext _db;
    private readonly IIndexBaseRowResolver _baseRows;

    public QueryIndexCrudIndexer(AppDbContext db, IIndexBaseRowResolver baseRows)
    {
        _db = db;
        _baseRows = baseRows;
    }

    public async Task UpsertOneAsync(
        string entityType, string recordId, Guid? organizationId, Guid? tenantId, string crudAction, CancellationToken ct = default)
    {
        try
        {
            var baseRow = await _baseRows.LoadAsync(entityType, recordId, organizationId, tenantId, ct);
            if (baseRow is null)
            {
                // Base record gone → remove the projection row (upstream doc == null path).
                await RemoveRowAsync(entityType, recordId, organizationId, tenantId, ct);
                return;
            }

            var cfValues = await LoadCustomFieldValuesAsync(entityType, recordId, organizationId, tenantId, ct);
            var doc = IndexDocument.Build(baseRow, cfValues, organizationId, tenantId);
            var json = DocJson.Serialize(doc);

            var existing = await FindRowAsync(entityType, recordId, organizationId, tenantId, ct);
            var now = DateTimeOffset.UtcNow;
            if (existing is null)
            {
                _db.Set<EntityIndexRow>().Add(new EntityIndexRow
                {
                    Id = Guid.NewGuid(),
                    EntityType = entityType,
                    EntityId = recordId,
                    OrganizationId = organizationId,
                    TenantId = tenantId,
                    Doc = json,
                    IndexVersion = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                    DeletedAt = null,
                });
            }
            else
            {
                existing.Doc = json;
                existing.TenantId = tenantId;
                existing.IndexVersion = 1;
                existing.UpdatedAt = now;
                existing.DeletedAt = null;
            }
            await _db.SaveChangesAsync(ct);

            // Write side of the tokenized search index (search-tokens.ts::replaceSearchTokensForRecord).
            // Best-effort and separate from the doc save, matching upstream indexer.ts (token replace is
            // wrapped in its own try/catch so token failures never roll back the entity_indexes write).
            await ReplaceSearchTokensAsync(entityType, recordId, organizationId, tenantId, doc, ct);
        }
        catch (Exception ex)
        {
            await RecordErrorAsync("query_index.upsert_one", entityType, recordId, organizationId, tenantId, ex, ct);
        }
    }

    public async Task DeleteOneAsync(
        string entityType, string recordId, Guid? organizationId, Guid? tenantId, CancellationToken ct = default)
    {
        try
        {
            await RemoveRowAsync(entityType, recordId, organizationId, tenantId, ct);
        }
        catch (Exception ex)
        {
            await RecordErrorAsync("query_index.delete_one", entityType, recordId, organizationId, tenantId, ex, ct);
        }
    }

    private async Task RemoveRowAsync(string entityType, string recordId, Guid? organizationId, Guid? tenantId, CancellationToken ct)
    {
        // Remove the record's tokens too (search-tokens.ts::deleteSearchTokensForRecord), best-effort.
        await DeleteSearchTokensAsync(entityType, recordId, organizationId, tenantId, ct);

        var existing = await FindRowAsync(entityType, recordId, organizationId, tenantId, ct);
        if (existing is null) return;
        _db.Set<EntityIndexRow>().Remove(existing);
        await _db.SaveChangesAsync(ct);
    }

    // search-tokens.ts::replaceSearchTokensForRecord — delete the record's existing tokens for the doc's
    // fields in scope, insert the freshly tokenized rows, in one unit of work. Scope match is null-aware
    // (EF null-semantics turns `== null` into `IS NULL`, mirroring upstream `is not distinct from`).
    private async Task ReplaceSearchTokensAsync(
        string entityType, string recordId, Guid? organizationId, Guid? tenantId,
        IReadOnlyDictionary<string, object?> doc, CancellationToken ct)
    {
        try
        {
            var config = SearchConfig.Resolve();
            if (!config.Enabled) return;

            var rows = SearchTokenRowBuilder.Build(doc, config);
            var docFields = SearchTokenRowBuilder.DocFields(doc);

            var existing = await _db.Set<SearchToken>()
                .Where(t => t.EntityType == entityType && t.EntityId == recordId)
                .Where(t => t.OrganizationId == organizationId)
                .Where(t => t.TenantId == tenantId)
                .Where(t => docFields.Contains(t.Field))
                .ToListAsync(ct);
            if (existing.Count > 0) _db.Set<SearchToken>().RemoveRange(existing);

            var now = DateTimeOffset.UtcNow;
            foreach (var r in rows)
            {
                _db.Set<SearchToken>().Add(new SearchToken
                {
                    Id = Guid.NewGuid(),
                    EntityType = entityType,
                    EntityId = recordId,
                    OrganizationId = organizationId,
                    TenantId = tenantId,
                    Field = r.Field,
                    TokenHash = r.TokenHash,
                    Token = r.Token,
                    CreatedAt = now,
                });
            }
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            // Upstream swallows token-index failures (they must not fail the write). Clear any partial
            // token tracking so the caller's error path can still record its own log if needed.
            try { _db.ChangeTracker.Clear(); } catch { /* ignore */ }
        }
    }

    // search-tokens.ts::deleteSearchTokensForRecord — remove all of a record's tokens in scope.
    private async Task DeleteSearchTokensAsync(
        string entityType, string recordId, Guid? organizationId, Guid? tenantId, CancellationToken ct)
    {
        try
        {
            var tokens = await _db.Set<SearchToken>()
                .Where(t => t.EntityType == entityType && t.EntityId == recordId)
                .Where(t => t.OrganizationId == organizationId)
                .Where(t => t.TenantId == tenantId)
                .ToListAsync(ct);
            if (tokens.Count == 0) return;
            _db.Set<SearchToken>().RemoveRange(tokens);
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            try { _db.ChangeTracker.Clear(); } catch { /* ignore */ }
        }
    }

    private Task<EntityIndexRow?> FindRowAsync(string entityType, string recordId, Guid? organizationId, Guid? tenantId, CancellationToken ct)
    {
        // Scope match (indexer.ts::scopeEntityIndexes): type + id + org(null-aware) + tenant is-not-distinct.
        return _db.Set<EntityIndexRow>()
            .Where(r => r.EntityType == entityType && r.EntityId == recordId)
            .Where(r => r.OrganizationId == organizationId)
            .Where(r => r.TenantId == tenantId)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<List<IndexCustomFieldValue>> LoadCustomFieldValuesAsync(
        string entityType, string recordId, Guid? organizationId, Guid? tenantId, CancellationToken ct)
    {
        // indexer.ts cf query: entity_id=<entityType>, record_id=<recordId>, org (=org OR null / is null),
        // tenant (=tenant OR null / is null). No deleted_at filter (matches upstream).
        var query = _db.Set<CustomFieldValue>().AsNoTracking()
            .Where(v => v.EntityId == entityType && v.RecordId == recordId);

        query = organizationId != null
            ? query.Where(v => v.OrganizationId == organizationId || v.OrganizationId == null)
            : query.Where(v => v.OrganizationId == null);

        query = tenantId != null
            ? query.Where(v => v.TenantId == tenantId || v.TenantId == null)
            : query.Where(v => v.TenantId == null);

        var rows = await query.ToListAsync(ct);

        var result = new List<IndexCustomFieldValue>(rows.Count);
        foreach (var r in rows)
            result.Add(new IndexCustomFieldValue(r.FieldKey, ValueOf(r), r.OrganizationId, r.TenantId));
        return result;
    }

    // Value read priority (indexer.ts): value_bool ?? value_int ?? value_float ?? value_text ?? value_multiline.
    private static object? ValueOf(CustomFieldValue r)
    {
        if (r.ValueBool is not null) return r.ValueBool;
        if (r.ValueInt is not null) return r.ValueInt;
        if (r.ValueFloat is not null) return r.ValueFloat;
        if (r.ValueText is not null) return r.ValueText;
        if (r.ValueMultiline is not null) return r.ValueMultiline;
        return null;
    }

    private async Task RecordErrorAsync(
        string handler, string entityType, string recordId, Guid? organizationId, Guid? tenantId, Exception ex, CancellationToken ct)
    {
        // Best-effort: the write already committed; indexing failures must not surface to the caller.
        try
        {
            _db.ChangeTracker.Clear();
            _db.Set<IndexerErrorLog>().Add(new IndexerErrorLog
            {
                Id = Guid.NewGuid(),
                Source = "query_index",
                Handler = handler,
                EntityType = entityType,
                RecordId = recordId,
                OrganizationId = organizationId,
                TenantId = tenantId,
                Message = ex.Message,
                Stack = ex.StackTrace,
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
        }
        catch { /* swallow — never fail the write */ }
    }
}
