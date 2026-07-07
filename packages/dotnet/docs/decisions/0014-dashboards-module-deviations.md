# ADR 0014 — Dashboards module deviations

Status: accepted
Date: 2026-07-07
Context: port of upstream `packages/core/src/modules/dashboards` (pin adc9da2) into
`OpenMercato.Modules.Dashboards`. Contract: `upstream/analysis/modules/dashboards.md`.

Observable behavior (envelopes, status codes, RBAC gating) and the Postgres schema are byte-exact
with upstream. The deviations below are internal or dependency-driven and are marked
`// PARITY-TODO` in code where they will be revisited once the dependency modules land.

## 1. Empty analytics registry ⇒ widget-data always "Invalid entity type" (PARITY-TODO)

The analytics registry (`di.ts` `analyticsRegistry = DefaultAnalyticsRegistry(getAnalyticsModuleConfigs())`)
is populated by entity configs contributed by `sales`/`customers`/`catalog` — **none ported**. The
port ships `DefaultAnalyticsRegistry` with an **empty** config set, so `IsValidEntityType` is always
false and every real `POST /widgets/data` (and each `/batch` item) rejects with
`400 {"error":"Invalid entity type: <x>"}` — the exact upstream behavior when no analytics configs
are registered. The endpoint responds 200-shaped only once a domain module registers a config; the
registry mechanism (`AnalyticsModuleConfig`/`AnalyticsEntityConfig`) is in place for that.

`WidgetDataService.FetchWidgetData` implements the full `validateRequest` order (entity type → metric
field → aggregate → date-range preset → groupBy) and, past validation, ships the empty response
envelope (`value:null, data:[], metadata`). The SQL build/execute + comparison + label-resolution path
(`buildAggregationQuery`) is a PARITY-TODO placeholder — unreachable until field mappings exist.

## 2. Widget bodies are UI; the server ships a metadata-only catalog

Upstream widget bundles (`widget.client.tsx`) are React and out of scope. Their **metadata** is
load-bearing (it is what layout GET / catalog GET return), so `Lib/WidgetCatalog` ships the 10
built-in analytics widgets' metadata verbatim (id/title/description/features/defaultSize/
defaultEnabled/defaultSettings/category/icon/supportsRefresh + moduleId). `loaderKey` is a
client-registry key with no backend meaning; the port emits the exact upstream synthetic value
(`dashboards:<dir>:widget`). All 10 ship `defaultEnabled:false`, so a first-time user's default
layout is empty — faithful, and no widget renders in a backend-only port until the client bundle
provides the loader.

## 3. RBAC gating via the shared RequireFeatures endpoint filter

Upstream declares `requireFeatures` in route `metadata` (enforced by its dispatcher) AND repeats an
in-handler `acl.isSuperAdmin || hasFeature(...)` check. The port enforces both auth (401
`{"error":"Unauthorized"}`) and features via the shared `RequireFeatures` endpoint filter (reused
from auth, per the port brief). Two consequences vs. a literal handler-level check:
- The 403 body is `{"error":"Forbidden","requiredFeatures":[...]}` (the repo-wide convention) rather
  than bare `{"error":"Forbidden"}`. Status code and the `error` field match.
- For an authenticated caller **lacking** the feature who also sends an invalid body, the filter
  returns 403 before the handler's 400 (upstream would 400 first). This pathological ordering does
  not affect the normal authorized client (admin/employee hold the features).

## 4. Layout item mutation is PATCH, not DELETE

The port brief's deliverable list mentioned `DELETE /layout/{itemId}`, but upstream (and the
`DashboardScreen` client) use **PATCH** `/api/dashboards/layout/{itemId}` for size/settings; widget
removal is a full-list `PUT /layout`. There is no DELETE route in the module. The port implements
PATCH to match the contract and the client — following the authoritative contract over the brief's
list.

## 5. Seeding via CLI, not the boot seeder (tier ordering)

Upstream `setup.ts` wires `onTenantCreated → seedDashboardDefaultsForTenant` and
`seedDefaults → appendWidgetsToRoles`. Wiring these into `InitialTenantSeeder` (owned by Directory,
Tier-1) would make Tier-1 depend on Dashboards (Tier-3), inverting the dependency graph. Instead:
- The **ACL** side is automatic: Dashboards declares `DefaultRoleFeatures`, which the registry merges
  and `InitialTenantSeeder.EnsureDefaultRoleAcls` grants to admin/employee — so seeded users can load
  the dashboard (layout GET returns a valid envelope) with no dashboards-specific seed step.
- The **widget-availability** side (`dashboard_role_widgets` records) is offered via the ported CLI
  commands `seed-defaults` / `enable-analytics-widgets`. It is optional: with no role/user records the
  availability algorithm falls back to "all widgets, feature-filtered", which is a sensible default.

`seed-analytics` / `debug-analytics` CLI commands are omitted (PARITY-TODO — they generate/inspect
`sales_orders`/`customers`/`catalog_products` data owned by unported modules).

## 6. Org-scope resolution + interceptor runner are no-op passthroughs (PARITY-TODO)

`resolveOrganizationScopeForRequest` (directory util) and `runApiInterceptorsBefore` (CRUD interceptor
framework) are not ported. In widget-data they only shape the (unreachable) SQL scope; the port uses a
simplified scope and a no-op interceptor passthrough. No observable effect while the registry is empty.
