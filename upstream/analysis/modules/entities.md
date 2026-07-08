# Port contract — entities

> Upstream commit adc9da27759e357febe9ed8d4b7182040d127349 (2026-07-01). Source: packages/core/src/modules/entities/. Regenerate via om-analyze-module.

## Overview

`entities` is the **custom-field engine** (EAV) plus the user-defined ("virtual") entity registry. It owns 6 tables: custom-field DEFINITIONS (`custom_field_defs`), per-entity fieldset config (`custom_field_entity_configs`), the virtual-entity registry (`custom_entities`), the JSONB doc store for virtual-entity records (`custom_entities_storage`), custom-field VALUES (`custom_field_values`, EAV), and field-level encryption maps (`encryption_maps`). It is how a module's declared CE field-sets (`IModule.customFieldSets` / upstream `ce.ts`) become real, queryable field definitions (install-from-ce), and it provides the read/write codec the CRUD factory uses to decorate records with custom values (`decorateRecordWithCustomFields`) and persist `cf_*` inputs (`setRecordCustomFields`). It declares 4 ACL features and requires `query_index` for hybrid record querying.

## Dependencies

| module id | why needed | must be ported first |
|---|---|---|
| query_index | records API lists via the shared `queryEngine` (cf projection/filter/sort, `forceCustomEntityStorage`); `index.ts` declares `requires: ['query_index']` | No (best-effort; records listing degrades to doc-storage read without it) |
| auth | records/definitions routes read `getAuthFromRequest` + `rbacService.userHasAllFeatures` / `RbacService`; `lib/entityAcl.ts` maps entity ids → view/manage features | Yes (RBAC) |
| directory | `resolveOrganizationScopeForRequest` / `resolveOrganizationScope` for tenant/org scoping of defs + records | Yes |
| dictionaries, currencies | `api/definitions.ts` validates `defaultValue` for `dictionary`/`currency` kinds against those tables (best-effort dynamic import) | No |
| Tier-0 shared runtime | `@open-mercato/shared/modules/entities` (kinds, validators, validation, options), `shared/lib/crud/custom-fields.ts` (the wire codec), `shared/lib/encryption/*` (field encryption), DI `em`/`cache`/`dataEngine` | Yes (scaffold) |

## Tables (exact DDL — MikroORM migrations 20251030150038 / 20251116183728 / 20251209080326)

### custom_field_defs — field DEFINITION model
```
id uuid pk default gen_random_uuid(), entity_id text not null, organization_id uuid null,
tenant_id uuid null, key text not null, kind text not null, config_json jsonb null,
is_active boolean not null default true, created_at timestamptz not null,
updated_at timestamptz not null, deleted_at timestamptz null
```
Indexes: `cf_defs_entity_key_idx(key)`, `cf_defs_active_entity_key_scope_idx(entity_id,key,tenant_id,organization_id)`, `cf_defs_active_entity_global_idx(entity_id)`, `cf_defs_active_entity_org_idx(entity_id,organization_id)`, `cf_defs_active_entity_tenant_idx(entity_id,tenant_id)`, `cf_defs_active_entity_tenant_org_idx(entity_id,tenant_id,organization_id)`.
- **entity_id** = `'<module>:<entity>'`. **key** unique within (entity, scope). **kind** ∈ CUSTOM_FIELD_KINDS. **config_json** carries label/description/options/optionsUrl/multi/required/filterable/formEditable/listVisible/editor/input/priority/validation/defaultValue/dictionaryId/relatedEntityId/encrypted/fieldset(s)/group. Scope = (tenant_id, organization_id); NULL = global. Scope precedence (`scopeScore`): tenant match +2, org match +1; ties → newest `updated_at`.

### custom_field_entity_configs — per-entity fieldset config
```
id uuid pk, entity_id text not null, organization_id uuid null, tenant_id uuid null,
config_json jsonb null, is_active boolean default true, created_at/updated_at timestamptz, deleted_at null
```
`config_json.fieldsets[]` ({code,label,icon,description,groups[]}) + `singleFieldsetPerRecord` (default true).

### custom_entities — virtual-entity registry
```
id uuid pk, entity_id text not null, label text not null, description text null,
label_field text null, default_editor text null, show_in_sidebar boolean default false,
organization_id uuid null, tenant_id uuid null, is_active boolean default true, timestamps, deleted_at
```
Unique-ish index `custom_entities_unique_idx(entity_id,organization_id,tenant_id)`.

### custom_entities_storage — virtual-record doc store
```
id uuid pk, entity_type text not null, entity_id text not null, organization_id uuid null,
tenant_id uuid null, doc jsonb not null, timestamps, deleted_at null
```
`entity_type` = the entity id; `entity_id` = the record id. Index `custom_entities_storage_unique_idx(entity_type,entity_id,organization_id)`.

### custom_field_values — field VALUE storage (EAV)
```
id uuid pk default gen_random_uuid(), entity_id text not null, record_id text not null,
organization_id uuid null, tenant_id uuid null, field_key text not null,
value_text text null, value_multiline text null, value_int int null,
value_float real null, value_bool boolean null,
created_at timestamptz not null, deleted_at timestamptz null
```
Indexes: `cf_values_entity_record_field_idx(field_key)`, `cf_values_entity_record_tenant_idx(entity_id,record_id,tenant_id)`.
- **record_id is text** (supports uuid/int PKs). One typed value column per row, chosen by kind (`columnFromKind`): text/select/currency/dictionary→`value_text`; multiline→`value_multiline`; integer→`value_int`; float→`value_float`; boolean→`value_bool`. **Multi-value** fields store one row per element (delete-all-then-insert on replace). No def → column inferred from JS value shape. Encrypted fields store ciphertext in `value_text`.

### encryption_maps — per-entity field encryption
```
id uuid pk, entity_id text not null, tenant_id uuid null, organization_id uuid null,
fields_json jsonb null, is_active boolean default true, timestamps, deleted_at null
```
`fields_json[] = {field, hashField?}`. Index `encryption_maps_entity_scope_idx(entity_id,tenant_id,organization_id)`.

## install-from-ce (how CE declarations become defs)

`lib/install-from-ce.ts::installCustomEntitiesFromModules` + `lib/field-definitions.ts::ensureCustomFieldDefinitions`:
1. Aggregate every module's `customFieldSets` (+ `customEntities` specs) by entity id (`buildAggregatedConfigs`). Last field-declaration per key wins (`resolveFields`).
2. For each scope (per-tenant for tenant entities; global (null) for `spec.global`), upsert a `custom_field_defs` row per field: `config_json` built from CONFIG_PASSTHROUGH_KEYS; idempotent — re-runs UPDATE only when kind or config changed (or the def was deactivated/tombstoned), else no-op. `createOnly` skips updates. A checksum cache (`custom-entities:v1:<scope>:<entity>`) short-circuits unchanged scopes.
3. Virtual-entity specs also `upsertCustomEntity` (label/description/labelField/defaultEditor/showInSidebar).
Invoked at CLI install/seed and lazily from `GET /api/entities/definitions` (when the caller can manage definitions, `createOnly`).

## Validation rules per kind

`shared/modules/entities/validation.ts::validateValuesAgainstDefs` — evaluates `config_json.validation[]` rules per field. Rule kinds: `required, date, integer, float, lt, lte, gt, gte, eq, ne, regex` (params numeric/any/regex-string; each carries a `message`). Multi-value: every rule except `required` applies per element. Empty values pass all non-required rules. Guards: `rejectUndeclaredKeys` (untrusted entry points → `cf_<key>: '[internal] Unknown custom field'`), per-record cap `MAX_CUSTOM_FIELD_KEYS_PER_RECORD = 128` (`_customFields: '[internal] Too many custom fields'`). Errors keyed by `cf_<key>` (request-side convention). Kinds (`kinds.ts`): text, multiline, integer, float, boolean, select, currency, relation, attachment, dictionary, date, datetime.

## Wire-codec (request vs response key convention)

`shared/lib/crud/custom-fields.ts`:
- **Request** (`splitCustomFieldPayload`): reads `cf_<key>`, `cf:<key>`, a `customValues` map, and a `customFields` array/map → bare-key `custom` dict for persistence.
- **Response** (`decorateRecordWithCustomFields` / `loadCustomFieldValues`): reads `custom_field_values`, resolves the winning def per key, emits values under **bare** names, plus `customValues` (bare map) and `customFields[]` ({key,label,value,kind,multi}). Values without an active def are skipped. Read column priority: multiline→text→int→float→bool. Multi (def `multi` or >1 row) → array.

## HTTP routes

| METHOD | path | auth | requiredFeatures | source |
|--------|------|------|------------------|--------|
| GET | /api/entities/definitions | yes | (any authed) | api/definitions.ts |
| POST | /api/entities/definitions | yes | entities.definitions.manage | api/definitions.ts |
| DELETE | /api/entities/definitions | yes | entities.definitions.manage | api/definitions.ts |
| POST | /api/entities/definitions.batch | yes | entities.definitions.manage | api/definitions.batch.ts |
| POST | /api/entities/definitions.restore | yes | entities.definitions.manage | api/definitions.restore.ts |
| GET/POST/DELETE | /api/entities/entities | yes | entities.definitions.* | api/entities.ts |
| GET/POST/PUT/DELETE | /api/entities/records | yes | entities.records.view / .manage | api/records.ts |
| GET/POST/DELETE | /api/entities/encryption | yes | entities.definitions.manage | api/encryption.ts |
| GET | /api/entities/relations/options | yes | (records.view) | api/relations/options.ts |
| GET | /api/entities/sidebar-entities | yes | — | api/sidebar-entities.ts |

- **definitions GET** returns `{items, fieldsetsByEntity, entitySettings}` — active defs for the requested entity ids, tenant-scoped (global or exact tenant), tombstone-aware, deduped per key by scope+priority score. **POST** upserts one def (validated by `upsertCustomFieldDefSchema`; kind-specific defaultValue validation for dictionary/currency/select; currency forces `optionsUrl`). **DELETE** soft-deactivates (isActive=false + deleted_at).
- **records** manages custom-entity records ONLY (system/ORM-backed ids → 400 `SYSTEM_ENTITY_RECORDS_BLOCKED`). Values normalized (strip `cf_`), validated (`rejectUndeclaredKeys`), reserved keys stripped, persisted via the DataEngine (`createCustomEntityRecord`/`update`/`delete`, soft delete). GET lists via the query engine (cf filters, exports csv/json/xml/markdown, `updated_at` merge for optimistic locking).

## ACL features (acl.ts)

`entities.definitions.view`, `entities.definitions.manage` (dependsOn view), `entities.records.view`, `entities.records.manage` (dependsOn view). `lib/entityAcl.ts` maps known entity ids → per-entity view/manage feature requirements (custom entities bypass; super-admin bypass; platform-only entities forbidden to non-super-admins).

## PARITY-TODO (clean seams deferred in the .NET port)

Field-value encryption (encryption_maps + tenant DEKs), the definitions cache + fieldset config loading, the query-index-backed records listing/filtering/export, dictionary/currency/select defaultValue DB validation, the batch/restore/encryption/relations-options/sidebar routes, and the entity-ACL requirement matrix. Each is a documented seam, not a behavioural change to the ported core.
