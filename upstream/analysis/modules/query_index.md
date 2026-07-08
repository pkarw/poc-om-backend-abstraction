# Port contract — query_index

> Upstream commit adc9da27759e357febe9ed8d4b7182040d127349 (pinned adc9da2). Source: packages/core/src/modules/query_index/. Regenerate via om-analyze-module.

## Overview

`query_index` is the **hybrid read-model / query layer**. It maintains a generic JSONB projection table (`entity_indexes`) that flattens every indexed record's **base columns** plus its **custom-field values** (under `cf:<key>` keys) plus optional **translations** (`l10n:<locale>:<field>`) and an aggregate `search_text` field into a single `doc jsonb`. List endpoints then filter/sort/paginate against this projection — crucially including **filtering and sorting by custom fields** — instead of touching the base tables + EAV joins on every read. It also owns long-running reindex/purge jobs, per-scope coverage snapshots, indexer error/status logs, and a `search_tokens` inverted index for token search.

Two write-side hooks are the contract the CRUD factory depends on (spec 03 R49–R53): on every create/update the factory emits `query_index.upsert_one` (→ `upsertIndexRow`), and on delete `query_index.delete_one` (→ `markDeleted`). This keeps `entity_indexes` read-your-writes consistent (R50). In this .NET port those hooks are the `ICrudIndexer` seam that the factory awaits inline.

## Dependencies

| module id | why needed | must be ported first |
|---|---|---|
| entities | reads `custom_field_values` to build `cf:<key>` doc keys; `custom_field_defs` for `includeCustomFields` resolution | Yes (this port references it for the value reader) |
| auth | status/reindex/purge routes require auth + `query_index.*` features | Yes (RBAC) |
| directory | `resolveOrganizationScopeForRequest` scopes status by org | Yes |
| Tier-0 shared runtime | `resolveEntityTableName` (base-table resolution), encryption/indexDoc, kysely query builder, event bus | Yes (scaffold) |

## Tables (exact DDL — MikroORM `data/entities.ts`)

### entity_indexes — the generic JSONB projection (the heart)
```
id uuid pk default gen_random_uuid(),
entity_type text not null,                 -- '<module>:<entity>'
entity_id text not null,                    -- record id as text (uuid/int compatible)
organization_id uuid null,
organization_id_coalesced uuid generated always as (coalesce(organization_id,'00000000-0000-0000-0000-000000000000'::uuid)) stored,
tenant_id uuid null,
doc jsonb not null,                         -- flattened base + cf:<key> + l10n + search_text
embedding jsonb null,                       -- optional vector/metadata from secondary indexers
index_version int not null default 1,
created_at timestamptz not null,
updated_at timestamptz not null,
deleted_at timestamptz null
```
Indexes: `entity_indexes_type_idx(entity_type)`, `entity_indexes_entity_idx(entity_id)`, `entity_indexes_org_idx(organization_id)`, `entity_indexes_type_tenant_idx(entity_type,tenant_id)`, unique `entity_indexes_type_entity_org_coalesced_unique(entity_type,entity_id,organization_id_coalesced)` (the upsert conflict target — one row per record per org bucket, NULL org collapsing to the zero-uuid bucket), plus 6 `customers:*` partial covering indexes (`... include (doc) where deleted_at is null and entity_type = 'customers:customer_*' ...`) — carried verbatim for the customers list hot path.

### entity_index_jobs — reindex/purge progress
```
id uuid pk, entity_type text not null, organization_id uuid null, tenant_id uuid null,
partition_index int null, partition_count int null, processed_count int null, total_count int null,
heartbeat_at timestamptz null, status text not null ('reindexing'|'purging'),
started_at timestamptz not null, finished_at timestamptz null
```
Unique `entity_index_jobs_scope_unique` on `(entity_type, coalesce(organization_id,zero), coalesce(tenant_id,zero), coalesce(partition_index,-1), coalesce(partition_count,-1))` — atomic per-scope upsert; two schedulers cannot double-insert. Index `entity_index_jobs_type_idx(entity_type)`, `entity_index_jobs_org_idx(organization_id)`.

### entity_index_coverage — per-scope base-vs-indexed counts
```
id uuid pk, entity_type text not null, tenant_id uuid null, organization_id uuid null,
with_deleted boolean default false, base_count int default 0, indexed_count int default 0,
vector_indexed_count int default 0, refreshed_at timestamptz not null
```
Unique `entity_index_coverage_scope_idx(entity_type,tenant_id,organization_id,with_deleted)`. `ok = baseCount == indexedCount` drives the status UI's "partial index" banner.

### indexer_error_logs / indexer_status_logs — observability
```
error: id uuid pk, source text, handler text, entity_type text null, record_id text null,
  tenant_id uuid null, organization_id uuid null, payload jsonb null, message text, stack text null, occurred_at timestamptz
status: id uuid pk, source text, handler text, level text ('info'), entity_type text null, record_id text null,
  tenant_id uuid null, organization_id uuid null, message text, details jsonb null, occurred_at timestamptz
```
Indexes on `source` and `occurred_at`. "Index errors never fail the write" — the subscribers/indexer swallow exceptions and record an `indexer_error_logs` row.

### search_tokens — inverted token index (secondary)
```
id uuid pk, entity_type text, entity_id text, organization_id uuid null, tenant_id uuid null,
field text, token_hash text, token text null, created_at timestamptz
```
Indexes: `search_tokens_lookup_idx(entity_type,field,token_hash,tenant_id,organization_id)`, `search_tokens_entity_idx(entity_type,entity_id)`, `search_tokens_tenant_token_hash_idx(tenant_id,token_hash)`. Powers `like`/`ilike` token search when the search module is enabled (EXISTS subquery over hashes). PARITY-TODO in this port.

## The DOCUMENT model (`lib/document.ts` + `lib/indexer.ts::buildIndexDoc`)

`buildIndexDoc(entityType, recordId, org, tenant)` composes `doc` from three sources, snake_case keys as stored:
1. **Base row** — `select * from <baseTable> where id = recordId` (`resolveEntityTableName`). For `customers:customer_person_profile` / `customer_company_profile` the parent `customer_entities` row is merged in first so search sees the combined record.
2. **Custom-field values** — `select field_key, value_* from custom_field_values where entity_id = <entityType> and record_id = <recordId>` scoped org (`= org OR null`) and tenant (`= tenant OR null`). Each becomes `doc["cf:<key>"]`; value is read with priority **`value_bool ?? value_int ?? value_float ?? value_text ?? value_multiline`**. One row → scalar; multiple rows for a key → **array**.
3. **Translations** — `entity_translations.translations` → `doc["l10n:<locale>:<field>"]` for non-empty strings (PARITY-TODO here).

Then `attachAggregateSearchField(doc)` builds `doc["search_text"]` = newline-joined, case-insensitively-deduped string values across all fields **except** `search_text`, `id`, `*_id`, `*.id`, `*_at`, and `tenant_id`/`organization_id` (arrays contribute their string elements). Finally `encryptIndexDocForStorage` encrypts flagged fields (PARITY-TODO — clean seam, not reproduced).

`document.ts::buildIndexDocument(baseRow, cfValues, scope)` is the pure/testable variant: same merge + `isScopedValueVisible` gate (a field row is visible when its org/tenant is null or equals the scope), same singleton-vs-array collapse, same `attachAggregateSearchField`.

**Doc shape (example):**
```json
{ "id":"<uuid>", "name":"Acme", "email":"a@b.c",
  "cf:priority":3, "cf:labels":["a","b"], "search_text":"Acme\na@b.c\na\nb" }
```

## The INDEXER (`lib/indexer.ts` — the `ICrudIndexer` write side)

- **`upsertIndexRow`** builds the doc; if the base row vanished (`doc == null`) it deletes the projection row (and clears search tokens); otherwise it upserts one `entity_indexes` row keyed by the coalesced-org unique index (`onConflict (entity_type, entity_id, organization_id_coalesced) do update set doc, index_version=1, updated_at=now(), deleted_at=null`), with a legacy update-then-insert fallback. Scope match on read uses `tenant_id is not distinct from <tenant>` and org `= org` / `is null`. Returns `{created, revived, existed, wasDeleted}`.
- **`markDeleted`** (delete side) removes the projection row (hard delete of the index row) and its search tokens; reports `wasActive`.
- The heavy **search-token rebuild** is deferrable (the subscriber runs it out-of-band) so the projection update — which list reads depend on — stays low-latency.

## The QUERY ENGINE (`lib/engine.ts`)

The engine runs list queries against `entity_indexes` (aliased `ei`), or joins the projection onto a base/custom-entity-storage query. Normalized filter fields: a `cf_<key>` param is rewritten to `cf:<key>` (`normalizeField`). For each filter:
- **base/unknown field** → compare `(doc ->> '<field>')` (text) with the value.
- **`cf:<key>` field** → text expr `(doc ->> 'cf:<key>')` **plus** array-contains `(doc -> 'cf:<key>') @> '[value]'::jsonb`, OR-joined for `eq`/`in` so a value stored as a singleton or inside a multi-value array both match.

Operators: `eq` (text `=` OR array-contains), `ne` (`<>`), `in`/`nin`, `like`/`ilike` (or a `search_tokens` EXISTS subquery when search is enabled), `exists` (`is [not] null`), `gt`/`gte`/`lt`/`lte` (text comparison — a known limitation for numeric cf values). Sorting: `id` → `ei.entity_id`; `created_at`/`updated_at`/`deleted_at` → the column; `cf:<key>` and other fields → `(doc ->> key)` text order. Scope: always `entity_type = ?`, `tenant_id = ?` (tenant required), org via `applyOrganizationScope` (`in (ids)` + optional `is null`), and `deleted_at is null` unless `withDeleted`. Paging: `page`/`pageSize` (default 1/20) → `limit`/`offset`. Selection projects requested fields, mapping `cf:<key>` back out via `doc ->>`/`doc ->`.

## Events

`query_index.upsert_one` (persistent:false — projection maintenance on create/update), `query_index.delete_one` (persistent:false — delete), `query_index.reindex` (persistent:true — full/partitioned rebuild), `query_index.purge` (persistent:true — drop a scope's index rows), `query_index.coverage.refresh` / `query_index.coverage.warmup` (recompute coverage snapshots). `eventNameFromEntity(entityType, action)` → `<module>.<entity>.<action>`.

## API + ACL

Routes (all auth-required): `GET /api/query_index/status` (`query_index.status.view`) — per-entity `{baseCount, indexCount, vectorCount, ok, job, refreshedAt}` items + recent errors/logs, `x-om-partial-index` header when base≠indexed; `POST /api/query_index/reindex` (`query_index.reindex`) — validates `entityType`, queues `query_index.reindex` per partition; `POST /api/query_index/purge` (`query_index.purge`) — queues `query_index.purge`. ACL features (`acl.ts`): `query_index.status.view`, `query_index.reindex` (dependsOn status.view), `query_index.purge` (dependsOn status.view). `setup.ts`: admin role default `query_index.*`.

## .NET port mapping

- **6 entities** in `Data/Entities.cs`; **byte-exact raw-SQL migration** `20260707060000_AddQueryIndexModule` (coalesced generated column, unique conflict target, all listed indexes incl. the 6 customers partial indexes). `QueryIndexModule : IModule` (Id `query_index`, 3 ACL features, `ConfigureModel`).
- **`ICrudIndexer` → `QueryIndexCrudIndexer`** (registered last-wins over Core no-op): `UpsertOneAsync` builds the doc (base row via `IIndexBaseRowResolver` + `cf:<key>` from `custom_field_values`, value priority + singleton/array + `search_text`) and upserts an `entity_indexes` row; `DeleteOneAsync` removes it. Wrapped so index errors never fail the write (best-effort `indexer_error_logs`).
- **`IQueryIndexEngine`** (`Lib/QueryIndexEngine.cs`): `QueryAsync(request)` → matching record ids + total from `entity_indexes`, filtering/sorting by base fields and `cf:<key>` (operators eq/ne/in/nin/like/ilike/exists/gt/gte/lt/lte; array-contains for cf eq/in). A module's list opts in via `CrudConfig.UseIndexList = true`; Core's `ICrudIndexQuery` seam (`QueryIndexCrudListQuery`) adapts `CrudListQuery` (`cf_*` filters, sort, paging, scope) to the engine so e.g. **customers people/companies** can filter/sort on custom fields.
- **Routes** `GET /api/query_index/status`, `POST reindex`, `POST purge`; **CLI** `query_index reindex`.

## PARITY-TODO (clean seams, documented; no behavioural change)

- **Engine SQL pushdown**: the .NET engine scopes via EF LINQ then evaluates doc filters/sorts in memory (portable across InMemory tests + Postgres). Native `jsonb ->>` / `@>` pushdown + the coalesced-org partial indexes are a perf seam.
- **search_tokens** inverted index + token `like`/`ilike` EXISTS search (search module).
- **Field-value / index-doc encryption** (`encryptIndexDocForStorage` / DEKs).
- **Reindex/purge jobs**: routes/CLI are best-effort (synchronous reindex over storage-backed records); partitioned worker jobs, `entity_index_jobs` heartbeat/progress, coverage snapshots + warmup, and the vector/fulltext status columns are seams.
- **System-entity base rows**: default `IIndexBaseRowResolver` reads `custom_entities_storage`; system tables (customers, …) register their own resolver when ported.
- **Translations** `l10n:*` doc keys.
