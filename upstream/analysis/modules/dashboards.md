# Port contract — dashboards
> Upstream commit adc9da27759e357febe9ed8d4b7182040d127349 (2026-07-01). Source: packages/core/src/modules/dashboards/. Regenerate via om-analyze-module.

## Overview

The `dashboards` module provides a per-user configurable admin dashboard built from **module-provided widgets**. It stores three things: each user's personal widget **layout** (`dashboard_layouts`), the set of widgets made available **per role** (`dashboard_role_widgets`), and **per-user** overrides of that availability (`dashboard_user_widgets`). It exposes an HTTP surface the `DashboardScreen` client consumes to load/save layout and to fetch aggregated **widget data** (KPIs/charts) via a generic `entityType + metric + groupBy + dateRange + comparison` aggregation service driven by an **analytics registry**. The widget catalog itself and the aggregation entity configs are **contributed by other modules** (the 10 built-in analytics widgets are UI/TSX; the `sales:orders`, `customers:entities`, etc. analytics configs live in `sales`/`customers`/`catalog`). It has no declared events, no subscribers, no workers, no notifications, no custom-field sets.

Role in the graph: a **Tier-3 domain module** that depends on `auth` (RBAC, `User`/`Role`/`UserRole`) and `directory` (org-scope resolution). Its widget-DATA path additionally depends on `sales`/`customers`/`catalog` for analytics configs and seed data — **none of which are ported**, so in the port the analytics registry is empty and widget-data requests reject with `Invalid entity type` (see "Dependencies" + PARITY-TODO notes).

## Dependencies

| module id | why needed | must be ported first |
|---|---|---|
| auth | `getAuthFromRequest`/JWT, `rbacService` (`loadAcl`, `userHasAllFeatures`), `hasFeature`/`hasAllFeatures`, entities `User`, `Role`, `UserRole`; ACL features + `defaultRoleFeatures` seeding | yes (ported 🔍/✅) |
| directory | `resolveOrganizationScopeForRequest` (org-scope for widget-data queries: selectedId/filterIds/allowedIds) | yes (ported 🔍/✅) |
| shared (pkg) | DI container, encryption `findOneWithDecryption`/`findWithDecryption`, `CacheStrategy`, analytics-config aggregation (`getAnalyticsModuleConfigs`), CRUD interceptor runner, openapi types | yes (runtime) |
| sales | contributes analytics entity configs `sales:orders`, `sales:order_lines`, `sales:quotes` + `sales_orders`/`sales_order_lines` tables the widget-data SQL aggregates; analytics **seed** target | no — port renders empty widget data until present (**PARITY-TODO**) |
| customers | contributes `customers:entities`, `customers:deals` analytics configs + seed target | no — **PARITY-TODO** |
| catalog | analytics seed target (`catalog_products`, `catalog_product_variants`) | no — **PARITY-TODO** |
| entities/query_index | none directly; widget-data uses raw SQL against module tables | no |

Ordered port-first list: `auth` → `directory` → (dashboards). `sales`/`customers`/`catalog` only affect widget-DATA output and analytics seeding, not the layout/availability surface.

## HTTP routes

Base path derived from file location: `/api/dashboards/...`. All handlers manually call `getAuthFromRequest` and return `401 {"error":"Unauthorized"}` when absent; `metadata` also declares `requireAuth`/`requireFeatures` for the framework dispatcher (specs/05). RBAC feature checks inside handlers use `acl.isSuperAdmin || hasFeature(acl.features, ...)` → `403 {"error":"Forbidden"}`.

| METHOD | path | auth | requiredFeatures | rateLimit | source file |
|---|---|---|---|---|---|
| GET | /api/dashboards/layout | yes | dashboards.view | — | api/layout/route.ts |
| PUT | /api/dashboards/layout | yes | dashboards.configure | — | api/layout/route.ts |
| PATCH | /api/dashboards/layout/[itemId] | yes | dashboards.configure | — | api/layout/[itemId]/route.ts |
| POST | /api/dashboards/widgets/data | yes | analytics.view | — | api/widgets/data/route.ts |
| POST | /api/dashboards/widgets/data/batch | yes | analytics.view | — | api/widgets/data/batch/route.ts |
| GET | /api/dashboards/widgets/catalog | yes | dashboards.admin.assign-widgets | — | api/widgets/catalog.ts |
| GET | /api/dashboards/roles/widgets | yes | dashboards.admin.assign-widgets | — | api/roles/widgets/route.ts |
| PUT | /api/dashboards/roles/widgets | yes | dashboards.admin.assign-widgets | — | api/roles/widgets/route.ts |
| GET | /api/dashboards/users/widgets | yes | dashboards.admin.assign-widgets | — | api/users/widgets/route.ts |
| PUT | /api/dashboards/users/widgets | yes | dashboards.admin.assign-widgets | — | api/users/widgets/route.ts |

> NOTE: the layout item mutation is **PATCH** `/api/dashboards/layout/[itemId]` (not DELETE — the client removes a widget by re-sending the full list via PUT). Widget removal is a full-list PUT. There is no DELETE route in the module.

Scope for every layout/availability query is the tuple `(userId|roleId, tenantId, organizationId)` where `tenantId = auth.tenantId ?? null`, `organizationId = auth.orgId ?? null`.

---

### GET /api/dashboards/layout

The endpoint the dashboard page loads on mount. Loads-or-creates the caller's layout, filters it to currently-allowed widgets, renumbers `order`/`priority` densely, persists any change, and returns the layout **envelope** the client expects.

Request: no body. No query params read (an unused `url` is parsed).

Behavior:
1. Resolve scope `{userId: auth.sub, tenantId: auth.tenantId ?? null, organizationId: auth.orgId ?? null}`.
2. `acl = rbac.loadAcl(userId, {tenantId, organizationId})`.
3. `widgets = loadAllWidgets()`; `allowedIds = resolveAllowedWidgetIds(em, ctx, widgets)` (see "Widget availability").
4. `allowedWidgets = widgets.filter(w => allowedIds.includes(w.metadata.id))`.
5. Load existing `DashboardLayout` for scope (`deleted_at IS NULL`).
   - **If none:** build default items from `allowedWidgets.filter(w => w.metadata.defaultEnabled)`, each `{ id: randomUUID(), widgetId, order: idx, priority: idx, size: defaultSize ?? 'md', settings: defaultSettings ?? undefined }`; create + persist a new row; `hasChanged=true`.
   - **If exists:** `normalizeLayoutItems` (dedupe by id, coerce order/priority, sort by `order ?? priority ?? 0`, re-index densely), then filter items whose `widgetId ∉ allowedIds`, re-index; `hasChanged` if items were dropped or ids/length differ.
6. If `hasChanged`, `em.flush()`.
7. `canConfigure = acl.isSuperAdmin || hasFeature(acl.features, 'dashboards.configure')`.
8. Load `User` (with decryption) for name/email → `userLabel = name || email || userId`.

> **DEFAULT-LAYOUT NOTE:** all 10 built-in widgets ship `defaultEnabled: false`. So for a first-time user the default layout is **empty** (`items: []`). A non-empty default only appears if a module registers a widget with `defaultEnabled: true`.

Response `200` (envelope — exact shape consumed by `DashboardScreen.tsx` `LayoutResponse`):
```json
{
  "layout": { "items": [
    { "id": "<uuid>", "widgetId": "dashboards.analytics.revenueKpi", "order": 0, "priority": 0, "size": "sm", "settings": { } }
  ] },
  "allowedWidgetIds": ["dashboards.analytics.revenueKpi", "..."],
  "canConfigure": true,
  "context": {
    "userId": "<uuid>", "tenantId": "<uuid|null>", "organizationId": "<uuid|null>",
    "userName": "Ada Admin", "userEmail": "admin@acme.test", "userLabel": "Ada Admin"
  },
  "widgets": [
    {
      "id": "dashboards.analytics.revenueKpi", "title": "Revenue", "description": "Total revenue with period comparison",
      "defaultSize": "sm", "defaultEnabled": false, "defaultSettings": { "dateRange": "this_month", "showComparison": true },
      "features": ["analytics.view", "sales.orders.view"], "moduleId": "dashboards",
      "icon": "dollar-sign", "loaderKey": "<registry key>", "supportsRefresh": true
    }
  ]
}
```
Field notes: layout item `settings` may be any JSON or omitted; `context.userName`/`userEmail` nullable, `userLabel` always a string; `widgets[].description`/`icon`/`defaultSettings` nullable; `defaultSize` one of `sm|md|lg`.

Errors: `401 {"error":"Unauthorized"}`.

### PUT /api/dashboards/layout

Persists the full layout for the caller. Request body = `dashboardLayoutSchema`:

| field | type | required | notes |
|---|---|---|---|
| items | array<LayoutItem> | yes | full replacement list |

LayoutItem (`dashboardLayoutItemSchema`): `id` uuid (req), `widgetId` string min 1 (req), `order` int ≥0 (req), `priority` int ≥0 (opt), `size` enum `sm|md|lg` (opt), `settings` unknown (opt).

Behavior: require `configure` (else 403). Load `widgets`/`allowedIds`. Map each incoming item → `{id, widgetId, order: index, priority: index, size: size ?? 'md', settings}`, **drop** items whose `widgetId ∉ allowedIds`. If duplicate `id` after filtering → `400 {"error":"Layout item IDs must be unique"}`. Upsert the `DashboardLayout` row (`layoutJson = sanitized`), `flush()`.

Response `200`: `{"ok": true}`.
Errors: `400 {"error":"Invalid JSON body"}` (unparseable); `400 {"error":"Invalid layout payload","issues":[...]}` (zod); `400 {"error":"Layout item IDs must be unique"}`; `401 Unauthorized`; `403 {"error":"Forbidden"}`.

### PATCH /api/dashboards/layout/[itemId]

Updates size/settings of one item inside the caller's layout. `itemId` = layout-item uuid (path). Body = `dashboardLayoutItemPatchSchema` with `id` omitted by client (`dashboardLayoutItemUpdateSchema`): `size` enum `sm|md|lg` (opt), `settings` unknown (opt). Handler injects `id: layoutItemId` before parsing.

Behavior: require `configure` (else 403). Load layout for scope; find item by `id`. Replace `layoutJson[idx] = {...current, size: patch.size ?? current.size ?? 'md', settings: patch.settings ?? current.settings}`; `flush()`.

Response `200`: `{"ok": true}`.
Errors: `400 {"error":"Missing layout item id"}`; `400 {"error":"Invalid JSON body"}`; `400 {"error":"Invalid payload"}` (non-object body); `400 {"error":"Invalid payload","issues":[...]}`; `401 Unauthorized`; `403 Forbidden`; `404 {"error":"Layout not found"}`; `404 {"error":"Layout item not found"}`.

### POST /api/dashboards/widgets/data

Generic aggregation endpoint powering KPI/chart widgets. Request = `widgetDataRequestSchema`:

| field | type | required | notes |
|---|---|---|---|
| entityType | string min 1 | yes | analytics entity id, e.g. `sales:orders` |
| metric.field | string min 1 | yes | must map in registry field mappings |
| metric.aggregate | enum `count\|sum\|avg\|min\|max` | yes | |
| groupBy.field | string min 1 | no | jsonb subfield `a.b` allowed if base is jsonb |
| groupBy.granularity | enum `day\|week\|month\|quarter\|year` | no | date bucketing |
| groupBy.limit | int 1..100 | no | |
| groupBy.resolveLabels | boolean | no | resolve uuid group keys → labels |
| filters[] | array of `{field(min1), operator, value?}` | no | operator enum `eq,neq,gt,gte,lt,lte,in,not_in,is_null,is_not_null` |
| dateRange.field | string min 1 | no | |
| dateRange.preset | enum (14 presets) | no | `today,yesterday,this_week,last_week,this_month,last_month,this_quarter,last_quarter,this_year,last_year,last_7_days,last_30_days,last_90_days` |
| comparison.type | enum `previous_period\|previous_year` | no | |

Behavior:
1. Parse body (zod) → `400 {"error":"Invalid request payload","issues":[...]}` on failure.
2. `entityFeatures = analyticsRegistry.getRequiredFeatures(entityType)`; if non-empty, `rbacService.userHasAllFeatures(auth.sub, entityFeatures, {tenantId, organizationId})` → else `403 {"error":"Forbidden"}`.
3. Require `tenantId` (`auth.tenantId`) else `400 {"error":"Tenant context is required"}`.
4. `scope = resolveOrganizationScopeForRequest(...)` → `organizationIds` = `[selectedId]` | `filterIds` | `undefined` (if `allowedIds===null`) | `[auth.orgId]` | `undefined`.
5. Run `runApiInterceptorsBefore({routePath:'dashboards/widgets/data', method:'POST', ...})`; if `!ok`, return `interceptorResult.body` with its status. (PARITY-TODO: interceptor framework — no-op passthrough until ported, per specs.)
6. `service.fetchWidgetData(request)` via `WidgetDataService` (validate against registry → 120s tag-cached → build SQL via `buildAggregationQuery` → execute → optional comparison period + optional label resolution).

Response `200` = `widgetDataResponseSchema`:
```json
{
  "value": 12345.67,
  "data": [ { "groupKey": "confirmed", "groupLabel": "Confirmed", "value": 42 } ],
  "comparison": { "value": 9000, "change": 37.2, "direction": "up" },
  "metadata": { "fetchedAt": "2026-07-07T10:00:00.000Z", "recordCount": 6 }
}
```
`value` number|null; each `data[]` = `{groupKey: unknown, groupLabel?: string, value: number|null}`; `comparison` optional `{value: number|null, change: number, direction: up|down|unchanged}`; `metadata.recordCount` = `data.length || (value!==null ? 1 : 0)`. Non-grouped requests return `data: []` and a scalar `value`.

Errors: `400 Invalid JSON body`; `400 Invalid request payload`; `400 Tenant context is required`; `400 {"error":"<WidgetDataValidationError.message>"}` (e.g. `Invalid entity type: sales:orders`, `Invalid metric field: ...`, `Invalid groupBy field: ...`, `Invalid date range preset: ...`); `401 Unauthorized`; `403 Forbidden`; `500 {"error":"An error occurred while processing your request"}`.

> **PARITY-TODO (widget data):** the analytics registry is populated from `getAnalyticsModuleConfigs()` — configs contributed by `sales`/`customers`/`catalog` (not ported). With an empty registry, `isValidEntityType` is false → every real request returns `400 Invalid entity type: <x>`. The port must implement the entity type + route surface and the registry mechanism; concrete entity configs arrive with those modules. Placeholder/empty behavior is acceptable and expected until then.

### POST /api/dashboards/widgets/data/batch

Resolves up to 50 widget-data requests with one auth/RBAC/org-scope/EM setup. Request:
```json
{ "requests": [ { "id": "w1", "request": { <widgetDataRequestSchema> } } ] }
```
`requests` min 1, max 50; each `{id: string min1, request: widgetDataRequestSchema}`.

Behavior: require tenant (400 if missing). Build container/registry/em/org-scope/cache/service once. `runWidgetDataBatch`: resolve per-entityType feature access once via union check (fallback to per-entity on union failure), then run each `fetchWidgetData` concurrently with per-item error isolation.

Response `200`:
```json
{ "results": [
  { "id": "w1", "ok": true, "data": { <widgetDataResponseSchema> } },
  { "id": "w2", "ok": false, "error": "Forbidden" }
] }
```
Per item: success `{id, ok:true, data}` | failure `{id, ok:false, error}` where `error` = `Forbidden` (feature-gated) or `WidgetDataValidationError.message` or `An error occurred while processing your request`.
Errors (whole batch): `400 Invalid JSON body`; `400 Invalid request payload`; `400 Tenant context is required`; `401 Unauthorized`; `500 An error occurred while processing your request`.

### GET /api/dashboards/widgets/catalog

Lists the full widget catalog (admin, for the visibility editor). Requires `dashboards.admin.assign-widgets`.
Response `200`: `{ "items": [ <same widget summary shape as layout GET `widgets[]`> ] }`.
Errors: `401 Unauthorized`; `403 Forbidden`.

### GET /api/dashboards/roles/widgets

Query: `roleId` uuid (required) → else `400 {"error":"roleId is required"}`; optional `tenantId`/`organizationId` (only honored for superadmin via `resolveWidgetAssignmentReadScope`). Requires `admin.assign-widgets`.
Behavior: resolve scope; verify role exists in tenant (`404 {"error":"Role not found"}`). Load all `DashboardRoleWidgets` for `roleId`, pick the **most specific** matching record (`pickBestRecord`: skip records whose tenant/org conflict with scope; score `tenantId?1:0 + organizationId?2:0`).
Response `200`:
```json
{ "widgetIds": ["dashboards.analytics.revenueKpi"], "hasCustom": true, "scope": { "tenantId": "<uuid|null>", "organizationId": "<uuid|null>" } }
```
Errors: `400 roleId is required`; `401 Unauthorized`; `403 Forbidden`; `404 Role not found`.

### PUT /api/dashboards/roles/widgets

Body = `roleWidgetSettingsSchema`: `roleId` uuid (req), `widgetIds` array<string min1> (req). Requires `admin.assign-widgets`. Scope = `(auth.tenantId ?? null, auth.orgId ?? null)`.
Behavior: filter `widgetIds` to known widget ids (`loadAllWidgets`); verify role in tenant (404). If resulting `widgetIds` empty → delete any existing record, return `{"ok":true,"widgetIds":[]}`. Else upsert `DashboardRoleWidgets` for `(roleId,tenant,org)`, `flush()`.
Response `200`: `{"ok": true, "widgetIds": [...]}`.
Errors: `400 Invalid JSON body`; `400 {"error":"Invalid payload","issues":[...]}`; `401 Unauthorized`; `403 Forbidden`; `404 Role not found`.

### GET /api/dashboards/users/widgets

Query: `userId` uuid (required) → `400 {"error":"userId is required"}`; optional `tenantId`/`organizationId` (superadmin only). Requires `admin.assign-widgets`.
Behavior: resolve scope; verify user exists in tenant (`404 {"error":"User not found"}`). Compute the target user's **effective** allowed widgets: `targetAcl = rbac.loadAcl(userId, scope)`, `effectiveWidgetIds = resolveAllowedWidgetIds(...)`. Load `DashboardUserWidgets` for `(userId,tenant,org)`.
Response `200`:
```json
{
  "mode": "inherit",
  "widgetIds": [],
  "hasCustom": false,
  "effectiveWidgetIds": ["dashboards.analytics.revenueKpi"],
  "scope": { "tenantId": "<uuid|null>", "organizationId": "<uuid|null>" }
}
```
`mode` = record.mode or `inherit`; `widgetIds` = record.widgetIdsJson only when `mode==='override'` else `[]`; `hasCustom` = record exists AND override.
Errors: `400 userId is required`; `401`; `403`; `404 User not found`.

### PUT /api/dashboards/users/widgets

Body = `userWidgetSettingsSchema`: `userId` uuid (req), `mode` enum `inherit|override` default `inherit`, `widgetIds` array<string min1> (req). Requires `admin.assign-widgets`. Scope = auth tenant/org.
Behavior: filter widgetIds to known ids; verify user in tenant (404). If `mode==='inherit'` → delete any record, return `{"ok":true,"mode":"inherit","widgetIds":[]}`. Else upsert with `mode='override'`, `flush()`.
Response `200`: `{"ok": true, "mode": "override", "widgetIds": [...]}`.
Errors: `400 Invalid JSON body`; `400 Invalid payload`; `401`; `403`; `404 User not found`.

## Entities

Byte-exact DDL from `migrations/Migration20251030150038.ts` (MikroORM-generated raw SQL). All three tables: uuid PK `id default gen_random_uuid()`, `tenant_id uuid null`, `organization_id uuid null`, `created_at timestamptz not null`, `updated_at timestamptz null`, `deleted_at timestamptz null` (soft delete), plus a unique constraint. `*_json` columns are `jsonb not null default '[]'`.

### dashboard_layouts
| column | pg type | nullable | default | notes |
|---|---|---|---|---|
| id | uuid | no | gen_random_uuid() | PK `dashboard_layouts_pkey` |
| user_id | uuid | no | — | |
| tenant_id | uuid | yes | — | |
| organization_id | uuid | yes | — | |
| layout_json | jsonb | no | '[]' | array of layout items `{id,widgetId,order,priority?,size?,settings?}` |
| created_at | timestamptz | no | — | set on create |
| updated_at | timestamptz | yes | — | set on update |
| deleted_at | timestamptz | yes | — | soft delete |

Unique: `dashboard_layouts_user_id_tenant_id_organization_id_unique (user_id, tenant_id, organization_id)`. No FKs declared (user_id/tenant_id/organization_id are loose uuids). Tenancy: tenant_id + organization_id (both nullable).

### dashboard_role_widgets
| column | pg type | nullable | default | notes |
|---|---|---|---|---|
| id | uuid | no | gen_random_uuid() | PK `dashboard_role_widgets_pkey` |
| role_id | uuid | no | — | |
| tenant_id | uuid | yes | — | |
| organization_id | uuid | yes | — | |
| widget_ids_json | jsonb | no | '[]' | array of widget id strings |
| created_at | timestamptz | no | — | |
| updated_at | timestamptz | yes | — | |
| deleted_at | timestamptz | yes | — | |

Unique: `dashboard_role_widgets_role_id_tenant_id_organization_id_unique (role_id, tenant_id, organization_id)`.

### dashboard_user_widgets
| column | pg type | nullable | default | notes |
|---|---|---|---|---|
| id | uuid | no | gen_random_uuid() | PK `dashboard_user_widgets_pkey` |
| user_id | uuid | no | — | |
| tenant_id | uuid | yes | — | |
| organization_id | uuid | yes | — | |
| mode | text | no | 'inherit' | `inherit` \| `override` |
| widget_ids_json | jsonb | no | '[]' | array of widget id strings |
| created_at | timestamptz | no | — | |
| updated_at | timestamptz | yes | — | |
| deleted_at | timestamptz | yes | — | |

Unique: `dashboard_user_widgets_user_id_tenant_id_organization_id_unique (user_id, tenant_id, organization_id)`.

> The migration only creates the three unique constraints; no additional secondary indexes. (MikroORM snapshot: `migrations/.snapshot-open-mercato.json`.)

## Widget availability (resolveAllowedWidgetIds)

`lib/access.ts` — the algorithm the layout GET, users/widgets GET, and PUT filtering all use. Given `ctx {userId, tenantId, organizationId, features, isSuperAdmin}` and the loaded widget list:
1. Load user override `DashboardUserWidgets(userId,tenant,org)`. If `mode==='override'` → `allowedByUser = widgetIds ∩ allWidgetIds`; if override list is **empty**, return `[]` immediately. If record absent or `mode==='inherit'` → `allowedByUser = null`.
2. Aggregate role settings: find the user's `UserRole`s (with decryption), load `DashboardRoleWidgets` for those role ids, keep the most-specific matching record per role (`specificity = tenant?1:0 + org?2:0`, filtering conflicting scopes), union their `widgetIdsJson ∩ allWidgetIds` → `allowedByRole`.
3. Base set: `allowedByUser` if set, else `allowedByRole` if non-empty, else **all** widget ids.
4. Final: keep widgets in the base set whose `metadata.features` the user satisfies (`isSuperAdmin` bypasses) via `hasAllFeatures(ctx.features, widget.features)`.

Net effect: no role/user config ⇒ all widgets whose feature requirements are met are allowed; a role config narrows to its list; a user override replaces role config; an empty override hides everything.

## Widget catalog shape

`lib/widgets.ts` `loadAllWidgets()` flattens each module's registry `dashboardWidgets` entries, lazily imports each, validates metadata, dedupes by `metadata.id` (first wins). Each loaded widget → `{metadata, moduleId, key}`. Metadata surfaced by the APIs: `id, title, description?, features[], defaultSize(sm|md|lg), defaultEnabled(bool), defaultSettings?, tags[], category, icon?, supportsRefresh(bool)`; API adds `moduleId`, `loaderKey (=key)`.

The 10 built-in analytics widgets (all `moduleId: 'dashboards'`, `category: 'analytics'`, `defaultEnabled: false`, `supportsRefresh: true`):

| id | title | defaultSize | icon | features |
|---|---|---|---|---|
| dashboards.analytics.revenueKpi | Revenue | sm | dollar-sign | analytics.view, sales.orders.view |
| dashboards.analytics.ordersKpi | Orders | sm | shopping-cart | analytics.view, sales.orders.view |
| dashboards.analytics.aovKpi | Average Order Value | sm | trending-up | analytics.view, sales.orders.view |
| dashboards.analytics.newCustomersKpi | Customer Growth | sm | user-plus | analytics.view, customers.people.view |
| dashboards.analytics.ordersByStatus | Orders by Status | sm | pie-chart | analytics.view, sales.orders.view |
| dashboards.analytics.revenueTrend | Revenue Trend | lg | line-chart | analytics.view, sales.orders.view |
| dashboards.analytics.salesByRegion | Sales by Region | md | map-pin | analytics.view, sales.orders.view |
| dashboards.analytics.pipelineSummary | Pipeline Summary | md | git-branch | analytics.view, customers.deals.view |
| dashboards.analytics.topCustomers | Top Customers | md | users | analytics.view, sales.orders.view, customers.people.view |
| dashboards.analytics.topProducts | Top Products | md | bar-chart-2 | analytics.view, sales.orders.view |

> **PARITY-TODO (widget catalog):** the widget bodies (`widget.client.tsx`) are React/UI → out of scope. But their **metadata** is load-bearing: it is what the layout GET / catalog GET return, and `resolveAnalyticsWidgetIds`/`defaultEnabled` seeding depend on it. The port must expose a **server-side widget catalog** carrying the metadata above (id/title/description/features/defaultSize/defaultEnabled/defaultSettings/category/icon/supportsRefresh + moduleId/loaderKey) so the APIs return a populated `widgets` array. `loaderKey` is a client-registry key with no backend meaning — the port may emit a stable synthetic key. Since all `defaultEnabled` are false, default layouts are empty and no widget renders until a role grants widgets AND the client bundle provides the loader — acceptable placeholder behavior for a backend-only port. `defaultSettings` per widget = `{dateRange: 'this_month', showComparison: true}`-style config (widget-specific; e.g. revenueKpi is that exact object).

## Analytics registry & aggregation (services)

`di.ts` registers `analyticsRegistry` (singleton) = `DefaultAnalyticsRegistry(getAnalyticsModuleConfigs())`. It maps `entityId → {entityConfig, fieldMappings, requiredFeatures?, labelResolvers?}`. `WidgetDataService.fetchWidgetData` validates against it, builds parameterized SQL via `lib/aggregations.buildAggregationQuery` (scoped by `tenant_id` + `organization_id = ANY(...)`, `deleted_at IS NULL`), executes raw SQL, applies date-range presets (`resolveDateRange`/`getPreviousPeriod` from `@open-mercato/ui/backend/date-range`), computes comparison % change/direction, optionally resolves uuid group keys to labels (with tenant encryption support). Cache: tag-based (`widget-data`, `widget-data:<entityType>`), 120s TTL. **All entity configs come from other modules** → empty in the port (PARITY-TODO).

## Custom entities & field sets

None. No `ce.ts`, `data/fields.ts`, or custom-field sets.

## Notifications

None. No `notifications.ts`.

## Events

Declared: **none** (`index.ts` exports only `metadata` + `features`; no `events.ts`, no `createModuleEvents`). Emitted: none (no `eventBus.emit*` in the module). Consumed: none (no `subscribers/`).

## Workers & queues

None. No `workers/`; the widget-data batch is synchronous request-time work.

## ACL features

From `acl.ts` (all `module: 'dashboards'`):

| feature id | title | used by |
|---|---|---|
| dashboards.view | View dashboard | GET /layout (requireFeatures) |
| dashboards.configure | Customize dashboard layout | PUT /layout, PATCH /layout/[itemId]; `canConfigure` flag |
| dashboards.admin.assign-widgets | Manage dashboard widget availability | GET/PUT roles/widgets, GET/PUT users/widgets, GET widgets/catalog |
| analytics.view | View analytics widgets | POST widgets/data (+ /batch); widget metadata `features` |

Note: widget-data entity gating also references cross-module features contributed by analytics configs (e.g. `sales.orders.view`, `customers.people.view`, `customers.deals.view`) — owned by those modules, not declared here.

## Setup & seeding

`setup.ts`:
- `defaultRoleFeatures`: `admin → ['dashboards.*', 'dashboards.admin.assign-widgets', 'analytics.view']`; `employee → ['dashboards.view', 'dashboards.configure', 'analytics.view']`.
- `onTenantCreated({em, tenantId, organizationId})` → `seedDashboardDefaultsForTenant(em, {tenantId, organizationId, logger:noop})`: for roles `['superadmin','admin','employee']`, upsert `DashboardRoleWidgets`; admin/superadmin get **all** widget ids, others get `defaultEnabled` widget ids (currently none). (`cli.ts` `seedDashboardDefaultsForTenant`.)
- `seedDefaults({em, tenantId, organizationId})` → `resolveAnalyticsWidgetIds()` (widgets with `category==='analytics'` or id starting `dashboards.analytics.`) then `appendWidgetsToRoles(em, {tenantId, organizationId, roleNames:['admin','employee'], widgetIds})` (append-only, transactional, falls back to org-null role record).
- No `seedExamples`, no `defaultCustomerRoleFeatures`.

Analytics **data** seed (`seed/analytics.ts`, invoked by CLI `seed-analytics`, not by tenant creation): generates fake `sales_orders`/`sales_order_lines`/`customers`/`catalog_products`/`customer_deals` rows (order numbers `SO-ANALYTICS-#####`). **PARITY-TODO** — depends on sales/customers/catalog entities; skip until those modules exist.

## DI services

| service name | role | consumed by |
|---|---|---|
| analyticsRegistry | singleton `DefaultAnalyticsRegistry(getAnalyticsModuleConfigs())`; entity validation + field/feature/label lookup | widgets/data(+batch) routes, WidgetDataService |
| em (shared) | MikroORM EntityManager (forked per request) | all routes |
| rbacService (auth) | `loadAcl`, `userHasAllFeatures` | all routes |
| cache (shared) | `CacheStrategy` for widget-data caching | widgets/data(+batch) |

## CLI commands

From `cli.ts`, command group `dashboards`:

| command | args | behavior |
|---|---|---|
| seed-defaults | --tenant <id> [--roles superadmin,admin,employee] [--widgets id1,id2] | upsert `DashboardRoleWidgets` per role (admin/superadmin→all widgets, others→defaultEnabled) |
| enable-analytics-widgets | --tenant <id> [--org <id>] [--roles admin,employee] | append analytics widget ids to roles (`appendWidgetsToRoles`) |
| seed-analytics | --tenant <id> --organization <id> [--months 6] [--ordersPerMonth 50] | generate fake analytics sales/customer/catalog data (**PARITY-TODO**: other-module deps) |
| debug-analytics | --tenant <id> [--organization <id>] | print raw SQL diagnostics over `sales_orders` (**PARITY-TODO**) |

## Configuration

No module-specific env vars or config keys. Cache TTLs are hardcoded: widget-data 120_000 ms, segment 86_400_000 ms. Batch max size = 50. Default widget size = `md`.

## Not ported (UI / out of scope)

- `widgets/dashboard/**/*.client.tsx`, `widget.ts`, `config.ts` — React widget bundles (metadata captured above for the server catalog).
- `components/WidgetVisibilityEditor.tsx` — admin UI.
- `i18n/*.json` — translation bundles.
- `packages/ui/src/backend/dashboard/DashboardScreen.tsx`, `widgetData.tsx`, `widgetRegistry`, `widgetDataBatcher` — client (referenced only to fix exact request/response shapes).
- Tests (`__tests__/`, `__integration__/`, `lib/__tests__/`) — behavior mirrored into the port's own tests.

## Porting checklist

- [ ] Migrations: raw-SQL EF migration creating `dashboard_layouts`, `dashboard_role_widgets`, `dashboard_user_widgets` byte-exact (3 unique constraints, jsonb `default '[]'`, `mode text default 'inherit'`, uuid PK `gen_random_uuid()`).
- [ ] Entities/models for the 3 tables (soft delete via `deleted_at`, tenant/org nullable).
- [ ] Server-side widget catalog carrying the 10 widgets' metadata (+ registry mechanism for other modules to contribute); `loadAllWidgets` equivalent.
- [ ] `resolveAllowedWidgetIds` availability algorithm (user override → role aggregation → all, then feature filter).
- [ ] Analytics registry + `WidgetDataService`/`buildAggregationQuery` skeleton (empty registry OK; `Invalid entity type` on real requests until sales/customers/catalog) — PARITY-TODO.
- [ ] Routes: GET/PUT `/layout`, PATCH `/layout/{itemId}`, POST `/widgets/data`, POST `/widgets/data/batch`, GET `/widgets/catalog`, GET/PUT `/roles/widgets`, GET/PUT `/users/widgets` — exact auth features, status codes, and JSON envelopes above.
- [ ] Layout GET default-layout computation (defaultEnabled widgets; empty today) + normalization/renumbering + persist-on-change + `context`/`canConfigure`/`widgets` envelope.
- [ ] Org-scope resolution wiring (`resolveOrganizationScopeForRequest` from directory) for widget-data.
- [ ] Interceptor `runApiInterceptorsBefore` hook on widgets/data — no-op passthrough until framework ported (PARITY-TODO).
- [ ] Declare surface (declare-now per specs/10): 4 ACL features; no notifications; no custom-field sets/custom entities; no declared events.
- [ ] Setup/seed: `defaultRoleFeatures`, `onTenantCreated` → seedDashboardDefaultsForTenant, `seedDefaults` → appendWidgetsToRoles.
- [ ] DI: `analyticsRegistry` singleton; reuse shared `em`/`rbacService`/`cache`.
- [ ] CLI: `seed-defaults`, `enable-analytics-widgets` (widget-availability); `seed-analytics`/`debug-analytics` PARITY-TODO.
- [ ] Tests (layout envelope, availability resolution, role/user widget upsert+delete, tenant isolation, widget-data validation/400s).
- [ ] Parity run (om-verify-parity dashboards <tech>).
