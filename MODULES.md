# 📊 Porting Tracker

Status of every upstream Open Mercato module/package across the target technologies. Module inventory sourced from [`upstream/analysis/07-shared-services.md`](upstream/analysis/07-shared-services.md), pinned at upstream commit `adc9da27759e357febe9ed8d4b7182040d127349` (2026-07-01, see [`upstream/UPSTREAM.md`](upstream/UPSTREAM.md)).

## Legend

| Status | Meaning |
|---|---|
| ⬜ | Not started |
| 🔍 | Analyzed — port contract produced via `om-analyze-module` |
| 🚧 | In progress — `om-port-module` running/partial |
| ✅ | Ported — implemented in the technology package |
| 🧪 | Parity-verified — `om-verify-parity` passed against the pinned commit |
| — | Not applicable (frontend-only / tooling, no backend port target) |

Update flow: statuses only move forward via the skills (`om-analyze-module` → 🔍, `om-port-module` → 🚧/✅, `om-verify-parity` → 🧪). A pin bump in `UPSTREAM.md` requires re-reviewing every ✅/🧪 module.

> 🧪 **.NET milestone.** `auth`, `directory` and `dashboards` are ported and working in `packages/dotnet` (105 tests pass). Together with the `packages/dotnet` CLI and the [`testbench/`](testbench/README.md), they power an end-to-end demo: log into a **real Open Mercato** UI and have login, the org switcher, and the dashboard shell served by the .NET port against a shared Postgres. See [`GETTING_STARTED.md`](GETTING_STARTED.md) and [`specs/11-testbench.md`](specs/11-testbench.md).

## Matrix

Tiers: **0** = runtime foundation (scaffold-level), **1** = platform base, **2** = cross-cutting services, **3** = domain modules, **4** = adapters & optional packages.

### Tier 0 — runtime foundation (built into each technology package scaffold)

These upstream packages are not ported as standalone modules; their behavior is baked into each `packages/<tech>/` runtime per [`specs/09-technology-package-standard.md`](specs/). Track them here because every module depends on them.

| Module | Purpose (short) | Tier | Python | .NET | Go |
|---|---|---|---|---|---|
| shared (pkg) | Cross-cutting helpers & type contracts (redis env resolution, webhooks signing, SSRF guards, i18n, CRUD errors, DI) | 0 | ⬜ | ⬜ | ⬜ |
| queue (pkg) | Multi-strategy job queue (local \| BullMQ/Redis), BullMQ-compatible jobs | 0 | ⬜ | ⬜ | ⬜ |
| events (pkg) | Event bus: subscribers, persistent events, worker | 0 | ⬜ | ⬜ | ⬜ |
| cache (pkg) | Multi-strategy cache with tag-based invalidation | 0 | ⬜ | ⬜ | ⬜ |
| cli (pkg) | `mercato` CLI host (module CLI discovery) | 0 | ⬜ | ⬜ | ⬜ |
| health_check (example) | Reference example module shipped with each scaffold — demonstrates route/migration/worker conventions | 0 | ✅ | ✅ | ✅ |

### Tier 1 — platform base (port first, in this order)

| Module | Purpose (short) | Tier | Python | .NET | Go |
|---|---|---|---|---|---|
| [directory](upstream/analysis/modules/directory.md) | Multi-tenant directory: tenants and organizations | 1 | 🔍 | ✅ | 🔍 |
| [auth](upstream/analysis/modules/auth.md) | User accounts, sessions, roles, password resets (JWT, RBAC features) | 1 | 🔍 | ✅ | 🔍 |
| api_keys | Access tokens for external API access | 1 | ⬜ | ⬜ | ⬜ |
| entities | User-defined entities, custom fields, dynamic records | 1 | ⬜ | ⬜ | ⬜ |
| query_index | Hybrid query layer with index maintenance | 1 | ⬜ | ⬜ | ⬜ |
| configs | Module settings storage (`module_configs`) + system status | 1 | ⬜ | ⬜ | ⬜ |
| feature_toggles | Global feature flags with tenant-level overrides | 1 | ⬜ | ⬜ | ⬜ |

> ⚠️ `directory` ↔ `auth` are mutually dependent — port them together as one unit.

### Tier 2 — cross-cutting services

| Module | Purpose (short) | Tier | Python | .NET | Go |
|---|---|---|---|---|---|
| translations | Entity translation storage + locale overlay | 2 | ⬜ | ⬜ | ⬜ |
| dictionaries | Org-scoped enumerations with appearance presets | 2 | ⬜ | ⬜ | ⬜ |
| notifications | In-app notifications with extensible types and actions | 2 | ⬜ | ⬜ | ⬜ |
| attachments | File attachments and media management (StorageDriver abstraction) | 2 | ⬜ | ⬜ | ⬜ |
| storage-s3 (pkg) | S3-compatible StorageDriver for attachments + admin APIs | 2 | ⬜ | ⬜ | ⬜ |
| audit_logs | User action/access tracking with undo/redo | 2 | ⬜ | ⬜ | ⬜ |
| progress | Server-side progress tracking for long-running ops | 2 | ⬜ | ⬜ | ⬜ |
| currencies | Currencies and exchange-rate management | 2 | ⬜ | ⬜ | ⬜ |
| perspectives | Persistence for DataTable saved views | 2 | ⬜ | ⬜ | ⬜ |
| integrations | Integration framework: external ID mapping, registry | 2 | ⬜ | ⬜ | ⬜ |
| business_rules | Rules engine: conditions + actions on entity events | 2 | ⬜ | ⬜ | ⬜ |
| scheduler (pkg) | DB-managed scheduled jobs (cron) with admin UI | 2 | ⬜ | ⬜ | ⬜ |
| search (pkg) | Multi-strategy search (tokens/Meilisearch/vector) + indexer | 2 | ⬜ | ⬜ | ⬜ |
| webhooks (pkg) | Standard-Webhooks outbound/inbound delivery | 2 | ⬜ | ⬜ | ⬜ |
| api_docs | Auto-generated documentation for all HTTP endpoints | 2 | ⬜ | ⬜ | ⬜ |
| data_sync | Streaming data-sync hub for import/export integrations | 2 | ⬜ | ⬜ | ⬜ |

### Tier 3 — domain modules

| Module | Purpose (short) | Tier | Python | .NET | Go |
|---|---|---|---|---|---|
| [customers](upstream/analysis/modules/customers.md) | CRM: people, companies, deals, activities | 3 | 🔍 | 🔍 | 🔍 |
| catalog | Products, variants, and pricing used by sales | 3 | ⬜ | ⬜ | ⬜ |
| sales | Quoting, ordering, fulfillment, billing | 3 | ⬜ | ⬜ | ⬜ |
| [dashboards](upstream/analysis/modules/dashboards.md) | Configurable admin dashboard with module widgets | 3 | 🔍 | ✅ | 🔍 |
| staff | Teams, roles, employee rosters | 3 | ⬜ | ⬜ | ⬜ |
| planner | Availability schedules, rulesets, planning rules | 3 | ⬜ | ⬜ | ⬜ |
| resources | Assets and resources with scheduling policies | 3 | ⬜ | ⬜ | ⬜ |
| customer_accounts | Customer-facing auth with two-tier identity and RBAC | 3 | ⬜ | ⬜ | ⬜ |
| messages | Internal messaging with attachments, actions, email forwarding | 3 | ⬜ | ⬜ | ⬜ |
| communication_channels | Hub bridging external chat/email channels to Messages | 3 | ⬜ | ⬜ | ⬜ |
| inbox_ops | LLM extraction of action proposals from forwarded emails (HITL) | 3 | ⬜ | ⬜ | ⬜ |
| payment_gateways | Payment gateway adapter contract + transactions + webhooks | 3 | ⬜ | ⬜ | ⬜ |
| shipping_carriers | Carrier adapter hub: rates, shipments, tracking, webhooks | 3 | ⬜ | ⬜ | ⬜ |
| workflows | Workflow engine: state machines, transitions, user tasks | 3 | ⬜ | ⬜ | ⬜ |
| checkout (pkg) | Pay links, checkout templates, public payment pages | 3 | ⬜ | ⬜ | ⬜ |
| sync_excel | CSV/Excel upload import on top of data_sync | 3 | ⬜ | ⬜ | ⬜ |
| onboarding (pkg) | Self-service tenant/organization onboarding flow | 3 | ⬜ | ⬜ | ⬜ |

### Tier 4 — adapters & optional packages

| Module | Purpose (short) | Tier | Python | .NET | Go |
|---|---|---|---|---|---|
| gateway-stripe (pkg) | Stripe payment gateway adapter (cards, wallets, transfers) | 4 | ⬜ | ⬜ | ⬜ |
| channel-gmail (pkg) | Gmail adapter for communication_channels | 4 | ⬜ | ⬜ | ⬜ |
| channel-imap (pkg) | IMAP/SMTP adapter for communication_channels | 4 | ⬜ | ⬜ | ⬜ |
| sync-akeneo (pkg) | Akeneo PIM import into catalog | 4 | ⬜ | ⬜ | ⬜ |
| ai-assistant (pkg) | MCP server for AI assistant integration (multi-tenant) | 4 | ⬜ | ⬜ | ⬜ |
| enterprise (pkg) | record_locks, sso, security, system_status_overlays | 4 | ⬜ | ⬜ | ⬜ |
| content (pkg) | Static informational pages (ToS, privacy) | 4 | ⬜ | ⬜ | ⬜ |

### Not port targets (frontend-only / tooling)

| Module | Purpose (short) | Tier | Python | .NET | Go |
|---|---|---|---|---|---|
| core | Framework glue module (no api/entities) | — | — | — | — |
| portal | Self-service customer portal framework (frontend-heavy) | — | — | — | — |
| widgets | Widget injection registry (frontend-only) | — | — | — | — |
| ui (pkg) | React component library (frontend only) | — | — | — | — |
| create-app (pkg) | `create-mercato-app` scaffolding CLI | — | — | — | — |

## Suggested porting order

Order follows the dependency graph in [`upstream/analysis/07-shared-services.md`](upstream/analysis/07-shared-services.md) (§ Module inventory, "Depends on" columns) — port a module only after its dependencies exist in the same technology package:

1. **Tier 0 — runtime foundation.** Everything imports the shared helpers, DI container, queue, event bus and cache. These are scaffold concerns: each `packages/<tech>/` must provide BullMQ-compatible queues (jobs interchangeable with Node BullMQ workers), a worker host, tag-invalidating cache, event bus with persistent subscribers, and Redis env resolution (`<PREFIX>_REDIS_URL` → `REDIS_URL` → error, never localhost). The `health_check` example in each scaffold demonstrates the conventions end to end.
2. **Tier 1 — platform base.** `directory` + `auth` (circular dependency — one porting unit) give tenants/orgs, users, roles, JWT and ACL features; virtually every route's auth semantics hang off them. Then `api_keys` (external auth), `entities` (custom fields consumed by attachments, customers, sales, …), `query_index` (list-endpoint backbone; translations cleanup subscribes to its events), and `configs` + `feature_toggles` (settings and flags read by many services).
3. **Tier 2 — cross-cutting services.** Services many domain modules call into: `translations`, `dictionaries`, `notifications` (needs configs + queue), `attachments` (needs entities; add `storage-s3` when S3 is required), `audit_logs` (CommandBus undo/redo), `progress`, `currencies`, `perspectives`, `integrations`, `business_rules`, plus the `scheduler`, `search` and `webhooks` packages. `api_docs` and `data_sync` fit here as low-dependency utilities.
4. **Tier 3 — domain modules.** The business layer, roughly in dependency order: `customers` → `catalog` → `sales` (largest surfaces: 66/15/41 routes), then `dashboards`, `staff`, `planner` → `resources`, `customer_accounts`, `messages` → `communication_channels` → `inbox_ops`, `payment_gateways` → `checkout`, `shipping_carriers`, `workflows` (needs business_rules + sales), `sync_excel` (needs data_sync + integrations + progress), `onboarding`.
5. **Tier 4 — adapters & optional.** Concrete integrations that plug into Tier 2/3 contracts: `gateway-stripe` (payment_gateways), `channel-gmail`/`channel-imap` (communication_channels), `sync-akeneo` (catalog + data_sync), `ai-assistant` (search + api_keys + …), `enterprise`, `content`. Port on demand.

Within a tier, prefer modules with **0 unported dependencies** first; the "Depends on" column in analysis 07 is the authoritative edge list. Surface counts there (api/ent/wrk/sub) are the effort estimate (±10%).
