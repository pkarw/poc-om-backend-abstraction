# Spec 00 — Overview: Goal, Architecture, Spec Map, Porting Loop

> Derived from upstream commit adc9da27759e357febe9ed8d4b7182040d127349 (2026-07-01). Upstream reference: [`../upstream/UPSTREAM.md`](../upstream/UPSTREAM.md); subsystem analyses: [`../upstream/analysis/`](../upstream/analysis/).

## Goal

Produce **1:1 API-compatible backend ports of Open Mercato modules in multiple technologies** (Python, .NET, Go, and any future target). [Open Mercato](https://github.com/open-mercato/open-mercato) is a TypeScript/Next.js modular commerce/ERP framework; its users ask for the same API on other stacks. This repository is the porting lab: it holds technology-agnostic requirement specs distilled from the upstream source, AI porting skills that execute against those specs, and one runnable package per target technology.

"1:1 compatible" means: an HTTP client, a shared PostgreSQL database, or a Redis-connected BullMQ instance cannot tell whether it is talking to the upstream TypeScript deployment or to a port. A Node process and a ported process may share the same Postgres and Redis simultaneously.

Every port, regardless of language, runs on the same infrastructure triad: **PostgreSQL** (with real migration tooling), **Redis**, and **BullMQ-compatible queues** — plus a queue worker host. Canonical environment variable names (`DATABASE_URL`, `REDIS_URL`, `QUEUE_STRATEGY`, `QUEUE_REDIS_URL`, `JWT_SECRET`, `OM_INIT_SUPERADMIN_EMAIL/PASSWORD`, …) are identical everywhere.

## Upstream architecture in one page

Open Mercato is composed of **modules** — self-contained vertical slices living in `packages/core/src/modules/<module>/` (plus app-local modules in `apps/mercato/src/modules/`). Each module ships convention files that are auto-discovered at build time:

| Convention file | Contents |
|---|---|
| `index.ts` | `metadata: ModuleInfo` (name, requires, …) + init side effects |
| `api/**` | HTTP route handlers; each exports per-method `metadata` (`requireAuth`, `requireFeatures`, `rateLimit`) and an `openApi` description |
| `data/entities.ts` | MikroORM entities → the module's Postgres tables |
| `data/validators.ts` | Zod schemas consumed by route/command code |
| `subscribers/*.ts` | Event subscribers (`{ event, persistent?, sync?, … }`) |
| `workers/*.ts` | Queue job handlers (`{ queue, concurrency? }`) |
| `acl.ts` | Feature declarations — the sole authorization vocabulary |
| `di.ts` | Awilix DI registrar (request-scoped services) |
| `setup.ts` | Tenant lifecycle hooks: `onTenantCreated`, `seedDefaults`, `seedExamples`, `defaultRoleFeatures` |
| `events.ts`, `ce.ts`, `cli.ts`, `i18n/*` | Declared events, custom entities/fields, CLI commands, translations |

The app's composition config (`apps/mercato/src/modules.ts`) lists enabled modules **in order** — order drives DI precedence, translation merges, ACL seeding, and setup hooks. A generator suite (`mercato generate`) materializes static registries into `.mercato/generated/*` so nothing scans the filesystem per request. A Next.js catch-all dispatcher (`apps/mercato/src/app/api/[...slug]/route.ts`) serves every module route under `/api/...` through a uniform pipeline: route match → auth (required by default) → tenant-pollution guard → RBAC feature check → rate limit → handler. Per-request Awilix DI scopes carry the ORM session, query engine, command bus, event bus, and cache.

Cross-cutting packages (`packages/{shared,queue,events,cache,cli,search,…}`) provide: a two-strategy queue (`QUEUE_STRATEGY=async` → BullMQ ^5 on Redis with keys `bull:<queue>:*`; anything else → local file queue), an event bus with persistent delivery through the `events` queue and a Postgres `NOTIFY om_event_bridge` SSE bridge, a scheduler (`scheduled_jobs` table mirrored to BullMQ repeatables), a tenant-aware Redis cache, per-module migrations tracked in `mikro_orm_migrations_<module>` tables, HS256 JWT auth with audience-derived keys, RBAC by feature strings with wildcard grants, a multi-tenant data layer (`tenant_id`/`organization_id` columns, application-level enforcement, no RLS), custom fields (EAV + JSONB doc storage), a `entity_indexes` query-index projection, and a command bus with undo/redo action logs.

Anything on those wires — HTTP shapes, Postgres DDL and rows, Redis keys and job payloads, event names, NOTIFY envelopes, CLI exit codes and grep-able output — is contract. Everything else is internal.

## Spec index

| Spec | Requirement prefix | Scope (one line) |
|---|---|---|
| [00-overview.md](00-overview.md) | — | This map: goal, architecture summary, spec index, porting loop, compatibility philosophy |
| [01-module-system.md](01-module-system.md) | `MODULESYSTEM-R*` | Module composition/config, convention artifacts, route dispatch pipeline, DI scopes, overrides, setup/seed lifecycle |
| [02-api-compatibility.md](02-api-compatibility.md) | `APIHTTP-R*` | HTTP layer: routing, guards, CRUD factory contract, error envelopes, custom fields on the wire, exports, interceptors/enrichers, OpenAPI docs |
| [03-data-layer.md](03-data-layer.md) | `DATALAYER-R*` | Postgres conventions, per-module migrations, multi-tenancy, custom fields/entities, query engine, `entity_indexes`, command pipeline |
| [04-events-and-queues.md](04-events-and-queues.md) | `EVENTSQUEUES-R*` | BullMQ-compatible queues and worker host, event bus, single-delivery semantics, NOTIFY bridge + SSE, scheduler |
| [05-auth-and-rbac.md](05-auth-and-rbac.md) | `AUTHRBAC-R*` | JWTs, staff/customer/API-key auth planes, sessions, RBAC features/ACLs, rate limiting, RBAC cache keys |
| [06-runtime-and-startup.md](06-runtime-and-startup.md) | `RUNTIMESTARTUP-R*` | CLI surface, init/seed pipeline, per-module migration tooling, production guards, cache subsystem, container contracts |
| [07-shared-services.md](07-shared-services.md) | `SHAREDSERVICES-R*` | Shared helpers (webhooks, SSRF guard, token search) and small core modules: translations, notifications, attachments, audit logs, dictionaries, configs, feature toggles, progress, search |
| [08-parity-testing.md](08-parity-testing.md) | `PARITY-R*` | How 1:1 is **proven**: golden request/response testing, schema/queue/event parity, authz matrix, the parity report format |
| [09-technology-package-standard.md](09-technology-package-standard.md) | `TECHPKG-R*` | The canonical shape of every `packages/<tech>/`: layout, Makefile, docker-compose, AGENTS.md/README outlines, queue honesty rule |
| [10-module-contract-parity.md](10-module-contract-parity.md) | `MODCONTRACT-R*` | The full per-module declaration surface, declared identically in every tech: RBAC feature declarations, notification types, custom-field sets / custom entities, declared typed events — module abstraction + registry aggregation, and the declare-now rule when delivery/storage engines are deferred |

Specs 01–07 each follow the same skeleton: **Scope → Requirements (numbered, MUST/SHOULD/MAY per RFC 2119) → Contracts (exact wire/persisted formats) → Concept mapping (upstream TS concept → tech-agnostic concept) → Allowed deviations → Verification**. Requirement IDs are stable and are cited by port contracts, ADRs, and parity reports.

## The porting loop (skills)

Five technology-agnostic skills in [`.claude/skills/`](../.claude/skills/) drive all work; the *same* skill ports to any target:

| Skill | What it does |
|---|---|
| `om-sync-upstream` | Refresh the pinned upstream commit in `upstream/UPSTREAM.md`, regenerate stale analyses, flag affected ports |
| `om-analyze-module` | Distill one upstream module into a **port contract** (`upstream/analysis/modules/<module>.md`): routes, schemas, tables, events, queues, ACL features |
| `om-port-module` | Implement the contract 1:1 inside `packages/<tech>/`, following that package's `AGENTS.md` |
| `om-verify-parity` | Prove parity per [spec 08](08-parity-testing.md); write the report to `.ai/parity/<module>-<tech>.md` |
| `om-add-technology` | Scaffold a new `packages/<tech>/` conforming to [spec 09](09-technology-package-standard.md) |

Typical flow (modules can be ported to multiple technologies in parallel — packages share no files except `MODULES.md`):

```
/om-analyze-module customers
/om-port-module customers python        # in parallel with…
/om-port-module customers dotnet
/om-verify-parity customers python
/om-verify-parity customers dotnet
```

Every step updates the tracker matrix in [`MODULES.md`](../MODULES.md) (⬜ not started → 🔍 analyzed → 🚧 porting → ✅ ported → 🧪 parity-verified). Porting proceeds infrastructure-tier first (shared runtime, `auth`, `directory`, `entities`, `query_index`), then domain modules.

## Compatibility philosophy

1. **Observable behavior is identical.** Same URL paths, methods, status codes, JSON envelopes (down to `"Not Found"` vs `"Not found"` casing), header names, cookie attributes, auth semantics, Postgres table/column/index names, Redis key layouts, queue names, job envelopes, and event ids. Where a spec says "byte-compare", it means it.
2. **Internals are idiomatic.** Ports do not transliterate TypeScript. Use the target language's best-in-class validation library instead of Zod, its native DI or plain constructors instead of Awilix, its own ORM and migration framework instead of MikroORM — as long as the observable surface is unchanged. Each spec's *Allowed deviations* section marks what is free and what is frozen.
3. **Better tech-native solutions are encouraged** when behavior is preserved — and every notable choice is recorded as an ADR in `packages/<tech>/docs/decisions/` (format per spec 09). Silently diverging is forbidden; if reality forces a behavioral deviation, the spec is amended first or the exception is captured in an ADR.
4. **Honesty over claims.** In particular for BullMQ compatibility: Python has an official client; .NET and Go do not. A package never claims wire compatibility it has not implemented — the true status lives in that package's queue ADR and in parity reports (spec 08, spec 09 TECHPKG queue rule).
5. **Upstream is pinned.** All specs, contracts, and ports target the commit recorded in [`upstream/UPSTREAM.md`](../upstream/UPSTREAM.md). Bumping the pin is a deliberate act through `om-sync-upstream`, which re-checks every derived artifact.
