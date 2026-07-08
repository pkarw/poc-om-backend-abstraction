# API & HTTP Compatibility (api-http)

> Derived from upstream commit adc9da27759e357febe9ed8d4b7182040d127349. Source analysis: ../upstream/analysis/02-api-http.md

## Scope

This spec binds every technology port's HTTP layer: URL shape, route matching, the dispatch/guard pipeline (auth → tenant-pollution guard → feature check → rate limit), the CRUD endpoint contract (list envelope, pagination, sorting, filtering, custom fields, exports), exact error envelopes and status codes, extension points (interceptors, enrichers), response headers, and the OpenAPI docs endpoint. Anything an HTTP client can observe MUST match upstream byte-for-byte where stated. Upstream references: `apps/mercato/src/app/api/[...slug]/route.ts` (dispatcher), `packages/shared/src/lib/crud/factory.ts` (CRUD factory), `packages/shared/src/modules/registry.ts` (route matching).

Out of scope: persistence details (03-data-layer), event/queue wire formats (04-events-queues), token issuance and RBAC resolution internals (05-auth-rbac). This spec covers only how those subsystems surface on HTTP.

## Requirements

### Routing & URL shape

- **APIHTTP-R1** — Every module endpoint MUST be served under `/api/<moduleId>/<segments...>`. A route definition's explicit path override (upstream `metadata.path`) MUST take precedence over the derived path.
- **APIHTTP-R2** — Only HTTP methods actually declared by a route MUST be routable; a request for an undeclared method on an existing path MUST return `404 {"error":"Not Found"}` (same body as an unknown path).
- **APIHTTP-R3** — Dynamic path segments MUST support single params (`[id]`), catch-all (`[...slug]`, ≥1 segment), and optional catch-all (`[[...slug]]`), with param values exposed to handlers as string or string-array.
- **APIHTTP-R4** — Literal path segments MUST match case-insensitively.
- **APIHTTP-R5** — When multiple routes match, the most specific MUST win, compared segment-by-segment with precedence literal < single-param < catch-all (upstream `sortRoutesBySpecificity`).
- **APIHTTP-R6** — Routes SHOULD be declared per-module in an `api/` area of the module and aggregated into a single dispatch table (the port's equivalent of the generated manifest), so that adding a module adds its routes without touching a central router file.

### Dispatch pipeline & authorization

- **APIHTTP-R7** — The dispatcher MUST execute, in order: route match → auth resolution → authorization check → rate limit → handler. A failure at any stage MUST short-circuit with the exact status/envelope in [Contracts](#contracts).
- **APIHTTP-R8** — Auth MUST be resolved in this precedence order: `Authorization: Bearer <jwt>` header → `auth_token` cookie JWT → API key from `x-api-key` header or `Authorization: ApiKey <key>`. Resolution status is one of authenticated / missing / invalid.
- **APIHTTP-R9** — Authentication MUST be required by default: a route without metadata, or without an explicit `requireAuth: false`, MUST return `401 {"error":"Unauthorized"}` to anonymous callers (upstream: `metadata?.requireAuth !== false`).
- **APIHTTP-R10** — `requireFeatures: string[]` metadata MUST be enforced via RBAC (all features required, evaluated in the request's resolved tenant/organization scope); failure MUST return `403 {"error":"Forbidden","requiredFeatures":[...]}`.
- **APIHTTP-R11** — `requireRoles` metadata MUST be accepted but ignored at runtime (deprecated as spoofable); a port SHOULD log a one-time warning when it is present.
- **APIHTTP-R12** — Tenant parameter pollution guard: before the handler runs, every distinct `tenantId` candidate — all repeated `?tenantId=` query params plus a body-level `tenantId` (JSON, urlencoded, or multipart) — MUST be validated against the actor's tenant-selection rights; an unauthorized candidate MUST fail the request (typically 403) without invoking the handler.
- **APIHTTP-R13** — Customer-type JWTs MUST be rejected on staff APIs (treated as unauthenticated).
- **APIHTTP-R14** — JWTs whose `tenantId` claim is not a UUID MUST be treated as unauthenticated by CRUD handlers.
- **APIHTTP-R15** — When auth resolution status is invalid (expired/garbage credentials) and the response is 401, the `auth_token` and `session_token` cookies MUST be cleared on the response.
- **APIHTTP-R16** — `rateLimit: { points, duration, blockDuration?, keyPrefix? }` metadata MUST be enforced per client IP (honoring configured trusted-proxy depth); on limit the response MUST be 429 with a `Retry-After` header and `X-RateLimit-*` headers.
- **APIHTTP-R17** — Handlers MUST receive the raw request plus a context of `{ params, auth }`; guard enforcement MUST NOT depend on handler cooperation (dispatcher-level).
- **APIHTTP-R18** — The port SHOULD emit request lifecycle events (received, auth-resolved, authorization-denied, rate-limited, completed, failed) carrying `{requestId, method, pathname, status, durationMs, userId, tenantId}`, best-effort and never failing the request. The `x-request-id` request header MUST be propagated into these events when present.

### CRUD factory (shared implementation requirement)

- **APIHTTP-R68** — Where the upstream module builds an endpoint with `makeCrudRoute` (`packages/shared/src/lib/crud/factory.ts`), a port MUST implement those endpoints through the target package's **shared CRUD factory** — a single reusable factory/base-class producing the standard list/get/create/update/delete contract of R19–R49 — NOT hand-written per-route logic. Hand-writing the factory contract per route is a parity defect: the list envelope, pagination clamping, sort aliasing, `ids` intersection, custom-field decoration, error envelopes, and pipeline ordering (R29/R39) all live in the factory so every ported module inherits them identically. The reference implementation is the .NET **`OpenMercato.Core.Crud`** package (`CrudRoute.Map<TEntity>(routes, CrudConfig<TEntity>)` = `makeCrudRoute`), with the wire-behavior nuances that are not yet built left as `// PARITY-TODO` + ADR behind clean extension points (`ICrudCustomFields`, `ICrudIndexer`, `ICrudRequestContext`). A port targeting a package that has no CRUD factory yet MUST add one before porting a `makeCrudRoute`-based module (it is shared infrastructure every module reuses, not per-module code).
- **APIHTTP-R69** — CRUD **write** operations (POST/PUT/DELETE) produced by the factory MUST go through the port's **command bus** (spec 03 R57): each mutation dispatches a named command (`'<module>.<domain>.<action>'`) whose `execute`/`undo`/`redo` handlers perform the write and whose action-log row backs the `x-om-operation` undo header (R41) and the undo/redo endpoints. The factory MUST NOT write to the base table directly, bypassing the command pipeline — the command bus owns the transaction boundary, the `action_logs` row, and the post-commit side-effect flush (CRUD events + query-index maintenance). Reference: .NET `OpenMercato.Core.Commands.CommandBus` dispatched from `CrudConfig<TEntity>`'s mutation handlers.

### CRUD list endpoints (factory GET)

- **APIHTTP-R19** — List endpoints on the canonical (query-engine) path MUST return the envelope `{items, total, page, pageSize, totalPages}` with `totalPages = ceil(total/pageSize)`; optional `meta` (query-engine metadata such as `partialIndexWarning`) and `_meta` (enrichment info) keys appear only when applicable.
- **APIHTTP-R20** — Pagination: `page` defaults to 1; `pageSize` defaults to 50 and MUST be clamped to [1, 100] regardless of what the route schema allows; non-numeric values fall back to the defaults.
- **APIHTTP-R21** — Sorting: field from `sortField ?? sort ?? 'id'`; direction from `sortDir ?? order ?? 'asc'` (`desc` matched case-insensitively); the field is mapped through the route's sort-field map, then a `cf_<key>` field MUST normalize to the `cf:<key>` custom-field selector.
- **APIHTTP-R22** — `?ids=` MUST be parsed as a comma-separated list: trimmed, deduplicated, non-UUID entries silently dropped, capped at 200 entries. It MUST be merged into existing filters by INTERSECTION (never widening); if an existing `id` filter has an unrecognized shape it MUST be kept as-is (fail closed). An intersection producing `id: {$in: []}` is a legitimate empty result — do not special-case it.
- **APIHTTP-R23** — `filter[...]` query params (advanced filters, v1 flat and v2 tree) MUST be parsed and AND-merged into route filters; `$or` combination uses cross-product AND-merge.
- **APIHTTP-R24** — `withDeleted` MUST be parsed as a boolean token; only `true` includes soft-deleted rows.
- **APIHTTP-R25** — Query-param validation failure MUST return `400 {"error":"Invalid input","details":[...]}` where `details` is an array of Zod-issue-like objects (each with `path` array, `message`, `code`).
- **APIHTTP-R26** — List query schemas MUST tolerate unknown query params (passthrough), so dynamic `cf_*` filter params survive validation.
- **APIHTTP-R27** — Empty organization scope (actor can see zero orgs, `organizationIds === []`): list endpoints MUST return **200** with `{items:[],total:0,page,pageSize,totalPages:0}` — not 403. After-list hooks, after-interceptors, and enrichers still run.
- **APIHTTP-R28** — Routes on the ORM-fallback list path (no query-engine config upstream) return **unpaginated, unsorted** `{items, total}` with no `page/pageSize/totalPages`. A port MUST reproduce this per-route (do not silently "fix" it); the per-module contract states which path each route uses.
- **APIHTTP-R29** — The list pipeline order MUST be: validate → before-interceptors (re-validate mutated query) → beforeList hook → cache check → query → per-item transform → custom-field decoration → translation overlay → access log → afterList hook → after-interceptors → enrichers → cache store → respond. Enrichers run last and their `_meta` MUST NOT be stripped.
- **APIHTTP-R30** — Every successful read SHOULD be access-logged per unique record id with `{tenantId, organizationId, actorUserId, resourceKind, resourceId, accessType(read|read:item|read:list), fields, context}`, asynchronously (blocking only when `OM_CRUD_ACCESS_LOG_BLOCKING=1`). For API-key auth, `actorUserId` is the key id.

### CRUD mutations (factory POST/PUT/DELETE)

- **APIHTTP-R31** — Default success responses MUST be: POST → `201 {"id":"<uuid>"}`; PUT → `200 {"success":true}`; DELETE → `200 {"success":true}`. Command-path routes (upstream `actions.*`) commonly override to `{"ok":true}` — the per-module contract is authoritative for each route.
- **APIHTTP-R32** — A method not configured on a CRUD route MUST return `501 {"error":"Not implemented"}`; an unauthenticated call `401 {"error":"Unauthorized"}`; a mutation with empty organization scope `403 {"error":"Forbidden"}`.
- **APIHTTP-R33** — Malformed JSON request bodies MUST be treated as `{}` (then typically failing schema validation with 400) — never a raw 500.
- **APIHTTP-R34** — Body validation failure MUST return the same `400 {"error":"Invalid input","details":[...]}` envelope as R25.
- **APIHTTP-R35** — PUT MUST take the id from the configured extractor or `body.id`; a non-UUID id MUST return `400 {"error":"Invalid id"}`. DELETE MUST take the id from `?id=` (or the body when the route says so); a missing/non-UUID id MUST return `400 {"error":"ID is required"}`.
- **APIHTTP-R36** — Update/delete target lookup MUST be scoped by tenant, organization, and soft-delete: rows in another tenant/org, or soft-deleted rows, MUST be invisible and yield `404 {"error":"Not found"}` (lowercase `f`, distinct from the dispatcher's `"Not Found"`).
- **APIHTTP-R37** — DELETE MUST soft-delete by default (set the soft-delete timestamp); hard delete only where the route opts out. Command-path DELETE validates the combined `{body, query}` object against the route schema (a different input shape from POST/PUT, which validate body only).
- **APIHTTP-R38** — Direct-path create/update MUST inject the resolved organization and tenant into the entity; a missing scope MUST return `400 {"error":"Organization context is required"}` or `400 {"error":"Tenant context is required"}`. Command-path routes do NOT auto-inject scope — the command/mapping is responsible (per-module contract).
- **APIHTTP-R39** — The mutation pipeline order MUST be: validate → before-interceptors (re-validate mutated body) → synchronous before-event subscribers (may block ⇒ `422 {"error":"Operation blocked"}`, or mutate input) → before hook → mutation guards (may block ⇒ `422 {"error":"Operation blocked by guard"}` or guard-provided status/body, or mutate) → persist + custom-field write in ONE transaction → after hook → guard success callbacks → synchronous after-event subscribers → event emission + index flush → cache invalidation → response mapping → after-interceptors → single-record enrichment → status.
- **APIHTTP-R40** — Optimistic locking (opt-in, env `OM_OPTIMISTIC_LOCK`): when the client sends `x-om-ext-optimistic-lock-expected-updated-at: <ISO date>` and it mismatches the row, the response MUST be `409 {"error":"record_modified","code":"optimistic_lock_conflict","currentUpdatedAt":"...","expectedUpdatedAt":"..."}`.
- **APIHTTP-R41** — When a command-path mutation produces an undo-able operation log entry, the response MUST carry header `x-om-operation: omop:<urlencoded JSON of {id,undoToken,commandId,actionLabel,resourceKind,resourceId,executedAt}>`.
- **APIHTTP-R42** — Typed HTTP errors thrown anywhere in handlers/hooks/commands (upstream `CrudHttpError`) MUST pass through as their own status + body; Postgres unique violations (23505) SHOULD map to 409 via the same mechanism.
- **APIHTTP-R43** — Any other unhandled error MUST be logged server-side and returned as `500 {"error":"Internal server error","message":"Something went wrong. Please try again later."}` — never leaking stack traces or internal messages.

### Custom fields on the wire

- **APIHTTP-R44** — Input: custom-field values MUST be collected from body keys `cf_<key>`, `cf:<key>`, a `customFields` array (`[{key,value}]`) or object, and a `customValues` object, and written in the same transaction as the base entity (where the route enables custom fields).
- **APIHTTP-R45** — List filtering: query params `cf_<key>=<v>` (equality, coerced by field kind: integer/float/boolean/date) and `cf_<key>In=a,b,c` (IN) MUST be supported on routes that wire custom-field filters.
- **APIHTTP-R46** — List output: items MUST be decorated with `customValues` (`{key: value}` map or `null`) and `customFields` (`[{key,label,value,kind,multi}]`, ordered by definition priority asc, then updatedAt desc, then key; definition resolution precedence org+tenant > org > tenant > global, skipping inactive/tombstoned). By default the raw `cf_*` keys REMAIN on the item alongside the decorated keys; stripping them is per-route opt-in (`stripPrefixedKeys`).
- **APIHTTP-R47** — Detail endpoints that return a custom-values map MUST strip the `cf_`/`cf:` prefixes and drop `undefined` entries (upstream `normalizeCustomFieldResponse`); multi-value fields serialize as JSON arrays.

### Exports

- **APIHTTP-R48** — When a route enables exports, `?format=csv|json|xml|markdown` MUST bypass the JSON envelope and return the serialized document with the matching `content-type` (`text/csv`, `application/json`, `application/xml`, `text/markdown`) and `content-disposition: attachment; filename="<name>.<ext>"`.
- **APIHTTP-R49** — Exports MUST iterate all pages until `total` records are collected (batch size clamped 100–10000, default 1000). `exportScope=full` (or `full=1`) exports raw records with `cf_` prefixes removed from keys and `_meta`/`_*` keys stripped.

### API interceptors

- **APIHTTP-R50** — The port MUST provide an API interceptor mechanism equivalent to upstream `api/interceptors.ts`: interceptors declare `id` (`<module>.<name>`), `targetRoute` (exact `sales/orders`, prefix `sales/*`, or `*` — matched against the pathname WITHOUT the `/api/` prefix), `methods`, optional `priority`, optional ACL `features` gate, optional `timeoutMs`.
- **APIHTTP-R51** — Interceptor ordering MUST be deterministic: priority descending, then module registration order, then declaration order. Interceptors whose `features` the caller lacks MUST be skipped.
- **APIHTTP-R52** — A `before` interceptor MUST be able to (a) block the request — default `400 {"error":"<message ?? 'Request blocked by API interceptor'>"}` with an interceptor-chosen status, plus `interceptorId` only outside production — or (b) replace body/query/headers, with replacements chained to the next interceptor and the final body/query RE-VALIDATED against the route schema. Replacement objects drop keys with undefined values; `metadata` returned by `before` is delivered to the same interceptor's `after`.
- **APIHTTP-R53** — An `after` interceptor MUST be able to shallow-merge into or fully replace the response body. Per-interceptor timeout (default 5000 ms) MUST yield `504 {"error":"Interceptor timeout"}`; a thrown error `500 {"error":"Internal interceptor error"}` (both with `interceptorId`/`message` only outside production).
- **APIHTTP-R54** — Hand-written routes MAY opt in to after-interceptors explicitly (upstream `runCustomRouteAfterInterceptors`, e.g. `auth/login`); where the per-module contract says a route does, the port MUST too.

### Response enrichers

- **APIHTTP-R55** — The port MUST provide a response enricher mechanism equivalent to upstream `data/enrichers.ts`: enrichers target an entity id (`customers.person`), are ACL-gated by `features` (supporting `*` and `prefix.*` grants) and `disabledTenantIds`, and run in registry order after after-interceptors.
- **APIHTTP-R56** — List endpoints MUST use a batch enrich (upstream `enrichMany`, N+1 prevention); single-record responses (detail, POST/PUT bodies) use per-record enrichment. Enricher output MUST be additive and namespaced under `_<module>` keys.
- **APIHTTP-R57** — Enricher timeout defaults to 2000 ms; on non-critical failure/timeout the enricher is skipped, its optional `fallback` object merged into each record, and its id appended to `_meta.enricherErrors`; `critical: true` failures MUST propagate (→ 500). Successful enrichment adds `_meta = {enrichedBy:[ids], enricherErrors?:[ids]}` to the payload only when non-empty.

### Caching & headers

- **APIHTTP-R58** — CRUD list caching MUST be opt-in via `ENABLE_CRUD_API_CACHE`; when on, GET list responses carry `x-om-cache: hit|miss`. The cache key MUST partition on resource, pathname, tenant, selected org, org-scope set, canonicalized query string, AND the active-enricher signature (so ACL-gated enriched fields never leak across users). Mutations MUST tag-invalidate (resource + record tags). Cached payloads missing an `items` array MUST be evicted and treated as a miss.
- **APIHTTP-R59** — `x-om-partial-index` MUST be set when the query engine reports a partial index, with JSON value `{type:'partial_index',entity,entityLabel,baseCount,indexedCount,scope}`.
- **APIHTTP-R60** — JSON responses MUST be compact serialization with `content-type: application/json` exactly (no charset parameter, no pretty-printing, no trailing newline).
- **APIHTTP-R61** — Generic extension request headers `x-om-ext-<module>-<key>` MUST be readable by module code (the optimistic-lock header of R40 is one instance).

### OpenAPI documentation

- **APIHTTP-R62** — The port MUST serve `GET /api/docs/openapi` returning an OpenAPI **3.1.0** document built from route declarations: one path item per route path, methods limited to declared handlers, operationId derived from module id + path + method unless provided.
- **APIHTTP-R63** — Route metadata MUST feed the docs: `requireAuth` ⇒ auto `401` error response + `bearerAuth` security + `x-require-auth: true`; `requireFeatures` ⇒ auto `403` error + `Requires features: ...` description line + `x-require-features`. Missing 2xx ⇒ fallback `201` for POST, `204` for DELETE, `200` otherwise. Each operation SHOULD include a cURL `x-codeSamples` entry.
- **APIHTTP-R64** — `GET /api/docs/markdown` SHOULD render the same document as Markdown. Note: upstream's docs endpoints live outside the module manifest and have no dispatcher metadata guard; a port MUST match their auth posture as observed upstream.
- **APIHTTP-R65** — Every ported route SHOULD carry a machine-readable doc declaration (the port's analog of the `openApi` export) so the docs endpoint stays generated, not hand-maintained.

### Validation & i18n

- **APIHTTP-R66** — Factory-route validation MAY use any native library, but the 400 envelope MUST be `{"error":"Invalid input","details":[...]}` with issue objects structurally compatible with Zod issues (`path` as array, `message`, `code`).
- **APIHTTP-R67** — Hand-written routes define their own validation envelopes (e.g. login's `{"ok":false,"error":"Invalid credentials"}`); a port MUST match each such route individually per its module contract.

## Contracts

### Error envelopes (exact bodies)

| Status | Body | Emitted by |
|---|---|---|
| 400 | `{"error":"Invalid input","details":[...issues]}` | schema validation failure (factory routes) |
| 400 | `{"error":"Invalid id"}` | PUT non-UUID id |
| 400 | `{"error":"ID is required"}` | DELETE missing/non-UUID id |
| 400 | `{"error":"Organization context is required"}` / `{"error":"Tenant context is required"}` | direct-path create/update without scope |
| 401 | `{"error":"Unauthorized"}` | dispatcher and factory |
| 403 | `{"error":"Forbidden","requiredFeatures":["..."]}` | dispatcher feature check |
| 403 | `{"error":"Forbidden"}` | factory mutation with empty org scope |
| 404 | `{"error":"Not Found"}` | dispatcher (no route / no handler) — capital F |
| 404 | `{"error":"Not found"}` | factory (scoped lookup miss) — lowercase f |
| 409 | `{"error":"record_modified","code":"optimistic_lock_conflict","currentUpdatedAt":"...","expectedUpdatedAt":"..."}` | optimistic-lock guard |
| 422 | `{"error":"Operation blocked"}` / `{"error":"Operation blocked by guard"}` (or subscriber/guard-provided status+body) | sync before-event subscribers / mutation guards |
| 429 | `{"error":"<rate limit message>"}` + `Retry-After`, `X-RateLimit-*` headers | rate limiter |
| 500 | `{"error":"Internal server error","message":"Something went wrong. Please try again later."}` | factory catch-all |
| 500 / 504 | `{"error":"Internal interceptor error"}` / `{"error":"Interceptor timeout"}` (+ `interceptorId`, `message` outside production) | interceptor runner |
| 501 | `{"error":"Not implemented"}` | factory method not configured |

### List response envelope (query-engine path)

```json
{
  "items": [
    {
      "id": "…",
      "…": "…",
      "cf_priority": 3,
      "customValues": { "priority": 3 },
      "customFields": [
        { "key": "priority", "label": "Priority", "value": 3, "kind": "integer", "multi": false }
      ]
    }
  ],
  "total": 123,
  "page": 1,
  "pageSize": 50,
  "totalPages": 3,
  "meta": { "partialIndexWarning": { "entity": "…", "baseCount": 10, "indexedCount": 8, "scope": "scoped" } },
  "_meta": { "enrichedBy": ["module.enricher"], "enricherErrors": ["…"] }
}
```

`meta` and `_meta` appear only when applicable. ORM-fallback routes return only `{"items":[...],"total":N}`.

### Standard list query parameters

| Param | Contract |
|---|---|
| `page` | default 1 |
| `pageSize` | default 50, clamped [1, 100] |
| `sortField` (alias `sort`) | default `id`; `cf_<key>` sorts on custom field |
| `sortDir` (alias `order`) | `asc`/`desc` (case-insensitive), default `asc` |
| `ids` | comma-separated UUIDs, max 200, dedup, non-UUIDs dropped; intersection merge |
| `withDeleted` | boolean token; include soft-deleted rows |
| `format` | `csv`/`json`/`xml`/`markdown` (if enabled); with `exportScope=full` or `full=1` |
| `filter[...]` | advanced filter tree/flat params, AND/OR-merged into route filters |
| `cf_<key>`, `cf_<key>In` | custom-field equality / IN filters |
| `tenantId` | super-admin tenant selection; every occurrence validated by pollution guard |

### Request headers understood

`Authorization: Bearer <jwt>` · `Authorization: ApiKey <key>` · `x-api-key: <key>` · `cookie: auth_token=…; session_token=…` · `x-request-id` · `x-om-ext-optimistic-lock-expected-updated-at: <ISO date>` · generic `x-om-ext-<module>-<key>`.

### Response headers

| Header | When | Format |
|---|---|---|
| `x-om-cache` | GET list, `ENABLE_CRUD_API_CACHE` on | `hit` \| `miss` |
| `x-om-partial-index` | partial query index | JSON `{type:'partial_index',entity,entityLabel,baseCount,indexedCount,scope}` |
| `x-om-operation` | undo-able command mutation | `omop:` + `encodeURIComponent(JSON.stringify({id,undoToken,commandId,actionLabel,resourceKind,resourceId,executedAt}))` |
| `content-disposition` | exports | `attachment; filename="<name>.<ext>"` |
| `Retry-After`, `X-RateLimit-*` | 429 | seconds / limiter stats |
| `content-type` | JSON responses | `application/json` exactly (no charset) |

### Reference contract 1 — CRUD route `/api/example/todos` (upstream `apps/mercato/src/modules/example/api/todos/route.ts`)

- Metadata: GET `{requireAuth: true, requireFeatures: ['example.todos.view']}`; POST/PUT/DELETE `{requireAuth: true, requireFeatures: ['example.todos.manage']}`.
- `GET` query: `id?` (uuid), `page`, `pageSize`, `ids?`, `sortField` (default `id`), `sortDir`, `title?` (ILIKE contains, `%`/`_` escaped), `isDone?`, `withDeleted?`, `organizationId?`, `createdFrom?/createdTo?`, `format?` (`json|csv`), dynamic `cf_*`. Response `200 {items:[{id,title,tenant_id,organization_id,is_done, cf_<key>…, customValues, customFields}], total, page, pageSize, totalPages}`. `?format=csv` ⇒ CSV columns `id,title,is_done,organization_id,tenant_id,cf_<key>…`, filename `todos.csv`.
- `POST` ⇒ command `example.todos.create`, `201 {"id":"<uuid>"}` + `x-om-operation`. `PUT` (body requires `id`) ⇒ `example.todos.update`, `200 {"ok":true}`. `DELETE` (validates `{body,query}`) ⇒ `200 {"ok":true}`.

### Reference contract 2 — hand-written route `POST /api/auth/login` (upstream `packages/core/src/modules/auth/api/login.ts`)

- `metadata = { requireAuth: false }` (public).
- Request: `application/x-www-form-urlencoded` or multipart, fields `email`, `password`, `remember?` (`on|1|true`), `tenantId?` (alias `tenant`), `requireRole?` (comma list, alias `role`), `redirect?`.
- Rate limits run BEFORE validation: per-IP 20/60 s and per-`login:{email}` 5/60 s with 60 s block (env-configurable) ⇒ 429 + `Retry-After`.
- Validation failure ⇒ `400 {"ok":false,"error":"Invalid credentials"}`. Bad credentials (including ambiguous multi-tenant email; constant-time password verify always runs) ⇒ `401 {"ok":false,"error":"Invalid email or password"}`. Failed role requirement ⇒ `403 {"ok":false,"error":"Not authorized for this area"}`.
- Success `200 {"ok":true,"token":"<jwt>","redirect":"/backend","refreshToken":"<token>"?}` (`refreshToken` only with `remember`); body passes through after-interceptors for route `auth/login`.
- Cookies set: `auth_token` (httpOnly, path=/, sameSite=lax, secure in production, maxAge 8 h) and `session_token` (httpOnly; `remember` ⇒ expiry `REMEMBER_ME_DAYS` days, default 30, else 8 h).

### Hand-written GET variant — `/api/currencies/currencies` (upstream `packages/core/src/modules/currencies/api/currencies/route.ts`)

Paginated hand-written GET with `totalPages = max(1, ceil(total/pageSize))` (differs from the factory's plain `ceil`) and 401/400 responses that carry an EMPTY list envelope body with the error status. Factory POST/PUT/DELETE via commands respond `201 {"id":…}` / `200 {"ok":true}` / `200 {"ok":true}`. Illustrates: hand-written routes deviate; per-module contracts win over factory defaults.

## Concept mapping

| Upstream TS concept | Technology-agnostic concept a port implements |
|---|---|
| `api/**/route.ts` / `api/<file>.ts` / legacy `api/<method>/<path>.ts` per module | Per-module route declarations discovered/registered into one dispatch table |
| Codegen manifest (`.mercato/generated/api-routes.generated.ts`) + Next catch-all `[...slug]/route.ts` | Central dispatcher: single entry point applying match → auth → guards → rate limit → handler |
| `metadata` export (`requireAuth`, `requireFeatures`, `rateLimit`, `path`) | Declarative per-route/per-method guard metadata enforced by the dispatcher |
| `matchRoutePattern` / `sortRoutesBySpecificity` | Path matcher with `[id]`/`[...slug]`/`[[...slug]]` params, case-insensitive literals, specificity ordering |
| `makeCrudRoute` (`packages/shared/src/lib/crud/factory.ts`) | Shared CRUD endpoint factory/base-class producing the list/create/update/delete contract of R19–R43 (R68). Reference port: .NET `OpenMercato.Core.Crud` — `CrudRoute.Map<TEntity>(routes, CrudConfig<TEntity>)` |
| `runCrudCommandWrite` + `CommandBus` (`packages/shared/src/lib/commands/*`) | Factory mutations dispatched through the command bus so writes get execute/undo/redo + an action-log row (R69). Reference port: .NET `OpenMercato.Core.Commands.CommandBus` |
| Zod schemas (`data/validators.ts`, `list.schema`, …) | Native validation producing the `{"error":"Invalid input","details":[...]}` envelope |
| `CrudHttpError` + `badRequest/forbidden/notFound/conflict` | Typed HTTP exception carrying status + JSON body, honored by the top-level error mapper |
| `withCtx` (request-scoped Awilix container + org scope) | Request context: DI scope, auth claims, resolved org scope (`null`=unrestricted, `[]`=fail-closed, `[ids]`=restricted) |
| `buildScopedWhere` / `findOneScoped` / `softDelete` | Tenant/org/soft-delete-scoped query builders used for all reads and mutation lookups |
| `api/interceptors.ts` + interceptor runner | Registered request/response middleware with route targeting, priority, ACL gate, timeouts (R50–R54) |
| `data/enrichers.ts` + enricher runner | Cross-module response decoration with batch mode, ACL gate, `_meta` bookkeeping (R55–R57) |
| `splitCustomFieldPayload` / `decorateRecordWithCustomFields` / `normalizeCustomFieldResponse` | Custom-field wire codec: input collection, list decoration, detail normalization (R44–R47) |
| `openApi` export + `buildOpenApiDocument` | Per-route doc declaration + runtime OpenAPI 3.1.0 generator at `/api/docs/openapi` |
| `serializeOperationMetadata` (`omop:` header) | Undo-operation response-header codec (R41) |
| `checkRateLimit` / `getClientIp` | IP-keyed rate limiter with 429 + `Retry-After`/`X-RateLimit-*` |
| CRUD list cache (`ENABLE_CRUD_API_CACHE`, tags) | Opt-in tenant/org/query/enricher-partitioned list cache with tag invalidation (R58) |
| `logCrudAccess` | Asynchronous read-access audit log (R30) |

## Allowed deviations

Idiomatic replacements are welcome; the observable wire behavior is not negotiable.

| Area | Free to change | MUST NOT change |
|---|---|---|
| Web framework | FastAPI / ASP.NET / chi / anything; no codegen step needed if routes register at startup | URL paths, methods, status codes, envelopes, header names/values, guard ordering (R7) |
| Validation | Pydantic / FluentValidation / go-validator instead of Zod | The `{"error":"Invalid input","details":[...]}` envelope with `path`/`message`/`code` issue objects; passthrough of unknown `cf_*` query params |
| DI / request context | Language-native DI or plain constructors instead of Awilix scopes | Org-scope semantics (`null`/`[]`/`[ids]`), empty-scope behavior (GET 200-empty vs mutation 403) |
| CRUD factory | A base class, generics, or code generation instead of a 2909-line options object | Every observable behavior in R19–R49, including pipeline ordering R29/R39 |
| Interceptors/enrichers | Native middleware/plugin idioms | Targeting syntax semantics, ordering, timeout statuses (504/500), `_meta` shape, ACL gating, additive `_<module>` namespacing |
| OpenAPI | Any generator library | 3.1.0 output, metadata-derived 401/403, `x-require-auth`/`x-require-features` extensions, method/status fallbacks |
| Internal helpers | Names, file layout, decomposition | Helper semantics that leak to the wire (ids intersection, sort alias fallback, clamping, boolean-token parsing) |
| ORM-fallback quirk | You MAY implement both list paths with one engine | Which envelope each ported route emits (paginated vs bare `{items,total}`) per its module contract |
| Legacy `api/<method>/` layout | Ports NEED NOT support authoring legacy-layout routes | The public URLs that upstream legacy routes produce (e.g. `GET /api/api_docs/version`) |
| Docs endpoints | Serving `/api/docs/*` from inside or outside the module system | Their paths and response formats |

Record every notable deviation as an ADR in `packages/<tech>/docs/decisions/`.

## Verification

How `om-verify-parity` checks this spec for a ported module (upstream reference vs port, both running against equivalent seed data):

1. **Route table diff** — fetch `/api/docs/openapi` from both; compare path set, per-path method set, `x-require-auth`/`x-require-features`, and response status maps for the module's routes (R1–R6, R62–R63).
2. **Guard matrix** — for each route: anonymous request (expect 401 unless `requireAuth:false`), authenticated-without-feature (expect exact 403 body incl. `requiredFeatures`), tenant-pollution probes (repeated `?tenantId=` + body `tenantId` for a foreign tenant), unknown path/method (404 `"Not Found"`), invalid-JWT cookie clearing (R7–R17).
3. **Envelope byte-diff** — golden-request suite per route: list defaults, `page/pageSize` clamping (`pageSize=999` ⇒ 100), sort aliases (`sort`+`order` vs `sortField`+`sortDir`), `ids` intersection incl. the empty-intersection case, `withDeleted`, `filter[...]`, `cf_*` filters; responses compared as canonical JSON, with `items` ordering asserted, plus header assertions (`content-type` exactly `application/json`, conditional `x-om-*` headers) (R19–R29, R44–R47, R58–R60).
4. **Mutation lifecycle** — create/update/delete happy paths asserting exact status+body (201 `{id}` / factory `{success:true}` / command `{ok:true}` per contract); malformed JSON ⇒ 400 not 500; non-UUID ids ⇒ exact 400 bodies; cross-tenant update/delete ⇒ 404 `"Not found"`; soft-delete visibility (`withDeleted`); optimistic-lock 409 body; `x-om-operation` header decodes to the documented JSON keys (R31–R43).
5. **Error-envelope catalogue** — trigger each row of the Contracts error table at least once per port and byte-compare bodies, including the `"Not Found"`/`"Not found"` casing pair (R25, R32–R36, R43).
6. **Export checks** — `?format=csv` (and other enabled formats): content-type, `content-disposition` filename, column set, full-pagination row count > one batch (R48–R49).
7. **Extension hooks** — register a test interceptor (block, mutate-body, after-merge, timeout) and a test enricher (normal, fallback, critical) in the port and assert R50–R57 semantics, including `_meta.enrichedBy` and the 504/500 interceptor statuses.
8. **Rate limiting** — hammer a rate-limited route (e.g. `auth/login`) past its limits and assert 429 + `Retry-After` + `X-RateLimit-*` presence (R16).
9. **Hand-written routes** — replay the per-module contract's recorded request/response pairs (e.g. login form-encode flows incl. cookies attributes) and diff (R67).

A requirement passes only when the port's observable output is indistinguishable from upstream's for every probe; SHOULD-level items (R6, R18, R30, R64–R65) are reported as warnings, not failures.
