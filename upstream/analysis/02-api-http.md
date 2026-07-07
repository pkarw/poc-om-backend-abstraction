# HTTP API Surface & Conventions (api-http)

> Analyzed at upstream commit adc9da27759e357febe9ed8d4b7182040d127349 (2026-07-01). Regenerate via the om-sync-upstream skill.

## Purpose

Open Mercato exposes its entire backend as a single HTTP JSON API under `/api/*`. Routes are **auto-discovered files inside module directories** (`packages/core/src/modules/<module>/api/...` and app-level `apps/mercato/src/modules/<module>/api/...`), compiled by a codegen step into a route manifest, and dispatched at runtime by one Next.js catch-all handler. Most CRUD endpoints are produced by a single shared factory, `makeCrudRoute` (`packages/shared/src/lib/crud/factory.ts`, 2,909 lines), which standardizes list/get/create/update/delete semantics, pagination envelope, Zod validation, tenant/organization scoping, custom-field (`cf_`) handling, response enrichers, API interceptors, caching, access logging, events and cache invalidation. Every route also exports an `openApi` doc object from which `/api/docs/openapi` builds an OpenAPI 3.1.0 document at runtime.

A port must reproduce: the URL shape (`/api/<moduleId>/<segments>`), the dispatch/guard pipeline (auth → tenant-pollution guard → feature check → rate limit → handler), the CRUD factory's observable request/response contract, and the exact error envelopes and status codes.

## Key source locations

| Path (upstream repo root) | Contains |
|---|---|
| `apps/mercato/src/app/api/[...slug]/route.ts` | The single Next.js catch-all dispatcher: manifest match, auth resolution, `checkAuthorization` (requireAuth/requireFeatures), tenant-parameter-pollution guard, rate limiting, lifecycle events, cookie clearing on invalid auth |
| `packages/shared/src/modules/registry.ts` | `ApiRouteManifestEntry`, `matchRoutePattern` (`[id]`, `[...rest]`, `[[...rest]]`), `sortRoutesBySpecificity`, `findApiRouteManifestMatch`, `registerApiRouteManifests` |
| `packages/cli/src/lib/generators/module-registry.ts` (`processApiRoutes`, ~line 1540) | Codegen that scans `api/` folders and emits `.mercato/generated/api-routes.generated.ts` (`apiRoutes` manifest) |
| `packages/shared/src/lib/crud/factory.ts` | `makeCrudRoute` — the CRUD factory (GET/POST/PUT/DELETE handlers + metadata) |
| `packages/shared/src/lib/api/crud.ts` | `buildScopedWhere`, `findOneScoped`, `softDelete` |
| `packages/shared/src/lib/crud/errors.ts` | `CrudHttpError`, `isCrudHttpError`, `badRequest/forbidden/notFound/conflict`, `isUniqueViolation`, `assertFound` |
| `packages/shared/src/lib/crud/ids.ts` | `parseIdsParam` (`?ids=` comma list, max 200 UUIDs), `mergeIdFilter` (intersection semantics) |
| `packages/shared/src/lib/crud/advanced-filter-integration.ts` | `mergeAdvancedFilters` / `mergeAdvancedFiltersFromQuery` (`filter[...]` query params → `Where` tree) |
| `packages/shared/src/lib/crud/api-interceptor.ts`, `interceptor-registry.ts`, `interceptor-runner.ts` | API interceptor contract (`before`/`after`), registry (module `api/interceptors.ts` exports `interceptors`), runner with timeout/priority |
| `packages/shared/src/lib/crud/response-enricher.ts`, `enricher-registry.ts`, `enricher-runner.ts` | Response enricher contract (module `data/enrichers.ts` exports `enrichers`), runner, `_meta.enrichedBy` |
| `packages/shared/src/lib/crud/custom-fields.ts` | `cf_`/`cf:` extraction, `splitCustomFieldPayload`, `decorateRecordWithCustomFields`, `applyCustomFieldsNormalization`, `buildCustomFieldFiltersFromQuery`, `loadCustomFieldValues` |
| `packages/shared/src/lib/custom-fields/normalize.ts` | `normalizeCustomFieldValues` (write side), `normalizeCustomFieldResponse` (strips `cf_`/`cf:` prefixes for detail responses) |
| `packages/shared/src/lib/crud/exporters.ts` | list export serialization (csv/json/xml/markdown) |
| `packages/shared/src/lib/crud/cache.ts`, `cache-stats.ts` | CRUD list cache (opt-in via `ENABLE_CRUD_API_CACHE`), tag-based invalidation |
| `packages/shared/src/lib/openapi/{types,crud,generator,sanitize}.ts` | `OpenApiRouteDoc` types, `createCrudOpenApiFactory`, `buildOpenApiDocument` (OpenAPI 3.1.0), sanitizer |
| `apps/mercato/src/app/api/docs/openapi/route.ts`, `.../docs/markdown/route.ts` | `/api/docs/openapi` and `/api/docs/markdown` endpoints |
| `packages/shared/src/lib/auth/server.ts` | `getAuthFromRequest` / `resolveAuthFromRequestDetailed`: Bearer JWT, `auth_token` cookie, `x-api-key` header, `Authorization: ApiKey <key>` |
| `packages/shared/src/lib/commands/operationMetadata.ts` | `x-om-operation` response header payload (`omop:<urlencoded JSON>`) |
| `packages/shared/src/lib/crud/optimistic-lock-headers.ts` | `x-om-ext-optimistic-lock-expected-updated-at` header, 409 conflict body |
| `packages/core/src/modules/currencies/api/currencies/route.ts` | Real mixed route (custom GET + factory POST/PUT/DELETE) |
| `apps/mercato/src/modules/example/api/todos/route.ts` | Canonical full `makeCrudRoute` example (query engine list, cf fields, CSV export) |
| `packages/core/src/modules/auth/api/login.ts` | Canonical hand-written route (form parsing, rate limiting, cookies, custom-route interceptors) |
| `.ai/docs/module-development.md` | Documented conventions incl. `api/<method>/<path>.ts → /api/<path>` and `api/interceptors.ts` / `data/enrichers.ts` discovery |

## How it works

### 1. Route file conventions and codegen

The CLI generator (`processApiRoutes` in `packages/cli/src/lib/generators/module-registry.ts`) scans each module's `api/` folder in both app (`src/modules/<mod>/api`) and package roots. Three variants are supported; **all produce URL paths prefixed with the module id**: `/<moduleId>/<segments>` served under `/api`:

1. **Route-file aggregation** (modern, dominant): `api/**/route.ts` exporting HTTP method functions `GET/POST/PUT/PATCH/DELETE`, plus `metadata` and `openApi`. Example: `modules/sales/api/orders/route.ts` → `/api/sales/orders`. Manifest entry `kind: 'route-file'`.
2. **Plain single files**: `api/login.ts` (not named `route.*`) with method exports → `/api/auth/login`. Also `kind: 'route-file'`.
3. **Legacy per-method dirs**: `api/<method>/<path>.ts` (e.g. `modules/api_docs/api/get/version.ts` → `GET /api/api_docs/version`). Handler is `default ?? mod[METHOD] ?? mod.handler`; `kind: 'legacy'`. Legacy metadata is auto-wrapped as `{ [METHOD]: metadata }` at dispatch time (`normalizeLoadedMetadata`).

Codegen details a port must mirror:
- `metadata.path` (string export) **overrides** the derived path (`resolveApiPathFromMetadata`).
- Only actually-exported methods land in the manifest `methods: HttpMethod[]`; a request for a non-exported method 404s.
- If a route file exports handlers but no `metadata`, codegen warns: auth then **defaults to required**.
- `openApi` export is attached to the manifest as `docs` for the docs generator.
- Dynamic segments use Next-style folder/file names: `[id]` (single param), `[...slug]` (catch-all, ≥1 seg), `[[...slug]]` (optional catch-all). `matchRoutePattern` in `registry.ts` implements matching; literal segments compare **case-insensitively**; params are returned as `string` or `string[]`.
- Routes are matched in **specificity order** (`sortRoutesBySpecificity`): literal (0) < `[param]` (1) < catch-all (2) compared segment-by-segment.

### 2. Runtime dispatch pipeline (the catch-all)

`apps/mercato/src/app/api/[...slug]/route.ts` handles every method identically via `handleRequest(method, req, params)`:

1. `bootstrap()` + `registerApiRouteManifests(apiRoutes)` at module load.
2. Compute `pathname = '/' + slug.join('/')` (no `/api` prefix). Emit lifecycle event `requestReceived` (best-effort, never throws).
3. `findApiRouteManifestMatch(manifests, method, pathname)` → miss ⇒ `404 {"error":"Not Found"}` (i18n key `api.errors.notFound`).
4. Dynamic-import route module (`match.route.load()`); resolve handler (`mod[METHOD]` for route-file; `default ?? mod[METHOD] ?? mod.handler` for legacy). Missing handler ⇒ same 404.
5. `resolveAuthFromRequestDetailed(req)`: order is (a) trusted pre-resolved context, (b) `Authorization: Bearer <jwt>`, (c) `auth_token` cookie JWT, (d) API key from `x-api-key` header or `Authorization: ApiKey <key>`. Customer-type JWTs are rejected for staff APIs. Super-admin scope cookies (`tenant`/`organization` selection) are applied on top. Status is `authenticated | missing | invalid`.
6. `extractMethodMetadata(metadata, method)` — per-method entry preferred, falling back to the flat object; recognizes `requireAuth: boolean`, `requireFeatures: string[]`, deprecated `requireRoles: string[]`, `rateLimit: { points, duration, blockDuration?, keyPrefix? }`.
7. `checkAuthorization(methodMetadata, auth, req)`:
   - `requiresAuthentication = metadata?.requireAuth !== false` → **auth is required by default**. Unauthenticated ⇒ `401 {"error":"Unauthorized"}`.
   - **Tenant parameter pollution guard** (issue #2665): every distinct `tenantId` candidate — all repeated `?tenantId=` query params plus a body-level `tenantId` (JSON / urlencoded / multipart) — is validated via `enforceTenantSelection`; a candidate the actor may not select ⇒ the `CrudHttpError`'s status/body (typically 403).
   - `requireFeatures`: resolved via `rbacService.userHasAllFeatures(auth.sub, features, {tenantId, organizationId})` in the request's resolved org scope. Failure ⇒ `403 {"error":"Forbidden","requiredFeatures":[...]}` (plus a detailed console warn).
   - `requireRoles` is **ignored at runtime** (deprecated as spoofable) — only a one-time console warning.
8. `rateLimit` metadata: `checkRateLimit(service, cfg, clientIp, msg)` keyed by client IP (honoring `trustProxyDepth`) ⇒ on limit `429` with `Retry-After` and `X-RateLimit-*` headers.
9. Handler invoked as `handler(req, { params, auth })` inside `runWithCacheTenant(auth?.tenantId ?? null, ...)`.
10. If auth resolution status was `invalid` and the response is 401, `auth_token` and `session_token` cookies are cleared on the response.
11. Lifecycle events `requestAuthResolved`, `requestAuthorizationDenied`, `requestRateLimited`, `requestCompleted`, `requestFailed` are emitted with `{requestId, method, pathname, status, durationMs, userId, tenantId}`.

Note the double auth check: the dispatcher enforces `metadata`, and `makeCrudRoute` handlers *also* independently resolve auth and return 401 — handlers stay safe if mounted outside the dispatcher.

### 3. `makeCrudRoute` — the CRUD factory

Signature: `makeCrudRoute<TCreate, TUpdate, TList>(opts: CrudFactoryOptions) → { metadata, GET, POST, PUT, DELETE }` (each handler `(request: Request) => Promise<Response>`).

`CrudFactoryOptions`:
- `metadata?: CrudMetadata` — per-method `{ requireAuth?, requireFeatures?, rateLimit? }`; returned untouched for codegen/dispatcher.
- `orm: { entity, idField='id', orgField='organizationId'|null, tenantField='tenantId'|null, softDeleteField='deletedAt'|null }` — pass `null` to disable a scope dimension.
- `list?: ListConfig` — `schema` (Zod for query params), optional query-engine config (`entityId` + `fields` + `sortFieldMap` + `customFieldSources` + `joins`), `buildFilters(query, ctx) → Where`, `transformItem`, CSV/export config, `decorateCustomFields`, `omitAutomaticTenantOrgScope`.
- `create?/update?/del?` — direct-ORM configs (`schema`, `mapToEntity`/`applyToEntity`, `customFields`, `response`), OR
- `actions?: { create/update/delete: { commandId, schema?, mapInput?, metadata?, response?, status? } }` — command-bus path (preferred in newer modules; the command handles persistence, events, indexing).
- `events?: { module, entity, persistent? }` — derives lifecycle event ids `<module>.<entity>.{creating,created,updating,updated,deleting,deleted}` and the cache/audit `resourceKind` (`module.entity`).
- `indexer?: { entityType }` — marks entity changes for the query-index; flushed via `dataEngine.markOrmEntityChange(...)` + `flushOrmEntityChanges()` after mutations.
- `hooks?: { beforeList, afterList, beforeCreate, afterCreate, beforeUpdate, afterUpdate, beforeDelete, afterDelete }`.
- `enrichers?: { entityId }` — activates response enrichers targeting that entity id.
- `resolveIdentifiers?` — customizes `{id, organizationId, tenantId}` extraction from the entity for events/cache tags.

**Request context (`withCtx`)**: creates a request-scoped Awilix container, resolves auth (`getAuthFromRequest`; **auth with a non-UUID `tenantId` is treated as unauthenticated**), then resolves the organization scope (`resolveOrganizationScopeForRequest`) into `ctx = { container, auth, organizationScope, selectedOrganizationId, organizationIds, request }`. `organizationIds` semantics: `null` = unrestricted (super admin), `[]` = no visible orgs (fail closed), `[ids...]` = restrict to these.

**GET (list)** — pipeline order (must be preserved):
1. `401 {"error":"Unauthorized"}` if no auth; `501 {"error":"Not implemented"}` if `opts.list` missing.
2. Parse all query params (`Object.fromEntries(url.searchParams)`) through `list.schema` (Zod). Failure throws `ZodError` → `400 {"error":"Invalid input","details":[...zod issues...]}`.
3. Run **before-interceptors** (may mutate query → re-parse through schema; may block with their own status/body).
4. `parseIdsParam(query.ids)` — comma-separated UUID list, trimmed, deduped, non-UUIDs dropped, capped at 200.
5. `hooks.beforeList`.
6. Export detection: `?format=csv|json|xml|markdown` (must be enabled via `list.allowCsv`/`list.export`); `exportScope=full` or `full=true` for raw-record export.
7. Pagination: `page = Number(query.page ?? 1) || 1`; `pageSize = clamp(Number(query.pageSize ?? 50) || 50, 1, 100)` (exports use a batch size clamped to 100..10000, default 1000).
8. CRUD cache lookup (only when `ENABLE_CRUD_API_CACHE` is truthy and not exporting). Cache key partitions on resource, pathname, tenant, selected org, org scope set, canonicalized query string, and active-enricher signature.
9. Sorting: `resolveSortParams` — field from `sortField ?? sort ?? 'id'`, dir from `sortDir ?? order ?? 'asc'` (`desc` case-insensitive); field mapped through `list.sortFieldMap`, then `cf_x` → `cf:x`.
10. Filters: `buildFilters(validated, ctx)` merged with `mergeAdvancedFilters(filters, query)` (parses `filter[...]` advanced-filter params; `$or` combination is a cross-product AND-merge), then `mergeIdFilter(filters, parsedIds)` — if the route already narrowed `id`, the user `ids` are **intersected**, never widened; unrecognized existing `id` filter shapes fail closed (kept as-is).
11. `withDeleted = parseBooleanToken(query.withDeleted) === true`.
12. **Empty org scope short-circuit**: if `orgField` is scoped and `ctx.organizationIds` is `[]` (and `omitAutomaticTenantOrgScope` is not set) ⇒ log "Forbidden request", return **200** `{items:[],total:0,page,pageSize,totalPages:0}` (afterList hook + interceptors still run).
13. **Query-engine path** (when `list.entityId && list.fields`): `queryEngine.query(entityId, { fields, includeCustomFields:true, sort, page, filters, withDeleted, tenantId, organizationId, organizationIds })` → `transformItem` per row → `decorateItemsWithCustomFields` (adds `customValues`/`customFields`, see §6) → translation overlay (per `Accept-Language`/locale plugin) → access logging (`logCrudAccess`) → export branch or JSON envelope.
14. **ORM fallback path** (no `entityId`/`fields`): `em.getRepository(entity).find(buildScopedWhere(filters, scope))` — **no pagination, no sorting**; envelope is only `{items, total: items.length}` (no `page/pageSize/totalPages`).
15. Envelope (query-engine path): `{ items, total, page, pageSize, totalPages: ceil(total/pageSize), meta? }` where `meta` passes through query-engine metadata (e.g. `partialIndexWarning`).
16. `hooks.afterList(payload, ctx)` → **after-interceptors** (may merge/replace body or fail the response) → enrichers (`payload.items` replaced; `payload._meta = {enrichedBy:[...], enricherErrors?}` added when non-empty) → cache store → respond.
17. Response headers: `x-om-cache: hit|miss` (only when cache enabled), `x-om-partial-index: <json>` when the query engine reports a partial index.
18. Export responses: body is the serialized document, `content-type` per format (`text/csv`, `application/json`, `application/xml`, `text/markdown`), `content-disposition: attachment; filename="<name>.<ext>"`; export loops extra pages until `total` collected.

**POST (create)**:
- `501` if neither `opts.create` nor `opts.actions.create`; `401` unauthenticated; `403 {"error":"Forbidden"}` if org-scoped and `organizationIds === []`.
- Body: `await request.json().catch(() => ({}))` — malformed JSON is treated as `{}` (then usually fails schema validation → 400).
- **Command path** (`actions.create`): parse body with `action.schema` → before-interceptors (body may be replaced, then re-parsed) → `mapInput` → sync "creating" subscribers (may block ⇒ default `422 {"error":"Operation blocked"}` or modify input) → `commandBus.execute(commandId, {input, ctx, metadata})` → sync "created" subscribers → `action.response({result, logEntry, ctx})` → after-interceptors → respond with `action.status ?? 201` plus header `x-om-operation: omop:<urlencodedJSON {id,undoToken,commandId,actionLabel,resourceKind,resourceId,executedAt}>` when the command produced an undo-able log entry.
- **Direct path** (`create`): schema parse → before-interceptors (+re-parse) → sync "creating" → `hooks.beforeCreate` (may replace input) → **mutation guards** (registered globally; may block ⇒ `422 {"error":"Operation blocked by guard"}` or custom status/body, may modify payload; includes the opt-in optimistic-lock guard) → `mapToEntity` → inject `organizationId` (else `400 {"error":"Organization context is required"}`) and `tenantId` (else `400 {"error":"Tenant context is required"}`) → inside `em.transactional`: `dataEngine.createOrmEntity` + custom-field write (`setCustomFields`) when `create.customFields.enabled` (values from `map(body)` or `pickPrefixed` ⇒ `cf_*`/`cf:*`/`customFields`/`customValues` keys of the RAW body) → `hooks.afterCreate` → guard afterSuccess callbacks → sync "created" → `dataEngine.markOrmEntityChange({action:'created',...}) + flushOrmEntityChanges()` (emits CRUD events + indexing) → CRUD cache invalidation by tags → response `create.response(entity) ?? {id: <newId>}` → after-interceptors → enrich single record → **201**.

**PUT (update)**: same shell; direct path additionally: `id` from `update.getId(input) ?? input.id`, must be a UUID else `400 {"error":"Invalid id"}`; entity located via `buildScopedWhere({[idField]: id}, {org, tenant, softDelete})` — cross-tenant/org rows or soft-deleted rows are invisible ⇒ `404 {"error":"Not found"}`; default success body `{"success":true}` with **200**; command path default status 200.

**DELETE**: `id` from query param `?id=` (default) or JSON body when `del.idFrom === 'body'`; non-UUID ⇒ `400 {"error":"ID is required"}`; `del.softDelete !== false` ⇒ soft delete (sets `deletedAt`), else hard delete; scoped lookup miss ⇒ `404 {"error":"Not found"}`; default body `{"success":true}`, **200**. Command path parses `{body, query}` as the raw input shape.

**Error handling** (`handleError`, wraps all handlers):
- thrown `Response` → returned as-is;
- `CrudHttpError` → `err.status` + `err.body`;
- `ZodError` → `400 {"error":"Invalid input","details": issues}`;
- anything else → logged and `500 {"error":"Internal server error","message":"Something went wrong. Please try again later."}` (no stack leakage).

### 4. Validation

Route input validation is Zod throughout (`data/validators.ts` per module + inline schemas in routes). Contracts:
- CRUD list query and body schemas are the factory's `list.schema` / `create.schema` / `update.schema` / `actions.*.schema`.
- Zod failure inside the factory ⇒ `400 {"error":"Invalid input","details":[{code,path,message,...}]}` — `details` is the raw `zodError.issues` array.
- Hand-written routes commonly use `safeParse` and craft their own 400 bodies (e.g. login returns `{ok:false,error:"Invalid credentials"}`), so validation envelopes are per-route on hand-written endpoints; a port must match each route individually.
- A port may use a native validation library **as long as** the 400 envelope reproduces `{error:"Invalid input", details:[...]}` with Zod-issue-like objects (`path` array, `message`, `code`) for factory routes.

### 5. `openApi` export and `/api/docs`

Every route file exports `openApi: OpenApiRouteDoc` (`packages/shared/src/lib/openapi/types.ts`):

```ts
type OpenApiRouteDoc = {
  tag?: string; summary?: string; description?: string
  pathParams?: ZodTypeAny
  methods: Partial<Record<'GET'|'POST'|'PUT'|'PATCH'|'DELETE', OpenApiMethodDoc>>
}
type OpenApiMethodDoc = {
  operationId?, summary?, description?, tags?,
  query?: ZodTypeAny, headers?: ZodTypeAny, pathParams?: ZodTypeAny,
  requestBody?: { contentType?, schema: ZodTypeAny, example?, description? },
  responses?: OpenApiResponseDoc[], errors?: OpenApiResponseDoc[],
  deprecated?, security?: ['bearerAuth'], codeSamples?, externalDocs?, extensions?
}
```

CRUD routes use `createCrudOpenApiFactory(config)(options)` (`packages/shared/src/lib/openapi/crud.ts`), which builds GET/POST/PUT/DELETE method docs from `querySchema` (auto-extended with the `ids` param description), `listResponseSchema`, and create/update/del schemas. `createPagedListResponseSchema(itemSchema)` = `{ items: item[], total: number, page: number, pageSize: number, totalPages: number }`. Defaults: create response `{id: uuid|null}` (201), update/delete `{ok: true}` (200).

`GET /api/docs/openapi` (`apps/mercato/src/app/api/docs/openapi/route.ts`) calls `buildOpenApiDocument(modules, {...})` then `sanitizeOpenApiDocument` and returns OpenAPI **3.1.0** JSON. Generator behavior (`packages/shared/src/lib/openapi/generator.ts`):
- one path item per manifest path; methods limited to actually-exported handlers;
- `operationId` derived from module id + path + method unless provided;
- route `metadata` feeds the doc: `requireAuth` ⇒ auto `401` error response + `bearerAuth` security + `x-require-auth: true`; `requireFeatures` ⇒ auto `403` error + description line `Requires features: ...` + `x-require-features`;
- missing 2xx response ⇒ fallback `201` for POST, `204` for DELETE, `200` otherwise;
- Zod schemas converted to JSON Schema; examples auto-generated; a cURL sample is injected as `x-codeSamples`;
- module `info.title` becomes the default tag. `GET /api/docs/markdown` renders the same document as Markdown.

### 6. Custom fields in requests/responses

- **Input**: `splitCustomFieldPayload(body)` collects custom values from body keys `cf_<key>`, `cf:<key>`, a `customFields` array (`[{key, value}]`) or object, and a `customValues` object. `extractCustomFieldValuesFromPayload` = its `.custom` part. The factory writes them via `dataEngine.setCustomFields({entityId, recordId, organizationId, tenantId, values})` inside the create/update transaction when `customFields: { enabled: true, entityId, pickPrefixed?: true, map? }` is configured.
- **List filtering**: query params `cf_<key>=<v>` (equality, type-coerced by field kind: integer/float/boolean/date) and `cf_<key>In=a,b,c` (`$in`) via `buildCustomFieldFiltersFromQuery`, producing `Where` keys `cf:<key>`.
- **Sorting**: `sortField=cf_<key>` normalizes to selector `cf:<key>`.
- **Output**: query-engine rows carry raw `cf:<key>`/`cf_<key>` keys. `decorateRecordWithCustomFields(record, definitionIndex, {organizationId, tenantId})` resolves the active definition per key (org+tenant > org > tenant > global; inactive/tombstoned skipped) and produces `{ customValues: {key: value}|null, customFields: [{key,label,value,kind,multi}] }` ordered by definition priority asc, then updatedAt desc, then key. `applyCustomFieldsNormalization(record, decorated, {stripPrefixedKeys})` merges those two keys into the item; with `stripPrefixedKeys: true` (opt-in per route, issue #1769) the raw `cf_*`/`cf:*` keys are removed — default `false`, so legacy items carry **both** the raw `cf_*` keys and `customValues`/`customFields`.
- **Detail endpoints**: `normalizeCustomFieldResponse(values)` (`packages/shared/src/lib/custom-fields/normalize.ts`) strips `cf_`/`cf:` prefixes and drops `undefined`, returning a plain `{key: value}` map or `undefined` when empty. Multi-value fields serialize as arrays; Postgres `{a,b}` array literals are parsed to `string[]` by `normalizeCustomFieldValue`.

### 7. API interceptors (`api/interceptors.ts`)

Modules export `interceptors: ApiInterceptor[]`; codegen registers them (`registerApiInterceptors`) with module order. Contract (`packages/shared/src/lib/crud/api-interceptor.ts`):

```ts
type ApiInterceptor = {
  id: string                      // '<module>.<name>'
  targetRoute: string             // exact 'sales/orders', prefix 'sales/*', or '*'
  methods: ('GET'|'POST'|'PUT'|'PATCH'|'DELETE')[]
  priority?: number               // higher runs first; ties broken by module order
  features?: string[]             // ACL gate; skipped when the user lacks them
  timeoutMs?: number              // default 5000
  before?: (request, context) => Promise<InterceptorBeforeResult>
  after?: (request, response, context) => Promise<InterceptorAfterResult>
}
```

Runner semantics (`interceptor-runner.ts`): `routePath` is the pathname **without** the `/api/` prefix. `before` may return `{ok:false, statusCode?, message?}` ⇒ request blocked with `statusCode ?? 400` and body `{error: message ?? 'Request blocked by API interceptor'}` (+ `interceptorId` outside production); or `{ok:true, body?, query?, headers?, metadata?}` — replacements are chained interceptor-to-interceptor and the CRUD factory **re-parses** the modified body/query through the route schema. Timeout ⇒ `504 {"error":"Interceptor timeout"}`; thrown error ⇒ `500 {"error":"Internal interceptor error"}`. `after` may `{merge}` (shallow-merge into body) or `{replace}` the body; failures likewise map to 504/500. `metadata` returned by `before` is passed to the same interceptor's `after` via `metadataByInterceptor`. Hand-written routes can opt in via `runCustomRouteAfterInterceptors` (see login route).

### 8. Response enrichers (`data/enrichers.ts`)

Modules export `enrichers: ResponseEnricher[]` targeting another module's entity (`targetEntity: 'customers.person'`). A CRUD route activates them with `enrichers: { entityId }`. Runner (`enricher-runner.ts`): enrichers filtered by ACL `features` (supports `*` and `prefix.*` grants) and `disabledTenantIds`, run in registry order; list endpoints require `enrichMany` (N+1 prevention), single-record endpoints use `enrichOne`. Timeout default 2000 ms; on failure/timeout non-critical enrichers are skipped (optional `fallback` object merged into each record); `critical: true` propagates the error (→ 500). Output must be additive and namespaced `_<module>`. Result adds `payload._meta = { enrichedBy: [ids], enricherErrors?: [ids] }` (only when non-empty). Enrichers also run on POST/PUT single-record responses (`enrichSingleRecord`). Optional read-through cache per enricher (`cache: {strategy:'read-through', ttl}` — key `umes:enricher:<id>:tenant:<t>:org:<o>:mode:<one|many>:ids:<json>`), and `cacheableOnListHit: true` allows embedding output in the CRUD list cache (cache key gains an `enrichers:` signature segment).

## Public contracts

### Error envelopes (exact)

| Status | Body | Source |
|---|---|---|
| 400 | `{"error":"Invalid input","details":[...zod issues]}` | factory Zod failure |
| 400 | `{"error":"Invalid id"}` / `{"error":"ID is required"}` | PUT/DELETE non-UUID id |
| 400 | `{"error":"Organization context is required"}` / `{"error":"Tenant context is required"}` | create/update without scope |
| 401 | `{"error":"Unauthorized"}` | dispatcher + factory |
| 403 | `{"error":"Forbidden","requiredFeatures":["..."]}` | dispatcher feature check |
| 403 | `{"error":"Forbidden"}` | factory mutation with empty org scope |
| 404 | `{"error":"Not Found"}` | dispatcher (no route/handler) |
| 404 | `{"error":"Not found"}` | factory (scoped lookup miss) — note lowercase `f` |
| 409 | `{"error":"record_modified","code":"optimistic_lock_conflict","currentUpdatedAt":"...","expectedUpdatedAt":"..."}` | optimistic-lock guard |
| 422 | `{"error":"Operation blocked"}` / `{"error":"Operation blocked by guard"}` (or guard-provided body/status) | sync subscribers / mutation guards |
| 429 | `{"error":"<i18n rate limit message>"}` + `Retry-After`, `X-RateLimit-*` headers | rate limiter |
| 500 | `{"error":"Internal server error","message":"Something went wrong. Please try again later."}` | factory catch-all |
| 500/504 | `{"error":"Internal interceptor error"}` / `{"error":"Interceptor timeout"}` (+`interceptorId`,`message` in non-prod) | interceptor runner |
| 501 | `{"error":"Not implemented"}` | factory method not configured |

### List response envelope

Query-engine path (canonical):

```json
{
  "items": [ { "id": "…", "…": "…", "cf_priority": 3, "customValues": {"priority": 3}, "customFields": [{"key":"priority","label":"Priority","value":3,"kind":"integer","multi":false}] } ],
  "total": 123,
  "page": 1,
  "pageSize": 50,
  "totalPages": 3,
  "meta": { "partialIndexWarning": { "entity": "…", "baseCount": 10, "indexedCount": 8, "scope": "scoped" } },
  "_meta": { "enrichedBy": ["module.enricher"], "enricherErrors": ["…"] }
}
```

`meta` and `_meta` appear only when applicable. ORM-fallback routes return only `{"items":[...],"total":N}`.

### Standard list query parameters (factory-level)

| Param | Meaning |
|---|---|
| `page` (default 1), `pageSize` (default 50, clamped 1–100) | pagination |
| `sortField` (alias `sort`, default `'id'`), `sortDir` (alias `order`, `asc`/`desc`, default `asc`) | sorting; `cf_<key>` sorts on custom field |
| `ids` | comma-separated UUIDs, max 200; intersected with route filters |
| `withDeleted` | boolean token; include soft-deleted |
| `format` | `csv`/`json`/`xml`/`markdown` export (if enabled); + `exportScope=full` or `full=1` |
| `filter[...]` | advanced-filter tree (v2) or flat (v1) params, AND/OR-merged into route filters |
| `cf_<key>`, `cf_<key>In` | custom-field equality / IN filters (route must wire `buildCustomFieldFiltersFromQuery`) |
| `tenantId` | super-admin tenant selection — validated by the dispatcher pollution guard |

### Response headers

| Header | When | Format |
|---|---|---|
| `x-om-cache` | GET list with `ENABLE_CRUD_API_CACHE` | `hit` \| `miss` |
| `x-om-partial-index` | query-engine partial index | JSON `{type:'partial_index',entity,entityLabel,baseCount,indexedCount,scope}` |
| `x-om-operation` | command-path mutations with undo | `omop:` + `encodeURIComponent(JSON.stringify({id,undoToken,commandId,actionLabel,resourceKind,resourceId,executedAt}))` |
| `content-disposition` | exports | `attachment; filename="todos.csv"` |
| `Retry-After`, `X-RateLimit-*` | 429 only | seconds / limiter stats |

### Request headers understood

`Authorization: Bearer <jwt>`, `Authorization: ApiKey <key>`, `x-api-key: <key>`, `cookie: auth_token=…; session_token=…`, `x-request-id` (propagated to lifecycle events), `x-om-ext-optimistic-lock-expected-updated-at: <ISO date>` (opt-in optimistic locking, module id `optimistic_lock`, env `OM_OPTIMISTIC_LOCK`), generic UMES extension headers `x-om-ext-<module>-<key>`.

### Concrete example 1 — CRUD route: `example` todos (`apps/mercato/src/modules/example/api/todos/route.ts`)

Registered path: `/api/example/todos`. Metadata:
```ts
GET:    { requireAuth: true, requireFeatures: ['example.todos.view'] }
POST/PUT/DELETE: { requireAuth: true, requireFeatures: ['example.todos.manage'] }
```
- **GET /api/example/todos** — query schema: `id?` (uuid), `page` (default 1), `pageSize` (1–100, default 50), `ids?`, `sortField` (default `id`), `sortDir` (`asc|desc`), `title?` (ILIKE contains), `isDone?` (bool), `withDeleted?`, `organizationId?`, `createdFrom?/createdTo?` (date range), `format?` (`json|csv`), plus dynamic `cf_*` filters (passthrough schema). Uses query engine on entity id `example:todo` with fields `[id,title,tenant_id,organization_id,is_done,created_at, cf:*…]`; `beforeList` hook re-discovers custom-field keys per request from `custom_field_defs` + `custom_field_values`. Response `200`: `{items:[{id,title,tenant_id,organization_id,is_done, cf_<key>…}], total, page, pageSize, totalPages}`. `?format=csv` streams CSV with headers `id,title,is_done,organization_id,tenant_id,cf_<key>…` as `todos.csv`.
- **POST /api/example/todos** — body: loose object (base fields + `cf_*` keys); command `example.todos.create`; response `201 {"id":"<uuid>"}` (+ `x-om-operation`).
- **PUT /api/example/todos** — body must include `id`; command `example.todos.update`; response `200 {"ok":true}`.
- **DELETE /api/example/todos** — command path parses `{body, query}`; response `200 {"ok":true}`.
- `openApi` built with `createExampleCrudOpenApi` (a `createCrudOpenApiFactory` instance) documenting exactly the above.

A production variant with the same skeleton but custom GET: `packages/core/src/modules/currencies/api/currencies/route.ts` (`/api/currencies/currencies`) — hand-written paginated GET (`em.findAndCount`, envelope `{items,total,page,pageSize,totalPages}`; note `totalPages = max(1, ceil(total/pageSize))` and **401/400 responses carry an empty list envelope body** with the error status), factory POST/PUT/DELETE via commands `currencies.currencies.{create,update,delete}` responding `201 {"id":…}` / `200 {"ok":true}` / `200 {"ok":true}` (DELETE id from `?id=`).

### Concrete example 2 — hand-written route: `POST /api/auth/login` (`packages/core/src/modules/auth/api/login.ts`)

- `export const metadata = { requireAuth: false }` — public.
- Request: `application/x-www-form-urlencoded` (or multipart form) with `email`, `password`, `remember?` (`on|1|true`), `tenantId?` (alias `tenant`), `requireRole?` (comma list, alias `role`), `redirect?`.
- Pipeline: two-layer rate limit **before validation** (per-IP: 20/60s; per-`login:{email}` compound: 5/60s, block 60s; both configurable via env `LOGIN`/`LOGIN_IP` endpoint config) → Zod `userLoginSchema.pick({email,password,tenantId})` (failure ⇒ `400 {"ok":false,"error":"Invalid credentials"}`) → user lookup (ambiguous multi-tenant email deliberately treated as no user; constant-time bcrypt verify always runs) → failure ⇒ `401 {"ok":false,"error":"Invalid email or password"}` → optional role requirement ⇒ `403 {"ok":false,"error":"Not authorized for this area"}` → rate-limit counter reset on success → session row + JWT (`{sub, sid, tenantId, orgId, email, roles}`).
- Success `200`: `{"ok":true,"token":"<jwt>","redirect":"/backend","refreshToken":"<token>"?}` (refreshToken only when `remember`). Passed through `runCustomRouteAfterInterceptors({routePath:'auth/login', method:'POST', ...})` — interceptors may replace body/status.
- Cookies set on the response: `auth_token` (httpOnly, path=/, sameSite=lax, secure in production, maxAge 8h) and `session_token` (httpOnly; `remember` ⇒ expires in `REMEMBER_ME_DAYS` (default 30) days, else maxAge 8h).
- `429` error body from `rateLimitErrorSchema` with `Retry-After` header.
- `openApi` documents the form request, `200` success schema, and errors 400/401/403/429.

## Helpers to mirror

| Helper (file) | Signature / behavior |
|---|---|
| `makeCrudRoute` (`shared/src/lib/crud/factory.ts`) | `(opts: CrudFactoryOptions) → {metadata, GET, POST, PUT, DELETE}` — the whole CRUD pipeline above |
| `buildScopedWhere` (`shared/src/lib/api/crud.ts`) | `(base, {organizationId?, organizationIds?, tenantId?, orgField?, tenantField?, softDeleteField?}) → where` — `organizationIds` wins over `organizationId`; `[]` ⇒ `{$in: []}` (matches nothing); one id ⇒ scalar; soft-delete field forced to `null` |
| `parseIdsParam` / `mergeIdFilter` (`crud/ids.ts`) | `(raw, max=200) → string[]` UUID-only dedupe; merge = install `$in` when no id filter, intersect when a recognized narrowing exists, keep existing when unrecognized |
| `mergeAdvancedFilters` / `mergeAdvancedFiltersFromQuery` (`crud/advanced-filter-integration.ts`) | `filter[...]` query params → Where; `$or` cross-product merge |
| `resolveSortParams` + `normalizeSortFieldSelector` (factory) | `sortField ?? sort ?? 'id'`, `sortDir ?? order`, `cf_x → cf:x` |
| `CrudHttpError` + `badRequest/forbidden/notFound/conflict/assertFound/isUniqueViolation` (`crud/errors.ts`) | typed HTTP errors thrown anywhere inside handlers/hooks/commands; `isUniqueViolation(err, constraint?)` maps PG 23505 → 409 |
| `handleError` (factory, private) | error → Response mapping incl. Zod → 400 envelope |
| `splitCustomFieldPayload` / `extractCustomFieldValuesFromPayload` (`crud/custom-fields.ts`) | body → `{base, custom}`; custom from `cf_*`, `cf:*`, `customFields[]`, `customValues{}` |
| `decorateRecordWithCustomFields` / `applyCustomFieldsNormalization` / `loadCustomFieldDefinitionIndex` (`crud/custom-fields.ts`) | list-item decoration → `customValues` + ordered `customFields` |
| `buildCustomFieldFiltersFromQuery` (`crud/custom-fields.ts`) | `cf_<key>` / `cf_<key>In` query params → `{'cf:<key>': coerced}` filters |
| `normalizeCustomFieldResponse` / `normalizeCustomFieldValues` (`custom-fields/normalize.ts`) | strip prefixes for detail responses / normalize primitives for writes |
| `runApiInterceptorsBefore/After` + `getApiInterceptorsForRoute` (`crud/interceptor-runner.ts`, `interceptor-registry.ts`) | route match (`*`, `prefix/*`, exact), priority desc ordering, 5s timeout, block/merge/replace semantics |
| `applyResponseEnrichers` / `applyResponseEnricherToRecord` / `resolveListCacheEnricherPlan` (`crud/enricher-runner.ts`) | enrichment with ACL gating, 2s timeout, fallback, `_meta` |
| `matchRoutePattern` / `sortRoutesBySpecificity` / `findApiRouteManifestMatch` (`shared/src/modules/registry.ts`) | path matching & precedence |
| `checkAuthorization` + `extractTenantCandidates` (`apps/mercato/src/app/api/[...slug]/route.ts`) | the guard pipeline incl. tenant-pollution defense |
| `getAuthFromRequest` / `resolveAuthFromRequestDetailed` (`shared/src/lib/auth/server.ts`) | Bearer → cookie → API key resolution, customer-token rejection, super-admin scope cookies |
| `logCrudAccess` (factory) | read-access audit writes (batch, non-blocking unless `OM_CRUD_ACCESS_LOG_BLOCKING=1`) |
| `serializeOperationMetadata` (`shared/src/lib/commands/operationMetadata.ts`) | `omop:` + urlencoded JSON for `x-om-operation` |
| `createPagedListResponseSchema` / `createCrudOpenApiFactory` (`shared/src/lib/openapi/crud.ts`) | doc schema factories |
| `buildOpenApiDocument` / `sanitizeOpenApiDocument` / `generateMarkdownFromOpenApi` (`shared/src/lib/openapi/generator.ts`) | `/api/docs` generation |
| `checkRateLimit` / `getClientIp` (`shared/src/lib/ratelimit/helpers.ts`) | 429 + `Retry-After` / `X-RateLimit-*` |
| `escapeLikePattern` (`shared/src/lib/db/escapeLikePattern.ts`) | escape `%`/`_` before `$ilike '%…%'` searches |
| `parseBooleanToken` (`shared/src/lib/boolean.ts`) | boolean query-token parsing used for `withDeleted`, `full`, etc. |

## Behavioral details a port MUST replicate

1. **Auth is required by default**: `metadata?.requireAuth !== false` — absence of metadata means 401 for anonymous callers. `requireRoles` must be accepted but ignored (warn once).
2. **URL shape**: `/api/<moduleId>/<segments...>`; `metadata.path` overrides; only exported methods routable; literal segments case-insensitive; specificity ordering literal > `[param]` > `[...catchAll]`.
3. **Exact status codes and envelopes** from the table in Public contracts, including the casing difference `"Not Found"` (dispatcher) vs `"Not found"` (factory) and the create default `{"id": "..."}` (201) vs update/delete default `{"success": true}` (200) vs command-route conventions `{"ok": true}`.
4. **Pagination defaults & clamping**: page ≥ 1 default 1; pageSize default 50, clamped to [1, 100] even if the route schema allows more; `totalPages = ceil(total/pageSize)` (0 for empty-scope short-circuit; currencies-style hand-written GET uses `max(1, …)` — per-route).
5. **Sorting defaults**: `id asc`; `sort`/`order` accepted as aliases; unknown fields pass through `sortFieldMap` unchanged (query engine decides); `cf_` prefix maps to `cf:` selector.
6. **`ids` param**: only UUIDs (v1–v8 pattern per `crud/ids.ts` regex), trim + dedupe, max 200, intersection with pre-existing id narrowing, fail-closed on unknown filter shapes.
7. **Scope semantics**: `organizationIds === []` ⇒ GET returns empty 200 list, POST/PUT/DELETE return 403; `null` ⇒ unrestricted; write scope guards (`buildScopedWhere`) apply even when `omitAutomaticTenantOrgScope` disables read scoping. Soft-deleted rows are invisible to update/delete (404), and to list unless `withDeleted=true`.
8. **Auth context hygiene**: JWTs with non-UUID `tenantId` are treated as unauthenticated by the factory; customer-type tokens rejected on staff APIs; invalid (expired/garbage) credentials on a 401 response cause `auth_token`/`session_token` cookies to be cleared.
9. **Tenant parameter pollution**: every distinct `tenantId` occurrence (all repeated query params + body) must be authorized before the handler runs.
10. **Malformed JSON bodies** on POST/PUT/DELETE become `{}` (then typically fail schema validation) — never a raw 500.
11. **Order of operations on mutations**: validate → before-interceptors (re-validate) → sync before-event (can block 422 / mutate) → beforeX hook → mutation guards (can block/mutate) → persist (+custom fields, same transaction) → afterX hook → guard afterSuccess → sync after-event → event emission + indexing flush → cache invalidation → response mapping → after-interceptors → single-record enrichment → status.
12. **Order of operations on list**: validate → before-interceptors → beforeList → cache check → query → transform → custom-field decoration → translation overlay → access log → afterList → after-interceptors → enrichers → cache store → respond. Enrichers run **after** afterList and after-interceptors, and their `_meta` must not be stripped.
13. **Exports** bypass the JSON envelope: streaming body + `content-disposition`; batch through all pages (batch size 100–10000, default 1000); `exportScope=full` exports raw records with `cf_` keys flattened (prefix removed) and `_meta`/`_*` keys stripped.
14. **Headers**: `x-om-cache` only when `ENABLE_CRUD_API_CACHE` is on; `x-om-operation` only when a command produced `{id, undoToken, commandId}`; `x-om-partial-index` mirrors query-engine meta.
15. **Interceptor determinism**: priority desc, then module registration order, then declaration order; per-interceptor timeout 5000 ms ⇒ 504; error ⇒ 500; `before` block default 400; modified bodies re-validated; `interceptorId`/`message` leak only outside production.
16. **Enricher determinism**: registry order after ACL/tenant filter; 2000 ms timeout; non-critical failures append to `_meta.enricherErrors` and apply `fallback`; `enrichMany` mandatory for lists; namespaced `_<module>` fields.
17. **OpenAPI**: 3.1.0; auto-401/403 error entries derived from metadata; `x-require-features` / `x-require-auth` vendor extensions; POST fallback 201 / DELETE fallback 204; a cURL `x-codeSamples` entry per operation.
18. **Access logging**: every successful read logs `{tenantId, organizationId, actorUserId, resourceKind, resourceId, accessType(read|read:item|read:list), fields, context}` per unique record id, asynchronously (blocking only when `OM_CRUD_ACCESS_LOG_BLOCKING=1`).
19. **CRUD list cache**: opt-in (`ENABLE_CRUD_API_CACHE`), tag-invalidated on create/update/delete (`invalidateCrudCache` with resource + record tags), key partitioned by tenant/selected-org/org-scope/query/enricher signature; cached payloads that lost their `items` array are evicted and treated as a miss.
20. **Optimistic locking** (opt-in via `OM_OPTIMISTIC_LOCK`): header `x-om-ext-optimistic-lock-expected-updated-at`; mismatch ⇒ 409 with the exact conflict body above.

## Gotchas

- **The ORM fallback list path is unpaginated and unsorted** — routes without `list.entityId`+`fields` return every matching row as `{items, total}`. Ports must not "fix" this silently; some UIs rely on the full list.
- The dispatcher and factory **both** enforce auth; the factory does NOT enforce `requireFeatures` (that is dispatcher-only). A port that mounts factory handlers directly must recreate the dispatcher's feature check.
- `metadata` for legacy (`api/<method>/…`) files is method-flat and only wrapped per-method at dispatch; route-file metadata is keyed by method but `extractMethodMetadata` falls back to reading flat keys off the top-level object, so `export const metadata = { requireAuth: false }` works for both kinds.
- `list.schema` usually ends with `.passthrough()`/`.loose()` — unknown query params (dynamic `cf_*`) must survive validation.
- On the command path (`actions.*`), the factory does **not** apply the empty-org-scope 403 differently, but it also does not inject org/tenant into the input — the command implementation is responsible for scoping; `mapInput` frequently injects `ctx.selectedOrganizationId`/`ctx.auth.tenantId` manually (see currencies delete).
- DELETE command path validates `{body, query}` as one object against `action.schema` — a different shape from POST/PUT (body only).
- Interceptor body/query replacement drops keys with `undefined` values (`sanitizeObject`), and after-interceptor `merge` is shallow.
- `mergeIdFilter` intersection can legitimately produce `id: {$in: []}` (empty result) — do not special-case it.
- Zod error `details` uses `err.issues` verbatim — ports should emit structurally similar issue objects (`path`, `message`, `code`).
- Cached list payloads are enriched-or-not depending on `cacheableOnListHit` of every active enricher; getting this wrong leaks ACL-gated fields across users — the cache key's `enrichers:` segment is the guard.
- `withCtx` swallows organization-scope resolution errors (scope = null ⇒ fall back to token org), so a broken directory module degrades to single-org scoping instead of 500.
- `x-api-key` auth (`keyId`) is used as `actorUserId` in access logs (`auth.keyId ?? auth.sub`).
- The factory's `json()` helper sets only `content-type: application/json` — no charset; byte-compat ports should match (`JSON.stringify`, no pretty-printing, no trailing newline).
- `GET /api/docs/openapi` and `/api/docs/markdown` are app-level Next routes **outside** the module manifest (no metadata guard in the route file itself) — dispatcher-level guards do not apply to them.
