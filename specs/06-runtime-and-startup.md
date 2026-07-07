# Runtime, Startup & Operations — Requirements Spec

> Derived from upstream commit adc9da27759e357febe9ed8d4b7182040d127349. Source analysis: ../upstream/analysis/06-runtime-startup.md

## Scope

This spec covers everything between "clone/deploy" and "a fully seeded, serving application with queue workers and scheduler running":

1. **Startup pipeline** — ensure database exists → generate → per-module migrate → initialize (tenant + superadmin + seeds) → serve app + worker host + scheduler.
2. **CLI operational surface** — the `mercato`-equivalent binary: `init`, `seed:defaults`, `db generate|migrate|greenfield`, `server dev|start`, cache commands, and its output/exit-code protocol.
3. **Per-module migrations** — one migration-history table per module, migrate-never-diffs, greenfield reset.
4. **Server process supervision** — production boot guards (single-instance strategy guard, start lock), background-service auto-spawn, child lifecycle.
5. **Cache subsystem** (`packages/cache`) — strategy selection, tenant-aware key hashing, the Redis on-wire layout, structural nav-cache purge.
6. **Environment variable surface** — names, defaults, resolution orders (Redis URLs, DB pool, SSL).
7. **Container/deployment contracts** — init-once-or-migrate entrypoint logic, health signals, compose topology requirements.

Normative surfaces: everything observable by an API client, by another process sharing the same Postgres/Redis, by a container orchestrator, or by scripts that parse CLI output (upstream `docker/scripts/init-or-migrate.sh` greps CLI output). Internal structure (Next.js, Turbopack, Node child processes, generated-file mechanics, the dev splash server) is descriptive context, not contract.

Out of scope: queue/worker/scheduler internals (spec 04), module discovery and bootstrap registration order details (spec 01), auth semantics of the seeded users (auth spec), HTTP routing (spec 02).

## Requirements

### Configuration & environment resolution

- **RUNTIMESTARTUP-R1** — The port MUST read configuration from environment variables using the upstream names and defaults listed in Contracts → Environment variables. Renaming variables is not allowed; adding port-specific extras is.
- **RUNTIMESTARTUP-R2** — Redis URL resolution MUST follow `getRedisUrl(prefix)` semantics: `<PREFIX>_REDIS_URL` → `REDIS_URL` → not configured. Recognized prefixes: `CACHE`, `QUEUE`, `EVENTS`. When a Redis-requiring feature is enabled and no URL resolves, the port MUST fail with an explicit "Redis URL is not configured" error. The port MUST NOT fall back to `localhost`.
- **RUNTIMESTARTUP-R3** — `DATABASE_URL` MUST be the single source for the Postgres connection. SSL MUST be enabled when the URL contains `sslmode=require` or `ssl=true`, or when `DB_SSL` parses true; `DB_SSL_REJECT_UNAUTHORIZED` (default true) controls certificate verification.
- **RUNTIMESTARTUP-R4** — DB pool sizing MUST honor: `DB_POOL_MIN` (default 2), `DB_POOL_MAX` (default 20), `DB_POOL_IDLE_TIMEOUT` (3000 ms), `DB_POOL_ACQUIRE_TIMEOUT` (6000 ms), `DB_IDLE_IN_TRANSACTION_TIMEOUT_MS` (120000 ms), `DB_IDLE_SESSION_TIMEOUT_MS` (600000 ms outside production, unset in production), `DB_STATEMENT_TIMEOUT_MS`/`DB_LOCK_TIMEOUT_MS` (unset = no timeout). Note the code defaults above differ from `.env.example` values (min 5, idle 10000, acquire 10000) — the code defaults are normative when the env is unset.
- **RUNTIMESTARTUP-R5** — Outside production, a missing `.env` in the app directory SHOULD be auto-created by copying `.env.example` (logged). In production the port MUST NOT auto-create `.env`.
- **RUNTIMESTARTUP-R6** — The port SHOULD enforce a minimum runtime version at CLI entry with an actionable error message (upstream: Node ≥ 24), refusing to run otherwise.

### CLI surface & protocol

- **RUNTIMESTARTUP-R7** — The port MUST provide an operational CLI covering at least: `init [--reinstall] [--no-examples] [--org=] [--email=] [--password=] [--roles=]`, `seed:defaults [--module <id>]`, `db generate`, `db migrate`, `db greenfield --yes`, `server start`, `queue worker <q>|--all [--concurrency=N]`, `queue clear <q>`, `queue status <q>`, `events emit <event> [json] [--persistent]`, and `configs cache stats|purge|structural`. Command spellings MAY be adapted to the port's CLI conventions; behavior and exit codes MUST match.
- **RUNTIMESTARTUP-R8** — CLI exit codes MUST be 0 on success and 1 on any failure. Failure output MUST include the error message; DB connectivity failures SHOULD produce actionable messages (host/port/db, refused-connection hint).
- **RUNTIMESTARTUP-R9** — `queue status <name>` MUST print the four counters `Waiting/Active/Completed/Failed` in the parseable format shown in Contracts.
- **RUNTIMESTARTUP-R10** — Module-contributed CLI commands MUST be dispatchable as `<binary> <module> <command> [args...]` (module `cli.ts` equivalent).

### Database bootstrap & per-module migrations

- **RUNTIMESTARTUP-R11** — Before initialization, the port MUST check that the target database in `DATABASE_URL` exists (`SELECT 1 FROM pg_database WHERE datname = $1` via a maintenance connection to the same server's `/postgres` database) and issue `CREATE DATABASE "<name>"` when missing. On creation failure it MUST print manual-recovery instructions and exit 1.
- **RUNTIMESTARTUP-R12** — Migrations MUST be tracked **per module**: each module gets its own history table named `mikro_orm_migrations_<moduleId>` (moduleId sanitized `[^a-z0-9_]` → `_`, case-insensitive; resulting name must match `^[a-zA-Z_][a-zA-Z0-9_]*$`) and its own migrations directory. Modules MUST be processed in alphabetical order by module id.
- **RUNTIMESTARTUP-R13** — `db migrate` MUST only execute committed migration files — it MUST NOT diff entity metadata against the live schema at migrate time. Pending migrations are applied one at a time per module; per-module output reports `N migrations applied` or `no pending migrations`.
- **RUNTIMESTARTUP-R14** — Generated migration files MUST carry a `_<moduleId>` suffix in filename and class/identifier name, and every generated `ALTER TABLE ... DROP CONSTRAINT <x>` MUST be rewritten to `DROP CONSTRAINT IF EXISTS <x>` (idempotent constraint drops).
- **RUNTIMESTARTUP-R15** — `db greenfield` without `--yes` MUST print a destructive-operation warning and exit 1. With `--yes` it MUST: delete all migration files and snapshots per module, `DROP TABLE IF EXISTS mikro_orm_migrations_<mod>` for every module in one transaction, drop **all** tables in `current_schema()` (using `session_replication_role='replica'` to bypass FK ordering), then regenerate and re-apply migrations.
- **RUNTIMESTARTUP-R16** — A port's schema migration tooling MUST produce a schema byte-compatible with upstream's applied migrations (same table/column/index/constraint names) so Node and ported processes can share one database.

### Initialization (`init`)

- **RUNTIMESTARTUP-R17** — `init` on a database whose `public.users` table exists and contains rows MUST abort with exit code 1 and output containing the exact message `Initialization aborted: found <N> existing user(s) in the database.` (see Contracts — this string is grepped by container entrypoints; changing it breaks init-once orchestration).
- **RUNTIMESTARTUP-R18** — `init` (fresh database) MUST execute in this order: (1) ensure database exists, (2) run generators (if the port has a generation step), (3) apply all module migrations, (4) bootstrap modules, (5) restore config defaults, (6) create the tenant/organization + superadmin + roles (auth setup), (7) seed roles / encryption defaults, (8) run every module's `seedDefaults` with a shared `{ em, tenantId, organizationId, container }` context, (9) re-run custom-role ACL fixup (roles may have been created during seeding), (10) run every module's `seedExamples` unless `--no-examples`, (11) seed dashboards, (12) trigger search and query-index reindex. Steps for subsystems the port has not (yet) ported MAY be no-ops.
- **RUNTIMESTARTUP-R19** — With no flags/env overrides, `init` MUST seed: organization `Acme Corp`; roles `superadmin`, `admin`, `employee`; users `superadmin@acme.com`/`secret` (role superadmin), `admin@acme.com`/`secret` (admin), `employee@acme.com`/`secret` (employee). Admin/employee emails are derived as `admin@<domain>`/`employee@<domain>` from the superadmin email's domain. All values MUST be overridable via `OM_INIT_*` env vars and CLI flags per Contracts; `OM_INIT_GENERATE_RANDOM_PASSWORD=true` MUST generate random passwords for the derived users. `init` MUST print the created credentials in its summary.
- **RUNTIMESTARTUP-R20** — Demo passwords MUST bypass the password policy by default during `init` (upstream `--skip-password-policy` defaults to true). A port MAY harden this as a documented decision only if the default `init` still succeeds with password `secret`.
- **RUNTIMESTARTUP-R21** — `init --reinstall` MUST: drop every table in `current_schema()` (plus `vector_search` and `vector_search_migrations` if present) in one transaction, then issue Redis `FLUSHALL` when a Redis URL is configured (skipping silently with a log line when it is not — never attempting a connection), then run the full init sequence.
- **RUNTIMESTARTUP-R22** — Tenant data encryption defaults MUST be seeded during init when `TENANT_DATA_ENCRYPTION` parses truthy; the default when unset is **truthy** (encryption on).
- **RUNTIMESTARTUP-R23** — `seed:defaults` MUST iterate every non-deleted organization (multi-tenant), run each module's `seedDefaults` (optionally filtered by `--module <id>`) followed by the custom-role ACL fixup, and MUST be safe to run repeatedly (seeders upsert). With zero organizations it MUST exit 1 with a "No organizations found" error directing the user to initialize first.

### Server lifecycle & production guards

- **RUNTIMESTARTUP-R24** — Production server start MUST run these guards, in order, before serving traffic: (1) events single-delivery guard (spec 04 R24), (2) single-instance strategy guard (R25), (3) server start lock (R26).
- **RUNTIMESTARTUP-R25** — Single-instance strategy guard: when `NODE_ENV=production` (or the port's production mode) AND a multi-instance topology is declared (`OM_MULTI_INSTANCE` truthy or `OM_INSTANCE_COUNT` > 1) AND any of the strategies is not multi-instance-safe — safe set: `CACHE_STRATEGY=redis`, `QUEUE_STRATEGY=async`, `RATE_LIMIT_STRATEGY=redis` — the server MUST refuse to start with an error naming the offending strategies, unless `OM_ALLOW_SINGLE_INSTANCE_STRATEGIES` is set. In production single-instance mode with unsafe strategies it MUST log a prominent warning instead of failing.
- **RUNTIMESTARTUP-R26** — Production start SHOULD acquire an exclusive per-app-directory lock file (upstream `.mercato/server-start.lock`, JSON `{"pid":N,"port":"<port>"|null,"startedAt":"<iso>"}`, created with exclusive-write semantics). If the lock exists and its recorded pid is alive, start MUST fail with a message identifying the running pid/port; stale locks (dead pid) MUST be removed and retried. The lock MUST be released on shutdown.
- **RUNTIMESTARTUP-R27** — The server process SHOULD auto-spawn the queue worker host (all queues) and, when `QUEUE_STRATEGY=local`, the scheduler, as supervised background services. Defaults: auto-spawn **on** (`AUTO_SPAWN_WORKERS`/`OM_AUTO_SPAWN_WORKERS`, `AUTO_SPAWN_SCHEDULER`/`OM_AUTO_SPAWN_SCHEDULER`, legacy unprefixed name wins); lazy mode only when the corresponding `OM_AUTO_SPAWN_*_LAZY` parses exactly true. When `QUEUE_STRATEGY=async` the scheduler MUST NOT be auto-spawned (scheduling rides BullMQ repeatables per spec 04).
- **RUNTIMESTARTUP-R28** — In a supervised topology, the first unexpected exit of any supervised service (app, worker host, scheduler) MUST tear down all siblings and terminate the supervisor with a non-zero exit code and a message naming the failed service. Shutdown signals (SIGTERM/SIGINT equivalent) MUST propagate to children with a grace period before force-kill.
- **RUNTIMESTARTUP-R29** — The worker host MUST clamp total (Σ per-queue) consumer concurrency to a DB-connection budget: `OM_WORKERS_DB_CONNECTION_BUDGET`, default `DB_POOL_MAX`, floor 1 per queue, logging when clamped (spec 04 R15; restated here because the budget defaults couple to R4's pool config).
- **RUNTIMESTARTUP-R30** — Observability agent activation SHOULD be toggled purely by env presence (upstream: `NEW_RELIC_LICENSE_KEY` set → agent preloaded; `NEW_RELIC_APP_NAME` default `open-mercato`). Request-header attributes forwarded to any APM MUST exclude at minimum: `cookie`, `authorization`, `x-api-key`, `x-sudo-token`, `x-domain-check-secret`, `x-domain-resolve-secret`, `x-force-host-secret`, `x-webhook-signature`, `svix-signature`.

### Cache subsystem

- **RUNTIMESTARTUP-R31** — The cache service MUST select its strategy from explicit option → `CACHE_STRATEGY` env → default `memory`. Recognized strategies: `memory`, `redis`, and at least one local persistent strategy equivalent to upstream `sqlite`/`jsonfile` (MAY be a different local store; see Allowed deviations). Default TTL from `CACHE_TTL` (milliseconds); memory LRU cap from `CACHE_MEMORY_MAX_ENTRIES` (default 50000).
- **RUNTIMESTARTUP-R32** — If a non-memory strategy's backing dependency is unavailable at runtime, the cache MUST transparently degrade to the memory strategy with exactly one logged warning — it MUST NOT crash the process or fail cache calls.
- **RUNTIMESTARTUP-R33** — A tenant-aware wrapper MUST always be applied: the ambient tenant id (request-scoped context; `null` → literal `global`; sanitized `[^a-zA-Z0-9._-]` → `_`) prefixes every physical key. Logical key/tag → storage names: value `tenant:<t>:key:k:<sha1hex(key)>`, metadata twin `tenant:<t>:key:meta:<sha1hex(key)>` containing `{ "key": "<original>", "expiresAt": <ms|null> }`, tag `tenant:<t>:tag:t:<sha1hex(tag)>`. Every `set` MUST also add the implicit scope tag `tenant:<t>:tag:__scope__`; `clear()` MUST clear only the current tenant scope (delete-by-scope-tag). Counts returned by `deleteByTags`/`clear` MUST hide the meta twin (`ceil(raw/2)`).
- **RUNTIMESTARTUP-R34** — The Redis cache strategy MUST use the on-wire layout in Contracts: physical prefixes `cache:` (value keys, JSON entry, `SETEX` with `ceil(ttl/1000)` seconds) and `tag:` (Redis SETs of member storage keys, membership updated on every set). Redis URL resolution uses the `CACHE` prefix per R2. This layout is a shared-Redis interop contract with Node processes.
- **RUNTIMESTARTUP-R35** — The cache interface MUST expose: `get(key)` (optionally returning expired entries on request), `set(key, value, {ttl?, tags?})`, `has`, `delete`, `deleteByTags(tags) → count`, `clear() → count`, `keys(pattern?)` (returning **logical** key names recovered via the meta twin), `stats() → {size, expired}`, and a healthcheck.
- **RUNTIMESTARTUP-R36** — Structural cache purge: after any operation that changes routing/navigation structure (upstream: every generator run), the port MUST purge cache entries matching the logical pattern `nav:*` plus the segments `admin-nav` and `portal-nav`, across all tenants. The CLI command `configs cache structural` MUST default to global scope only, with `--tenant <id>` / `--all-tenants` options.
- **RUNTIMESTARTUP-R37** — Nav/sidebar caching, if implemented, MUST use logical keys of the form `nav:sidebar:<locale>:<userId>:<tenantId>:<orgId>` tagged `nav:sidebar:user:<userId>` so R36's purge pattern reaches them.

### Health & deployment

- **RUNTIMESTARTUP-R38** — `GET /` MUST return HTTP 200 unauthenticated (upstream platform healthchecks target `/` with a 360 s first-boot timeout). A port SHOULD additionally expose a cheap dedicated health endpoint as a documented improvement, without removing the `/` behavior.
- **RUNTIMESTARTUP-R39** — If the port supports custom domains, origin-verification responses MUST carry the marker header (name from `CUSTOMER_DOMAIN_ORIGIN_HEADER`, default `X-Open-Mercato-Origin`, value `1`) on its health path so DNS verification can confirm the origin.
- **RUNTIMESTARTUP-R40** — Container entrypoints MUST implement the init-once-or-migrate contract: if the marker file (`INIT_MARKER_FILE`, default `/tmp/init-marker/.seeded`) is absent, run the init command; on init success create the marker; if init fails AND its output matches the regex `Initialization aborted: found [0-9][0-9]* existing user\(s\) in the database\.`, run migrations instead and create the marker; any other init failure propagates the exit code. If the marker is present, run migrations only. This converges correctly even when the marker volume is lost (R17 provides the fallback signal).
- **RUNTIMESTARTUP-R41** — Deployment topology MUST provide: Postgres with the `pgvector` extension available (upstream image `pgvector/pgvector:pg17`), Redis 7+ (SHOULD be capped with `maxmemory`), and the app starting only after Postgres/Redis (and search, if used) report healthy. Initialization runs inside the app container's own start command (via R40), not as a separate one-shot service. DB/Redis SHOULD NOT be exposed to the host in production stacks.
- **RUNTIMESTARTUP-R42** — Boolean env parsing throughout this subsystem MUST follow the shared token table (spec 04 R5): true = `1,true,yes,y,on,enable,enabled`; false = `0,false,no,n,off,disable,disabled`; case-insensitive after trimming; unrecognized → the variable's documented default.

## Contracts

### Init abort message (byte-exact)

```
❌ Initialization aborted: found <N> existing user(s) in the database.
```

Grepped by container entrypoints with:

```
Initialization aborted: found [0-9][0-9]* existing user\(s\) in the database\.
```

(The leading `❌ ` emoji is upstream cosmetics; the regex — and therefore the required invariant — is the sentence itself. Do not reword, re-punctuate, or localize it.)

### Default seeded identities (`init`, no flags/env)

| User | Email | Password | Role |
|---|---|---|---|
| Superadmin | `superadmin@acme.com` | `secret` | `superadmin` |
| Admin | `admin@acme.com` (derived: `admin@<superadmin-domain>`) | `secret` | `admin` |
| Employee | `employee@acme.com` (derived) | `secret` | `employee` |

Organization: `Acme Corp`. Roles seeded: `superadmin,admin,employee`.

### `queue status` output (parseable)

```
Queue "<name>" status:
  Waiting:   N
  Active:    N
  Completed: N
  Failed:    N
```

### Postgres structures

- Migration history: one table per module, `mikro_orm_migrations_<moduleId>` (sanitize `[^a-z0-9_]/i` → `_`; validate `^[a-zA-Z_][a-zA-Z0-9_]*$`).
- SQL used verbatim by the startup pipeline (a port sharing the DB must be compatible with these probes):
  - `SELECT 1 FROM pg_database WHERE datname = $1` (against `/postgres`)
  - `SELECT to_regclass('public.users')` then `SELECT COUNT(*)::text FROM users` (init guard)
  - `SELECT o.id AS org_id, o.tenant_id FROM organizations o JOIN users u ON u.organization_id = o.id LIMIT 1` (init reads back tenant/org)
  - `SELECT tablename FROM pg_tables WHERE schemaname = current_schema()` (reinstall/greenfield drop list)
- Greenfield/reinstall drops run with `SET session_replication_role = 'replica'` inside one transaction; reinstall additionally force-drops `vector_search`, `vector_search_migrations`.

### Redis structures (shared-Redis interop)

Cache value key:

```
cache:tenant:<tenantId|global>:key:k:<sha1hex(logicalKey)>
```

Value = JSON:

```json
{ "key": "<storageKey>", "value": <cached value>, "tags": ["<storageTag>", "..."],
  "expiresAt": <epoch ms | null>, "createdAt": <epoch ms> }
```

TTL applied via `SETEX` with `ceil(ttlMs/1000)` seconds. Metadata twin:

```
cache:tenant:<t>:key:meta:<sha1hex(logicalKey)>   →   {"key":"<logicalKey>","expiresAt":<ms|null>}
```

Tag indexes (Redis SETs whose members are value storage keys):

```
tag:tenant:<t>:tag:t:<sha1hex(logicalTag)>
tag:tenant:<t>:tag:__scope__                      # implicit, added on every set
```

Structural purge targets: logical pattern `nav:*` + segments `admin-nav`, `portal-nav`. Nav logical keys: `nav:sidebar:<locale>:<userId>:<tenantId>:<orgId>`, tag `nav:sidebar:user:<userId>`.

`init --reinstall` issues `FLUSHALL` when a Redis URL is configured. (Queue keys `bull:<queue>:*` — spec 04.)

### Filesystem contracts (relative to app dir; Node-port-shape, see Allowed deviations)

| Path | Content |
|---|---|
| `.mercato/server-start.lock` | Production lock, JSON `{"pid":N,"port":"3000"|null,"startedAt":"<iso>"}` |
| `.mercato/queue/` | Local file-queue base dir (`QUEUE_BASE_DIR`) |
| `.mercato/cache/cache.db` / `cache.json` | Local persistent cache defaults |
| `/tmp/init-marker/.seeded` (compose) / `storage/.initialized` (Railway) | Init-once markers (`INIT_MARKER_FILE`) |

### Environment variables (normative names, defaults, resolution)

**Core connectivity & auth**

| Var | Default | Contract |
|---|---|---|
| `DATABASE_URL` | — (required) | Postgres URL; `sslmode=require`/`ssl=true` enables SSL |
| `JWT_SECRET` | — (required) | Token signing; `AUTH_SECRET` preferred when set (`NEXTAUTH_SECRET` alias) |
| `REDIS_URL` | — | Base Redis URL; **no localhost fallback** |
| `CACHE_REDIS_URL` / `QUEUE_REDIS_URL` / `EVENTS_REDIS_URL` | → `REDIS_URL` | Per-subsystem override (R2) |
| `QUEUE_STRATEGY` | `local` | `async` (exact literal) = BullMQ/Redis; anything else = local |
| `CACHE_STRATEGY` | `memory` (code default) | `memory\|redis\|sqlite\|jsonfile`; `.env.example` ships `sqlite`, docker stacks `redis` |
| `CACHE_TTL` | unset | Default TTL, ms |
| `CACHE_MEMORY_MAX_ENTRIES` | 50000 | Memory LRU cap |
| `RATE_LIMIT_STRATEGY` | `memory` | `redis` required for multi-instance prod (R25) |
| `PORT` | 3000 | App port |
| `APP_URL` | `http://localhost:3000` | Base URL; resolution order `APP_URL` → `NEXT_PUBLIC_APP_URL` → `NEXTAUTH_URL` → `http://localhost:$PORT` |

**DB pool** — `DB_POOL_MIN`=2, `DB_POOL_MAX`=20, `DB_POOL_IDLE_TIMEOUT`=3000, `DB_POOL_ACQUIRE_TIMEOUT`=6000, `DB_IDLE_SESSION_TIMEOUT_MS`=600000 (non-prod only), `DB_IDLE_IN_TRANSACTION_TIMEOUT_MS`=120000, `DB_STATEMENT_TIMEOUT_MS`/`DB_LOCK_TIMEOUT_MS` unset, `DB_SSL`=false, `DB_SSL_REJECT_UNAUTHORIZED`=true, `OM_WORKERS_DB_CONNECTION_BUDGET`=`DB_POOL_MAX`.

**Initialization**

| Var | Default |
|---|---|
| `OM_INIT_SUPERADMIN_EMAIL` | `superadmin@acme.com` |
| `OM_INIT_SUPERADMIN_PASSWORD` | `secret` |
| `OM_INIT_ADMIN_EMAIL` / `OM_INIT_EMPLOYEE_EMAIL` | `admin@<domain>` / `employee@<domain>` |
| `OM_INIT_ADMIN_PASSWORD` / `OM_INIT_EMPLOYEE_PASSWORD` | `secret` |
| `OM_INIT_GENERATE_RANDOM_PASSWORD` | false (true → random passwords for derived users) |
| `TENANT_DATA_ENCRYPTION` | truthy (encryption on by default) |
| `INIT_MARKER_FILE` | `/tmp/init-marker/.seeded` (container entrypoint) |

**Background services & topology** — `AUTO_SPAWN_WORKERS`/`OM_AUTO_SPAWN_WORKERS`=true (legacy unprefixed wins), `OM_AUTO_SPAWN_WORKERS_LAZY`=false, `OM_AUTO_SPAWN_WORKERS_LAZY_POLL_MS`=1000 (min 250), `OM_AUTO_SPAWN_WORKERS_LAZY_RESTART`=true, `AUTO_SPAWN_SCHEDULER`/`OM_AUTO_SPAWN_SCHEDULER`=true (+ `_LAZY` variants), `QUEUE_BASE_DIR`=`.mercato/queue`, `OM_MULTI_INSTANCE`=false, `OM_INSTANCE_COUNT`=—, `OM_ALLOW_SINGLE_INSTANCE_STRATEGIES`=false, `NEW_RELIC_LICENSE_KEY`=— (presence activates APM), `NEW_RELIC_APP_NAME`=`open-mercato`.

### CLI output protocol (SHOULD-level; load-bearing only for the init abort message)

Upstream prints `🚀 Running <mod>:<cmd> <args>` before and `⏱️ Done in <ms>ms` / `💥 Failed: <message>` after each command; a port SHOULD keep comparable structure but only R17's abort sentence is byte-normative.

## Concept mapping

| Upstream TS concept | Technology-agnostic concept a port implements |
|---|---|
| `packages/cli/src/bin.ts` + `mercato.ts` dispatcher | Operational CLI binary with module-command dispatch, env loading, exit-code protocol |
| `ensureDatabaseExists` (`packages/cli/src/mercato.ts`) | Auto-create target DB via maintenance connection to `/postgres` (R11) |
| `dbGenerate`/`dbMigrate`/`dbGreenfield` (`packages/cli/src/lib/db/commands.ts`) | Per-module migration tooling: history table per module, migrate-never-diffs, suffixed files, idempotent constraint drops, greenfield reset (R12–R15) |
| MikroORM migrations + `.snapshot-open-mercato.json` | Any real migration framework (Alembic, EF Core, golang-migrate, ...) producing the same live schema and per-module history tables |
| `mercato init` sequence (`packages/cli/src/mercato.ts`) | Ordered initialization command: tenant/org/users/roles + per-module `seedDefaults`/`seedExamples` hooks (R17–R22) |
| `resolveInitDerivedSecrets` (`packages/cli/src/lib/init-secrets.ts`) | Derived admin/employee credential resolution from superadmin email + `OM_INIT_*` (R19) |
| `mercato seed:defaults` | Idempotent re-seed across all organizations (R23) |
| `mercato server start` supervisor | Production process supervisor: guards → serve + worker host + scheduler, fail-together semantics (R24–R28) |
| `assertSingleInstanceStrategies` (`packages/cli/src/lib/single-instance-strategy-guard.ts`) | Multi-instance safety gate over cache/queue/rate-limit strategies (R25) |
| `acquireServerStartLock` (`packages/cli/src/lib/server-start-lock.ts`) | Exclusive production start lock with stale-pid recovery (R26) |
| `resolveAutoSpawnWorkersMode` / `resolveAutoSpawnSchedulerMode` (`packages/cli/src/lib/auto-spawn-*.ts`) | off/eager/lazy background-service spawn policy (R27) |
| `resolveWorkerConnectionBudget` / `planWorkerConcurrency` (`packages/cli/src/lib/worker-connection-budget.ts`) | DB-connection-aware worker concurrency clamp (R29) |
| `buildServerProcessEnvironment` (NODE_ENV=production, ±`-r newrelic`) | Deterministic child/runtime environment construction; env-presence-driven APM activation (R30) |
| `getRedisUrl`/`getRedisUrlOrThrow` (`packages/shared/src/lib/redis/connection.ts`) | Prefixed Redis URL resolution, never-localhost (R2) |
| `getSslConfig` / `resolvePoolConfig` (`packages/shared/src/lib/db/{ssl,mikro}.ts`) | DB SSL + pool env resolution (R3–R4) |
| `createCacheService` + tenant wrapper (`packages/cache/src/service.ts`, `tenantContext.ts`) | Multi-strategy cache with ambient-tenant key scoping, sha1 hashing, meta twins, scope tag (R31–R33, R35) |
| Redis cache strategy (`packages/cache/src/strategies/redis.ts`) | The `cache:`/`tag:` Redis on-wire layout (R34) |
| `CacheDependencyUnavailableError` fallback | Degrade-to-memory-with-one-warning policy (R32) |
| `STRUCTURAL_CACHE_REQUESTS` + `configs cache structural` (`packages/core/src/modules/configs/cli.ts`) | Structural nav-cache purge command + post-structural-change hook (R36) |
| `docker/scripts/init-or-migrate.sh` | Init-once-or-migrate container entrypoint with marker file + abort-message fallback (R40) |
| `docker-compose*.yml` health/dependency wiring | pgvector Postgres + Redis + healthy-before-app ordering (R41) |
| Railway `healthcheckPath = "/"` | Unauthenticated 200 on `/` (R38) |
| Node ≥ 24 gate in `bin.ts` | Runtime version gate with remediation message (R6) |
| `next.config.ts` marker header on `/_next/health` | Origin-marker header on health path (R39) |

## Allowed deviations

Idiomatic replacements are welcome when the observable surface (Postgres, Redis, HTTP, CLI exit codes, the R17 abort sentence, marker-file logic) is unchanged. Document each as a decision.

**MAY deviate:**

- **Migration framework**: Alembic / EF Core / goose / anything with real up-migrations — as long as history is tracked per module in `mikro_orm_migrations_<moduleId>`-named tables (required for shared-DB coexistence with Node) and the applied schema matches upstream. Snapshot-file mechanics, file naming beyond the `_<moduleId>` suffix, and the `db generate` diffing approach are internal.
- **Code generation**: upstream's `.mercato/generated/*` pipeline is a TypeScript-specific mechanism. A port using compile-time registration, reflection, or source generators needs no generation step; init step (2) becomes a no-op. The structural cache purge (R36) must still run after whatever the port's equivalent of a structural change is.
- **Process model**: threads, goroutines, systemd/supervisord units, or separate containers instead of child-process supervision — provided R27's spawn defaults, R28's fail-together/graceful-shutdown semantics, and the single-delivery fail-safe interplay (spec 04 R24) hold. The start lock (R26) MAY be replaced by an orchestrator-level singleton guarantee (documented).
- **Local cache strategies**: `sqlite`/`jsonfile` may be replaced by any local persistent store; only the `memory` and `redis` strategy names and semantics are contractual (Redis layout is interop; memory is the universal fallback). Unsupported strategy tokens SHOULD fall back like a missing dependency (R32).
- **Redis `KEYS` scans**: upstream's cache `clear`/`keys`/`stats` use `KEYS cache:*`; a port MAY use `SCAN` (behavior-preserving improvement).
- **Dev orchestration**: the dev splash server (port 4000), warmup marker files, `.env`-change runtime restart, Turbopack cache recovery, and dev log parsing are Node/Next.js dev-experience features — not required. A port SHOULD offer some single-command dev startup, shape free.
- **Health endpoint**: adding a dedicated cheap `/health`/`/api/health` is encouraged (documented improvement); `/` must keep returning 200 (R38).
- **Filesystem layout**: `.mercato/*` paths are contractual only for artifacts another process reads (local queue dir if file-compat is claimed; container marker files via `INIT_MARKER_FILE`). Lock-file location/name may differ.
- **CLI cosmetics**: banners, emoji, timing lines, command spellings — free, except the R17 abort sentence and the R9 `queue status` block.
- **Hardening**: rejecting the insecure demo defaults (`JWT_SECRET=JWT`, password `secret`) in production mode is an acceptable documented decision, provided default `init` in dev still seeds the R19 identities.
- **`QUEUE_STRATEGY` alias**: accepting `redis` as an alias for `async` (documented; `async` must remain accepted).

**MUST NOT change:**

- The init abort sentence and its greppability (R17), and the init-or-migrate marker logic (R40).
- Default seeded identities, org name, and role names (R19) and their `OM_INIT_*` override vars.
- Per-module migration history table naming and the migrate-never-diffs rule (R12–R13).
- The Redis cache on-wire layout: `cache:`/`tag:` prefixes, tenant/`global` scoping, sha1-hashed names, meta twins, `__scope__` tag, JSON entry shape, `SETEX` TTL (R33–R34).
- Env var names, defaults, and resolution orders (R1–R4, env tables), including `QUEUE_STRATEGY=async` as the exact BullMQ literal and never-localhost Redis resolution.
- The single-instance strategy guard's safe set and failure condition (R25).
- Structural purge targets `nav:*`, `admin-nav`, `portal-nav` (R36).
- `/` returning 200 unauthenticated (R38); pgvector-capable Postgres (R41).
- Init ordering guarantees that other specs depend on: migrations before seeding, `seedDefaults` before the ACL fixup, `seedExamples` after `seedDefaults` (R18).

## Verification

How `om-verify-parity` checks these requirements (harness runs the port and upstream Node against a shared Postgres + Redis):

1. **Fresh-boot pipeline (R11–R13, R17–R19)** — point the port at a Postgres server with no target DB; run its init command; assert the database was created, all `mikro_orm_migrations_<mod>` tables exist with ≥ 1 row each, and the R19 identities/roles/org exist (SQL probes). Re-run init → assert exit 1 and stdout/stderr matches the R17 regex byte-for-byte.
2. **Schema parity (R16)** — `pg_dump --schema-only` diff between a database migrated by upstream (`yarn mercato db migrate`) and one migrated by the port, per ported module (tables, columns, defaults, indexes, constraints).
3. **Init-or-migrate contract (R40)** — run upstream's `docker/scripts/init-or-migrate.sh` verbatim with `INIT_COMMAND`/`MIGRATE_COMMAND` pointed at the port's CLI: fresh DB + no marker → init runs, marker created; delete marker, keep DB → script must detect the abort message and fall back to migrate (exit 0); corrupt init failure (e.g. bad `DATABASE_URL`) → non-zero propagates, no marker.
4. **Reinstall (R21)** — seed extra tables/rows and Redis keys; run reinstall; assert `current_schema()` has only freshly migrated tables, Redis is empty (`DBSIZE` 0), and the R19 identities are re-seeded. Repeat with `REDIS_URL` unset → assert no Redis connection attempt (harness watches for connections) and a skip log line.
5. **seed:defaults idempotence (R23)** — run twice against a two-organization DB; assert row counts of seeded defaults are stable between runs and both orgs were visited; run against an empty DB → exit 1 with the no-organizations error.
6. **Production guards (R24–R26)** — matrix boot tests: prod + `OM_MULTI_INSTANCE=1` + `CACHE_STRATEGY=memory` → startup fails naming `cache`; same + `OM_ALLOW_SINGLE_INSTANCE_STRATEGIES=1` → boots with warning; prod single-instance unsafe → boots with warning; safe triple (`redis`/`async`/`redis`) → silent. Lock: start two production instances on one app dir → second fails citing the first's pid; kill -9 the first, restart → stale lock recovered.
7. **Supervision (R27–R28)** — boot with `QUEUE_STRATEGY=local` → scheduler service present; with `async` → absent; with `OM_AUTO_SPAWN_WORKERS=0` → no worker host (and spec 04 R24 downgrade observed). Kill the worker service → whole topology exits non-zero naming it. SIGTERM → all children exit within the grace period, lock released.
8. **Worker budget (R29)** — start the worker host with `DB_POOL_MAX=4` and module workers requesting Σ concurrency 10 → assert effective Σ ≤ 4 with ≥ 1 per queue (probe via startup logs or live BullMQ consumer counts), and a clamp warning is logged.
9. **Cache wire parity (R31–R35)** — shared-Redis cross-runtime test: port `set("nav:sidebar:en:u1:t1:o1", v, {tags:["nav:sidebar:user:u1"]})` under tenant `t1`; harness asserts the exact key `cache:tenant:t1:key:k:<sha1>` exists with the JSON entry shape, the meta twin, tag SET membership, and `__scope__` membership; upstream Node `cacheService.get` must return the value, and vice versa. `clear()` under tenant `t1` must not touch tenant `t2` keys; `deleteByTags`/`clear` counts halved; `keys()` returns logical names. TTL set → `TTL` ≈ `ceil(ttl/1000)`. Start the redis strategy with the Redis client library removed/unreachable module → one warning, calls served from memory.
10. **Structural purge (R36–R37)** — seed `nav:*`-keyed entries for multiple tenants plus unrelated keys; run the port's `configs cache structural --all-tenants` → nav entries gone across tenants, unrelated keys intact; default invocation → global scope only.
11. **Env semantics (R1–R5, R42)** — resolution matrix: `CACHE_REDIS_URL` beats `REDIS_URL`; no Redis env + `CACHE_STRATEGY=redis` → explicit not-configured error, harness confirms no localhost connection attempt; boolean token fuzz over the R42 table; unset pool vars → effective values equal the R4 code defaults (probe via pool debug output or `pg_settings` for the session timeouts).
12. **Health (R38–R39)** — `GET /` unauthenticated → 200 within the deploy healthcheck budget on a cold seeded instance; when custom domains are enabled, health path response carries `X-Open-Mercato-Origin: 1` (or the configured header name).
13. **CLI protocol (R7–R9)** — smoke every required command for exit code 0/1 discipline; `queue status` output parsed with the same extraction used against upstream; `db greenfield` without `--yes` → exit 1, database untouched.
