# Port contract — directory

> Upstream commit adc9da27759e357febe9ed8d4b7182040d127349 (2026-07-01). Source: packages/core/src/modules/directory/. Regenerate via om-analyze-module.

## Overview

`directory` is the multi-tenant foundation module. It owns the canonical `tenants` and `organizations` tables that every other module's `tenant_id` / `organization_id` columns reference logically, and it provides the organization-hierarchy engine (materialized tree: `parent_id`, `root_id`, `tree_path`, `depth`, `ancestor_ids`, `child_ids`, `descendant_ids`) plus the request-scoped organization-scope resolver (`resolveOrganizationScopeForRequest`) that auth/RBAC and CRUD list filtering depend on. It exposes CRUD over tenants and organizations, an organization branding endpoint, an organization switcher menu endpoint, and two public lookup endpoints. It emits `crud` events for tenant/organization mutations and maintains query-index projections via the shared DataEngine. It has a mutual dependency with `auth` (it imports `enforceTenantSelection` / `resolveIsSuperAdmin` / `RbacService`), so `directory` + `auth` must be ported together as one unit. It declares no custom-field sets, no custom entities, and no field encryption of its own (though tenant create best-effort provisions a tenant DEK via `kmsService`).

## Dependencies

| module id | why needed | must be ported first |
|---|---|---|
| auth | `commands/organizations.ts` imports `enforceTenantSelection` (`@open-mercato/core/modules/auth/lib/tenantAccess`); `utils/organizationScope.ts` + `api/organization-switcher/route.ts` consume `RbacService` (`rbac.loadAcl`); `api/organizations/route.ts` imports `resolveIsSuperAdmin` + `enforceTenantSelection`. `enforceTenantSelection` failure → HTTP 403 `{error:'Not authorized to target this tenant.'}` (`shared/lib/crud/errors.ts` `forbidden`). | Together (mutual dependency — see MODULES.md tier-1 note "port directory ↔ auth as one unit") |
| query_index | Both CRUD configs pass an `indexer`; each create/update/delete emits `query_index.upsert_one` / `query_index.delete_one` / `query_index.coverage.refresh` (spec 03) consumed by the shared query-index subscribers/worker. Not a hard build dependency (best-effort `.catch`), but the projection is part of observable behavior. | No (best-effort; port before parity of index projections) |
| Tier-0 shared runtime | DI resolutions: `dataEngine`, `em`, `cache`, `rbacService`, `kmsService`. Shared helpers: `@open-mercato/shared/lib/commands/*` (`registerCommand`, `emitCrudSideEffects`, `emitCrudUndoSideEffects`, `makeCreateRedo`, `withAtomicFlush`, customFieldSnapshots), `@open-mercato/shared/lib/crud/*` (`makeCrudRoute`, `CrudHttpError`, `forbidden`), `@open-mercato/shared/lib/i18n/server` (`resolveTranslations`), `@open-mercato/shared/lib/slugify`, `@open-mercato/shared/lib/encryption/toggles`, `@open-mercato/shared/lib/auth/organizationAccess`, `@open-mercato/shared/modules/events` (`createModuleEvents`), `#generated/entities.ids.generated` (`E.directory.tenant`, `E.directory.organization`). | Yes (scaffold-level, per specs/01–07) |

**Ordered must-port-first:** (1) Tier-0 shared runtime (scaffold), (2) `auth` — together with `directory` as one porting unit. `query_index` is required only for full index-projection parity.

No outgoing cross-module FKs. `directory` is a base module; dependencies flow INTO it.

## HTTP routes

Routing model per spec 02: public path = `/api/directory/<file-dir>`; `route.ts` is the segment file; explicit `metadata.path` override wins. CRUD write methods (`POST`/`PUT`/`DELETE`) are produced by the shared `makeCrudRoute` factory (spec 02 §makeCrudRoute; standard envelope + dispatcher auth/feature guards spec 05). GET handlers are hand-written and bypass the factory dispatcher — their auth is enforced inside the handler via `getAuthFromRequest` (returning inline JSON) while `metadata.GET.requireAuth`/`requireFeatures` still drive the platform dispatcher guard (spec 05). No `interceptors.ts`. No `rateLimit` on any route. No route sets or clears cookies; `om_selected_org` / `om_selected_tenant` are only READ (via `utils/scopeCookies.ts`).

| METHOD | path | auth | requiredFeatures | rateLimit | source file |
|--------|------|------|------------------|-----------|-------------|
| GET | /api/directory/organizations | yes | directory.organizations.view | none | api/organizations/route.ts |
| POST | /api/directory/organizations | yes | directory.organizations.manage | none | api/organizations/route.ts |
| PUT | /api/directory/organizations | yes | directory.organizations.manage | none | api/organizations/route.ts |
| DELETE | /api/directory/organizations | yes | directory.organizations.manage | none | api/organizations/route.ts |
| GET | /api/directory/tenants | yes | directory.tenants.view | none | api/tenants/route.ts |
| POST | /api/directory/tenants | yes | directory.tenants.manage | none | api/tenants/route.ts |
| PUT | /api/directory/tenants | yes | directory.tenants.manage | none | api/tenants/route.ts |
| DELETE | /api/directory/tenants | yes | directory.tenants.manage | none | api/tenants/route.ts |
| GET | /api/directory/organization-branding | yes | directory.organizations.view | none | api/organization-branding/route.ts |
| PUT | /api/directory/organization-branding | yes | directory.organizations.manage | none | api/organization-branding/route.ts |
| GET | /api/directory/organization-switcher | yes | (none) | none | api/organization-switcher/route.ts |
| GET | /api/directory/organizations/lookup | no (public) | (none) | none | api/get/organizations/lookup.ts |
| GET | /api/directory/tenants/lookup | no (public) | (none) | none | api/get/tenants/lookup.ts |

**Route count: 13** (method+path combinations).

---

### GET /api/directory/organizations

Hand-written (route.ts lines 127–575). Read-only. Uses encryption-aware finds (`findWithDecryption`/`findOneWithDecryption`). Calls `logCrudAccess` (`resourceKind: 'directory.organization'`; `accessType: 'read:item'` when exactly one id requested). Hierarchy via `computeHierarchyForOrganizations`.

Query schema `viewSchema` (lines 53–62):
| name | type | required | default | constraints/coercions |
|------|------|----------|---------|-----------------------|
| page | number | no | 1 | `z.coerce.number().min(1)` |
| pageSize | number | no | 50 | `z.coerce.number().min(1).max(200)` |
| search | string | no | — | optional |
| view | enum | no | `options` | `options` \| `manage` \| `tree` |
| ids | string | no | — | comma-separated → unique list |
| tenantId | string(uuid) | no | — | literal `all` (case-insensitive) normalized to null |
| includeInactive | enum | no | — | `true`/`false` via `parseBooleanToken` |
| status | enum | no | `all` | `all` \| `active` \| `inactive` |

Tenant-scope resolution (lines 148–238): super-admin via `resolveIsSuperAdmin`. `allowAllTenants = isSuperAdmin && !tenantId && view==='manage'`. Non-super-admins constrained to `auth.tenantId`; a different requested tenant → null → 400. Fallback scope resolution via cookie `om_selected_org`, `auth.orgId`, `resolveOrganizationScopeForRequest`; failing that → 400.

**200** — shape depends on `view`:
- `view=options` (240–273): `{ items: [{ id, name, logoUrl|null, parentId|null, tenantId, isActive, depth, treePath }] }` (no pagination). Ordered name ASC; filtered by `status`/`includeInactive`/`ids`.
- `view=tree` (275–319): `{ items: TreeNode[] }`, `TreeNode = { id, name, parentId|null, tenantId|null, depth, ancestorIds[], childIds[], descendantIds[], isActive, treePath|null, pathLabel, children: TreeNode[] }` (recursive roots).
- `view=manage` single tenant (466–574): `{ items, total, page, pageSize, totalPages, isSuperAdmin }`. Item = `{ id, name, slug|null, logoUrl|null, updatedAt(ISO)|null, tenantId, tenantName|null, parentId, parentName|null, depth, rootId, treePath, pathLabel, ancestorIds[], childIds[], descendantIds[], childrenCount, descendantsCount, isActive, ...cf_* }`. Custom-field values merged via `loadCustomFieldValues` (entity `E.directory.organization`), keys spread as `cf_*`. `tenantName` only when super-admin.
- `view=manage` `allowAllTenants` super-admin aggregate (325–463): same envelope; items always carry `tenantName`, no `updatedAt`; aggregated across all tenants, sorted by tenant name then pathLabel, JS-paginated.

**400** — `{ items: [] }` (invalid query / unknown view, lines 142, 322); OR `{ items: [], error: 'Tenant scope required' }` (lines 192, 237, 242, 277, 467); OR a `CrudHttpError` body forwarded from `enforceTenantSelection` (186–188).
**401** — `{ items: [] }` (no auth, line 129).

### POST /api/directory/organizations

Factory (`makeCrudRoute`, config lines 88–123). Command `directory.organizations.create`. Response **201** `{ id: String(result.id) }`. Auth/feature guard 401/403 per spec 05.

- `orm`: `{ entity: Organization, idField: 'id', orgField: null, tenantField: null, softDeleteField: 'deletedAt' }`
- `events`: `organizationCrudEvents` = `{ module:'directory', entity:'organization', persistent:true, buildPayload → { id, tenantId, organizationId:id } }`
- `indexer`: `organizationCrudIndexer` = `{ entityType: E.directory.organization, buildUpsertPayload/buildDeletePayload → { entityType, recordId:id, organizationId:id, tenantId } }`
- `actions.create`: `commandId:'directory.organizations.create'`, `schema: z.object({}).passthrough()` (raw pass-through; real validation is `organizationCreateSchema` inside the command), `mapInput:({parsed})=>parsed`, `response:({result})=>({ id:String(result.id) })`, `status:201`.

Command request schema `organizationCreateSchema` (data/validators.ts 26–34):
| name | type | required | default | constraints |
|------|------|----------|---------|-------------|
| tenantId | string(uuid) | no | — | resolved via `enforceTenantSelection` |
| name | string | yes | — | min 1, max 200 |
| slug | string\|null | no | — | trimmed, lowercased, `^[a-z0-9\-_]+$`, max 150; auto-slugified from name if absent; uniqueness-resolved per tenant (`resolveUniqueSlug`) |
| logoUrl | string\|null | no | — | http(s) URL max 2048 OR internal `^/api/attachments/(?:image\|file)/[A-Za-z0-9%_.~/?=&-]+$` max 2048 |
| isActive | boolean | no | true | |
| parentId | string(uuid)\|null | no | — | must exist in tenant |
| childIds | string(uuid)[] | no | — | must all exist in tenant; child==parent invalid |

**Error cases (CrudHttpError):**
- 400 `{error:'Tenant scope required'}` (`enforceTenantSelection` falsy)
- 403 `{error:'Not authorized to target this tenant.'}` (`enforceTenantSelection` cross-tenant, from auth)
- 400 `{error:'Parent not found'}`
- 400 `{error:'Invalid child assignment'}`
- 400 `{error:'Child cannot equal parent'}`
- 401 / 403 auth+feature guard (spec 05)

Behavior: runs inside `withAtomicFlush([...], {transaction:true})`; `de.createOrmEntity`, `assignChildren`, `setCustomFieldsIfAny`, then `rebuildHierarchyForTenant(em, tenantId)`. Supports undo (hard-delete + restore child parents + reset custom fields + rebuild) and redo (re-create/un-soft-delete + reassign children + rebuild + re-emit `created`). **Event emitted:** `directory.organization.created` (persistent).

### PUT /api/directory/organizations

Factory. Command `directory.organizations.update`. `schema` raw pass-through; `response:()=>({ ok:true })` (**200**).

Command request schema `organizationUpdateSchema` (validators.ts 36–45): same fields, all optional except `id` (uuid, required).

**Error cases:**
- 404 `{error:'Not found'}` (missing)
- 400 `{error:'Tenant scope required'}`
- 403 `{error:'Not authorized to target this tenant.'}`
- 400 `{error:'Organization cannot be its own parent'}`
- 400 `{error:'Cannot assign descendant as parent'}`
- 400 `{error:'Parent not found'}`
- 400 `{error:'Child cannot equal parent'}`
- 400 `{error:'Cannot assign ancestor as child'}`
- 400 `{error:'Invalid child assignment'}`
- 400 `{error:'Cannot assign descendant cycle'}`
- 401 / 403 auth+feature guard

Behavior: atomic; `de.updateOrmEntity` (applies name/slug/logoUrl/isActive if defined; **always sets `entity.parentId = parentId`**), `clearRemovedChildren`, `assignChildren`, `setCustomFieldsIfAny`, `rebuildHierarchyForTenant`. Audit key `directory.audit.organizations.update`; `buildChanges` over `['name','slug','logoUrl','isActive','parentId']` + per-custom-field `cf_<key>` diffs. Undo via `emitCrudUndoSideEffects` (action `updated`). **Event emitted:** `directory.organization.updated` (persistent).

### DELETE /api/directory/organizations

Factory. Command `directory.organizations.delete`. `response:()=>({ ok:true })` (**200**). `requireId(input,'Organization id required')`.

**Error cases:** 404 `{error:'Not found'}`; 400 `{error:'Tenant scope required'}`; 403 auth; 401/403 guard.

Behavior: soft delete (`soft:true, softDeleteField:'deletedAt'`), sets deleted `isActive=false`, `parentId=null`; **re-parents children** — every org with `parentId===id` gets `parentId = <deleted org's original parentId>` (children promoted to grandparent); `rebuildHierarchyForTenant`. Undo (un-soft-delete/recreate + restore child parents + custom + rebuild, emits `updated`). **Event emitted:** `directory.organization.deleted` (persistent).

---

### GET /api/directory/tenants

Hand-written (route.ts 95–235). Read-only. `logCrudAccess` `resourceKind:'directory.tenant'` (`accessType:'read:item'` when `id` present). Custom fields loaded per record with `tenantId=null`/`organizationId=null` (tenants are top-level), `tenantFallbacks:[auth.tenantId]`.

Query schema `listQuerySchema` (19–27, `.passthrough()`):
| name | type | required | default | constraints |
|------|------|----------|---------|-------------|
| id | string(uuid) | no | — | single-record fetch |
| page | number | no | 1 | `z.coerce.number().min(1)` |
| pageSize | number | no | 50 | `z.coerce.number().min(1).max(100)` |
| search | string | no | — | `$ilike %escaped%` on name |
| sortField | enum | no | — | `name` \| `createdAt` \| `updatedAt` (default sort name ASC) |
| sortDir | enum | no | — | `asc` \| `desc` |
| isActive | enum | no | — | `true`/`false` via `parseBooleanToken` |
| cf_* / cf:* | passthrough | no | — | custom-field filters via `buildCustomFieldFiltersFromQuery`, matched JS-side |

**200** — `{ items, total, page, pageSize, totalPages }`. Item (`toRow`, 81–88): `{ id, name, isActive, createdAt(ISO)|null, updatedAt(ISO)|null, ...customFields }`. With cf filters present the whole table is loaded + filtered + sliced in JS; otherwise DB-side `findAndCount`.
**400** — `{ error: 'Invalid query parameters', details: <zod flatten> }` (111–116).
**401** — `{ items: [], total: 0, page: 1, pageSize: 50, totalPages: 1 }` (97–99).

### POST /api/directory/tenants

Factory (config 49–79). Command `directory.tenants.create`. `response:({result})=>({ id:String(result.id) })`, **201**.

- `orm`: `{ entity: Tenant, idField:'id', orgField:null, tenantField:null, softDeleteField:'deletedAt' }`
- `events`: `tenantCrudEvents` = `{ module:'directory', entity:'tenant', persistent:true }`
- `indexer`: `tenantCrudIndexer` = `{ entityType: E.directory.tenant }`

Command schema `tenantCreateSchema`: `name` string min1 max200 (required); `isActive` boolean optional (default true).

Behavior: `de.createOrmEntity(Tenant, { name, isActive: parsed.isActive ?? true })`; sets custom fields (`E.directory.tenant`, `organizationId:null`, `tenantId: ctx.auth?.tenantId ?? null`). **Identifiers use the tenant's OWN id as `tenantId`** (`{ id, organizationId:null, tenantId: String(tenant.id) }`). Encryption hook: if `isTenantDataEncryptionEnabled()` and `kmsService.isHealthy()` → `kms.createTenantDek(tenantId)` (best-effort; warn/skip on failure). Redo via `makeCreateRedo` (afterRestore sets `tenantId=String(entity.id)`). **Event emitted:** `directory.tenant.created` (persistent). Errors: 401/403 guard.

### PUT /api/directory/tenants

Factory. Command `directory.tenants.update`. `response:()=>({ ok:true })`, **200**.

Command schema `tenantUpdateSchema`: `id` uuid (required); `name` string min1 max200 optional; `isActive` boolean optional.

Behavior: `prepare` loads current (`em.findOne(Tenant,{id,deletedAt:null})`) → 404 if missing; `execute` `de.updateOrmEntity` applying name/isActive if defined + `updatedAt=new Date()`. Audit `directory.audit.tenants.update`; `buildChanges` over `['name','isActive']`. **Event emitted:** `directory.tenant.updated`.
**Error:** 404 `{error:'Tenant not found'}`; 401/403 guard.

### DELETE /api/directory/tenants

Factory. Command `directory.tenants.delete`. `response:()=>({ ok:true })`, **200**. `requireId(input,'Tenant id required')`.

Behavior: `de.deleteOrmEntity(Tenant, soft:true, softDeleteField:'deletedAt')`. Identifiers `{ id:String(id), organizationId:null, tenantId:String(id) }`. Audit `directory.audit.tenants.delete`. **Event emitted:** `directory.tenant.deleted`.
**Error:** 404 `{error:'Tenant not found'}`; 401/403 guard.

---

### GET /api/directory/organization-branding

Hand-written (133–138). Resolves current org via `resolveOrganizationScopeForRequest` → `scope.selectedId ?? auth.orgId`, tenant via `scope.tenantId ?? auth.tenantId`. All error bodies are i18n-translated (`resolveTranslations`). Reads via `findOneWithDecryption`.

**200** — `{ organizationId, organizationName, tenantId, logoUrl|null }`.
**400** — `{ error: '<Select a single organization before changing sidebar branding.>' }` (no single org/tenant scope).
**401** — `{ error: '<Unauthorized>' }` (no `auth.sub`).
**404** — `{ error: '<Organization not found>' }`.

### PUT /api/directory/organization-branding

Hand-written (140–203). Request body `brandingUpdateSchema` (line 29–31): `{ logoUrl: <organizationUpdateSchema.shape.logoUrl> }` (same http(s)/attachment-path union, optional+nullable). Body must be a JSON object that OWNS a `logoUrl` key.

**200** — `{ organizationId, organizationName, tenantId, logoUrl|null }` (post-update).
**400** — from resolve step, or forwarded `CrudHttpError` body, or `{ error: '<Failed to update organization branding.>' }`.
**401** — `{ error: '<Unauthorized>' }`.
**404** — `{ error: '<Organization not found>' }`.
**422** — `{ error: '<Enter a valid image URL.>' }` (bad JSON, missing `logoUrl`, or schema failure; on schema failure also `issues: <zod issues>`). Confirmed by `__tests__/route.test.ts` (status 422, body.error `'Enter a valid image URL.'`).

Behavior: executes command `directory.organizations.update` via `commandBus.execute` with input `{ id, tenantId, logoUrl }` and a hand-built `CommandRuntimeContext` (organizationScope pinned to selected org). **Event emitted:** `directory.organization.updated` (through the command). After success, best-effort `runWithCacheTenant(tenantId, cache.deleteByTags([...]))` with tags `nav:sidebar:organization:<orgId>` and `nav:sidebar:tenant:<tenantId>`.

---

### GET /api/directory/organization-switcher

Hand-written (92–226). `metadata`: `GET: { requireAuth: true }` — no `requireFeatures`. Read-only. Reads `?tenantId=` and cookies `om_selected_tenant` / `om_selected_org`. Special token `__all__` (`isAllOrganizationsSelection`) → selectedId null. Super-admins get full tenant list (`Tenant` ordered name ASC) and may switch tenants; non-super-admins pinned to `auth.tenantId`. Loads ACL via `rbacService.loadAcl`; checks `directory.organizations.manage` for `canManage`. Builds accessible-org menu (`resolveOrganizationScope`). `logCrudAccess` `resourceKind:'directory.organization_switcher'`. No cookies set.

**200** — `{ items: OrganizationMenuNode[], selectedId: uuid|null, canManage: boolean, canViewAllOrganizations: boolean, tenantId: uuid|null, tenants: [{ id, name, isActive }], isSuperAdmin: boolean }`, `OrganizationMenuNode = { id, name, depth, selectable, children: OrganizationMenuNode[] }`.
- Degenerate 200s: `!tenantId` → `{ items:[], selectedId:null, canManage:false, tenantId:null, tenants:<records>, isSuperAdmin }` (130–139); `!showMenu` → `{ items:[], selectedId:null, canManage:false }` (195).
**401** — `{ items: [], selectedId: null, canManage: false, tenantId: null, tenants: [], isSuperAdmin: false }` (no auth, 94–96; only when `auth` is null).
**500** — same empty-shape body on unexpected error (222–224).

---

### GET /api/directory/organizations/lookup

`api/get/organizations/lookup.ts`; `metadata.path = '/directory/organizations/lookup'` (override) → `/api/directory/organizations/lookup`. `GET: { requireAuth: false }` — **PUBLIC**, no features. Read-only, `em.findOne(Organization, { slug, deletedAt: null })`. No audit/cache/decryption.

Query `orgLookupQuerySchema`: `slug` string min1 max150 (required; `?slug=`).
**200** — `{ ok: true, organization: { id, name, slug } }`.
**400** — `{ ok: false, error: 'Invalid slug.' }`.
**404** — `{ ok: false, error: 'Organization not found.' }`.

### GET /api/directory/tenants/lookup

`api/get/tenants/lookup.ts`; `metadata.path = '/directory/tenants/lookup'` (override). `GET: { requireAuth: false }` — **PUBLIC**. Read-only, `em.findOne(Tenant, { id, deletedAt: null })`. No audit/cache.

Query `tenantLookupQuerySchema`: `tenantId` string(uuid) required (`?tenantId=` OR fallback `?tenant=`).
**200** — `{ ok: true, tenant: { id, name } }`.
**400** — `{ ok: false, error: 'Invalid tenant id.' }`.
**404** — `{ ok: false, error: 'Tenant not found.' }`.

---

**OpenAPI:** all routes tagged `Directory`. `api/openapi.ts` is a shared schema module (not a route) exporting `directoryTag='Directory'` and reusable Zod schemas: `directoryErrorSchema`, `directoryOkSchema`, `directoryIdSchema`, `tenantListItemSchema`, `tenantListResponseSchema`, `organizationNodeSchema` (lazy/recursive), `organizationListResponseSchema`, `organizationSwitcherNodeSchema` (recursive), `organizationSwitcherResponseSchema`.

## Entities

DDL from migrations (migrations win on exact DDL). Migration order: `Migration20251030150038` (both tables + FK) → `Migration20260314143323` (slug + unique) → `Migration20260607222259_directory` (logo_url). No `ce.ts`, `data/fields.ts`, `data/extensions.ts`, or `encryption.ts`. Timestamp columns are `timestamptz` (no explicit precision in migration DDL; corrects Fragment B's `timestamptz(6)`).

### Tenant → table `tenants`

Source: `data/entities.ts:4-26`, `Migration20251030150038`.

| column | pg type | nullable | default | notes |
|---|---|---|---|---|
| id | uuid | NO | `gen_random_uuid()` | PK `tenants_pkey` |
| name | text | NO | — | |
| is_active | boolean | NO | `true` | |
| created_at | timestamptz | NO | — | `onCreate` |
| updated_at | timestamptz | NO | — | `onUpdate` |
| deleted_at | timestamptz | YES | — | soft delete |

- **PK:** `tenants_pkey` (`id`).
- **Indexes:** only `tenants_pkey`.
- **FKs:** none.
- **Tenancy:** this IS the tenant root (no own `tenant_id`/`organization_id`).
- **Soft delete:** `deleted_at`.
- Relation: `OneToMany` → `Organization` (no DB column).

### Organization → table `organizations`

Source: `data/entities.ts:28-78`; DDL from all three migrations.

| column | pg type | nullable | default | notes |
|---|---|---|---|---|
| id | uuid | NO | `gen_random_uuid()` | PK `organizations_pkey` |
| tenant_id | uuid | NO | — | FK → tenants.id; part of composite unique |
| name | text | NO | — | |
| slug | text | YES | — | composite unique w/ tenant_id (migration 2) |
| logo_url | text | YES | — | added by migration 3 |
| is_active | boolean | NO | `true` | |
| parent_id | uuid | YES | — (entity default `null`; no DB DEFAULT) | self-ref by convention, NO DB FK |
| root_id | uuid | YES | — | hierarchy root, NO DB FK |
| tree_path | text | YES | — | materialized path |
| depth | int | NO | `0` | |
| ancestor_ids | jsonb | NO | `'[]'` | array of uuids |
| child_ids | jsonb | NO | `'[]'` | array of uuids |
| descendant_ids | jsonb | NO | `'[]'` | array of uuids |
| created_at | timestamptz | NO | — | `onCreate` |
| updated_at | timestamptz | NO | — | `onUpdate` |
| deleted_at | timestamptz | YES | — | soft delete |

- **PK:** `organizations_pkey` (`id`).
- **Unique constraints:** `organizations_tenant_slug_uniq` on (`tenant_id`, `slug`) (`data/entities.ts:29` `@Unique`; migration 2).
- **Indexes:** only `organizations_pkey` and `organizations_tenant_slug_uniq`. No standalone index on `tenant_id`/`parent_id`/`root_id`/`deleted_at`.
- **FKs:** `organizations_tenant_id_foreign` — `organizations.tenant_id` → `tenants.id`, `ON UPDATE CASCADE`, no ON DELETE clause (defaults to NO ACTION). `parent_id`/`root_id` have NO DB FK (hierarchy managed in `lib/hierarchy.ts`).
- **Tenancy:** `tenant_id` (not null). No `organization_id` (this is the org table).
- **Soft delete:** `deleted_at`.

## Custom entities & field sets

None. No `ce.ts`, no `data/fields.ts`, no custom-field sets, no MikroORM-external custom entities, no field encryption declared by this module. (CRUD commands still read/write generic custom-field VALUES via the shared DataEngine for `E.directory.tenant` / `E.directory.organization`, but declare no field definitions.)

## Events

Declared via `createModuleEvents({ moduleId: 'directory', events })` (`events.ts`). All `category: 'crud'`. Emission happens through the shared DataEngine on flush (`emitCrudSideEffects`/`emitCrudUndoSideEffects` → `markOrmEntityChange` → `flushOrmEntityChanges`), NOT via a direct `bus.emitEvent` in module code. Exported `emitDirectoryEvent` is never called in-module. No `workers/` directory; module enqueues no BullMQ jobs of its own.

**Emitted:**
| event id | label | payload shape | emitted from |
|---|---|---|---|
| `directory.tenant.created` | Tenant Created | `{ id, organizationId: null, tenantId: <tenant's own id> }` | `directory.tenants.create` (+ create redo), persistent |
| `directory.tenant.updated` | Tenant Updated | `{ id, organizationId: null, tenantId: <tenant's own id> }` | `directory.tenants.update`, persistent |
| `directory.tenant.deleted` | Tenant Deleted | `{ id, organizationId: null, tenantId: <tenant's own id> }` | `directory.tenants.delete` (soft), persistent |
| `directory.organization.created` | Organization Created | `{ id: <orgId>, tenantId: <resolved>, organizationId: <orgId> }` | `directory.organizations.create` (+ create redo), persistent |
| `directory.organization.updated` | Organization Updated | `{ id: <orgId>, tenantId: <resolved>, organizationId: <orgId> }` | `directory.organizations.update`; update undo; **delete undo/restore re-emits as `updated`** (not created); organization-branding PUT (via command), persistent |
| `directory.organization.deleted` | Organization Deleted | `{ id: <orgId>, tenantId: <resolved>, organizationId: <orgId> }` | `directory.organizations.delete` (soft), persistent |

Org `buildPayload` (`commands/organizations.ts:36-43`) overrides the engine default: `tenantId = resolveTenantIdFromEntity(entity) ?? identifiers.tenantId ?? null`; `organizationId = org's own id`. Tenant emissions use the engine default payload (no custom `buildPayload`).

Query-index side-effect events (via `indexer`, engine, spec 03; best-effort `.catch`): `query_index.upsert_one` (created/updated), `query_index.delete_one` (deleted), `query_index.coverage.refresh` (conditional). `entityType` = `E.directory.tenant` / `E.directory.organization` (`directory:tenant` / `directory:organization`). Owned by spec 03; listed here as directory-triggered emissions only.

**Consumed:**
| event | subscriber id | sync/persistent | effect |
|---|---|---|---|
| `directory.organization.*` (wildcard: created/updated/deleted) | `directory:invalidate-org-scope-cache` | persistent: false (sync/inline per spec 04) | `subscribers/invalidateOrgScopeCache.ts`: reads `payload.tenantId` (string else no-op); resolves DI `cache` (try/catch → no-op); calls `cache.deleteByTags([ buildOrgScopeTenantCacheTag(tenantId) ])` → tag literal `org-scope:tenant:<tenantId>`. Best-effort; failures swallowed. Intra-module (self-emitted). |

No events from OTHER modules are consumed. No `directory.*` event is consumed by any other module (grep-confirmed).

## Workers & queues

None. No `workers/` directory. Module declares/enqueues no BullMQ queues of its own. All async work is event-bus driven (deferred CRUD side effects + shared query-index events on flush). Query-index/coverage jobs are owned by the shared query_index runtime (spec 03), not by directory.

## ACL features

Exactly 4 (`acl.ts`, default + named `features`):
| feature id | title | dependsOn | used by |
|---|---|---|---|
| `directory.tenants.view` | View tenants | — | GET /api/directory/tenants |
| `directory.tenants.manage` | Manage tenants | `['directory.tenants.view']` | POST/PUT/DELETE /api/directory/tenants |
| `directory.organizations.view` | View organizations | — | GET /api/directory/organizations, GET+PUT /api/directory/organization-branding |
| `directory.organizations.manage` | Manage organizations | `['directory.organizations.view']` | POST/PUT/DELETE /api/directory/organizations, PUT /api/directory/organization-branding, `canManage` check in organization-switcher |

All feature `module: 'directory'`.

## Setup & seeding

`setup.ts` (`export const setup: ModuleSetupConfig`):
- **`defaultRoleFeatures`:**
  - `superadmin: ['directory.tenants.*']` (wildcard grant)
  - `admin: ['directory.organizations.view', 'directory.organizations.manage']`
- **`seedDefaults({ em, tenantId })`** → calls `backfillOrganizationSlugs(em, tenantId)` ONLY. No `onTenantCreated`, no `seedExamples`, no `defaultCustomerRoleFeatures`, no seeded role/tenant/organization records.

`backfillOrganizationSlugs(em, tenantId)`: finds `Organization` where `{ tenant: tenantId, slug: null, deletedAt: null }`; returns if none. Builds a `Set` of existing non-null slugs for that tenant (`{ tenant, deletedAt: null }`). For each slug-less org: `base = slugify(org.name)`; skip if empty; dedupe by appending `-1`, `-2`, … (`${base}-${suffix}`, suffix from 1) until candidate not in set; assigns `org.slug = candidate`, adds to set. Single `em.flush()` at end.

## DI services

`di.ts` `register(container)` is a **no-op placeholder** — module registers NO services. It consumes container services registered elsewhere: `dataEngine`, `em`, `rbacService`, `cache`, `kmsService`.

| service name | role | consumed by |
|---|---|---|
| (none registered) | — | — |

## CLI commands

None. No `cli.ts`. `commands/tenants.ts` and `commands/organizations.ts` are CQRS command handlers (`registerCommand`), not CLI commands. `index.ts` side-effect-imports both to register handlers at module load.

## Configuration

| env var | read at | default | behavior |
|---|---|---|---|
| `OM_ORG_SCOPE_CACHE_TTL_MS` | `utils/organizationScope.ts:36` (`resolveOrgScopeTtlMs`) | `0` (cache disabled) | unset → 0; else `Number(raw)`; if `!Number.isFinite` or `< 0` → 0; else parsed ms. Cross-request org-scope cache only consulted when `ttlMs > 0`. On write, tags = `[buildOrgScopeUserCacheTag(userId), buildOrgScopeTenantCacheTag(tenantId)]`. |

Cache tag/key formats (byte-exact; shared with auth RBAC invalidation and the subscriber): `org-scope:user:<userId>`, `org-scope:tenant:<tenantId>`, `buildOrgScopeCacheKey` = `org-scope:<userId>:<effectiveTenantId>:<selectedOrgId||'none'>:<requestedTenantId||'none'>`. Cookie names read (never set): `om_selected_org`, `om_selected_tenant`. All-orgs selection token: `ALL_ORGANIZATIONS_COOKIE_VALUE = '__all__'`.

**i18n:** `i18n/{de,en,es,pl}.json` (4 locales, 100 leaf keys each). No `translations.ts`. Audit labels resolved via `resolveTranslations()` with inline English fallbacks: keys `directory.audit.tenants.{create,update,delete}`, `directory.audit.organizations.{create,update,delete}`.

**index.ts metadata:** `name: 'directory'`, `title: 'Directory (Tenants & Organizations)'`, `version: '0.1.0'`, `description: 'Multi-tenant directory with tenants and organizations.'`, `author: 'Open Mercato Team'`, `license: 'Proprietary'`. **No `requires` field** (declares no explicit module deps despite runtime imports from `auth`).

## Behavior helpers (port-critical, non-UI)

- **`lib/hierarchy.ts` `computeHierarchyForOrganizations(organizations, tenantId)`** → `{ map, ordered }`. Pure tree builder. `normalizeUuid` treats empty / `'null'` / `'undefined'` (case-insensitive) as null. Self-reference (`parentId===id`) or missing parent → root. Cycle detection: repeated id in ancestor chain → standalone root (depth 0, empty arrays), marked visited. `childIds` sorted by lowercased name, tiebreak `localeCompare` (source compares `an.localeCompare(b)` — sort key uses child-id on tie; reproduce byte-exact). `depth = ancestors.length`; `rootId = ancestors[0] ?? id`; `treePath = [...ancestors, id].join('/')`; `pathLabel = [...ancestorNames, name].join(' / ')`. Explicit roots walked first, then unvisited orphans/cycles.
- **`rebuildHierarchyForTenant(em, tenantId)`**: loads `Organization {tenant, deletedAt:null}` ordered name ASC, computes hierarchy, writes back `parentId/rootId/treePath/depth/ancestorIds/childIds/descendantIds/updatedAt=now`; orgs not in map reset to self-root defaults; single `em.flush()`. Invoked after every org create/update/delete/undo/redo.
- **`resolveUniqueSlug(em, tenantId, baseSlug, excludeId?)`**: candidate = baseSlug; up to 50 iterations checking `em.findOne(Organization,{tenant, slug:candidate, deletedAt:null})`; free/owned-by-excludeId → return; else `${baseSlug}-${suffix}` (suffix from 1); after 50 → `${baseSlug}-${Date.now()}`.
- **`utils/organizationScope.ts`** — `resolveOrganizationScope` (pure) and `resolveOrganizationScopeForRequest` (per-request `WeakMap` memoization + optional TTL cache). Tenant-override guard: non-super-admin forced back to actor tenant on mismatch. `OrganizationScope = { selectedId, filterIds, allowedIds, tenantId }`. `resolveFeatureCheckContext` derives `organizationId = scope.selectedId ?? auth.orgId(if allowed) ?? allowedOrganizationIds[0] ?? null`.
- **`utils/organizationScopeFilter.ts` / `organizationScopeGuard.ts`** — list `where` filter derivation and fail-closed single-record read guard (`isOrganizationReadAccessAllowed`; unrestricted iff super-admin or `allowedIds===null`).
- **`enforceTenantSelection` (from auth)** — non-super-admin cross-tenant → `forbidden(403, {error:'Not authorized to target this tenant.'})`.

## Not ported

- `backend/**` — admin pages (`page.meta.ts`, `page.tsx`, `page.test.tsx`).
- `components/**` — `OrganizationSelect.tsx`, `TenantSelect.tsx` (+ tests).
- No `frontend/`, `widgets/`, `emails/` present.
- `api/**/__tests__/**`, `subscribers/__tests__/**`, `utils/__tests__/**`, `__integration__/TC-DIR-*.spec.ts` (+ `meta.ts`), `__tests__/acl.test.ts`, `README.md` — tests/docs (consulted only to confirm behavior: 422 branding body, subscriber cache-tag string, pagination).
- `lib/tree.ts` — UI-oriented tree formatting helpers (`formatOrganizationTreeLabel`, `buildOrganizationTreeOptions`); pure but consumed only by UI/switcher rendering.
- `api/openapi.ts` — shared schema module (not a route); reproduce schemas as part of route contracts above.

## Porting checklist

1. [ ] **Migrations** — create `tenants` and `organizations` tables with exact columns/defaults; add `organizations_tenant_id_foreign` (ON UPDATE CASCADE, no ON DELETE); add `slug` + `organizations_tenant_slug_uniq` unique (`tenant_id`,`slug`); add `logo_url`. Timestamps `timestamptz`.
2. [ ] **Entities** — `Tenant`, `Organization` (with jsonb hierarchy arrays defaulting `[]` NOT NULL, `depth` default 0, soft-delete `deleted_at`).
3. [ ] **Hierarchy engine** — `computeHierarchyForOrganizations` + `rebuildHierarchyForTenant` (byte-exact sort/cycle rules) + `resolveUniqueSlug` + `slugify`.
4. [ ] **Org-scope resolver** — `resolveOrganizationScope`, `resolveOrganizationScopeForRequest` (WeakMap memoization + TTL cache + tags), `organizationScopeFilter`, `organizationScopeGuard`, cookie parsers, cache-tag/key builders, `__all__` constant.
5. [ ] **Command handlers** — `directory.tenants.{create,update,delete}` and `directory.organizations.{create,update,delete}` with all validation → status/error mappings, `withAtomicFlush`, custom-field read/write, `enforceTenantSelection`, tenant-DEK best-effort provision, undo/redo, audit labels.
6. [ ] **Validators** — `tenantCreateSchema`, `tenantUpdateSchema`, `organizationCreateSchema`, `organizationUpdateSchema`, `slugField`, `logoUrlField`.
7. [ ] **Routes** — 4 org CRUD (GET hand-written 3 views + factory writes), 4 tenant CRUD, branding GET/PUT (incl. 422), switcher GET, 2 public lookups; exact envelopes/status codes/error bodies; audit `logCrudAccess`.
8. [ ] **Events** — declare 6 `crud` events; wire CRUD side-effect emission with exact payload builders (org custom `buildPayload`, tenant-own-id-as-tenantId); query-index indexer configs.
9. [ ] **Subscribers** — `directory:invalidate-org-scope-cache` on `directory.organization.*` (persistent:false), tag `org-scope:tenant:<tenantId>`.
10. [ ] **ACL** — 4 features with `dependsOn` chains.
11. [ ] **Setup/seed** — `defaultRoleFeatures` (`superadmin: directory.tenants.*`, `admin: organizations.view+manage`); `seedDefaults` → `backfillOrganizationSlugs`.
12. [ ] **DI** — no-op register (no services); ensure `dataEngine`/`em`/`cache`/`rbacService`/`kmsService` resolvable.
13. [ ] **i18n** — de/en/es/pl (100 keys); audit-label fallbacks.
14. [ ] **Config** — `OM_ORG_SCOPE_CACHE_TTL_MS` (default 0).
15. [ ] **Tests** — port behavior tests (branding 422, subscriber tag, pagination, hierarchy/cycle, slug uniqueness).
16. [ ] **Parity run** — `om-verify-parity directory <tech>` against pinned commit.
