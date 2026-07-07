# Data Layer: Entities, Migrations, Multi-Tenancy, Custom Fields, Query Engine, Commands

> Analyzed at upstream commit adc9da27759e357febe9ed8d4b7182040d127349 (2026-07-01). Regenerate via the om-sync-upstream skill.

## Purpose

Open Mercato's data layer is PostgreSQL-only, built on **MikroORM v7** (PostgreSqlDriver) with raw SQL escape hatches via **Kysely** (`em.getKysely()`). It provides:

1. **Per-module MikroORM entities** (`data/entities.ts`) with a rigid naming convention (snake_case tables/columns, uuid PKs, `created_at`/`updated_at`/`deleted_at` soft-delete columns).
2. **Per-module migrations** (`mercato db generate` / `mercato db migrate`) with a separate migration-tracking table per module.
3. **Row-level multi-tenancy** via `tenant_id` + `organization_id` uuid columns on every scoped table, enforced centrally by the query engine, the CRUD factory, and command-level scope guards (there is no Postgres RLS — enforcement is application code).
4. **Custom entities & custom fields** (`entities` module): EAV storage (`custom_field_values`), JSONB doc storage (`custom_entities_storage`), and per-scope field definitions (`custom_field_defs`).
5. **A query engine abstraction** (`QueryEngine.query(entityId, opts)`) with two implementations: `BasicQueryEngine` (joins EAV tables live) and `HybridQueryEngine` (query_index module — reads flattened JSONB projections from `entity_indexes`).
6. **A Command pattern** (`CommandBus` + `CommandHandler`) for all writes, with snapshots, undo/redo, action logs, and atomic multi-phase flushing (`withAtomicFlush`).

A port must reproduce the **exact table/column names** and the **scoping semantics** documented here — the HTTP APIs, indexers, and cross-module joins all assume them.

## Key source locations

| Path (upstream repo root) | Contents |
|---|---|
| `packages/core/src/modules/directory/data/entities.ts` | `Tenant`, `Organization` entities (tables `tenants`, `organizations`) |
| `packages/core/src/modules/directory/utils/organizationScope.ts` | `OrganizationScope` resolution (selected/filter/allowed org ids, descendant expansion, caching) |
| `packages/core/src/modules/directory/utils/scopeCookies.ts` | `om_selected_org` / `om_selected_tenant` cookie parsing |
| `packages/core/src/modules/entities/data/entities.ts` | `custom_field_defs`, `custom_field_entity_configs`, `custom_entities`, `custom_entities_storage`, `custom_field_values`, `encryption_maps` |
| `packages/core/src/modules/entities/lib/helpers.ts` | `setRecordCustomFields()` — EAV write path (kind→column mapping, multi-value replace, transaction) |
| `packages/core/src/modules/entities/lib/install-from-ce.ts` | Installs `ce.ts` declarations (custom entities + field sets) per tenant with checksum caching |
| `packages/core/src/modules/customers/ce.ts` | Example `ce.ts` declaration (array of `CustomEntitySpec`) |
| `packages/shared/src/modules/entities.ts` | `EntityId`, `CustomFieldKind`, `CustomFieldDefinition`, `CustomFieldSet`, `CustomEntitySpec`, `EntityExtension` types |
| `packages/shared/src/lib/query/types.ts` | `QueryEngine`, `QueryOptions`, `QueryResult`, `Filter`, `Where`, `Sort`, `Page` contracts |
| `packages/shared/src/lib/query/engine.ts` | `BasicQueryEngine`, `resolveEntityTableName`, `ENTITY_ID_PATTERN` |
| `packages/core/src/modules/query_index/data/entities.ts` | `entity_indexes`, `entity_index_jobs`, `entity_index_coverage`, `indexer_error_logs`, `indexer_status_logs`, `search_tokens` |
| `packages/core/src/modules/query_index/lib/engine.ts` | `HybridQueryEngine` (2286 lines; index-backed query path, custom-entity doc path, coverage fallback) |
| `packages/core/src/modules/query_index/lib/indexer.ts` | `buildIndexDoc`, `upsertIndexRow`, `markDeleted`, `reindexSearchTokensForRecord` |
| `packages/core/src/modules/query_index/lib/reindexer.ts` | Full reindex jobs (partitioned, batched, coverage snapshots) |
| `packages/core/src/modules/query_index/lib/jobs.ts` | `entity_index_jobs` upsert with coalesced-scope unique index |
| `packages/core/src/modules/query_index/subscribers/*.ts` | Event handlers: `query_index.upsert_one`, `.delete_one`, `.reindex` (persistent), `.purge` (persistent), `.coverage.refresh`, `.coverage.warmup` |
| `packages/core/src/modules/query_index/di.ts` | Registers `HybridQueryEngine` as `queryEngine` in DI; subscribes to `<module>.<entity>.{created,updated,deleted}` CRUD events |
| `packages/shared/src/lib/data/engine.ts` | `DataEngine` interface + `DefaultDataEngine` (custom fields, doc-storage CRUD, ORM CRUD, event/indexer side effects) |
| `packages/shared/src/lib/commands/{types,command-bus,flush,scope,helpers}.ts` | Command pattern: handler interface, bus pipeline, `withAtomicFlush`, scope guards |
| `packages/core/src/modules/customers/commands/*.ts` | Canonical command implementations (e.g. `tags.ts`, `shared.ts`) |
| `packages/cli/src/lib/db/commands.ts` | `dbGenerate`, `dbMigrate`, `dbGreenfield` — the migration workflow |
| `packages/shared/src/lib/crud/factory.ts` | CRUD route factory: `withCtx()` org-scope computation, list handler → query engine wiring |
| `packages/shared/src/lib/di/container.ts` | `createRequestContainer()`: forks `em` per request, registers `queryEngine` (Basic; overridden by query_index), `dataEngine`, `commandBus` |
| `packages/core/src/modules/<mod>/migrations/` | Per-module migration files + `.snapshot-open-mercato.json` |

## How it works

### 1. Entity conventions (MikroORM)

Every module declares its persistence classes in `data/entities.ts` using decorators from `@mikro-orm/decorators/legacy` (Entity, PrimaryKey, Property, Index, Unique, ManyToOne, OneToMany, OneToOne). Conventions (universally followed — a port must produce identical DDL):

- **Explicit table name, snake_case, plural**: `@Entity({ tableName: 'customer_entities' })`. Core module prefixes: `customers` module uses `customer_*` (`customer_entities`, `customer_people`, `customer_companies`, `customer_deals`, `customer_activities`, `customer_tags`, `customer_tag_assignments`, `customer_dictionary_entries`, `customer_pipelines`, ...); `auth` uses unprefixed (`users`, `roles`, `user_roles`, `sessions`, `role_acls`, `user_acls`, ...); `directory` uses `tenants`, `organizations`.
- **PK**: always `@PrimaryKey({ type: 'uuid', defaultRaw: 'gen_random_uuid()' })` → column `id uuid DEFAULT gen_random_uuid()`. The **database** generates the uuid (Postgres `gen_random_uuid()`, i.e. pgcrypto/13+ builtin).
- **Column names**: explicit snake_case via `@Property({ name: 'display_name', ... })`; TS property camelCase. Types are Postgres-native: `text` (never varchar), `boolean`, `int`, `float`, `uuid`, `jsonb`/`json`, `Date` (→ `timestamptz`).
- **Timestamps**: `created_at` (`onCreate: () => new Date()`), `updated_at` (`onUpdate: () => new Date()`). Timestamps are set **application-side** by the ORM, not by DB triggers/defaults (raw Kysely writes use `sql`now()``).
- **Soft delete**: nullable `deleted_at` timestamp on nearly every table. There is **no ORM-level filter** — every read path adds `deleted_at IS NULL` explicitly (query engine does it automatically when the column exists and `withDeleted` is not set).
- **Active flag**: `is_active boolean DEFAULT true` on many tables.
- **Tenancy columns**: `tenant_id uuid` and `organization_id uuid` as **plain scalar columns** (`@Property({ name: 'tenant_id', type: 'uuid' })`), *not* FK relations (only `directory.Organization.tenant` is a real `ManyToOne(() => Tenant)`, producing column `tenant_id` with FK). On business tables they are usually NOT NULL; on the entities/query_index cross-cutting tables they are nullable (NULL = global scope).
- **Indexes**: named explicitly, snake_case (`@Index({ name: 'customer_entities_org_tenant_kind_idx', properties: [...] })`). Partial/covering indexes are declared with raw `expression:` SQL (see `entity_indexes_customer_entity_doc_idx` in query_index and `idx_ce_tenant_org_person_id` in customers).
- **Entity id string** (`EntityId`): `'<module>:<entity>'`, both segments snake_case starting with a lowercase letter — validated by `ENTITY_ID_PATTERN = /^[a-z][a-z0-9_]*:[a-z][a-z0-9_]*$/` (`packages/shared/src/lib/query/engine.ts:50`). A codegen step emits `#generated/entities.ids.generated` exporting `E` (`E.customers.customer_entity === 'customers:customer_entity'`) and `M` (module ids).
- **EntityId → table resolution** (`resolveEntityTableName`, engine.ts:124): PascalCase the entity segment, look up ORM metadata by class name (`CustomerEntity`, then `<Name>Entity`); secondary lookup by candidate table names `<module>_<name>`, `pluralize(name)`, `<module>_<pluralize(name)>`; final fallback = naive pluralization (`s`; trailing `y` → `ies`) with a console warning. `resolveRegisteredEntityTableName` is the strict variant returning `null` (used in security-sensitive spots like the reindexer).

### 2. Migrations workflow

Scripts: root `package.json` → `yarn db:generate` / `yarn db:migrate` → `apps/mercato` → `mercato db generate` / `mercato db migrate` (implemented in `packages/cli/src/lib/db/commands.ts`).

**`dbGenerate`** — per enabled module, **sorted alphabetically by module id**:
1. `MetadataStorage.clear()` before loading each module's entities (so a module's migration only contains its own tables).
2. Load entity classes from `data/entities.ts` (or `data/schema.ts`, `db/entities.ts`, `db/schema.ts`); modules with no entities are skipped.
3. Init MikroORM with `clientUrl: process.env.DATABASE_URL`, `migrations: { path: <module>/migrations, glob: '!(*.d).{ts,js}', tableName: 'mikro_orm_migrations_<sanitized module id>', snapshotName: '.snapshot-open-mercato', dropTables: false }`, `schemaGenerator: { disableForeignKeys: true }`.
4. `orm.migrator.create(...)` diffs entities vs. the module's snapshot JSON and emits `Migration<YYYYMMDDHHMMSS>.ts`; the CLI then renames the file to `Migration<ts>_<moduleId>.ts`, renames the class to `Migration<ts>_<moduleId>`, and rewrites `alter table ... drop constraint X;` to `drop constraint if exists X;` (`makeConstraintDropsIdempotent`).
5. An initial migration is created when the module has no snapshot and no `Migration*.ts` files.

**`dbMigrate`** — per module (same alphabetical order): init ORM with `entities: []` and `snapshot: false` (no diffing, only run committed files), get pending migrations from `mikro_orm_migrations_<module>` and apply them one at a time via `migrator.up({ migrations: [name] })`.

**`dbGreenfield --yes`** — deletes all `Migration*.ts` + snapshot files per module, drops every `mikro_orm_migrations_*` table, drops **all** tables in the current schema (with `session_replication_role='replica'`), then regenerates + reapplies.

Key facts for a port:
- Migration state is **per module** (`mikro_orm_migrations_directory`, `mikro_orm_migrations_customers`, ...), each table having MikroORM's standard columns (`id`, `name`, `executed_at`).
- Migration files live in each module's `migrations/` directory next to the code, e.g. `packages/core/src/modules/directory/migrations/Migration20260607222259_directory.ts`. Format:
  ```ts
  import { Migration } from '@mikro-orm/migrations';
  export class Migration20260607222259_directory extends Migration {
    override up(): void | Promise<void> {
      this.addSql(`alter table "organizations" add "logo_url" text null;`);
    }
    override down(): void | Promise<void> { ... }
  }
  ```
- FKs are generated but applied with `disableForeignKeys: true` semantics during schema ops.
- `sanitizeModuleId` = replace `[^a-z0-9_]` (case-insensitive) with `_`; `validateTableName` enforces `/^[a-zA-Z_][a-zA-Z0-9_]*$/`.
- Env: `DATABASE_URL` (required, throws if unset); optional SSL via `getSslConfig()`.

### 3. Multi-tenancy model (directory module)

**Schema** (`directory/data/entities.ts`):

- `tenants`: `id uuid PK`, `name text`, `is_active bool default true`, `created_at`, `updated_at`, `deleted_at`.
- `organizations`: `id uuid PK`, `tenant_id uuid FK→tenants`, `name text`, `slug text null`, `logo_url text null`, `is_active bool default true`, **materialized tree**: `parent_id uuid null`, `root_id uuid null`, `tree_path text null`, `depth int default 0`, `ancestor_ids jsonb default []`, `child_ids jsonb default []`, `descendant_ids jsonb default []`, timestamps + `deleted_at`. Unique: `organizations_tenant_slug_uniq (tenant_id, slug)`.

**Scoping columns everywhere else**: exact names are `tenant_id` and `organization_id` (both `uuid`). NULL means "global" on the entities/query_index tables; business tables generally require both.

**Request-scope resolution** (`directory/utils/organizationScope.ts`):
- `OrganizationScope = { selectedId: string|null, filterIds: string[]|null, allowedIds: string[]|null, tenantId: string|null }`.
- Inputs: JWT auth (`sub`, `tenantId`, `orgId`, `isSuperAdmin`), RBAC ACL (`rbac.loadAcl(sub, {tenantId, organizationId})` → allowed org list or super-admin), cookies `om_selected_org` (selected org) and `om_selected_tenant` (tenant override — only honored for super admins; non-super-admins are pinned to their token tenant).
- Every allowed/selected org id is **expanded with its persisted `descendant_ids`** (single `SELECT ... WHERE tenant_id=? AND id IN (...) AND deleted_at IS NULL`). Selecting a parent org therefore includes all children.
- `filterIds === null` means "no org restriction" (super admin / all-orgs); `filterIds === []` means "access nothing".
- Cached per-request (WeakMap keyed by the Request) and optionally cross-request (cache tags `org-scope:user:<userId>`, `org-scope:tenant:<tenantId>`, TTL from `OM_ORG_SCOPE_CACHE_TTL_MS`, default 0 = off).

**Enforcement mechanism — three layers (there is no DB-level RLS):**

1. **CRUD factory** (`packages/shared/src/lib/crud/factory.ts`): `withCtx(request)` computes `ctx.organizationIds` from `scope.filterIds` (fallbacks documented at factory.ts:1310-1333). List handlers pass `queryOpts.tenantId = ctx.auth.tenantId` and `queryOpts.organizationIds = ctx.organizationIds` into the query engine. If `ctx.organizationIds` is an **empty array** (user has zero accessible orgs) the route short-circuits with `{ items: [], total: 0, ... }` HTTP 200 and logs a `organization_scope_empty` forbidden event — it never queries. `orm.tenantField` defaults to `'tenantId'`; passing `null` disables automatic tenant scoping for that resource.
2. **Query engine** (both implementations): `tenantId` is **mandatory** — `query()` throws `'QueryEngine: tenantId is now required for all queries (breaking change)...'` when missing. When the base table has a `tenant_id` column: `WHERE base.tenant_id = :tenantId`. When it has `organization_id` and an org scope was provided: `WHERE organization_id IN (:ids)` (plus `OR organization_id IS NULL` when the caller included a null/empty entry in `organizationIds`); an empty scope emits `WHERE 1 = 0`. Same scoping is re-applied to every join alias whose table has the columns, and to `search_tokens` / `entity_indexes` subqueries. Soft delete: `deleted_at IS NULL` unless `withDeleted: true`. `omitAutomaticTenantOrgScope: true` disables the automatic guards (caller must encode visibility in filters; hybrid engine then delegates to basic).
3. **Commands** (`packages/shared/src/lib/commands/scope.ts`): `ensureTenantScope(ctx, tenantId)` → 403 `{ error: 'Forbidden' }` when the actor's token tenant differs; `ensureOrganizationScope(ctx, organizationId)` → 403 unless super admin or `organizationId ∈ scope.allowedIds` (null allowedIds = unrestricted; when `ctx.organizationScope` is null, falls back to comparing against `selectedOrganizationId ?? auth.orgId`, and a completely unscoped call is allowed-but-logged unless `OM_ENFORCE_ORG_SCOPE_STRICT=true`); `ensureSameScope(entity, orgId, tenantId)` → 403 `{ error: 'Cross-tenant relation forbidden' }`. Command handlers additionally include `tenantId`/`organizationId` in every `em.findOne`/`em.find` where clause (see `customers/commands/shared.ts` — `requireCustomerEntity` filters `{ id, deletedAt: null, tenantId, organizationId }` and 404s otherwise).

### 4. Custom entities & custom fields (entities module)

**Table layout** (`entities/data/entities.ts`) — a port must reproduce these exactly:

- `custom_field_defs`: `id uuid PK`, `entity_id text` (the `'<module>:<entity>'` string), `organization_id uuid null`, `tenant_id uuid null` (null = global), `key text`, `kind text` (one of `text|multiline|integer|float|boolean|select|currency|relation|attachment|dictionary`), `config_json json null` (label, options, multi, filterable, listVisible, formEditable, editor, validation rules, dictionaryId, encrypted flag, priority, ...), `is_active bool default true`, `created_at`, `updated_at`, `deleted_at`. Indexes: `cf_defs_entity_tenant_org_idx(entity_id,tenant_id,organization_id)`, `cf_defs_entity_tenant_idx`, `cf_defs_entity_org_idx`, `cf_defs_entity_global_idx(entity_id)`, `cf_defs_entity_key_scope_idx(entity_id,key,tenant_id,organization_id)`, `cf_defs_entity_key_idx(key)`.
- `custom_field_entity_configs`: per-entity UI/config overlay — `id`, `entity_id text`, `organization_id/tenant_id uuid null`, `config_json jsonb null`, `is_active`, timestamps, `deleted_at`.
- `custom_entities` (registry of user-defined entity types): `id`, `entity_id text`, `label text`, `description text null`, `label_field text null`, `default_editor text null` (`markdown|simpleMarkdown|htmlRichText`), `show_in_sidebar bool default false`, `organization_id/tenant_id uuid null`, `is_active`, timestamps, `deleted_at`. Unique index `custom_entities_unique_idx(entity_id, organization_id, tenant_id)`.
- `custom_entities_storage` (JSONB doc store for custom-entity records): `id uuid PK`, `entity_type text` (entity id), `entity_id text` (**record id as text**), `organization_id uuid null`, `tenant_id uuid null`, `doc json`, timestamps, `deleted_at`. Unique `custom_entities_storage_unique_idx(entity_type, entity_id, organization_id)`.
- `custom_field_values` (EAV): `id uuid PK`, `entity_id text` (entity type), `record_id text` (supports uuid or int PKs), `organization_id uuid null`, `tenant_id uuid null`, `field_key text`, and **exactly one typed value column populated**: `value_text text`, `value_multiline text`, `value_int int`, `value_float float`, `value_bool boolean`; `created_at`, `deleted_at` (note: **no `updated_at`**). Indexes: `cf_values_entity_record_tenant_idx(entity_id, record_id, tenant_id)`, `cf_values_entity_record_field_idx(field_key)`.
- `encryption_maps`: `id`, `entity_id text`, `tenant_id/organization_id uuid null`, `fields_json jsonb null` (`[{field, hashField?}]`), `is_active`, timestamps, `deleted_at`; index `encryption_maps_entity_scope_idx(entity_id,tenant_id,organization_id)`.

**Kind → value column** (`entities/lib/helpers.ts:columnFromKind`): `text|select|currency|dictionary → value_text`, `multiline → value_multiline`, `integer → value_int`, `float → value_float`, `boolean → value_bool`, default `value_text`. Without a definition, JS type infers the column (`boolean→value_bool`, integer number→`value_int`, float→`value_float`, else `value_text`). Encrypted fields always store in `value_text`.

**`setRecordCustomFields`** write semantics: loads active defs scoped `organizationId IN (org, NULL)` AND `tenantId IN (tenant, NULL)`, picking per key the def with highest scope score (tenant=+2, org=+1, ties broken by newest `updated_at`). Multi-value (array) input: `nativeDelete` all rows for `(entity_id, record_id, organization_id, tenant_id, field_key)` then insert one row per element — wrapped in a **single DB transaction** (opened only if no ambient transaction). Single value: find-or-create the row, clear all value columns, set the one column. Enforces `MAX_CUSTOM_FIELD_KEYS_PER_RECORD` per write.

**`ce.ts` declarations**: a module exports `entities: CustomEntitySpec[]` (see `customers/ce.ts`) — `{ id: 'customers:customer_deal', label, description, labelField, showInSidebar, defaultEditor?, global?, fields: CustomFieldDefinition[] }`. `installCustomEntitiesFromModules` (run at setup/CLI) aggregates all modules' `customEntities` + `customFieldSets`, and for each entity × scope (global spec → tenantId NULL once; otherwise once per existing tenant): upserts the `custom_entities` row (skipped for "system" ids present in the generated registry) and `ensureCustomFieldDefinitions` upserts `custom_field_defs` rows (organizationId NULL, tenantId = scope). An MD5 checksum of the normalized spec is cached (cache key `custom-entities:v1:<scope>:<entityId>`) to skip unchanged installs.

**Doc-storage CRUD** (`DefaultDataEngine`): `createCustomEntityRecord` rejects ORM-backed system entity ids with HTTP 400 `{ error: 'Records are available for custom entities only', code: 'system_entity_records_blocked', entityId }`; generates a uuid when `recordId` is missing/non-uuid/one of the sentinels `create|new|null|undefined`; normalizes value keys (`cf_x`/`cf:x` → `cf:x`, drops `id`/`entity_id`/`entityId`); upserts into `custom_entities_storage` with `ON CONFLICT (entity_type, entity_id, organization_id) DO UPDATE SET doc, updated_at=now(), deleted_at=NULL`. Update = shallow merge of `doc`. Delete = soft (`deleted_at=now()`) by default, plus best-effort soft delete of matching `custom_field_values`. Legacy dual-write to EAV only when `ENTITIES_BACKCOMPAT_EAV_FOR_CUSTOM=true`.

### 5. Query engine & query_index module

**Contract** (`packages/shared/src/lib/query/types.ts`):

```ts
export interface QueryEngine { query<T>(entity: EntityId, opts?: QueryOptions): Promise<QueryResult<T>> }
export type FilterOp = 'eq'|'ne'|'gt'|'gte'|'lt'|'lte'|'in'|'nin'|'like'|'ilike'|'exists'
export type QueryOptions = {
  fields?: string[]                    // base columns and/or 'cf:<key>'
  includeCustomFields?: boolean | string[]
  filters?: Filter[] | Where           // array or Mongo-style {$eq,$ne,$gt,$gte,$lt,$lte,$in,$nin,$like,$ilike,$exists}, plus $or groups
  sort?: { field: string; dir?: 'asc'|'desc' }[]
  page?: { page?: number; pageSize?: number }
  tenantId?: string                    // REQUIRED at runtime
  organizationId?: string
  organizationIds?: string[]           // takes precedence over organizationId
  omitAutomaticTenantOrgScope?: boolean
  withDeleted?: boolean
  customFieldSources?: QueryCustomFieldSource[]   // pull cf:* from a joined entity
  joins?: QueryJoinEdge[]
  forceCustomEntityStorage?: boolean
  ...
}
export type QueryResult<T> = { items: T[]; page: number; pageSize: number; total: number;
  meta?: { partialIndexWarning?: {...} }; customFieldDefinitions?: ... }
```

**DI wiring**: `createRequestContainer()` registers `queryEngine = new BasicQueryEngine(em)`; `query_index/di.ts` then **replaces** it with `HybridQueryEngine(em, basic, ...)`. Both are Kysely-based (`em.getKysely()`), not ORM queries.

**BasicQueryEngine** (fallback; `packages/shared/src/lib/query/engine.ts`): selects from the resolved base table; applies tenant/org/soft-delete guards via live `information_schema.columns` checks (cached); custom fields are read by **joining EAV live** — per requested key it left-joins `custom_field_defs as cfd_*` (entity_id + key + is_active + tenant match-or-null) and `custom_field_values as cfv_*` (entity_id + field_key + record_id = base.id::text + tenant match-or-null), projecting a typed `CASE cfd.kind WHEN 'integer' THEN value_int::text ... ELSE value_text::text END`, aggregated with `array_agg(DISTINCT ...)`/`max(...)` + a `bool_or(config_json->>'multi')` flag, grouped by `base.id`; multi fields come back as JSON arrays, singletons as scalars. Unknown filter fields fall through to an `EXISTS (SELECT 1 FROM entity_indexes ei WHERE ei.doc ->> field <op> value ...)` subquery. `like/ilike` filters are rewritten to `search_tokens` EXISTS subqueries (token-hash conjunction: `token_hash IN (...) GROUP BY entity_id HAVING count(distinct token_hash) >= N`) when the search subsystem has tokens for the entity. Count = `count(distinct base.id)` when joins may fan out, else `count(*)`. Pagination applied after count; default `page=1`, `pageSize=20`.

**query_index tables** (`query_index/data/entities.ts`):

- `entity_indexes`: `id uuid PK`, `entity_type text`, `entity_id text` (record id as text), `organization_id uuid null`, **`organization_id_coalesced uuid` GENERATED ALWAYS AS (`coalesce(organization_id,'00000000-0000-0000-0000-000000000000'::uuid)`) STORED**, `tenant_id uuid null`, `doc json` (flattened base row + custom fields), `embedding json null`, `index_version int default 1`, timestamps, `deleted_at`. Unique `entity_indexes_type_entity_org_coalesced_unique(entity_type, entity_id, organization_id_coalesced)`; btree indexes on type, entity, org, `(entity_type,tenant_id)`; plus six covering partial indexes for hot customer entity types (raw `INCLUDE ("doc") WHERE deleted_at is null AND entity_type = '...'` expressions).
- `entity_index_jobs`: reindex/purge progress rows — `entity_type`, `organization_id/tenant_id null`, `partition_index/partition_count int null`, `processed_count/total_count int null`, `heartbeat_at`, `status text` (`reindexing|purging`), `started_at`, `finished_at`. Raw unique index over `(entity_type, coalesce(org,zero-uuid), coalesce(tenant,zero-uuid), coalesce(partition_index,-1), coalesce(partition_count,-1))` so `prepareJob` can upsert atomically.
- `entity_index_coverage`: `(entity_type, tenant_id, organization_id, with_deleted)` unique → `base_count`, `indexed_count`, `vector_indexed_count`, `refreshed_at`.
- `indexer_error_logs` / `indexer_status_logs`: diagnostics (source, handler, entity_type, record_id, tenant/org, payload/details, message, stack, occurred_at).
- `search_tokens`: `entity_type text`, `entity_id text`, `organization_id/tenant_id uuid null`, `field text`, `token_hash text`, `token text null`, `created_at`. Indexes: `search_tokens_lookup_idx(entity_type,field,token_hash,tenant_id,organization_id)`, `search_tokens_entity_idx(entity_type,entity_id)`, `search_tokens_tenant_token_hash_idx(tenant_id,token_hash)`.

**Index document** (`buildIndexDoc`): the base row selected as-is (**snake_case keys**), merged with parent `customer_entities` row for the two customer profile types, plus `cf:<key>` entries from `custom_field_values` (scoped `organization_id = X OR IS NULL` / `tenant_id = Y OR IS NULL`; single values scalar, multi as array), plus `l10n:<locale>:<field>` entries from `entity_translations`, plus an aggregate search field; optionally encrypted at rest per `encryption_maps`.

**Write path**: every CRUD write ends in `DataEngine.emitOrmEntityEvent` which (a) emits `<module>.<entity>.<created|updated|deleted>` with payload `{ id, organizationId, tenantId }` and (b) **awaits** `query_index.upsert_one` / `query_index.delete_one` with payload `{ entityType, recordId, organizationId, tenantId, crudAction, coverageBaseDelta? }` for read-your-writes consistency. The `upsert_one` subscriber (on a **forked** EM) resolves scope from the base row when absent, synchronously upserts `entity_indexes` via `INSERT ... ON CONFLICT (entity_type, entity_id, organization_id_coalesced) DO UPDATE`, applies coverage count deltas, then fire-and-forget rebuilds `search_tokens` (delete + chunked insert) and emits `query_index.vectorize_one` + `search.index_record`. `markDeleted` **hard-deletes** the projection row and its tokens. Full reindex (`query_index.reindex`, persistent event) runs partitioned (default 5 partitions) batched (500) jobs tracked in `entity_index_jobs`.

**HybridQueryEngine read path** (`query_index/lib/engine.ts`): decision cascade —
1. `forceCustomEntityStorage` or classified custom entity (active `custom_entities` row for a non-ORM-backed id, or existing `custom_entities_storage` rows) → **doc-storage query**: `FROM custom_entities_storage ce WHERE ce.entity_type=? AND ce.tenant_id=? [org scope] [deleted_at IS NULL]`; `id` maps to `ce.entity_id`, `created_at/updated_at/deleted_at` map to real columns, everything else reads `ce.doc ->> field`; `cf:*` selected via JSONB aliases; count = `count(distinct ce.entity_id)`.
2. Base table missing, or `omitAutomaticTenantOrgScope` → delegate to `BasicQueryEngine`.
3. Query wants custom fields (`cf:*`/`l10n:*` in fields/filters, or explicit `includeCustomFields` array) and entity has active defs: check coverage (`entity_index_coverage`, TTL `QUERY_INDEX_COVERAGE_CACHE_MS` default 5min). No index rows at all → fallback to basic. Coverage gap → schedule auto-reindex and fall back to basic, attaching `meta.partialIndexWarning = { entity, entityLabel, baseCount, indexedCount, scope }` (unless `FORCE_QUERY_INDEX_ON_PARTIAL_INDEXES` forces index usage).
4. Indexed path: `FROM <base> b LEFT JOIN entity_indexes ei ON ei.entity_type=:entity AND ei.entity_id = b.id::text [AND ei.organization_id = b.organization_id AND NOT NULL] [AND ei.tenant_id = b.tenant_id AND NOT NULL] [AND ei.deleted_at IS NULL]`; base columns come from the table, custom fields from `(ei.doc ->> 'cf:key')` (with bare-key coalesce), sorting on doc keys via `(ei.doc ->> field) asc|desc`; tenant/org/soft-delete scopes as in Basic; `customFieldSources` add extra base-table joins each with their own `entity_indexes` alias.

### 6. Transaction patterns (Command pattern)

All mutating API routes execute **commands** through the `CommandBus` (`packages/shared/src/lib/commands/command-bus.ts`). Handler shape (`types.ts`):

```ts
interface CommandHandler<TInput, TResult> {
  readonly id: string                       // e.g. 'customers.tags.create'
  prepare?(input, ctx): { before?: unknown }        // pre-write snapshot
  execute(input, ctx): TResult
  captureAfter?(input, result, ctx): unknown        // post-write snapshot
  buildLog?({input, result, ctx, snapshots}): CommandLogMetadata  // action log row
  undo?({input, ctx, logEntry}): void
  redo?({input, ctx, logEntry}): TResult
}
```

Bus pipeline: command interceptors (before) → `prepare` → `execute` (or `redo` when replaying) → `captureAfter` → `buildLog` → merge metadata (auto undoToken via `randomUUID()`, auto snapshotBefore/After, auto-diffed `changes` map) → persist to `action_logs` via `actionLogService` (the command payload is wrapped in `{ __redoInput: input, ... }` and stored under `command_payload`) → interceptors (after) → CRUD cache invalidation → `dataEngine.flushOrmEntityChanges()` (queued CRUD events/indexer side effects). `undo(undoToken)` atomically claims the log row (`done → undoing` CAS), runs `handler.undo`, marks undone, and writes an inverse trace log.

Handler conventions (see `customers/commands/tags.ts`):
- Validate input with the module's Zod schema (`data/validators.ts`) inside `execute`.
- `ensureTenantScope(ctx, input.tenantId)` + `ensureOrganizationScope(ctx, input.organizationId)` first.
- **Fork the EM**: `const em = (ctx.container.resolve('em') as EntityManager).fork()` — each command gets a fresh unit of work; `ctx.transactionalEm`, when present, must be reused instead (composing commands share one transaction and its row locks).
- Uniqueness/duplicate checks are explicit `em.findOne` + `throw new CrudHttpError(409, { error: '...' })`.
- Multi-phase writes use `withAtomicFlush(em, [phase1, phase2, ...], { transaction: true })` (`commands/flush.ts`): flushes **after each phase** (SPEC-018 — prevents MikroORM v7 dropped-changeset bugs), wraps all phases in one `BEGIN/COMMIT` when `transaction: true`, joins an ambient transaction instead of nesting, and runs a commit-boundary guard that defensively flushes any remaining changesets. Side-effect emission (`emitCrudSideEffects` → events + query-index upsert) happens **outside/after** the atomic block.
- Simple single-entity writes skip the wrapper and just `em.persist(x); await em.flush()` (no explicit transaction — a single flush is one implicit transaction).

## Public contracts

**Environment variables**: `DATABASE_URL` (Postgres URL, required), `OM_ORG_SCOPE_CACHE_TTL_MS` (default 0), `OM_ENFORCE_ORG_SCOPE_STRICT` (default false), `QUERY_INDEX_COVERAGE_CACHE_MS` (default 300000), `QUERY_INDEX_CF_KEYS_CACHE_MS` (default 300000), `FORCE_QUERY_INDEX_ON_PARTIAL_INDEXES`, `OM_QUERY_INDEX_DEBUG`, `ENTITIES_BACKCOMPAT_EAV_FOR_CUSTOM` (default false).

**Cookies**: `om_selected_org` (uuid of selected organization or the all-orgs sentinel), `om_selected_tenant` (super-admin tenant override).

**Entity id**: `'<module>:<entity>'` matching `/^[a-z][a-z0-9_]*:[a-z][a-z0-9_]*$/`. Custom-field selector prefix `cf:<key>`; translation selector `l10n:<locale>:<field>`.

**Event names & payloads** (in-process event bus; see events analysis doc for transport):
- CRUD: `<module>.<entity>.created|updated|deleted` → `{ id, organizationId, tenantId }` (default `buildPayload`).
- Index maintenance: `query_index.upsert_one` / `query_index.delete_one` (persistent: false) → `{ entityType, recordId, organizationId, tenantId, crudAction?, coverageBaseDelta?, coverageIndexDelta?, suppressCoverage?, coverageDelayMs?, searchTokenDoc?, syncOrigin? }`; `query_index.reindex` and `query_index.purge` (persistent: true); `query_index.coverage.refresh` → `{ entityType, tenantId, organizationId, delayMs }`; `query_index.vectorize_one`; `search.index_record` → `{ entityId, recordId, organizationId, tenantId }`.

**HTTP error shapes surfaced by the data layer** (`CrudHttpError(status, body)`):
- 400 `{ error: 'Validation failed', fields: {...} }` (custom-field validation);
- 400 `{ error: 'Records are available for custom entities only', code: 'system_entity_records_blocked', entityId }`;
- 403 `{ error: 'Forbidden' }` (tenant/org scope), 403 `{ error: 'Cross-tenant relation forbidden' }`;
- 404 `{ error: 'Customer entity not found' }` / `{ error: 'Tag not found' }` etc.;
- 409 `{ error: 'Tag slug already exists' }` (duplicate checks are explicit queries, not DB-constraint mapping).

**List result JSON** (query engine → CRUD list routes): `{ items: [...], total: <number>, page, pageSize, totalPages }`; items carry base columns under **snake_case** keys (query engine selects raw columns) and custom fields under `cf:<key>` aliases (sanitized to `cf_<key>` per Kysely alias rules — sanitizer replaces non `[a-zA-Z0-9_]` with `_`), multi-value CFs as JSON arrays.

**Cross-cutting DDL a port must generate byte-identically** — table names: `tenants`, `organizations`, `custom_field_defs`, `custom_field_entity_configs`, `custom_entities`, `custom_entities_storage`, `custom_field_values`, `encryption_maps`, `entity_indexes` (with generated column `organization_id_coalesced` and its unique index), `entity_index_jobs`, `entity_index_coverage`, `indexer_error_logs`, `indexer_status_logs`, `search_tokens`, plus per-module business tables and `mikro_orm_migrations_<module>` tracking tables. Column/index names as listed in "How it works".

## Helpers to mirror

| Helper (upstream location) | Signature / behavior a port needs |
|---|---|
| `resolveEntityTableName(em, entityId)` (`shared/lib/query/engine.ts`) | EntityId → table via ORM metadata (PascalCase class, `<Name>Entity`, `<module>_<name>`, pluralized) with pluralize fallback + cache |
| `resolveRegisteredEntityTableName(em, entityId)` | Strict variant, returns `null` when unregistered (security boundary) |
| `isValidEntityIdShape(value)` / `ENTITY_ID_PATTERN` | Entity-id validation at API boundaries |
| `resolveOrganizationScopeForRequest({container, auth, request, selectedId?, tenantId?})` (`directory/utils/organizationScope.ts`) | → `OrganizationScope`; descendant expansion, super-admin widening, cookie handling, memo/caching |
| `resolveFeatureCheckContext(...)` | → `{ organizationId, scope, allowedOrganizationIds }` used by ACL feature checks |
| `ensureTenantScope(ctx, tenantId)` / `ensureOrganizationScope(ctx, organizationId)` / `ensureSameScope(entity, orgId, tenantId)` (`shared/lib/commands/scope.ts`) | 403 guards; exact fallback semantics documented above |
| `setRecordCustomFields(em, { entityId, recordId, organizationId?, tenantId?, values, preferDefs?, onChanged?, encryptionService? })` (`entities/lib/helpers.ts`) | EAV write with kind→column mapping, multi replace-in-transaction, def scope scoring |
| `installCustomEntitiesFromModules(em, cache, options)` (`entities/lib/install-from-ce.ts`) | ce.ts sync per tenant with checksum skip; returns `{processed, synchronized, skipped, fieldChanges}` |
| `buildIndexDoc(em, { entityType, recordId, organizationId?, tenantId? })` (`query_index/lib/indexer.ts`) | base row + `cf:*` + `l10n:*` + aggregate search field → doc or null |
| `upsertIndexRow(em, { entityType, recordId, organizationId?, tenantId?, searchTokenDoc?, deferSearchTokens? })` | ON CONFLICT upsert keyed by coalesced org; returns `{doc, existed, wasDeleted, created, revived}` |
| `markDeleted(em, { entityType, recordId, organizationId?, tenantId? })` | hard-deletes projection row + tokens; returns `{ wasActive }` |
| `DataEngine` interface (`shared/lib/data/engine.ts`) | `setCustomFields`, `createCustomEntityRecord`, `updateCustomEntityRecord`, `deleteCustomEntityRecord`, `createOrmEntity`, `updateOrmEntity`, `deleteOrmEntity`, `emitOrmEntityEvent`, `markOrmEntityChange`, `flushOrmEntityChanges` |
| `assertCustomEntityStorageEntityId(em, entityId)` / `isOrmBackedSystemEntityId` | doc-storage write guard (400 `system_entity_records_blocked`) |
| `withAtomicFlush(em, phases, { transaction?, isolationLevel?, label? })` (`shared/lib/commands/flush.ts`) | per-phase flush + optional single transaction + commit-boundary guard; ambient-transaction join |
| `CommandBus.execute(commandId, { input, ctx, metadata?, redoLogEntry? })` / `.undo(undoToken, ctx)` | full pipeline incl. action-log persistence and cache invalidation |
| `sanitizeModuleId`, `validateTableName`, `makeConstraintDropsIdempotent`, `getMigrationSnapshotName` (`cli/src/lib/db/commands.ts`) | migration tooling invariants |
| `QueryEngine.query(entity, opts)` | the single read abstraction — both engines must be behavior-compatible |

## Behavioral details a port MUST replicate

- **`tenantId` is mandatory** in every query-engine call; missing tenantId throws (surfaces as 500, not 403).
- **Empty org scope short-circuit**: CRUD list with `organizationIds === []` returns HTTP **200** with `{ items: [], total: 0, page, pageSize, totalPages: 0 }` (never leaks and never errors).
- **Org scope SQL**: `organization_id IN (:ids)` with optional `OR organization_id IS NULL` (only when caller included a null/empty id in `organizationIds`); ids+includeNull both empty → `WHERE 1 = 0`. `organizationIds` takes precedence over `organizationId`; single-element `organizationIds` also serves as the fallback org for encryption/decoration.
- **Soft delete default**: rows with non-null `deleted_at` excluded whenever the column exists; `withDeleted=true` includes them. `entity_indexes` rows for deleted records are **removed**, not soft-marked, by `markDeleted`.
- **Pagination defaults**: `page=1`, `pageSize=20`; `total` computed by a separate count query before applying limit/offset; count uses `count(distinct <base>.id)` only when joins can fan out.
- **Filter op semantics**: `eq`/`ne` with `null` → `IS NULL` / `IS NOT NULL`; `in`/`nin` wrap non-arrays; `exists: true` → `IS NOT NULL`; `$or` groups combine as (AND within group) OR (across groups) in a single WHERE.
- **`like`/`ilike` rewrite**: when search is enabled and `search_tokens` has rows for the entity+scope, these ops become token-hash EXISTS subqueries (ALL query-token hashes must match — `HAVING count(distinct token_hash) >= N`); this is approximate token matching, **not** SQL LIKE. Without tokens, plain SQL `like`/`ilike` applies.
- **Custom-field value shape**: single-value fields → scalar (all values serialized to text by the SQL CASE, i.e. `"42"`, `"true"`), multi fields → JSON array; multi-ness comes from `config_json->>'multi'` on the def. Doc-path (`entity_indexes.doc`) preserves native JSON types instead.
- **Custom-field def scoping on read**: defs visible when `is_active AND (tenant_id = :tenant OR tenant_id IS NULL)`; per-key winner chosen by scope specificity (tenant > org > global) then newest `updated_at`.
- **CF value scoping on read/index**: values match when `(organization_id = :org OR IS NULL)` and `(tenant_id = :tenant OR IS NULL)`; when the record scope itself is null, only NULL-scope rows match.
- **Hybrid→basic fallback triggers** (must keep result-compatibility): base table missing; `omitAutomaticTenantOrgScope`; CF requested but no `entity_indexes` rows; CF requested and coverage gap (base_count > indexed_count or indexed > base) — with `meta.partialIndexWarning` attached and an auto-reindex scheduled (unless `skipAutoReindex`).
- **Doc-storage record ids**: uuid sentinels `create|new|null|undefined` (case-insensitive) or non-uuid input trigger server-side uuid generation; the record's `doc` always contains its own `id`; callers can never override `id`/`entity_id` inside `doc`.
- **Migration ordering**: modules processed **alphabetically by module id**; each module's migrations tracked in its own `mikro_orm_migrations_<module>` table; generated file/class names carry the `_<module>` suffix; snapshot file `.snapshot-open-mercato.json` per module directory.
- **uuid PK generation happens in Postgres** (`gen_random_uuid()` default) — inserts must not pre-assign ids unless explicitly needed (undo/redo restores reuse the original id).
- **Timestamps are app-generated** (`new Date()` on create/update); raw-SQL paths use `now()`. `custom_field_values` has no `updated_at` — updates rewrite value columns in place or replace rows.
- **Command status codes**: 409 for duplicate slugs/keys, 404 for missing in-scope records, 403 for scope violations, 400 for invalid relations (e.g. `{ error: 'Invalid entity type' }`, 422 `{ error: 'entityId must reference a person or company' }` in customers timeline).
- **Read-your-writes**: the `query_index.upsert_one`/`delete_one` emits are **awaited** by the data engine so the projection row is updated before the write returns; the search-token rebuild is deferred (eventually consistent). Errors in index maintenance are logged (`indexer_error_logs`), never fail the originating write.
- **Coverage counters** are incrementally adjusted per write (`coverageBaseDelta`: +1 created / −1 deleted) and periodically refreshed (throttled 5min per entity+tenant).

## Gotchas

- **Two engines, one contract**: `BasicQueryEngine` (shared package) and `HybridQueryEngine` (query_index module) must return identical shapes for the same query; upstream keeps them behavior-aligned by hand. A port could implement one engine, but must keep the `entity_indexes` projection (other modules — search, AI, dashboards — read it) and the fallback semantics (`partialIndexWarning`).
- **`organization_id_coalesced` is a Postgres stored generated column** — the upsert `ON CONFLICT` target depends on it; a port's schema tool must support generated columns (or replicate via expression unique index + matching conflict target as `entity_index_jobs` does).
- **`markDeleted` hard-deletes** `entity_indexes` rows even though the table has `deleted_at`; don't "fix" this — coverage math and the unique key rely on rows being removed and revived (`deleted_at: null` reset on upsert).
- **`resolveBaseColumn` quirk**: a filter/sort on `organization_id` against a table lacking that column silently maps to `id` (engine.ts:1001 and hybrid:525). Preserve or you'll break org-scoped queries on `organizations` itself (`directory:organization` where org id = id).
- **Snapshot naming history**: module migration dirs contain both `.snapshot-openmercato.json` (legacy) and `.snapshot-open-mercato.json` (current, from `getMigrationSnapshotName`). Only the latter is read.
- **`information_schema` probing**: both engines discover columns/tables at runtime (`columnExists`/`tableExists`, positive-only caching) rather than trusting metadata — schema drift degrades gracefully. Ports doing static schemas can hardcode, but must keep the "guard only when column exists" behavior for tables without tenant/org columns.
- **MikroORM v7 flush-ordering landmines** are the reason for `withAtomicFlush`'s per-phase flush and for `setRecordCustomFields`' delete-then-insert-in-one-transaction ordering; in another ORM keep the observable guarantee: multi-value CF replacement is atomic, and a command's phases commit all-or-nothing when `transaction: true`.
- **Subscribers must fork the EM** (see `upsert_one.ts` comment) because index maintenance runs synchronously inside the request; sharing the unit of work can drop the caller's pending writes.
- **Commands are not transactional by default** — a bare `execute` with multiple flushes can partially commit unless the handler opts into `withAtomicFlush({transaction:true})` or the caller passes `ctx.transactionalEm`. Replicate per-handler, don't blanket-wrap.
- **Duplicate detection is query-based**, not constraint-error mapping — race conditions can still hit DB uniques (e.g. `organizations_tenant_slug_uniq`, `custom_entities_storage_unique_idx`); the doc-storage code paths have explicit `ON CONFLICT` fallbacks for schemas missing the unique index.
- **`custom_field_values.record_id` and `entity_indexes.entity_id` are `text`** — all joins cast the base uuid PK with `::text`. Keep the text type or every join/index breaks.
- **query_index DI setup registers CRUD-event listeners dynamically** from distinct `custom_field_defs.entity_id` values (fallback: generated entity-id registry) — new entity types start being indexed only once a field def exists or they appear in the registry.
- **Doc-vs-ORM classification** (#2939): an entity id backed by a registered ORM table is *never* routed to doc storage, even if `custom_entities`/`custom_entities_storage` rows exist for it; only `forceCustomEntityStorage` overrides. Writes to doc storage for such ids are rejected with the 400 code above. Ports must implement both the read-side and write-side guard or record reads get hijacked.
