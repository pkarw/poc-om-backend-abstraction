# 0012 — Directory module port deviations

Status: accepted
Date: 2026-07-07
Scope: `OpenMercato.Modules.Directory` — tenants/organizations CRUD, organization branding, the
organization switcher, the public lookups, and the organization-hierarchy engine
(`Lib/OrganizationHierarchy`). Contract: `upstream/analysis/modules/directory.md`.

## Context

Upstream (`packages/core/src/modules/directory`) owns the canonical `tenants`/`organizations`
tables and the materialized organization tree. Its CRUD flows run through the shared **crud command
bus** (`makeCrudRoute`, `withAtomicFlush`, undo/redo, audit `buildChanges`), the **DataEngine**
custom-field VALUE read/write, the **query_index** projection subscribers, the **@open-mercato/cache**
org-scope cache, and the **kmsService** tenant-DEK provisioning — none of which are ported yet. It
also has a mutual runtime dependency on **auth** (`enforceTenantSelection`, `resolveIsSuperAdmin`,
`RbacService`), which IS ported. This port reproduces the byte-exact Postgres schema and the
observable HTTP contract (paths, methods, status codes, JSON envelopes/field names, documented
quirks) using EF Core against the shared `AppDbContext`, and records the deviations below.

## Decisions

1. **EF model cache is now registry-aware (core change).** `AppDbContext` caches its built model
   keyed by context CLR type alone. Introducing a module (directory) whose registry contributes
   entities different from auth's exposed a latent bug: the first registry to build the model leaked
   it to every other registry, so an auth-only registry would be missing `Tenant`/`Organization` and
   vice-versa (observed as `Cannot create a DbSet for 'X'` in tests). `AppDbContext` now replaces
   `IModelCacheKeyFactory` with `RegistryModelCacheKeyFactory`, which folds
   `ModuleRegistry.ModelCacheKey` (ordered module ids) into the key. Production wires a single
   registry, so behavior is unchanged; tests with per-module registries now isolate correctly.

2. **CRUD is inlined, not command-bus-backed.** POST/PUT/DELETE reproduce the command handlers'
   validation → status/error mapping, hierarchy rebuild, and event emission directly in the route
   groups (mirrors the auth port). Undo/redo, atomic-flush transactions, and audit `buildChanges`
   are not reproduced (no command bus yet). Observable envelopes/status codes are faithful.

3. **Custom-field VALUES and query-index projections are `// PARITY-TODO` no-ops.** The `manage`/list
   envelopes omit the merged `cf_*` keys; no `query_index.*` side-effect events are emitted. Depends
   on the unported DataEngine + query_index runtime.

4. **Org-scope resolution is simplified to `auth.tenantId` / `auth.orgId`.** The full
   `resolveOrganizationScopeForRequest` (WeakMap memoization, `OM_ORG_SCOPE_CACHE_TTL_MS` TTL cache,
   `org-scope:*` cache tags, cookie `om_selected_org`/`om_selected_tenant` parsing, the `__all__`
   all-orgs token, and the super-admin all-tenants aggregate view) is not ported. GET
   organizations resolves scope from the actor's tenant; the switcher exposes `canViewAllOrganizations`
   from super-admin status. The `invalidateOrgScopeCache` subscriber is not wired (no cache module).

5. **Tenant DEK provisioning is skipped.** `directory.tenants.create` does not call
   `kms.createTenantDek` (upstream does this best-effort behind `isTenantDataEncryptionEnabled()`).
   Marked `// PARITY-TODO`.

6. **GET auth returns `{"error":"Unauthorized"}`, not the inline empty envelope.** Like the auth
   port, GET routes apply the `RequireFeatures`/`RequireAuth` endpoint filters (spec 05 dispatcher
   guard), so an unauthenticated request gets a `401 {"error":"Unauthorized"}` rather than the
   handler's inline `{items:[]}` shape. Feature/authorization semantics are faithful; only the
   unauthenticated body differs, consistent with the established auth-port choice.

7. **Cache-tag invalidation on branding PUT is a no-op.** `runWithCacheTenant(... deleteByTags(
   nav:sidebar:*))` is skipped (no cache module). The `directory.organization.updated` event still
   fires.

8. **Hierarchy child-sort tiebreak uses ordinal comparison.** The pure engine reproduces the
   upstream comparator exactly — including the quirk that, when child names differ, it compares
   child a's lowercased *name* against child b's *id* — but `localeCompare` is approximated with
   ordinal `string.CompareOrdinal` sign. Marked `// PARITY-TODO` for exact ICU collation; results are
   identical for the lowercase-ascii names + lowercase-uuid ids in practice.

## Consequences

- Schema is byte-exact (`OpenMercato.Api/Migrations/20260707020000_AddDirectoryModule.cs`): tables
  `tenants`/`organizations`, `organizations_tenant_id_foreign` (ON UPDATE CASCADE), unique
  `organizations_tenant_slug_uniq (tenant_id, slug)`, jsonb hierarchy arrays default `'[]'`,
  timestamps `timestamptz`.
- The hierarchy engine + slugify are fully faithful and unit-tested.
- Remaining PARITY-TODOs are unblocked as the DataEngine, query_index, cache, and kms modules land.
