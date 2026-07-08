# Port contract — `dictionaries`

Upstream: `packages/core/src/modules/dictionaries/` @ pinned `adc9da27759e357febe9ed8d4b7182040d127349`.

Organization-scoped enumerations: named **dictionaries** each owning an ordered set of
**dictionary entries** (value/label + appearance: color/icon/position/default). Reusable option
lists (e.g. `customers.status`, deal loss reasons) surfaced to custom fields (`dictionary` field
kind) and admin UIs. This contract covers the **backend** surface only (2 tables, CRUD, lookup/list,
ACL, translations note, seed). Frontend components (`components/`, `fields/`, `backend/`) are out of
scope for the port.

## 1. Data model — 2 tables (byte-exact DDL)

### `dictionaries` (entity `Dictionary`, MikroORM)
| column | type | notes |
|---|---|---|
| `id` | `uuid` PK | `default gen_random_uuid()` |
| `organization_id` | `uuid NOT NULL` | owning org |
| `tenant_id` | `uuid NOT NULL` | owning tenant |
| `key` | `text NOT NULL` | stable lookup key (lowercased) |
| `name` | `text NOT NULL` | display name |
| `description` | `text NULL` | |
| `is_system` | `boolean NOT NULL default false` | system-managed flag |
| `is_active` | `boolean NOT NULL default true` | soft-active toggle |
| `manager_visibility` | `text NOT NULL default 'default'` | `'default' | 'hidden'` |
| `entry_sort_mode` | `text NOT NULL default 'label_asc'` | added in later migration |
| `created_at` | `timestamptz NOT NULL` | |
| `updated_at` | `timestamptz NOT NULL` | |
| `deleted_at` | `timestamptz NULL` | soft delete |

Unique: `dictionaries_scope_key_unique (organization_id, tenant_id, key)`.

### `dictionary_entries` (entity `DictionaryEntry`, MikroORM)
| column | type | notes |
|---|---|---|
| `id` | `uuid` PK | `default gen_random_uuid()` |
| `dictionary_id` | `uuid NOT NULL` | FK → `dictionaries(id)` `on update cascade` |
| `organization_id` | `uuid NOT NULL` | denormalized scope |
| `tenant_id` | `uuid NOT NULL` | denormalized scope |
| `value` | `text NOT NULL` | raw value |
| `normalized_value` | `text NOT NULL` | `trim().toLowerCase()` of value; dedupe key |
| `label` | `text NOT NULL` | display label (defaults to value) |
| `color` | `text NULL` | sanitized 6-digit hex `#rrggbb` |
| `icon` | `text NULL` | sanitized, ≤64 chars |
| `position` | `int NOT NULL default 0` | manual sort position |
| `is_default` | `boolean NOT NULL default false` | one default per dictionary |
| `created_at` | `timestamptz NOT NULL` | |
| `updated_at` | `timestamptz NOT NULL` | |

- Index `dictionary_entries_scope_idx (dictionary_id, organization_id, tenant_id)`.
- Unique `dictionary_entries_unique (dictionary_id, organization_id, tenant_id, normalized_value)`.
- **Partial** unique `dictionary_entries_one_default_per_dict (dictionary_id, organization_id, tenant_id) WHERE is_default = true` — at most one default per dictionary.

Migration history (3 upstream migrations, folded into ONE port migration): base tables →
`position`+`is_default` (+backfill position by lower(label)/lower(value)/id; +partial default index;
+seed `customers.status` `active`=default) → `entry_sort_mode`.

## 2. Validation (zod → FluentValidation-ish)
- `dictionaryKeySchema`: trim, 1..100, regex `^[a-z0-9][a-z0-9_-]*$`.
- `upsertDictionarySchema`: key + name(1..200) + description(≤2000) + isSystem?/isActive?/entrySortMode?.
- entry create: value(1..150) + label?(1..150) + color?(hex nullable) + icon?(1..64 nullable) + position?(int≥0).
- entry update: same fields all optional + isDefault?; must provide ≥1 field.
- `entrySortMode` enum: `label_asc | label_desc | value_asc | value_desc | created_at_asc | created_at_desc` (default `label_asc`).

## 3. HTTP surface (all under `/api/dictionaries`)

Upstream hand-writes these routes (NO `makeCrudRoute`); the port keeps the exact response shapes and
so also hand-writes the routes. **All write ops dispatch through the command bus** (port convention;
see §7). Response envelopes are `{ items }` (no pagination) for the two list endpoints.

| method + path | features | behavior |
|---|---|---|
| `GET /api/dictionaries` | `dictionaries.view` | list dictionaries visible to org (self + ancestors), `deleted_at IS NULL`, `is_active=true` unless `?includeInactive=true`; order by `name asc`; `{ items:[{id,key,name,description,isSystem,isActive,managerVisibility,entrySortMode,organizationId,isInherited,createdAt,updatedAt}] }` |
| `POST /api/dictionaries` | `dictionaries.manage` | create; 400 if no org; 409 duplicate key; 201 `{id,key,name,description,isSystem,isActive,entrySortMode,createdAt,updatedAt}` |
| `GET /api/dictionaries/{id}` | `dictionaries.view` | fetch (allow inherited); 404 if missing; full detail incl. `isInherited` |
| `PATCH /api/dictionaries/{id}` | `dictionaries.manage` | partial update; optimistic lock; currency-dictionary protection; key-change strict-regex + dup check; `is_active=false` also sets `deleted_at`; 200 detail |
| `DELETE /api/dictionaries/{id}` | `dictionaries.manage` | soft delete (`is_active=false`, `deleted_at`); currency protected → 400; 200 `{ok:true}` |
| `GET /api/dictionaries/{id}/entries` | `dictionaries.view` | list entries (allow inherited), sorted by dictionary's `entry_sort_mode` (tie-break by id); `{ items:[{id,value,label,color,icon,position,isDefault,createdAt,updatedAt}] }` |
| `POST /api/dictionaries/{id}/entries` | `dictionaries.manage` | command `dictionaries.entries.create`; 409 dup value; 201 entry; `x-om-operation` header |
| `PATCH /api/dictionaries/{id}/entries/{entryId}` | `dictionaries.manage` | optimistic lock; command `dictionaries.entries.update`; 200 entry; `x-om-operation` |
| `DELETE /api/dictionaries/{id}/entries/{entryId}` | `dictionaries.manage` | command `dictionaries.entries.delete`; 200 `{ok:true}`; `x-om-operation` |
| `POST /api/dictionaries/{id}/entries/reorder` | `dictionaries.manage` | command `dictionaries.entries.reorder` (`{entries:[{id,position}]}`); 200 `{ok:true}` |
| `POST /api/dictionaries/{id}/entries/set-default` | `dictionaries.manage` | command `dictionaries.entries.set_default` (`{entryId}`); clears prior default in a separate flush (partial-index ordering); 200 `{ok:true}` |

Errors map through `CrudHttpError`/`CommandHttpException`: 400 org-required/validation, 401 unauth,
403 cross-scope, 404 not-found, 409 duplicate/optimistic-lock.

### Org inheritance (read scope)
Reads resolve `readableOrganizationIds` = selected org + its ancestor ids (from
`organizations.ancestor_ids`), so child orgs see parent dictionaries. `isInherited = dictionary.organizationId !== selectedOrg`.
Writes are strictly scoped to the selected org (no inheritance). Currency-dictionary protection:
key `currency`/`currencies` cannot be renamed, deactivated, or deleted.

## 4. Commands (command bus)
- `dictionaries.dictionary.create|update|delete` — dictionary writes (port convention; upstream did these inline).
- `dictionaries.entries.create|update|delete` — undoable entry writes (upstream `commands/factory.ts`).
- `dictionaries.entries.reorder` — bulk position update in one transaction.
- `dictionaries.entries.set_default` — clear-then-set across two flushes (partial-unique-index ordering).

Entry commands: value normalization + color/icon sanitize; duplicate normalized-value → 409; label
defaults to value; scope guard (`ensureScope`) rejects cross-tenant/org (403).

## 5. ACL (`acl.ts`)
- `dictionaries.view` — "View shared dictionaries"
- `dictionaries.manage` — "Manage shared dictionaries"

## 6. Setup / seed (`setup.ts`)
`defaultRoleFeatures`: `admin → [view, manage]`, `employee → [view]`. No standalone data seed in the
module; the only migration-time seed is the `customers.status` `active`-default backfill (guarded, only
if a `customers.status` dictionary exists) carried in the port migration for parity.

## 7. Events / translations
- `events.ts` (persistent, category `crud`): `dictionaries.entry.created|updated|deleted`.
- `translations.ts`: translatable field `dictionaries:dictionary_entry.label` (locale overlay handled by the future `translations` module — noted, not ported here).

## 8. Deviations / PARITY-TODO
- Routes hand-written (upstream hand-writes them; `makeCrudRoute` never used here) — see ADR.
- All writes routed through the command bus (upstream dictionary writes were inline `em` mutations).
- Undo/redo implemented for entry create/update/delete; reorder/set_default undo left `// PARITY-TODO`.
- Field-value encryption (`findOneWithDecryption`), CRUD mutation-guard, and full zod message parity are seams left as `// PARITY-TODO`.
</content>
</invoke>
