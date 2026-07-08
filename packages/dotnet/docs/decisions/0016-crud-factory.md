# 0016 — CRUD factory (makeCrudRoute equivalent)

## Status

Accepted

## Context

Upstream Open Mercato generates the standard list/get/create/update/delete
endpoints for every entity from a single ~2900-line options object,
`makeCrudRoute` (`packages/shared/src/lib/crud/factory.ts`). It owns the whole
observable HTTP contract: the list envelope `{items,total,page,pageSize,totalPages}`,
query-param parsing (page/pageSize clamp, sort aliases, `?ids=` intersection,
`withDeleted`), tenant/org/soft-delete scoping, command-bus-backed mutations with
`{id}`/`{ok:true}` responses + the `x-om-operation` header, custom-field
decoration, query-index maintenance, lifecycle events, and the error envelopes of
spec 02 (R19–R43). The customers (CRM) module and every future module needs this,
so it is ported into `OpenMercato.Core` (namespace `OpenMercato.Core.Crud`) on top
of the command bus (ADR 0015).

## Decision

- **A declarative config, not a 2900-line closure.** A module builds a
  `CrudConfig<TEntity>` and calls `CrudRoute.Map(routes, config)` from its
  `MapRoutes`. The factory owns the pipeline (auth → parse → scope → query →
  decorate → hook for reads; auth → validate → dispatch → side-effects → respond
  for writes) and the exact status codes/envelopes; entity-specific concerns come
  in as delegates/selectors on the config so the factory stays generic.
- **Reusable helpers.** `CrudListQueryParser` ports the shared query-param parsing
  (`Parse`, `ParseIds`, `ParseBooleanToken`) and the paged-envelope builder
  (`BuildEnvelope` → `{items,total,page,pageSize,totalPages}`, `totalPages =
  ceil(total/pageSize)`). Semantics that leak to the wire are reproduced exactly:
  page default 1, pageSize default 50 clamped to `[1, MaxPageSize]` (default 100),
  sort field `sortField ?? sort ?? default`, direction `sortDir ?? order`
  (`desc` case-insensitive), `?ids=` trimmed/deduped/UUID-filtered/capped at 200.
- **Generic EF querying via selectors.** `IdSelector` / `DeletedAtSelector` /
  `TenantIdSelector` / `OrganizationIdSelector` (LINQ expressions) let the factory
  build the soft-delete + tenant + org-scope `WHERE` clauses itself; `Sorts`
  (named delegates) and `ApplyFilters` (a delegate) cover entity-specific sort +
  filter/search. `ProjectItem`/`ProjectDetail` return a **mutable dictionary** so
  custom-field decoration can merge `customValues`/`customFields` in place.
- **Mutations dispatch through the command bus, typed by the module.** Because the
  bus is generic (`ICommand<TInput,TResult>`) and the factory does not know an
  entity's input/result types, each mutation is a `CrudDispatch` delegate that the
  module writes with concrete types (it calls
  `bus.ExecuteWithLog<TInput,TResult>(commandId, input, ctx)` and returns a
  `CrudMutationOutcome{ Id, Log }`). The factory then applies the standard response
  (`201 {id}` / `200 {ok:true}`), the `x-om-operation` header (from the returned
  `ActionLog`, port of `serializeOperationMetadata` → `omop:` + url-encoded JSON),
  and the post-commit side effects.
- **Optimistic lock + typed errors pass straight through.** Command handlers call
  `OptimisticLock.Enforce` (reads the expected-version header from
  `CommandContext.Headers`) and throw `CommandHttpException`; the factory maps
  `Status` + `Body` straight to the response, yielding the 409
  `{error:"record_modified",code:"optimistic_lock_conflict",…}` and 404
  `{error:"Not found"}` contracts without factory-specific logic.
- **Auth via a DI bridge, not a Core→Auth dependency.** Core owns the factory but
  must not reference the Auth module. `ICrudRequestContext` (in Core) turns the
  request into a `CommandContext` (headers populated) or `null` (→ 401) and runs
  the RBAC feature check (→ 403 `{error:"Forbidden",requiredFeatures:[…]}`). The
  Auth module registers the real `AuthCrudRequestContext`
  (`HttpContextAuth` + `IRbacService`); Core ships a fail-closed default so hosts
  without Auth still resolve the service. This reproduces the 401/403 envelopes and
  the empty-org-scope split (GET → 200 empty; mutation → 403) inside the factory
  rather than via the Auth endpoint filters.
- **Two extension interfaces with no-op defaults.** `ICrudCustomFields`
  (`MergeIntoListItems` / `MergeIntoDetail` / `Persist`) and `ICrudIndexer`
  (`UpsertOne` / `DeleteOne`) are registered as no-ops by `AddOpenMercatoCrud()`
  (Api + Worker) so the factory works today; the entities and query_index modules
  later override them via a plain `AddScoped` (last registration wins), without
  touching the factory. Lifecycle events go through the existing `IEventBus`
  (`<module>.<entity>.created|updated|deleted` → `{id,organizationId,tenantId}`).

## Deferred (PARITY-TODO — clean extension points, not behavioural changes)

Each is a documented seam in `CrudRoute`/`CrudConfig` where upstream runs extra
pipeline stages; the observable defaults are unchanged until they land:

- **API interceptors** (before/after, blocking + body/query rewrite, timeouts) —
  spec 02 R50–R54.
- **Response enrichers** (`_meta`, batch enrich, ACL gate) — R55–R57.
- **Exports** (`?format=csv|json|xml|markdown`, full-pagination) — R48–R49.
- **CRUD list cache** (`ENABLE_CRUD_API_CACHE`, `x-om-cache`, tag invalidation) —
  R58.
- **Mutation guards** + **sync before/after event subscribers** (422 blocks) —
  R39.
- **Read-access logging** (`logCrudAccess`) — R30.
- **Custom-field kind coercion / def-scope precedence / multi-value replace** and
  the **real query-index projection** (coverage, search tokens, "index errors
  never fail the write") arrive with the entities + query_index module ports; the
  factory already calls the hooks at the right pipeline points.

## Consequences

- A module registers a CRUD entity with one `CrudRoute.Map` call plus a
  `CrudConfig<TEntity>` (selectors + projection + typed dispatch delegates) and its
  command handlers — no per-endpoint boilerplate, and the observable contract is
  centralized and tested.
- The factory + helpers live in `OpenMercato.Core/Crud/`
  (`CrudRoute`, `CrudConfig`, `CrudListQuery`, `CrudExtensionPoints`,
  `CrudServiceCollectionExtensions`); the Auth bridge is
  `OpenMercato.Modules.Auth/Security/AuthCrudRequestContext`.
- Tests: `tests/OpenMercato.Tests/Crud/CrudRouteTests.cs` (13 HTTP-level tests via
  `TestServer`) cover the list envelope + pagination/clamp/sort/soft-delete/empty
  scope, single-item 404, create/update/delete dispatch with events + indexer +
  custom-field hooks, the `x-om-operation` header, the optimistic-lock 409, and the
  401/403 guards. Full suite green (135 passed).
