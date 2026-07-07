# Module System & App Composition — Requirements Spec

> Derived from upstream commit adc9da27759e357febe9ed8d4b7182040d127349. Source analysis: ../upstream/analysis/01-module-system.md

## Scope

This spec covers the technology-agnostic contract of Open Mercato's module system: the enabled-module configuration, module convention artifacts (metadata, ACL features, DI registrars, setup hooks, API routes, subscribers, workers, translations, entity extensions), the composition/registry pipeline, request-scoped dependency injection, the HTTP API dispatch pipeline (auth, tenant guard, RBAC features, rate limiting), the module override surface, and the tenant setup/seed lifecycle.

Out of scope (covered by other specs): entity/ORM details, query/data engine, event bus and queue internals, custom fields/entities engine, auth token issuance, frontend/backend pages and widgets (UI is not ported).

Terminology: MUST / SHOULD / MAY per RFC 2119. "Port" = a backend implementation in any language. "Enabled order" = the ordered list of module entries in the composition config.

## Requirements

### Composition & module identity

- **MODULESYSTEM-R1** — The port MUST define a single ordered composition config listing enabled modules as entries of shape `{ id, from?, overrides? }`, where `id` is the module identifier, `from` names the providing package/source (upstream: `@open-mercato/core`, `@app`, or a third-party package), and `overrides` is an optional per-entry override map (see R40–R46). Upstream: `apps/mercato/src/modules.ts`.
- **MODULESYSTEM-R2** — Entry order in the composition config MUST be preserved end-to-end. It determines: baseline API route order (pre-specificity-sort tie-breaking), DI registrar execution order (later wins), translation merge order, ACL feature concatenation order, and setup/seed hook execution order. The port MUST NOT sort modules alphabetically or topologically.
- **MODULESYSTEM-R3** — The composition config MUST support environment-conditional entries (upstream examples: `storage_s3` behind `OM_ENABLE_STORAGE_S3`; enterprise modules `record_locks`, `system_status_overlays`, `sso`, `security` behind `OM_ENABLE_ENTERPRISE_MODULES[_SSO|_SECURITY]`), appended after the base list and deduplicated by `id`. A port MAY use declarative config (JSON/YAML/env) instead of upstream's statically-evaluated TS file; the observable contract is only the resulting ordered `{id, from, overrides}` list.
- **MODULESYSTEM-R4** — Each module MUST declare metadata equivalent to upstream `index.ts` `export const metadata: ModuleInfo`: at minimum `name`; optionally `title`, `version`, `description`, `author`, `license`, `requires: string[]`, `ejectable: boolean`.
- **MODULESYSTEM-R5** — `metadata.requires` MUST be validated at build/startup: if a module requires an id not in the enabled set, composition MUST fail fatally (upstream: message `Module "<id>" requires: <missing>` and exit code 1). `requires` MUST NOT be used to reorder modules at runtime.
- **MODULESYSTEM-R6** — Loading a module's metadata MUST execute the module's initialization code (upstream `index.ts` has side effects, e.g. `packages/core/src/modules/auth/index.ts` imports `./commands/*` which register command handlers). A port MUST provide an equivalent per-module init hook that runs at composition time, in enabled order.

### Discovery & convention artifacts

- **MODULESYSTEM-R7** — For each enabled module the port MUST discover (by convention, reflection, or codegen — see Allowed deviations) the following artifact kinds and expose them in a composed `Module` record: metadata (`index.ts`), ACL features (`acl.ts`), DI registrar (`di.ts`), setup config (`setup.ts`), API routes (`api/**`), event subscribers (`subscribers/*`), queue workers (`workers/*`), translations (`i18n/<locale>.json`), entity extensions (`data/extensions.ts`), custom field sets (`data/fields.ts`), custom entities (`ce.ts`), default encryption maps (`encryption.ts`), CLI commands (`cli.ts`), validators (`data/validators.ts`, consumed by route code directly), and ORM entities (`data/entities.ts`).
- **MODULESYSTEM-R8** — The port SHOULD support app-over-package artifact override: an app-local file at the same logical path as a package module file replaces it per-artifact (per route file, subscriber, worker, `di.ts`, etc.), without forking the module. For i18n locale files, package and app dictionaries MUST be merged with app keys winning.
- **MODULESYSTEM-R9** — ORM entity source resolution SHOULD honor the candidate priority order `data/entities.override` → `data/entities` → `db/entities` (legacy) → `db/schema`, app tree before package tree.
- **MODULESYSTEM-R10** — Discovery MUST run before the request path: no filesystem scanning per request. Upstream materializes registries at generate time (`.mercato/generated/*`); a port MAY materialize them at process startup instead.
- **MODULESYSTEM-R11** — Composition-time validation errors (missing required modules per R5, duplicate default encryption maps per R35, conflict detection equivalent to upstream UMES errors) MUST be fatal before serving traffic. Ports skipping codegen MUST run these validations at startup.
- **MODULESYSTEM-R12** — The port SHOULD emit a developer warning when an API route declares handlers but no metadata (upstream: `Route file exports handlers but no metadata — auth will default to required`).

### ACL features

- **MODULESYSTEM-R13** — Each module MAY declare features as `Array<{ id, title, module }>` with ids of form `<module>.<area>.<action>` (e.g. `auth.users.list`). Feature ids are the sole authorization vocabulary and MUST be stable across ports.
- **MODULESYSTEM-R14** — Role feature lists MUST support trailing-wildcard entries such as `auth.*` (matching all features of the module/prefix).

### API routes & dispatch

- **MODULESYSTEM-R15** — API route paths MUST be derived as `/<moduleId>/<fileSegments>` (route file's own name excluded), mounted under `/api` by the host. An explicit `metadata.path` string overrides the derived path. Dynamic segments use bracket syntax in the path pattern: `[param]`, `[...param]`, `[[...param]]`.
- **MODULESYSTEM-R16** — Each route MUST declare which HTTP methods it handles (`GET|POST|PUT|PATCH|DELETE`) and per-method metadata `{ requireAuth?, requireRoles?, requireFeatures?, rateLimit? }`, plus an OpenAPI description (`export const openApi` upstream). A flat (non-method-keyed) metadata object MUST be treated as applying to every method of the route.
- **MODULESYSTEM-R17** — Route matching MUST implement upstream `matchRoutePattern` semantics: patterns/paths normalized to leading `/` and no trailing `/`; literal segments compared case-insensitively; `[param]` captures one segment; `[...param]` requires ≥1 remaining segment (captured as list); `[[...param]]` matches 0+ segments; extra URL segments beyond the pattern are a non-match.
- **MODULESYSTEM-R18** — Route candidates MUST be ordered by a stable specificity sort: compare segment-by-segment left-to-right with rank literal(0) < dynamic `[param]`(1) < catch-all(2), missing segment = −1; equal-specificity routes keep enabled-module/registration order. First match in sorted order wins (so `/customers/export` beats `/customers/[id]`).
- **MODULESYSTEM-R19** — Authentication MUST default to REQUIRED: absence of metadata or of `requireAuth` means an unauthenticated request gets `401 {"error":"Unauthorized"}`. Only explicit `requireAuth: false` makes a route public.
- **MODULESYSTEM-R20** — When the presented auth token is invalid (not merely absent), the 401 response MUST also expire the `auth_token` and `session_token` cookies.
- **MODULESYSTEM-R21** — `requireRoles` MUST NOT be enforced (deprecated upstream). The port SHOULD log a one-time-per-route warning when it is present; authorization is exclusively feature-based.
- **MODULESYSTEM-R22** — When `requireFeatures` is non-empty: an unauthenticated request MUST get 401; an authenticated user failing `userHasAllFeatures(sub, features, {tenantId, organizationId})` MUST get `403 {"error":"Forbidden","requiredFeatures":[...]}`.
- **MODULESYSTEM-R23** — Unknown path or missing method handler MUST return `404 {"error":"Not Found"}` (message localizable).
- **MODULESYSTEM-R24** — Tenant parameter pollution guard: before invoking any handler, the port MUST validate EVERY distinct `tenantId` candidate — all repeated `?tenantId=` query values plus a root-level `tenantId` body field (JSON, urlencoded, multipart; non-GET/HEAD/OPTIONS only) — against the actor's allowed tenants. Candidate strings `'null'`/`'undefined'` (case-insensitive, trimmed) map to null/skip. A violation returns the tenant-enforcement error's status/body (typically 403), not the handler's response.
- **MODULESYSTEM-R25** — Per-method `rateLimit: { points, duration, blockDuration?, keyPrefix? }` MUST be enforced keyed by client IP via a process-level rate limiter before the handler runs; exceeding it returns the shared rate-limit error response (429 semantics).
- **MODULESYSTEM-R26** — The dispatch pipeline order MUST be: match → load handler → auth check → tenant-candidate guard → feature check → rate limit → invoke handler with `{ params, auth }` context (with tenant-scoped cache context set to `auth.tenantId ?? null`).
- **MODULESYSTEM-R27** — The port SHOULD emit request-lifecycle application events (`requestReceived`, `requestNotFound`, `requestAuthResolved`, `requestAuthorizationDenied`, `requestRateLimited`, `requestCompleted`, `requestFailed`) best-effort, with payload including `requestId` (from `x-request-id` header or a random UUID), `method`, `pathname`, `durationMs`, `status`, `userId`, `tenantId`.

### Subscribers & workers

- **MODULESYSTEM-R28** — Each subscriber MUST declare `{ event, id?, persistent?, sync?, priority? }` plus a handler `(payload, ctx)`. Default id MUST be `'<moduleId>:<subdirs>:<basename>'` (path segments joined with `:`).
- **MODULESYSTEM-R29** — Each worker MUST declare `{ queue, id?, concurrency? }` plus a handler `(job, ctx)`. Default id MUST be `'<moduleId>:workers:<subdirs>:<basename>'`; default concurrency MUST be 1. A worker definition without a `queue` MUST be skipped (not an error).
- **MODULESYSTEM-R30** — Subscriber/worker handlers SHOULD be lazily loaded (first invocation), and a definition whose handler is not callable MUST fail with an error identifying the id (upstream: `[registry] Invalid subscriber|worker module "<id>" (missing default export handler)`).
- **MODULESYSTEM-R31** — At bootstrap, all module subscribers MUST be auto-registered on the event bus in enabled order; subscribers with `sync: true` MUST additionally be registered in the synchronous-subscriber store.

### Dependency injection (request scope)

- **MODULESYSTEM-R32** — The port MUST build a fresh DI scope per unit of work (HTTP request, CLI command, worker job) containing at minimum: a per-scope ORM session/EntityManager (fresh, isolated identity map and event hooks), `queryEngine`, `dataEngine`, `commandRegistry`, `commandBus`, and mutation-guard service — then run every module's DI registrar in enabled order, then core bootstrap services (`cache`, `eventBus`, KMS/tenant-encryption, `rateLimiterService`, search), then the app-level registrar, then per-entry `di` overrides (R44).
- **MODULESYSTEM-R33** — DI registration MUST use replace semantics: a later registration under the same key silently replaces the earlier one. This is the sanctioned mechanism for module service overrides (e.g. a module replacing `rbacService`).
- **MODULESYSTEM-R34** — A failing module DI registrar MUST NOT abort container creation (upstream swallows exceptions per registrar). Core bootstrap failures MUST degrade gracefully (no-op event bus, memory cache, local queue fallbacks).
- **MODULESYSTEM-R35** — `getDefaultEncryptionMaps(modules)` semantics: default encryption maps from all modules are collected; a duplicate `entityId` declared by two modules MUST throw fatally (upstream message: `[registry] Duplicate default encryption map for "<entityId>" declared by "<a>" and "<b>"`).
- **MODULESYSTEM-R36** — Upstream uses Awilix CLASSIC mode (constructor/function parameter names resolved against cradle keys) with `.scoped()` lifetimes. A port MUST preserve the observable semantics — per-request instances, name-keyed resolution, replaceability — but MAY use its platform's idiomatic DI container (see Allowed deviations).

### Setup lifecycle & role ACL seeding

- **MODULESYSTEM-R37** — Each module MAY provide a setup config with hooks `onTenantCreated({tenantId, organizationId, em})`, `seedDefaults({tenantId, organizationId, em, container})`, `seedExamples(ctx)` (skipped with `--no-examples`), and maps `defaultRoleFeatures` / `defaultCustomerRoleFeatures` (role name → feature-id list, wildcards allowed).
- **MODULESYSTEM-R38** — Initial tenant setup MUST: create Tenant + Organization (or reuse those of an existing user with the primary email), ensure roles `employee`, `admin`, `superadmin`, seed default encryption maps, create the primary user plus derived `admin@acme.com` / `employee@acme.com` users (emails/passwords overridable via `OM_INIT_ADMIN_EMAIL`/`OM_INIT_EMPLOYEE_EMAIL` and `OM_INIT_ADMIN_PASSWORD`/`OM_INIT_EMPLOYEE_PASSWORD`, default password `secret`, bcrypt cost 10), then seed role ACLs (R39), then call each module's `onTenantCreated` sequentially in enabled order.
- **MODULESYSTEM-R39** — Role ACL seeding MUST concatenate `defaultRoleFeatures` across all enabled modules in enabled order into per-role feature lists, then upsert each role's ACL by SET-UNION with existing features — never removing features. The superadmin role's ACL MUST get `isSuperAdmin: true`, and `isSuperAdmin` may only ever be turned on by seeding, never off. Feature maps for custom (non-default) role names are applied only if the role already exists.
- **MODULESYSTEM-R40** — The init flow (upstream `mercato init`) MUST run, in order: initial tenant setup (R38) → per-module `seedDefaults` sequentially in plain enabled order → a SECOND custom-role-ACL pass (`ensureCustomRoleAcls`, because `seedDefaults` may create custom roles) → per-module `seedExamples` in enabled order unless examples are disabled. Hooks are awaited sequentially; there is no dependency-based reordering.

### Overrides (per composition entry)

- **MODULESYSTEM-R41** — The port MUST support per-entry `overrides` across these domains (subset acceptable for backend-only ports, see Allowed deviations): `routes.api`, `events.subscribers`, `workers`, `cli`, `setup`, `acl.features`, `di`, `encryption.maps` — with the uniform rule: **`null` disables the target, a definition replaces it**.
- **MODULESYSTEM-R42** — `routes.api` override keys MUST have the form `'METHOD /api/path'` (method case-insensitive). A `null` value removes that method from the route's method set (dropping the route entirely when no methods remain); a `{ handler, metadata? }` value replaces the handler/metadata for that method. Malformed keys MUST warn (upstream: `Skipping malformed routes.api key "<k>" — expected "METHOD /api/path"`), not error.
- **MODULESYSTEM-R43** — Override keying: subscribers by subscriber `id`, workers by worker `id`, CLI by `command`, ACL by feature `id`, encryption by `entityId`, DI by container key. Stale override keys (matching nothing) MUST produce warnings, not errors.
- **MODULESYSTEM-R44** — `di` overrides: `null` unregisters (resolves to undefined), a definition with a `register(container, key)` function self-registers, any other value is registered as a constant value. Applied LAST in container construction (after module registrars, core bootstrap, and app registrar).
- **MODULESYSTEM-R45** — `setup` overrides: `seedDefaults: false` / `seedExamples: false` / `onTenantCreated: false` disable the respective hook; `defaultRoleFeatures` / `defaultCustomerRoleFeatures` maps replace the module's per-role lists.
- **MODULESYSTEM-R46** — Override precedence (highest first) MUST be: programmatic application → composition-config inline → file-based (where applicable) → module base registration. API-route overrides take effect only when applied before route-manifest registration.

### Entity extensions & translations

- **MODULESYSTEM-R47** — Modules MUST NOT form direct ORM relations across module boundaries. Cross-module links are plain UUID columns plus declared `EntityExtension` records `{ base: '<module>:<entity>', extension: '<module>:<entity>', join: { baseKey, extensionKey }, cardinality?, required?, description? }` that the query/data engine traverses.
- **MODULESYSTEM-R48** — Entity identifiers MUST use the canonical form `'<module>:<entity_snake_case>'` (upstream `E.<module>.<entity>` constants, class names snake_cased).
- **MODULESYSTEM-R49** — Module translations are `locale → key → text` dictionaries merged in enabled order (later modules override earlier keys); the translation cache MUST be invalidated whenever the module registry is (re)registered.

## Contracts

These exact wire/persisted formats MUST be honored by every port.

### HTTP dispatch responses

| Condition | Status | Body |
|---|---|---|
| No route / no method handler | 404 | `{"error":"Not Found"}` (message localizable) |
| Auth required, none/invalid presented | 401 | `{"error":"Unauthorized"}`; invalid token additionally expires cookies `auth_token`, `session_token` |
| `requireFeatures` unmet | 403 | `{"error":"Forbidden","requiredFeatures":["<feature-id>", ...]}` |
| Tenant candidate violation | error's own status (typically 403) | error's own body |
| Rate limit exceeded | 429 | shared rate-limit error body |

Request id source: `x-request-id` header, else random UUID.

### Route metadata (per method)

```jsonc
{
  "requireAuth": true,            // DEFAULT when absent
  "requireRoles": ["..."],        // deprecated, never enforced
  "requireFeatures": ["auth.users.list"],
  "rateLimit": { "points": 10, "duration": 60, "blockDuration": 120, "keyPrefix": "..." }
}
```

### Identifiers & keys

- Feature id: `<module>.<area>.<action>` (e.g. `auth.users.edit`); wildcard grant: `<module>.*` (e.g. `auth.*`).
- Entity id: `<module>:<entity_snake>` (e.g. `customers:customer_interaction`).
- Subscriber default id: `<module>:<subdirs>:<basename>` (e.g. `example:todo:audit`).
- Worker default id: `<module>:workers:<subdirs>:<basename>`; default concurrency `1`.
- API route path: `/<moduleId>/<segments>` mounted under `/api`; dynamic segments `[id]`, `[...rest]`, `[[...rest]]`.
- API override key: `METHOD /api/<path>` (e.g. `GET /api/example/override-probe`).

### Setup / seeding

- Default roles: `employee`, `admin`, `superadmin` (roles are always tenant-scoped; global roles unsupported).
- Derived user emails: `admin@acme.com`, `employee@acme.com` (overridable via `OM_INIT_ADMIN_EMAIL` / `OM_INIT_EMPLOYEE_EMAIL`); default password `secret`; password hash bcrypt, cost 10.
- Role ACL persistence: per-role feature list (upstream column `RoleAcl.featuresJson`), updated by set-union; superadmin row has `isSuperAdmin = true`.
- Tenant name on creation: `<orgName> Tenant`; organization created at depth 0 with empty hierarchy arrays.

### Canonical example fixtures (from upstream, usable as parity fixtures)

`acl.ts` features (`packages/core/src/modules/auth/acl.ts`):

```ts
{ id: 'auth.users.list', title: 'List users', module: 'auth' }
```

Entity extension (`packages/core/src/modules/customers/data/extensions.ts`):

```ts
{
  base: 'customers:customer_interaction',
  extension: 'communication_channels:message_channel_link',
  join: { baseKey: 'external_message_id', extensionKey: 'id' },
  cardinality: 'many-to-one',
}
```

API route override probe (`apps/mercato/src/modules.ts`, module `example`): `GET /api/example/override-probe` replaced inline, responding `{"ok":true,"source":"modules.ts override","route":"example.override-probe"}` with `requireAuth: false`.

### Environment variables in scope

`QUEUE_STRATEGY` / `EVENTS_STRATEGY` (`async|redis` ⇒ async event bus, else local), `OM_ENABLE_STORAGE_S3`, `OM_ENABLE_ENTERPRISE_MODULES[_SSO|_SECURITY]`, `OM_OPTIMISTIC_LOCK` (`off` disables mutation guard), `TENANT_DATA_ENCRYPTION` (default on), `SELF_SERVICE_ONBOARDING_ENABLED`, `DEMO_MODE`, `OM_INIT_SUPERADMIN_EMAIL/PASSWORD`, `OM_INIT_ADMIN_EMAIL/PASSWORD`, `OM_INIT_EMPLOYEE_EMAIL/PASSWORD`. A port MUST read the same names with the same defaults where the behavior is observable (queue strategy, encryption toggle, init credentials).

## Concept mapping

| Upstream TS concept | Technology-agnostic concept a port implements |
|---|---|
| `apps/mercato/src/modules.ts` `enabledModules` (statically evaluated TS) | Ordered module composition config (JSON/YAML/env-conditional), entries `{id, from, overrides}` |
| `mercato generate` → `.mercato/generated/*` static registries | Any pre-request materialization: startup reflection/annotation scan, source generators, or codegen |
| `official-modules.generated.ts` (versioned) vs `.mercato/generated/` (ephemeral) | Committed activation config vs rebuildable discovery artifact — keep the split |
| Module convention files (`index.ts`, `acl.ts`, `di.ts`, `setup.ts`, `api/**`, `subscribers/*`, `workers/*`, `i18n/*`, `data/*`, `ce.ts`, `encryption.ts`, `cli.ts`) | Module manifest/contract: metadata, features, DI registrar, setup hooks, routes, subscribers, workers, translations, entities, encryption defaults, CLI commands |
| `globalThis.__openMercato*__` registries | Ordinary process singletons (module registry, route manifests, DI registrars) |
| Next.js catch-all `app/api/[...slug]/route.ts` dispatcher | HTTP middleware/dispatch pipeline: match → auth → tenant guard → RBAC → rate limit → handler |
| `matchRoutePattern` / `sortRoutesBySpecificity` | Router with bracket-syntax dynamic segments, case-insensitive literals, literal<param<catch-all specificity, stable sort |
| Awilix container, CLASSIC mode, `.scoped()` | Per-request DI scope with name-keyed, replace-on-reregister service resolution |
| MikroORM `em.fork({clear, freshEventManager})` | Per-request ORM session/unit-of-work with fresh hooks (re-attach tenant-encryption hook per session when enabled) |
| `ModuleSetupConfig` hooks | Tenant lifecycle hooks: on-tenant-created, seed-defaults, seed-examples, default role features |
| `ensureDefaultRoleAcls` / `ensureCustomRoleAcls` | Additive (set-union) role-ACL seeder + post-seedDefaults second pass for custom roles |
| `entry.overrides` domains with `null|replacement` | Uniform disable/replace override layer applied at composition time |
| `createLazyModuleSubscriber/Worker` | Lazy handler resolution with fail-fast on non-callable handler |
| Zod `data/validators.ts` | Language-native validation library producing identical accept/reject behavior and error shapes |
| UMES conflict detection at generate time | Startup-time composition validation (fatal on conflicts) |

## Allowed deviations

Welcome (document each as a decision in the port's notes):

- **Discovery mechanism.** Replace generate-time TS codegen with reflection, attributes/annotations, decorators, or platform codegen — provided registries are materialized before serving traffic (R10) and ordering/override semantics are identical.
- **Composition config format.** Replace the statically-evaluated `modules.ts` with declarative JSON/YAML + env conditionals (R3). Do NOT replicate the TS AST mini-interpreter.
- **DI container.** Any idiomatic container (or hand-rolled service registry) satisfying per-request scoping, name-keyed resolution, and replace semantics (R32–R33, R36).
- **Validation library.** Native alternative to Zod, if observable accept/reject behavior and error payloads match.
- **Registry unification.** Upstream keeps dual registries (route manifests vs `Module[]`); a port MAY unify them if every consumer sees the same effective data.
- **globalThis workarounds.** Not needed; use process singletons.
- **UI/widget/page artifacts.** Frontend/backend pages, dashboard/injection widgets, component overrides, and their generator extensions are Next.js/React-specific and MAY be omitted from backend ports.
- **Regex-based method detection & AST metadata extraction.** TS-specific; ports declare methods/metadata natively.

MUST NOT change:

- Enabled-module ORDER and its downstream effects (R2).
- Auth-required-by-default, status codes, and error body shapes (R19–R25, Contracts table).
- Route matching and specificity semantics (R17–R18).
- Feature ids, entity ids, subscriber/worker id derivation, queue names, default concurrency (Contracts).
- Additive set-union role-ACL seeding and the seed lifecycle order incl. the second custom-role pass (R39–R40).
- `null`=disable / value=replace override semantics and key formats (R41–R46).
- Fatal duplicate-encryption-map and missing-`requires` validation (R5, R35).
- Canonical env var names/defaults where behavior is observable.

## Verification

How `om-verify-parity` checks these requirements (run the TS reference app and the port against the same PostgreSQL/Redis where applicable):

1. **Route inventory parity** (R15–R16): dump both implementations' route tables (method, path pattern, requireAuth, requireFeatures, rateLimit) — e.g. via generated OpenAPI documents — and diff. Must be identical for ported modules.
2. **Matching/specificity fixtures** (R17–R18): table-driven tests hitting overlapping paths (`/api/customers/export` vs `/api/customers/<uuid>`, mixed-case literals, 0/1/N catch-all segments) and asserting the same handler is selected and params are equal.
3. **Dispatch pipeline probes** (R19–R26): for a representative route, send (a) no token → expect 401 `{"error":"Unauthorized"}`; (b) invalid token → 401 + `Set-Cookie` expiring `auth_token`/`session_token`; (c) token lacking a feature → 403 with exact `requiredFeatures` array; (d) unknown path → 404 body; (e) polluted `?tenantId=` (repeated params + body field, incl. `'null'`/`'undefined'` strings) → tenant-enforcement status; (f) burst beyond `rateLimit.points` → 429. Byte-compare JSON bodies.
4. **Override probe** (R41–R42, R46): configure the `example` module entry with the `GET /api/example/override-probe` inline override; expect unauthenticated 200 with the exact JSON `{"ok":true,"source":"modules.ts override","route":"example.override-probe"}`. Also verify a `null` API override yields 404 and a stale key yields only a warning.
5. **Setup/seed parity** (R38–R40): run init on an empty database in both stacks; diff resulting rows: tenants, organizations, roles, users (emails, bcrypt-verifiable default passwords), role-ACL feature sets (order-insensitive set comparison, `isSuperAdmin` flags). Re-run init to prove set-union additivity (no features removed, idempotent hooks).
6. **Subscriber/worker id & queue parity** (R28–R29): enumerate registered subscriber ids/events and worker ids/queues/concurrency from both stacks and diff; enqueue a BullMQ job from the TS side and assert the port's worker consumes it (and vice versa) using identical queue names.
7. **Composition validation** (R5, R11, R35): enable a module with an unsatisfied `requires` → expect fatal failure and non-zero exit; declare a duplicate default encryption map `entityId` in two test modules → expect fatal failure with both module names identified.
8. **Ordering evidence** (R2): with two test modules registering the same DI key and the same translation key, assert the later-listed module wins in both stacks; swap the order and assert the outcome flips.
