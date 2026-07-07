# Module System & App Composition

> Analyzed at upstream commit adc9da27759e357febe9ed8d4b7182040d127349 (2026-07-01). Regenerate via the om-sync-upstream skill.

## Purpose

Open Mercato is composed of **modules** — self-contained vertical slices (auth, customers, currencies, …) that ship API routes, pages, ORM entities, event subscribers, queue workers, CLI commands, ACL features, tenant setup hooks, translations and more, all discovered **by file convention**. There is no runtime filesystem scanning in the request path: a build-time **generator suite** (`mercato generate`, package `@open-mercato/cli`) scans every enabled module's directory tree and emits static TypeScript registries into `apps/mercato/.mercato/generated/`. The app's `src/bootstrap.ts` imports those generated files and registers them into process-global registries (modules, DI registrars, ORM entities, route manifests…). A per-request **Awilix DI container** is then built for every HTTP request / CLI command / worker job.

A port must reproduce: (1) the module contract (which convention files exist and what they export), (2) the composition pipeline (enabled-module list → discovery → registry), (3) the request-scoped DI semantics and override layering, and (4) the setup lifecycle (tenant init, role feature seeding, seed hooks). The *generated-files* mechanism itself is a TypeScript/bundler workaround — a port may replace it with reflection/annotation scanning or compile-time codegen native to its platform, **as long as ordering, defaults and override semantics are identical**.

## Key source locations

| Path (upstream repo root) | Contents |
|---|---|
| `apps/mercato/src/modules.ts` | `enabledModules: ModuleEntry[]` — the single source of truth for which modules are on, in which order, from which package; plus per-entry `overrides` |
| `apps/mercato/src/official-modules.generated.ts` | Versioned generated list of activated "official modules" (from `official-modules.json`); appended to `enabledModules` |
| `official-modules.json` (repo root) | `{ repo, path: 'external/official-modules', branch, available: [], activated: [] }` — managed by `yarn official-modules` (`scripts/official-modules.mjs`) |
| `apps/mercato/src/bootstrap.ts` | App bootstrap: applies modules.ts overrides, imports all `.mercato/generated/*` files, calls `createBootstrap(...)` |
| `apps/mercato/src/di.ts` | App-level DI `register(container)` — runs core `bootstrap(container)` if eventBus missing, emits application lifecycle events |
| `apps/mercato/src/app/api/[...slug]/route.ts` | Next.js catch-all API dispatcher: manifest match → auth → tenant-candidate enforcement → RBAC features → rate limit → handler |
| `packages/shared/src/modules/registry.ts` | `Module`, `ModuleInfo`, `ModuleRoute`, `ModuleApi*`, `ModuleSubscriber`, `ModuleWorker` types; route pattern matcher; specificity sort; manifest registries; lazy subscriber/worker wrappers; `getDefaultEncryptionMaps` |
| `packages/shared/src/lib/modules/registry.ts` | Process-global `registerModules`/`getModules` (globalThis key `__openMercatoModulesRegistry__`) |
| `packages/shared/src/modules/overrides.ts` | Unified `entry.overrides` surface: 15 domains, per-domain apply/compose functions, `applyModuleOverridesToModules`, `applyApiOverridesToManifests`, `applyDiOverridesToContainer` |
| `packages/shared/src/modules/setup.ts` | `ModuleSetupConfig` (`onTenantCreated`, `seedDefaults`, `seedExamples`, `defaultRoleFeatures`, `defaultCustomerRoleFeatures`) |
| `packages/shared/src/modules/entities.ts` | `EntityExtension`, `CustomFieldDefinition`, `CustomFieldSet`, `CustomEntitySpec` types |
| `packages/shared/src/modules/generators/types.ts` | `GeneratorPlugin` interface (module-contributed generators via `generators.ts`) |
| `packages/shared/src/lib/di/container.ts` | Awilix helpers: `registerDiRegistrars`, `createRequestContainer`, process-scoped bootstrap cache (`OM_BOOTSTRAP_CACHE`) |
| `packages/shared/src/lib/bootstrap/factory.ts` | `createBootstrap(data)` — registers ORM entities, DI registrars, modules, entity IDs, search/analytics configs, enrichers, interceptors, component overrides, guards, command interceptors, notification handlers, widgets |
| `packages/shared/src/lib/bootstrap/dynamicLoader.ts` | CLI-side bootstrap: esbuild-compiles `.mercato/generated/modules.cli.generated.ts` to `.mjs` and imports it without Next.js |
| `packages/cli/src/lib/resolver.ts` | `createResolver()` — monorepo vs standalone detection, module path/import resolution, **TS-AST parsing of `src/modules.ts`** (static evaluation of `enabledModules` incl. env-conditional pushes) |
| `packages/cli/src/lib/generators/scanner.ts` | Directory scan configs per convention folder; app-over-package override merge; static-before-dynamic sorting |
| `packages/cli/src/lib/generators/module-registry.ts` | The main generator (~3.8k lines): builds `modules.generated.ts`, `modules.runtime.generated.ts`, `frontend/backend/api-routes.generated.ts`, `subscribers.generated.ts`, `bootstrap-registrations.generated.ts`, `modules.app.generated.ts`, `enabled-module-ids.generated.ts`, `modules.cli.generated.ts` (+ legacy aliases) |
| `packages/cli/src/lib/generators/module-di.ts` | Emits `di.generated.ts` — array of each module's `di.ts` `register` functions |
| `packages/cli/src/lib/generators/module-entities.ts` | Emits `entities.generated.ts` — flattened MikroORM entity classes from each module's `data/entities.ts` (fallbacks `data/entities.override`, legacy `db/entities`, `db/schema`) |
| `packages/cli/src/lib/generators/entity-ids.ts` | Emits `entities.ids.generated.ts` (`E.<module>.<entity_snake> = '<module>:<entity>'`) and `entity-fields-registry.ts` |
| `packages/cli/src/lib/generators/extensions/*.ts` | 18 built-in generator extensions mapping extra convention files → extra generated registries (see table below) |
| `packages/cli/src/mercato.ts` | CLI entry: `runGeneratorSuite()` order, `mercato init` flow (setup tenant → seedDefaults → seedExamples), module CLI dispatch |
| `packages/core/src/bootstrap.ts` | `bootstrap(container)` — cache, event bus (QUEUE_STRATEGY), subscriber auto-registration, KMS/tenant encryption, rate limiter, search |
| `packages/core/src/modules/auth/lib/setup-app.ts` | `setupInitialTenant`, `ensureDefaultRoleAcls`, `ensureCustomRoleAcls` — consumes `Module.setup` |
| Representative modules | `packages/core/src/modules/{auth,customers,currencies}/` — `index.ts`, `acl.ts`, `di.ts`, `setup.ts`, `data/`, `api/`, `subscribers/`, `workers/`, `i18n/` |

## How it works

### 1. Enabled module list (`src/modules.ts`)

`apps/mercato/src/modules.ts` exports `const enabledModules: ModuleEntry[]` where `ModuleEntry = { id: string; from?: '@open-mercato/core' | '@app' | string; overrides?: ModuleOverrides }`. Order matters (route matching, seed order, translation merging are all list-ordered). After the literal array, the file *pushes* additional entries conditionally:

- `officialModuleEntries` from `official-modules.generated.ts` (deduped by id),
- `example_customers_sync` if `example` is enabled,
- `storage_s3` if `OM_ENABLE_STORAGE_S3` truthy,
- enterprise modules (`record_locks`, `system_status_overlays`, `sso`, `security`) behind `OM_ENABLE_ENTERPRISE_MODULES[_SSO|_SECURITY]` env flags (parsed with `parseBooleanWithDefault`).

Crucially, the CLI resolver does **not** execute this file. `packages/cli/src/lib/resolver.ts:parseModulesFromSource()` parses it with the TypeScript compiler API and **statically evaluates**: the array literal, subsequent `enabledModules.push(...)` statements, `if` conditions over `process.env.X`, `parseBooleanWithDefault(...)` calls, `===`/`!==`/`&&`/`||`/`!`, and `.some(...)` predicates. Unresolvable entries are dropped. If parsing fails or yields zero entries, it falls back to scanning `src/modules/*` directories as `{ id: dirName, from: '@app' }`.

### 2. Module directory layout and app-over-package overrides

For entry `{ id, from }`, the resolver computes two roots:
- `appBase = <appDir>/src/modules/<id>` (app-local override tree)
- `pkgBase = packages/<pkg>/src/modules/<id>` (monorepo) or `node_modules/<from>/dist/modules/<id>` (standalone; falls back to `src/modules`).

and two import bases: `@/modules/<id>` (app) and `<from>/modules/<id>` (package). For `from: '@app'` modules the generated files import via relative path `../../src/modules/<id>/...` so they work in both Next.js and Node CLI contexts.

`scanModuleDir()` (scanner.ts) walks a convention folder in *both* roots, skipping `__tests__`/`__mocks__`, and merges by logical path (extension-stripped): **an app file with the same logical path replaces the package file**. This gives per-file ejection/override without forking a module. `resolveModuleFile()` does the same for single convention files (`index.ts`, `acl.ts`, `setup.ts`, …), trying extensions `.ts,.tsx,.js,.jsx`.

Convention files discovered per module (in the exact processing order of `generateModuleRegistry`, which fixes generated import-ID sequence):

1. `index.ts` → `info` (`export const metadata: ModuleInfo`); `metadata.requires: string[]` is read via `require()` for dependency validation.
2. `frontend/**` pages → `frontendRoutes` (Next-style `page.tsx` under nested dirs, or legacy direct `login.tsx` files; each must have a **default export** or is skipped; optional sibling `page.meta.ts`/`meta.ts`/`<name>.meta.ts` supplies `metadata: PageMetadata`).
3. `data/extensions.ts` → `entityExtensions` (export `extensions` or default; `EntityExtension[]`).
4. `acl.ts` → `features` (export `features` or default; `Array<{ id, title, module }>`).
5. `ce.ts` → `customEntities` (export `entities` or default; entries with non-empty `fields` also contribute to `customFieldSets` with `source: <moduleId>`).
6. Generator-extension convention files (see table in §5).
7. Module-contributed generator plugins from `generators.ts` (exports `generatorPlugins: GeneratorPlugin[]`).
8. `setup.ts` → `setup` (export `setup` or default; `ModuleSetupConfig`).
9. `encryption.ts` → `defaultEncryptionMaps`.
10. `integration.ts` → `integrations` (export `integrations` or singular `integration`) and `bundles` (or `bundle`).
11. `data/fields.ts` → static `customFieldSets` (export `fieldSets` or default).
12. `backend/**` pages → `backendRoutes` (route path is `/backend/<segments>`, or `/backend/<moduleId>` for a top-level page).
13. `api/**` → `apis` (three shapes, see §4).
14. `cli.ts` → `cli` (default export: `ModuleCli[] = Array<{ command, run(argv) }>`).
15. `i18n/<locale>.json` → `translations` — when both package and app files exist for a locale they are merged with **app keys winning** (`{...pkg, ...app}`).
16. `subscribers/*.ts` → `subscribers` (each file default-exports a handler and exports `const metadata = { event, id?, persistent?, sync?, priority? }`).
17. `workers/*.ts` → `workers` (default-export handler + `const metadata = { queue, id?, concurrency? }`; **files without `metadata.queue` are skipped**).
18. `widgets/dashboard/**/widget.tsx` → `dashboardWidgets` (key `<moduleId>:<subdirs>:<basename>`, `source: 'app'|'package'`, lazy loader).
19. `widgets/injection/**/widget.tsx` → injection widgets (via generator extension).

Additional per-module conventions handled by other generators: `data/entities.ts` (MikroORM entities; candidates in priority order `data/entities.override`, `data/entities`, `db/entities`(legacy), `db/schema`), `di.ts` (`export function register(container)`), `data/validators.ts` (imported directly by route code, not by the registry).

Metadata extraction for subscribers/workers/API files works in two tiers: try `await import(fileUrl?v=<mtime>-<size>)` of the source module; if that fails (TS in Node), fall back to **AST extraction** of the exported `metadata` object literal (`extractNamedObjectLiteralExport` — evaluates the literal with `Function(...)`, then falls back to a property-by-property AST reader resolving local string constants). Ports doing runtime reflection can ignore this dual path but must honor the same metadata keys and defaults.

### 3. Generator suite and outputs

`mercato generate` (also embedded in `mercato server dev` as an in-process watcher polling a structure checksum of all module roots + `src/modules.ts`) runs, in order (`packages/cli/src/mercato.ts:runGeneratorSuite`):

1. `generateEntityIds` → `entities.ids.generated.ts` (const `E`: `E.<module>.<entity_snake> = '<module>:<entity_snake>'`, from exported class names in the entities file, snake_cased), `entity-fields-registry.ts` (entity slug → field-name map, used by encryption).
2. `generateModuleRegistry` → `modules.generated.ts` (eager static imports; used by builds), `modules.runtime.generated.ts` (same shape, lazy `import()` handlers/components), `frontend-routes.generated.ts` / `backend-routes.generated.ts` / `api-routes.generated.ts` (flat manifests with `moduleId` + lazy `load()`), `subscribers.generated.ts` (legacy alias flat-mapping `modules[].subscribers`), `bootstrap-registrations.generated.ts` (`runBootstrapRegistrations()` — registers backend/frontend route manifests + plugin registrations), plus one generated file per generator extension and per module plugin. Also performs **UMES conflict detection** (duplicate component-override targets, interceptor conflicts, feature-gated extensions referencing undeclared features) — warnings to stderr, errors abort generation.
3. `generateModuleRegistryApp` → `modules.app.generated.ts` (module array **without** `apis`/`cli` — the shape `bootstrap.ts` consumes; page components eagerly imported), `bootstrap-modules.generated.ts` (legacy alias), `enabled-module-ids.generated.ts`.
4. `generateModuleRegistryCli` → `modules.cli.generated.ts` (excludes routes/APIs/widget components — loadable without Next.js; includes cli, subscribers, workers, setup, features, translations, entityExtensions, customFieldSets, vector config), `cli-modules.generated.ts` (legacy alias).
5. `generateModuleEntities` → `entities.generated.ts` (all entity classes flattened, each tagged via `enhanceEntities(namespace, moduleId)`).
6. `generateModuleDi` → `di.generated.ts` (`export const diRegistrars = ([D_auth_0.register, ...]).filter(Boolean)`; app `di.ts` preferred over package `di.ts` per module).
7. `generateModulePackageSources` → `module-package-sources.css` (Tailwind `@source` globs for non-core packages).
8. `generateOpenApi` → OpenAPI documents from route `openApi` exports.

Every output is written with a content checksum + structure checksum sidecar (`*.checksum`) so unchanged files aren't rewritten. After generation the CLI best-effort purges the tenant structural cache (`configs cache structural --all-tenants`).

Module dependency validation: for every module whose `index.ts` `metadata.requires` lists ids not in the enabled set, the generator prints `Module "<id>" requires: <missing>` and **`process.exit(1)`**.

Third-party module guard: entries with `from` outside `@app`/`@open-mercato/*` must have an existing `modules/<id>` subtree, otherwise generation throws (`verifyThirdPartyModuleShape`).

### 4. API route discovery (three shapes)

Under `api/` a module may use (all can coexist; discovery order below fixes tie-breaking):

1. **Route files** (canonical): `api/<segments...>/route.ts` exporting HTTP-method functions (`GET/POST/PUT/PATCH/DELETE`, detected by regex on the source: `export async function GET`, `export const GET`, re-export lists, destructured exports), plus `export const metadata: Partial<Record<Method, {requireAuth?, requireRoles?, requireFeatures?, rateLimit?}>>` and (required by convention) `export const openApi`. Route path = `/<moduleId>/<segments>` (URL-mounted under `/api` by the catch-all). `metadata.path` (string) can override the derived path. Files with zero exported methods are skipped; files with methods but no metadata log `[generate] ⚠ Route file exports handlers but no metadata — auth will default to required: <file>`.
2. **Plain files**: `api/<name>.ts` (not `route.*`, not tests, not inside method dirs) — same handling, path `/<moduleId>/<segments>/<name>`.
3. **Legacy per-method dirs**: `api/get/foo.ts`, `api/post/foo.ts` etc. — handler is `default ?? <METHOD> ?? handler` export; manifest kind `'legacy'`; a single-method metadata object is normalized to `{ [method]: metadata }` at dispatch.

Dynamic segments use Next.js bracket syntax in file/dir names: `[id]`, `[...rest]`, `[[...rest]]`.

The API manifest entry shape (`ApiRouteManifestEntry`): `{ moduleId, kind: 'route-file'|'legacy', path, methods: HttpMethod[], method?, load: () => Promise<module> }`.

### 5. Generator extensions (built-in) — convention file → generated registry

| Convention file (module root unless noted) | Output | Registry consumed by |
|---|---|---|
| `search.ts` | `search.generated.ts` | search service DI |
| `notifications.ts`, `notifications.client.ts`, `notifications.handlers.ts` | `notification-*.generated.ts` | notification type/handler registries |
| `message-types.ts`, `message-objects.ts` | `message-*.generated.ts` | messages module registries |
| `ai-tools.ts`, `ai-agents.ts` | `ai-*.generated.ts` | ai-assistant |
| `events.ts` | `events.generated.ts` (`eventModuleConfigs`, `allEvents`) | `registerEventModuleConfigs` |
| `analytics.ts` | `analytics.generated.ts` | analytics registry |
| `translations.ts` | `translations-fields.generated.ts` (side-effect registration) | translatable-fields registry |
| `data/enrichers.ts` | `enrichers.generated.ts` | CRUD response enrichers |
| `api/interceptors.ts` (exports `interceptors: ApiInterceptor[]`) | `interceptors.generated.ts` (`interceptorEntries: {moduleId, interceptors}[]`) | CRUD route interception |
| `widgets/components.ts` | `component-overrides.generated.ts` | component override registry |
| `inbox-actions.ts` | `inbox-actions.generated.ts` | inbox_ops |
| `data/guards.ts` | `guards.generated.ts` | CRUD mutation guards |
| `commands/interceptors.ts` | `command-interceptors.generated.ts` | command bus |
| `frontend/middleware.ts`, `backend/middleware.ts` | `frontend-middleware.generated.ts`, `backend-middleware.generated.ts` | page middleware |
| `widgets/dashboard/**` , `widgets/injection/**` | `dashboard-widgets.generated.ts`, `injection-widgets.generated.ts`, `injection-tables.generated.ts` | widget registries |
| `workflows.ts` | `workflows.generated.ts` (`allCodeWorkflows`) | `registerCodeWorkflows` |
| `generators.ts` (module-contributed) | plugin-defined `outputFileName` | plugin-defined; may hook into `bootstrap-registrations.generated.ts` |

`ApiInterceptor` (shared/lib/crud/api-interceptor.ts): `{ id, targetRoute, methods: Method[], priority?, features?, timeoutMs?, before?(request, ctx) => {ok, body?, query?, headers?, message?, statusCode?, metadata?}, after?(request, response, ctx) => {merge?, replace?} }`.

### 6. Runtime bootstrap and registries

`apps/mercato/src/bootstrap.ts` (imported by `layout.tsx` and the API catch-all):

1. `applyModuleOverridesFromEnabledModules(enabledModules)` — dispatches `entry.overrides.<domain>` maps to per-domain appliers (must run **before** any registry first-load).
2. Side-effect registrations: `registerEventModuleConfigs`, `registerMessageTypes(…, {replace:true})`, `registerMessageObjectTypes`, `registerCodeWorkflows`, `runBootstrapRegistrations()` (route manifests + plugin bootstrap hooks).
3. `createBootstrap({modules, entities, diRegistrars, entityIds, entityFieldsRegistry, ...})` returns `bootstrap()` which (idempotent per process; always re-runs in dev for HMR) registers, in order: ORM entities → DI registrars → modules (into globalThis registry, with module-level overrides applied and i18n dictionary cache invalidated) → integrations/bundles → entity IDs → entity fields → search configs → analytics configs → enrichers → interceptors → component overrides (with overrides applied) → mutation guards → command interceptors → notification handlers → (async, fire-and-forget) UI widget registries + enabled module ids.

All cross-module registries are stored on `globalThis` under `__openMercato*__` keys to survive bundler module duplication (tsx/esbuild loading the same file twice). A port with a sane module system just uses process singletons.

Route manifest registration (`registerApiRouteManifests` / `registerBackendRouteManifests` / `registerFrontendRouteManifests`) applies composed overrides then stores a **specificity-sorted** copy (see matching rules in Behavioral details).

### 7. Request-scoped DI (`createRequestContainer`)

`packages/shared/src/lib/di/container.ts`. Per request/CLI-command/worker-job:

1. Get the singleton MikroORM instance (`getOrm()`), fork the EM: `em = baseEm.fork({ clear: true, freshEventManager: true, useContext: true })`.
2. `createContainer({ injectionMode: InjectionMode.CLASSIC })` (Awilix; CLASSIC = parameter-name-based injection).
3. Core registrations: `em` (value), `queryEngine` (`BasicQueryEngine(em, undefined, () => tenantEncryptionService|null)`), `dataEngine` (`DefaultDataEngine(em, container)`), `commandRegistry`, `commandBus`, `crudMutationGuardService` (scoped optimistic-lock guard reading the global reader store; `OM_OPTIMISTIC_LOCK=off` short-circuits).
4. Run every module DI registrar `reg(container)` in enabled-module order — **exceptions are swallowed** (`try { reg?.(container) } catch {}`). Later registrations replace earlier ones (Awilix replace semantics) — this is the module-override mechanism for services.
5. Core bootstrap `@open-mercato/core/bootstrap` `bootstrap(container)` (skipped if `eventBus` already registered): registers `cache` (process singleton, `OM_CACHE_SINGLETON` escape hatch), `eventBus` (`createEventBus` with `queueStrategy: 'async'` when `QUEUE_STRATEGY`/`EVENTS_STRATEGY` is `async|redis`, else `'local'`; falls back to local then to a no-op bus), sets the global event bus, auto-registers all module subscribers on the bus and `sync: true` subscribers into the sync-subscriber store, registers `kmsService` + `tenantEncryptionService` (+ MikroORM encryption subscriber when enabled and KMS healthy), `rateLimiterService` (process singleton), search module. Optional process-scoped replay cache behind `OM_BOOTSTRAP_CACHE` (default OFF) for keys `cache, eventBus, kmsService, tenantEncryptionService, rateLimiterService, searchModuleConfigs, searchIndexer`.
6. App-level `@/di` `register(container)` (last-chance override; awaited if it returns a promise; errors swallowed).
7. `applyDiOverridesToContainer` — `entry.overrides.di` map: `null` unregisters (registers `asValue(undefined)`), a `{register(container,key)}` definition self-registers, any other value is `asValue`-wrapped.
8. Re-register tenant encryption subscriber on the fresh EM when encryption enabled (result of `isEnabled()` cached on globalThis for the process).

Registrar registration itself (`registerDiRegistrars`) resets the bootstrap cache (HMR safety).

### 8. Setup lifecycle (`mercato init` / tenant creation)

`ModuleSetupConfig` (shared/modules/setup.ts):

```ts
export type ModuleSetupConfig = {
  onTenantCreated?: (ctx: { tenantId; organizationId; em }) => Promise<void>   // structural defaults; idempotent; always runs
  seedDefaults?:    (ctx: { tenantId; organizationId; em; container }) => Promise<void> // reference data; always runs during init
  seedExamples?:    (ctx: { tenantId; organizationId; em; container }) => Promise<void> // demo data; skipped with --no-examples
  defaultRoleFeatures?:        { superadmin?: string[]; admin?: string[]; employee?: string[]; [customRole: string]: string[] | undefined }
  defaultCustomerRoleFeatures?: { portal_admin?: string[]; buyer?: string[]; viewer?: string[]; [slug: string]: string[] | undefined }
}
```

`setupInitialTenant(em, options)` (`packages/core/src/modules/auth/lib/setup-app.ts`):
- Default roles `['employee','admin','superadmin']`; primary user default role `['superadmin']`.
- If a user with the primary email exists: reuse their tenant/org, ensure roles + user-role links (unless `failIfUserExists`).
- Else: create `Tenant` (`name: '<orgName> Tenant'`) and `Organization` (depth 0, empty hierarchy arrays), optionally create tenant DEK via KMS, ensure roles, seed `EncryptionMap` rows from `getDefaultEncryptionMaps(modules)` (which **throws on duplicate `entityId` across modules**), then create the primary user plus (unless `includeDerivedUsers: false`) derived `admin@acme.com` / `employee@acme.com` users (emails overridable via `OM_INIT_ADMIN_EMAIL`/`OM_INIT_EMPLOYEE_EMAIL`, passwords `OM_INIT_ADMIN_PASSWORD`/`OM_INIT_EMPLOYEE_PASSWORD`, default `secret`; bcrypt cost 10).
- `rebuildHierarchyForTenant` (skip if reusing user).
- `ensureDefaultRoleAcls(em, tenantId, modules)`: concatenates `setup.defaultRoleFeatures` across **all enabled modules in list order** into per-role feature lists; upserts `RoleAcl` per role with **set-union merge** of features (never removes); superadmin ACL gets `isSuperAdmin: true`; logs `✅ Seeded default role features {...}`. Custom-role keys are collected too, but only applied if the role already exists.
- Demo superadmin (`superadmin@acme.com`) deactivated when `SELF_SERVICE_ONBOARDING_ENABLED === 'true'` (unless init flow keeps it under DEMO_MODE).
- Finally calls each module's `setup.onTenantCreated({ em, tenantId, organizationId })` in enabled-module order.

`mercato init` then (in order): seeds feature toggles, `auth seed-roles`, optional entities reinstall, encryption defaults (`TENANT_DATA_ENCRYPTION`, default on), then loops `allModules` **in enabled order** calling `setup.seedDefaults(ctx)` (ctx has a fresh request container + em), then `ensureCustomRoleAcls` (second pass — custom roles may have been created by seedDefaults), then `setup.seedExamples(ctx)` unless `--no-examples`, then dashboard/analytics widget seeding. Note: despite doc comments saying "dependency order (based on ModuleInfo.requires)", the code iterates plain `enabledModules` order — `requires` is only *validated*, not topologically sorted.

### 9. Module enable/disable & overrides (`entry.overrides`)

Fifteen domains (`packages/shared/src/modules/overrides.ts`): `ai, routes, events, workers, widgets, notifications, interceptors, commandInterceptors, enrichers, guards, cli, setup, acl, di, encryption`. Semantics: **`null` disables, a definition replaces**. Precedence (highest first): programmatic `apply*Overrides()` calls → `modules.ts` inline → file-based (where applicable) → module base registration.

Key maps and their keys:
- `routes.api`: `'METHOD /api/path'` (method case-insensitive) → `null` | `{ handler: ApiHandler, metadata? }`. Applied inside `registerApiRouteManifests` via `applyApiOverridesToManifests` — a disable removes the method from `methods` (dropping the entry when empty); a replacement rewrites `load()`. Overrides applied *after* manifests are registered do not retro-apply.
- `routes.pages`: `'/backend/...'` or `'/frontend/...'` → null/definition; applied in `registerBackend/FrontendRouteManifests`.
- `events.subscribers` keyed by subscriber `id`; `workers` by worker `id`; `cli` by `command`; `acl.features` by feature id; `encryption.maps` by `entityId`; `di` by cradle key. These five plus `setup` are applied by `applyModuleOverridesToModules(modules)` inside `registerModules`/`registerCliModules` (stale override keys that match nothing log warnings).
- `setup`: `{ defaultRoleFeatures?, defaultCustomerRoleFeatures?, seedDefaults?: false, seedExamples?: false, onTenantCreated?: false }` — `false` disables the hook; feature maps replace per-role lists.
- `widgets.injection/components/dashboard`, `notifications.types/handlers`, `interceptors`, `commandInterceptors`, `enrichers`, `guards` — applied to their generated entry arrays during `createBootstrap`.

Official modules: `official-modules.json` (+ gitignored `official-modules.local.json`) lists `activated` module ids resolved against a git submodule `external/official-modules`; `yarn official-modules add|remove|set|sync` mutates the config and regenerates `apps/mercato/src/official-modules.generated.ts` (a *versioned* generated file — deliberately in `src/`, not `.mercato/generated/`, so it survives `clean-generated`).

## Public contracts

### `Module` (the composed unit — `packages/shared/src/modules/registry.ts`)

```ts
export type Module = {
  id: string
  info?: ModuleInfo                       // from index.ts `metadata`
  backendRoutes?: ModuleRoute[]
  frontendRoutes?: ModuleRoute[]
  apis?: ModuleApi[]                      // route-file or legacy shape
  cli?: ModuleCli[]                       // { command, run(argv) }
  translations?: Record<string, Record<string, string>>  // locale -> key -> text
  features?: Array<{ id: string; title: string; module: string }>  // from acl.ts
  subscribers?: ModuleSubscriber[]        // { id, event, persistent?, sync?, priority?, handler }
  workers?: ModuleWorker[]                // { id, queue, concurrency, handler }
  entityExtensions?: EntityExtension[]    // from data/extensions.ts
  customFieldSets?: CustomFieldSet[]      // data/fields.ts + ce.ts entities-with-fields
  customEntities?: Array<{ id; label?; description? }>  // from ce.ts
  dashboardWidgets?: ModuleDashboardWidgetEntry[]
  injectionWidgets?: ModuleInjectionWidgetEntry[]
  injectionTable?: ModuleInjectionTable
  vector?: VectorModuleConfig             // from vector.ts
  setup?: ModuleSetupConfig               // from setup.ts
  defaultEncryptionMaps?: ModuleEncryptionMap[]  // from encryption.ts
  integrations?: IntegrationDefinition[]; bundles?: IntegrationBundle[]  // from integration.ts
}

export type ModuleInfo = {
  name?; title?; version?; description?; author?; license?; homepage?; copyright?: string
  requires?: string[]      // hard deps — validated at generate time
  ejectable?: boolean      // may be copied into app src/modules for customization
}
```

Real example (`packages/core/src/modules/auth/index.ts`):

```ts
import './commands/users'
import './commands/roles'
export const metadata: ModuleInfo = {
  name: 'auth',
  title: 'Authentication & Accounts',
  version: '0.1.0',
  description: 'User accounts, sessions, roles and password resets.',
  author: 'Open Mercato Team',
  license: 'Proprietary',
}
export { features } from './acl'
```

### ACL features (`acl.ts`) — exact example from auth

```ts
export const features = [
  { id: 'auth.users.list', title: 'List users', module: 'auth' },
  { id: 'auth.users.create', title: 'Create users', module: 'auth' },
  { id: 'auth.users.edit', title: 'Edit users', module: 'auth' },
  { id: 'auth.users.delete', title: 'Delete users', module: 'auth' },
  { id: 'auth.roles.list', title: 'List roles', module: 'auth' },
  { id: 'auth.roles.manage', title: 'Manage roles', module: 'auth' },
  { id: 'auth.acl.manage', title: 'Manage ACLs', module: 'auth' },
  { id: 'auth.sidebar.manage', title: 'Manage sidebar presets', module: 'auth' },
]
export default features
```

Feature IDs are `<module>.<area>.<action>`; role feature lists support wildcards like `'auth.*'` (see auth `setup.ts`: `defaultRoleFeatures: { admin: ['auth.*'] }`).

### API route metadata & dispatch (byte-relevant)

Per-method metadata shape recognized by both generator and dispatcher:

```ts
type MethodMetadata = {
  requireAuth?: boolean          // default: TRUE (absence of metadata means auth required)
  requireRoles?: string[]        // DEPRECATED and NOT enforced at runtime (warn-only)
  requireFeatures?: string[]     // enforced via RbacService.userHasAllFeatures
  rateLimit?: { points: number; duration: number; blockDuration?: number; keyPrefix?: string }
}
```

Dispatcher behavior (`apps/mercato/src/app/api/[...slug]/route.ts`), in order:
1. Match path against sorted manifests; no match → `404 {"error": "<t('api.errors.notFound','Not Found')>"}`.
2. Load module, pick handler (`route-file`: `module[METHOD]`; `legacy`: `default ?? module[METHOD] ?? module.handler`); missing → 404.
3. Resolve auth (JWT cookie/header). `requireAuth !== false` and no auth → `401 {"error":"Unauthorized"}`; if the presented token was invalid, the 401 response also clears `auth_token` and `session_token` cookies.
4. Tenant parameter pollution guard: every distinct `tenantId` candidate (all repeated query params + JSON/form body field) that differs from the actor's tenant is checked via `enforceTenantSelection`; a CrudHttpError propagates its status/body (typically 403).
5. `requireFeatures` non-empty: no auth → 401; else `rbac.userHasAllFeatures(auth.sub, requiredFeatures, {tenantId, organizationId})`; failure → `403 {"error":"Forbidden","requiredFeatures":[...]}` (+ detailed console.warn).
6. `rateLimit` present: consult process-singleton RateLimiterService by client IP → 429-style error response from `checkRateLimit` when exceeded.
7. Invoke handler with `ctx = { params, auth }` inside `runWithCacheTenant(auth?.tenantId ?? null, ...)`.
8. Application lifecycle events emitted best-effort at each stage: `requestReceived, requestNotFound, requestAuthResolved, requestAuthorizationDenied, requestRateLimited, requestCompleted, requestFailed` (payload includes `requestId` from `x-request-id` header or random UUID, `method`, `pathname`, `durationMs`, `status`, `userId`, `tenantId`).

### Route pattern matching (`matchRoutePattern`)

- Patterns and paths normalized to leading `/`, no trailing `/`.
- Segment types: literal (compared **case-insensitively**), `[param]` (single segment → `params[param] = seg`), `[...param]` (catch-all, requires ≥1 remaining segment → `params[param] = string[]`), `[[...param]]` (optional catch-all, matches 0+ → `string[]`, possibly empty).
- Returns a params record or `undefined`; extra URL segments beyond the pattern → no match.

Specificity sort (`sortRoutesBySpecificity`): compare segment-by-segment with rank literal=0 < dynamic=1 < catch-all=2 (missing segment = −1); a stable sort of the manifest array so `/customers/export` beats `/customers/[id]`. First match in sorted order wins.

### Subscribers / workers

```ts
// subscribers/<name>.ts
export const metadata = { event: 'customers.person.created', persistent: true, sync?: false, priority?: 0, id?: 'custom-id' }
export default async function handle(payload, ctx) { ... }
// default id: '<moduleId>:<subdirs>:<basename>' joined with ':'

// workers/<name>.ts
export const metadata = { queue: 'sync_excel', concurrency: 2, id?: 'custom-id' }
export default async function run(job, ctx) { ... }
// default id: '<moduleId>:workers:<subdirs>:<basename>'; default concurrency: 1; NO queue => file skipped
```

Handlers are wrapped in `createLazyModuleSubscriber/Worker` — module is imported on first invocation; a non-function default export throws `[registry] Invalid subscriber|worker module "<id>" (missing default export handler)`.

### Entity extensions (`data/extensions.ts`) — exact example from customers

```ts
const entityExtensions: EntityExtension[] = [
  {
    base: 'customers:customer_interaction',
    extension: 'communication_channels:message_channel_link',
    join: { baseKey: 'external_message_id', extensionKey: 'id' },
    cardinality: 'many-to-one',
    description: 'Links an email CustomerInteraction to the MessageChannelLink that tracks its external channel metadata',
  },
]
export const extensions = entityExtensions
export default entityExtensions
```

`EntityExtension = { base: '<module>:<entity>', extension: '<module>:<entity>', join: { baseKey, extensionKey }, cardinality?: 'one-to-one'|'one-to-many'|'many-to-one'|'many-to-many', required?, description? }`. Modules never form direct ORM relations across module boundaries — plain UUID columns + declared extension links let the query/data engine traverse.

### `ModuleEntry` overrides — exact example from `apps/mercato/src/modules.ts`

```ts
{
  id: 'example',
  from: '@app',
  overrides: {
    routes: {
      api: {
        'GET /api/example/override-probe': {
          handler: async () => Response.json({ ok: true, source: 'modules.ts override', route: 'example.override-probe' }),
          metadata: { requireAuth: false },
        },
      },
    },
  },
},
```

Full override-key catalog (from `moduleOverrideExamples` in the same file): `ai.agents['catalog.catalog_assistant']`, `ai.tools['inbox_ops_accept_action']`, `routes.api['DELETE /api/example/items']`, `routes.pages['/backend/example/reports']`, `events.subscribers['example.todo.audit']`, `workers['example:sync']`, `widgets.injection['example.sidebar']`, `widgets.components['page:/backend/example']`, `widgets.dashboard['example.kpi']`, `notifications.types['example.notice']`, `notifications.handlers['example.notice.toast']`, `interceptors['example.items.interceptor']`, `commandInterceptors['example.command.interceptor']`, `enrichers['example.items.enricher']`, `guards['example.backend.guard']`, `cli['example seed']`, `setup.seedExamples: false`, `acl.features['example.manage']`, `di.exampleService`, `encryption.maps['example:item']` — each `null` = disable.

### `di.ts` — exact example from auth

```ts
export function register(container: AppContainer) {
  container.register({ authService: asClass(AuthService).scoped() })
  if (isRbacDefaultCacheEnabled()) {   // OM_RBAC_DEFAULT_CACHE=on
    container.register({
      rbacService: asFunction((cradle: { em: EntityManager; cache?: CacheStrategy }) =>
        new RbacService(cradle.em, cradle.cache ?? createRbacFallbackCache())).scoped(),
    })
  } else {
    container.register({ rbacService: asClass(RbacService).scoped() })
  }
}
```

### Generated file inventory (`.mercato/generated/`, gitignored)

`modules.generated.ts`, `modules.runtime.generated.ts`, `modules.app.generated.ts`, `modules.cli.generated.ts`, `frontend-routes.generated.ts`, `backend-routes.generated.ts`, `api-routes.generated.ts`, `subscribers.generated.ts`, `bootstrap-registrations.generated.ts`, `enabled-module-ids.generated.ts`, `di.generated.ts`, `entities.generated.ts`, `entities.ids.generated.ts`, `entity-fields-registry.ts`, `search.generated.ts`, `events.generated.ts`, `analytics.generated.ts`, `enrichers.generated.ts`, `interceptors.generated.ts`, `component-overrides.generated.ts`, `guards.generated.ts`, `command-interceptors.generated.ts`, `notification-handlers.generated.ts` (+ types/client), `message-types.generated.ts`, `message-objects.generated.ts`, `dashboard-widgets.generated.ts`, `injection-widgets.generated.ts`, `injection-tables.generated.ts`, `translations-fields.generated.ts`, `workflows.generated.ts`, `frontend-middleware.generated.ts`, `backend-middleware.generated.ts`, `ai-tools.generated.ts`, `ai-agents.generated.ts`, `inbox-actions.generated.ts`, `module-package-sources.css`, legacy aliases (`bootstrap-modules`, `cli-modules`), each with a `.checksum` sidecar (`{content, structure}` hashes).

## Helpers to mirror

A port needs functional equivalents of (TS signatures as-is):

```ts
// Pattern matching / ordering — MUST be byte-compatible in behavior
matchRoutePattern(pattern: string, pathname: string): Record<string, string | string[]> | undefined
sortRoutesBySpecificity<T extends {pattern?: string; path?: string}>(routes: T[]): T[]
findApiRouteManifestMatch(routes, method: HttpMethod, pathname): { route; params } | undefined
findRouteManifestMatch(routes, pathname): { route; params } | undefined

// Registries (process singletons)
registerModules(modules: Module[]): void            // applies module-level overrides, invalidates i18n cache
getModules(): Module[]                              // throws '[Bootstrap] Modules not registered...' if unset
registerApiRouteManifests(routes: ApiRouteManifestEntry[]): void   // apply overrides, then specificity-sort
getApiRouteManifests(): ApiRouteManifestEntry[]     // [] when unregistered
registerCliModules(modules: Module[]): void; getCliModules(): Module[]  // [] when unregistered
registerDiRegistrars(regs: DiRegistrar[]): void; getDiRegistrars(): DiRegistrar[]  // throws if unset

// DI
type DiRegistrar = (container: AppContainer) => void
createRequestContainer(): Promise<AppContainer>     // full sequence in §7

// Overrides
applyModuleOverridesFromEnabledModules(modules: ModuleEntryWithOverrides[]): void
applyModuleOverridesToModules(modules: readonly Module[]): Module[]   // subscribers/workers/cli/features/encryption/setup
applyApiOverridesToManifests(routes, overrides: ApiRouteOverridesMap): routes
applyDiOverridesToContainer(container): void
composeApiRouteOverrides(): ApiRouteOverridesMap    // modules.ts tier + programmatic tier merged (programmatic wins)

// Lazy handler wrappers
createLazyModuleSubscriber(loadModule: () => Promise<unknown>, id: string): ModuleSubscriberHandler
createLazyModuleWorker(loadModule: () => Promise<unknown>, id: string): ModuleWorkerHandler

// Encryption defaults
getDefaultEncryptionMaps(modules: Module[]): ModuleEncryptionMap[]   // throws on duplicate entityId across modules

// Setup lifecycle
setupInitialTenant(em, options: SetupInitialTenantOptions): Promise<{ tenantId, organizationId, users, reusedExistingUser }>
ensureDefaultRoleAcls(em, tenantId, modules, { includeSuperadminRole? }): Promise<void>
ensureCustomRoleAcls(em, tenantId, modules?): Promise<void>
ensureRoles(em, { roleNames?, tenantId }): Promise<void>   // tenantId REQUIRED — global roles unsupported

// Bootstrap
createBootstrap(data: BootstrapData, options?): () => void
bootstrap(container): Promise<void>    // core/bootstrap.ts — cache/eventBus/encryption/ratelimit/search

// Resolver / discovery (generate-time; a port may substitute reflection)
createResolver(cwd?): PackageResolver  // loadEnabledModules(), getModulePaths(entry), getModuleImportBase(entry), getOutputDir()
scanModuleDir(roots: {appBase,pkgBase}, config: {folder, include, skipDirs?, sort?}): Array<{relPath, fromApp}>
resolveModuleFile(roots, imps, relativePath): { absolutePath, fromApp, importPath } | null
detectExportedHttpMethods(sourceFile): HttpMethod[]
extractNamedObjectLiteralExport(sourceFile, exportName): Record<string, unknown> | null
```

Utility semantics: `toVar(s)` = non-alphanumerics → `_` (identifier building); `toSnake(ClassName)` = camel→snake for entity ids; `calculateStructureChecksum(paths)` = sorted, deduped path list hashed with per-entry `dir|file:<path>:<mtimeMs>` plus recursive directory entries (drives the dev watcher's "did module structure change" decision).

## Behavioral details a port MUST replicate

1. **Module order = `enabledModules` order.** It determines: route manifest baseline order (before specificity sort, which is stable — equal-specificity routes keep module order), DI registrar execution order (later wins), translation merge order, seed hook order, ACL feature concatenation order. The core list at this commit is 40+ modules starting `dashboards, auth, directory, customers, perspectives, entities, configs, query_index, audit_logs, attachments, catalog, sales, api_keys, dictionaries, ...`.
2. **Auth defaults to required.** Absence of route metadata or `requireAuth` ⇒ 401 without auth. Only explicit `requireAuth: false` makes a route public.
3. **`requireRoles` is dead.** Never enforce it; log a one-shot-per-route warning and rely solely on `requireFeatures` (immutable feature ids from `acl.ts`).
4. **Status codes:** 404 unknown route or missing method handler; 401 missing/insufficient auth (`{"error":"Unauthorized"}` localized); 403 feature failure with body `{"error":"Forbidden","requiredFeatures":[...]}`; tenant-selection violations use the thrown CrudHttpError's status/body; rate limit responses come from the shared rate-limit helper. Invalid (not merely absent) auth tokens on a 401 also expire `auth_token` and `session_token` cookies.
5. **Tenant parameter pollution guard:** validate **every** distinct `tenantId` candidate — all repeated `?tenantId=` query values plus a body-level `tenantId` (JSON object root, urlencoded, multipart) for non-GET/HEAD/OPTIONS — against the actor's allowed tenants before invoking the handler. String candidates `'null'`/`'undefined'` (case-insensitive, trimmed) map to null/skip.
6. **Route matching:** literal segments case-insensitive; `[...param]` requires ≥1 segment, `[[...param]]` allows 0; specificity sort ranks literal < `[param]` < catch-all per segment left-to-right; first match wins.
7. **API path derivation:** `'/'+moduleId+'/'+fileSegments` (route dir name `route.ts` excluded); `metadata.path` overrides. Mounted under `/api` by the host app.
8. **Default subscriber id** `'<module>:<path>:<name>'`, **default worker id** `'<module>:workers:<path>:<name>'`, **default worker concurrency 1**, workers without `metadata.queue` silently dropped.
9. **App-over-package file override:** identical logical path in the app tree replaces the package file per artifact (page, route file, subscriber, worker, i18n locale (merge, app keys win), `di.ts`, `data/entities.override.ts`, any convention file).
10. **Entities file candidate order:** `data/entities.override` → `data/entities` → `db/entities` → `db/schema`, app root before package root.
11. **`ModuleInfo.requires` validation is fatal** at generate time (exit code 1 with the exact `Module "<id>" requires: ...` message), but does **not** reorder anything at runtime.
12. **Seed order caveat:** `seedDefaults`/`seedExamples`/`onTenantCreated` run in plain enabled-module order (not topological), each awaited sequentially; `ensureCustomRoleAcls` must run **after** seedDefaults as a second pass.
13. **Role ACL merge is additive:** existing `RoleAcl.featuresJson` is set-unioned with module defaults, never pruned; `isSuperAdmin` can only be turned on.
14. **Duplicate default encryption maps across modules throw**: `[registry] Duplicate default encryption map for "<entityId>" declared by "<a>" and "<b>"`.
15. **Override semantics:** `null` disables, value replaces; unknown override keys produce stale-override warnings, not errors; malformed `routes.api` keys warn `Skipping malformed routes.api key "<k>" — expected "METHOD /api/path"`; API-route overrides only take effect if applied before manifest registration.
16. **DI registrar errors are swallowed** (a broken module `di.ts` must not take down container creation); app `@/di` and core bootstrap failures likewise degrade gracefully (no-op event bus fallback, memory cache fallback, local queue fallback).
17. **Awilix CLASSIC injection mode**: services are constructed by matching constructor/function parameter names to cradle keys; `.scoped()` lifetimes for per-request services; re-registration replaces (module DI can override the core `crudMutationGuardService`, `rbacService`, etc.).
18. **Env vars in scope for this subsystem:** `QUEUE_STRATEGY`/`EVENTS_STRATEGY` (`async|redis` ⇒ async bus), `OM_ENABLE_STORAGE_S3`, `OM_ENABLE_ENTERPRISE_MODULES[_SSO|_SECURITY]`, `OM_BOOTSTRAP_CACHE` (default off), `OM_CACHE_SINGLETON` (default on), `OM_RBAC_DEFAULT_CACHE`, `OM_OPTIMISTIC_LOCK`, `TENANT_DATA_ENCRYPTION` (default yes), `SELF_SERVICE_ONBOARDING_ENABLED`, `DEMO_MODE`, `OM_INIT_SUPERADMIN_EMAIL/PASSWORD`, `OM_INIT_ADMIN_EMAIL/PASSWORD`, `OM_INIT_EMPLOYEE_EMAIL/PASSWORD`, `OM_INIT_FLOW`.
19. **Missing-metadata warning** at generate time (`auth will default to required`) is developer UX a port should preserve in its analogous step.
20. **`registerModules` invalidates the i18n dictionary cache** — translation changes ride module re-registration.

## Gotchas

- **Two module registries exist**: the *manifest* registries (`api-routes` etc., used by the HTTP dispatcher) and the *Module[]* registry (used by CLI, i18n, encryption, setup, subscribers). They are generated from the same scan but registered through different code paths (`runBootstrapRegistrations()` vs `createBootstrap()` vs `registerCliModules()` in the CLI dynamic loader). A port can unify them but must keep the same effective data at each consumer.
- **`modules.app.generated.ts` deliberately omits `apis` and `cli`**; the dispatcher reads `api-routes.generated.ts` instead, and the CLI reads `modules.cli.generated.ts` (which omits routes/APIs/UI so it loads without Next.js). Don't assume `getModules()` inside the web app can see API handlers.
- **globalThis-keyed singletons everywhere** (`__openMercatoModulesRegistry__`, `__openMercatoDiRegistrars__`, `__openMercatoBootstrapCache__`, rate limiter, cache): a workaround for double-loaded JS modules. In a port these are ordinary process singletons — but note HMR semantics (re-registration allowed, logs debug in development).
- **The `modules.ts` static evaluator is a mini-interpreter.** If a port keeps a config-file approach, prefer declarative config (JSON/YAML/env) over replicating TS AST evaluation; the *observable* contract is just "list of `{id, from, overrides}` in order, with env-conditional entries".
- **`enabledModules` order ≠ dependency order.** `requires` only gates generation. If module A's seed depends on B's seed, upstream relies on list order in `modules.ts`. Ports must preserve the shipped order, not sort alphabetically.
- **Route-file method detection is regex-based**, so unconventional export styles (computed exports) silently produce zero methods and the route disappears. Metadata extraction may run the file's top-level code via dynamic import (side effects at generate time!) with an AST fallback.
- **`index.ts` may have side effects** (e.g. auth's `import './commands/users'` registers command handlers at import time). Composition must import module index files, not merely read their metadata.
- **Legacy API metadata normalization**: a legacy per-method file's flat metadata object is wrapped to `{ [METHOD]: metadata }` at dispatch, but only when it doesn't already contain a method key. Route-file metadata is always keyed by method, and `extractMethodMetadata` falls back to treating the whole object as the method's metadata when the method key is absent — so flat `{ requireAuth: false }` on a route file applies to all its methods.
- **Page metadata is dual-sourced**: `page.meta.ts` beside the page wins over `metadata` exported from the page itself; backend pages allow runtime metadata (functions `visible`/`enabled`, ReactNode icons) via a runtime-import fallback in the manifest; frontend manifests require statically serializable metadata.
- **The `example` module in the app tree doubles as the override-surface integration test** (`GET /api/example/override-probe` replaced inline); keep it in mind when diffing route inventories against a port.
- **`official-modules.generated.ts` is a *versioned* generated file** living in `src/` on purpose; `.mercato/generated/**` is ephemeral and rebuilt by `mercato generate`. Ports should mirror that split: activation state is committed config, discovery output is build artifact.
- **UMES conflict detection is generate-time-fatal for errors** (duplicate component override priorities on the same target, interceptor conflicts). If a port skips codegen, it must run the equivalent validation at startup.
- **`createRequestContainer` forks the EM with a fresh event manager** specifically so per-request encryption subscribers don't accumulate globally; a port with per-request ORM sessions gets this for free, but must still register the tenant-encryption hook per session when enabled.
