# Runtime, Startup & Ops (apps/mercato, dev orchestration, `mercato` CLI, Docker, cache, env)

> Analyzed at upstream commit adc9da27759e357febe9ed8d4b7182040d127349 (2026-07-01). Regenerate via the om-sync-upstream skill.

## Purpose

This subsystem is everything between "clone the repo" and "a fully seeded, serving application with queue workers and scheduler running": the `apps/mercato` Next.js host app and its bootstrap, the `yarn dev` orchestration (`scripts/dev.mjs` + `apps/mercato/scripts/dev.mjs`), the `yarn mercato` CLI (`packages/cli`) including `db` commands, `init` (fresh-install initialization), `seed:defaults`, `server dev|start` (process supervisor for Next.js + workers + scheduler), Docker/Compose deployment topologies, New Relic observability, the multi-strategy cache package (`packages/cache`) with its Redis key layout, and the complete environment-variable surface. A port needs equivalents for all of this to achieve "single command startup" parity: `create DB if missing → migrate → generate → init (tenant + superadmin + seeds) → serve app + queue worker host + scheduler`.

## Key source locations

| Path (upstream repo root) | What it contains |
|---|---|
| `apps/mercato/package.json` | App workspace `@open-mercato/app`; scripts map to `mercato` CLI: `dev`→`scripts/dev.mjs`, `start`→`mercato server start`, `generate`→`mercato generate`, `db:migrate`→`mercato db migrate`, `initialize`→`mercato init`, `reinstall`→`mercato init --reinstall`, `seed:defaults`→`mercato seed:defaults` |
| `apps/mercato/src/bootstrap.ts` | App-level bootstrap: imports all `.mercato/generated/*` artifacts, registers registries, exports `bootstrap()` via `createBootstrap` |
| `apps/mercato/src/modules.ts` | `enabledModules` list (module id + `from` package) and the unified per-app `overrides` surface |
| `apps/mercato/src/app/api/[...slug]/route.ts` | Single catch-all API route handler; calls `bootstrap()` at module load, then dispatches to generated route manifests |
| `apps/mercato/src/app/api/docs/{openapi,markdown}/route.ts` | OpenAPI + markdown docs endpoints |
| `apps/mercato/src/proxy.ts` | Next.js middleware ("proxy") for custom-domain → `/{orgSlug}/portal` rewrites; excludes `/api/*` |
| `apps/mercato/src/instrumentation.ts` | `register()` is a **no-op** (dev warmup lives in the dev runner) |
| `apps/mercato/next.config.ts` | CSP headers; adds marker header (default `X-Open-Mercato-Origin: 1`) on `/_next/health`; Next `distDir` is `.mercato/next` |
| `apps/mercato/.env.example` | Canonical env var inventory (753 lines, all documented defaults) |
| `apps/mercato/scripts/dev.mjs` | App-level dev runner: runs `mercato generate`, then `mercato server dev`; filters logs, drives splash state, runs HTTP warmup of `/login` + `POST /api/auth/login` + `/backend` |
| `scripts/dev.mjs` (root) | Monorepo/standalone dev orchestrator: package builds, watch, splash HTTP server (port 4000), greenfield flow, auto-migrate |
| `scripts/dev-database-url.mjs` | `--database-name` override flow (rewrites `DATABASE_URL` db name, optionally persists to `.env`) |
| `packages/cli/src/bin.ts` | `mercato` binary entry: Node ≥24 assertion, bootstrap-free command list, generated-files bootstrap |
| `packages/cli/src/mercato.ts` | Command dispatcher + built-in modules (`queue`, `events`, `generate`, `db`, `server`, `test`, `deploy`), `init`, `seed:defaults` |
| `packages/cli/src/registry.ts` | `registerCliModules` / `getCliModules` in-process registry |
| `packages/cli/src/lib/db/commands.ts` | `dbGenerate` / `dbMigrate` / `dbGreenfield` — per-module MikroORM migrations |
| `packages/cli/src/lib/init-secrets.ts` | Derived admin/employee credentials for `init` |
| `packages/cli/src/lib/auto-spawn-workers.ts`, `auto-spawn-scheduler.ts` | Env resolution for eager/lazy/off worker & scheduler auto-spawn |
| `packages/cli/src/lib/queue-worker-supervisor.ts`, `scheduler-supervisor.ts` | Lazy supervisors (poll for pending jobs / enabled schedules, then spawn) |
| `packages/cli/src/lib/worker-connection-budget.ts` | Fits Σ worker concurrency to DB pool budget |
| `packages/cli/src/lib/server-start-lock.ts` | Production single-server lock file `.mercato/server-start.lock` |
| `packages/cli/src/lib/single-instance-strategy-guard.ts` | Refuses multi-instance prod boot on unsafe cache/queue/rate-limit strategies |
| `packages/cli/src/lib/in-process-generate-watcher.ts` | Structural regeneration watcher embedded in `server dev` |
| `packages/shared/src/lib/redis/connection.ts` | `getRedisUrl(prefix)` / `getRedisUrlOrThrow` — canonical Redis URL resolution |
| `packages/shared/src/lib/db/ssl.ts`, `packages/shared/src/lib/db/mikro.ts` | `getSslConfig()`, `resolvePoolConfig()` (DB pool env defaults) |
| `packages/shared/src/lib/bootstrap/factory.ts` | `createBootstrap()` registration order (entities → DI → modules → entity IDs → …) |
| `packages/cache/src/service.ts` | `createCacheService` + tenant-aware key hashing wrapper + memory fallback |
| `packages/cache/src/strategies/redis.ts` | Redis strategy: `cache:` / `tag:` key layout, JSON entries, tag sets |
| `packages/cache/src/tenantContext.ts` | `runWithCacheTenant` AsyncLocalStorage tenant scoping |
| `packages/core/src/modules/configs/cli.ts` | `configs cache stats|purge|structural` CLI; `STRUCTURAL_CACHE_REQUESTS` (`nav:*`, `admin-nav`, `portal-nav`) |
| `docker-compose.yml` | Infra-only stack: Postgres (pgvector), Redis, Meilisearch, LocalStack (profile), Verdaccio, opencode |
| `docker-compose.fullapp.yml` / `.fullapp.dev.yml` / `.fullapp.traefik*.yml` | Full app stacks (prod-like / dev with volume mounts / Traefik TLS overlays) |
| `docker-compose.preview.yaml` | Ephemeral preview app driving testcontainers via host Docker socket |
| `Dockerfile` | 3 stages: `builder` (full build), `dev` (packages only + dev entrypoint), `runner` (production, non-root `omuser`) |
| `docker/scripts/init-or-migrate.sh` | Marker-file init-once / migrate-thereafter logic |
| `docker/scripts/dev-entrypoint.sh`, `railway-entrypoint.sh` | Container entrypoints |
| `newrelic.js` | New Relic agent config (root, copied into image) |
| `railway.toml` | Railway deploy: Dockerfile build, healthcheck `/`, entrypoint script |

## How it works

### 1. apps/mercato — the host app

`apps/mercato` is a Next.js 16 app (Turbopack dev, `distDir: .mercato/next`) that hosts every module through **generated artifacts** in `apps/mercato/.mercato/generated/` (produced by `mercato generate`). There are only three route entry points:

- `src/app/api/[...slug]/route.ts` — catch-all for all `/api/*` module routes. At module scope it runs `bootstrap()` (from `src/bootstrap.ts`) and `registerApiRouteManifests(apiRoutes)` with the generated route table, then per-request: auth resolution, `requireFeatures` RBAC enforcement (`requireRoles` is deprecated and **ignored** with a one-time console warning), tenant selection enforcement, rate limiting, `runWithCacheTenant` wrapping, and dispatch to the module handler.
- `src/app/(backend)/backend/[...slug]/page.tsx` + `(frontend)/[...slug]` — page catch-alls resolved against generated page registries.
- `src/app/api/docs/openapi` and `/api/docs/markdown` — API documentation.

`src/bootstrap.ts` wires generated registries into `createBootstrap()` (`packages/shared/src/lib/bootstrap/factory.ts`), which registers in strict order: (1) ORM entities + DI registrars, (2) modules registry + integrations/bundles, (3) entity IDs, (4) entity-fields registry (encryption), (5) search configs, (6) analytics configs, enrichers, interceptors, component overrides, mutation guards, command interceptors, notification handlers, (7-8) UI widgets + optional packages asynchronously. `bootstrap()` is idempotent (guarded by `_bootstrapped`) except in `NODE_ENV=development` where it re-runs for HMR. `waitForAsyncRegistration()` lets CLI contexts await step 7-8.

`src/proxy.ts` (Next middleware) handles custom-domain portal routing: platform hosts pass through with `x-next-url` header set to the pathname; custom hosts get resolved via `customDomainResolver` and rewritten to `/{orgSlug}/portal/*`. A test-only `x-force-host` + `x-force-host-secret` (env `FORCE_HOST_SECRET`, only honored when `NODE_ENV=test`) allows Host spoofing for Playwright.

`src/instrumentation.ts` `register()` is intentionally a no-op.

### 2. `yarn dev` — two-layer dev orchestration

**Layer 1 — root `scripts/dev.mjs`** (invoked by root `yarn dev`):

- Detects mode: `monorepo` if `apps/mercato/package.json` + `packages/` exist, else `standalone` (a create-app project).
- Starts the **dev splash HTTP server** (default port **4000**, `OM_DEV_SPLASH_PORT`; `random`/`0` = ephemeral, `off` disabled; binds `127.0.0.1`, or `0.0.0.0` when `/.dockerenv` exists). Routes: `GET /` → HTML splash (template `scripts/dev-splash.html`, locale from `locale` cookie then `Accept-Language`, supported `en,pl,es,de`), `GET /status` → JSON of merged splash state, anything else → 404 `Not found`. Auto-opens a browser when TTY and `CI !== 'true'` and `OM_DEV_AUTO_OPEN !== '0'`.
- Child (app runner) shares splash state through a JSON file `.mercato/dev-splash-child-state.json` (env `OM_DEV_SPLASH_CHILD_STATE_FILE`) and a warmup marker file `apps/mercato/.mercato/dev-warmup-ready.json` (env `OM_DEV_WARMUP_READY_FILE`).
- Monorepo standard flow: `turbo run build --filter=./packages/*` (stage 1), start package watcher `yarn watch:packages` (stage 2; `OM_WATCH_PACKAGES_MODE=legacy` = per-package Turbo fan-out), then `yarn workspace @open-mercato/app dev` (stage 3).
- Greenfield flow (`yarn dev:greenfield`): purge app build caches → `build:packages` → `generate` → `build:packages` (again, so core gets `dist/generated/`) → `initialize -- --reinstall` → watch + app dev (5 stages).
- Standalone flow: optional local-registry cache refresh; **auto-migrate on by default** (`OM_DEV_AUTO_MIGRATE`, opt out with `0/false/no/off`) runs `yarn db:migrate` before launching; `--setup` mode additionally copies `.env.example` → `.env` (keeps existing), `yarn install`, `generate`, `db:migrate`, `initialize`.
- Defaults injected into the app child env: `OM_AUTO_SPAWN_WORKERS_LAZY=true` and `OM_AUTO_SPAWN_SCHEDULER_LAZY=true` (only when not explicitly set) — dev boots background services lazily.
- Signals: `SIGINT` → exit 130, `SIGTERM` → 143; children get SIGTERM then SIGKILL after 3 s (`killProcessTree`). An unexpected child exit surfaces as non-zero even if the child reported 0.

**Layer 2 — `apps/mercato/scripts/dev.mjs`** (the app runner):

- Resolves the project-local `mercato` binary; spawns `mercato generate` first, then `mercato server dev` (in `--classic` mode with `stdio: inherit`; otherwise piped with a log classifier that turns known lines into splash/progress updates and buffers raw logs, toggled with the `d` key).
- Legacy sidecar `mercato generate watch --skip-initial` only when `OM_DEV_GENERATE_WATCH_MODE=legacy`; the default is the in-process watcher inside `server dev` (saves ~190 MB RSS).
- **Targeted route warmup** once the Next.js `✓ Ready in …` line and the `- Local: <url>` line are seen: `GET /login` → `POST /api/auth/login` (form-urlencoded `email`, `password`, optional `tenantId`; credentials from `OM_INIT_SUPERADMIN_EMAIL`/`OM_INIT_SUPERADMIN_PASSWORD`, defaults `superadmin@acme.com` / `secret`) → `GET /backend` with the returned cookies. Timeouts 45 s then 120 s per request; transient statuses (404, 408, 425, 429, ≥500) and redirects to `/login` or `/api/auth/session/refresh` retry up to 3 times with 2 s delay; a 401 marks the app ready anyway with a "credentials invalid, run yarn initialize" warning. On success it writes the warmup ready file `{"ready":true,"reason":"warmup-complete","at":"<iso>"}` — which `server dev` waits on before starting workers/scheduler. If login fails with a tenant-selection error it resolves `tenantId` directly from Postgres (`select tenant_id from users where … lower(email) = $1`).
- Memory monitor samples the process tree RSS every 5 s (non-win32) and reports to splash/console.

### 3. `mercato` CLI (`packages/cli`)

`bin.ts`: asserts **Node 24.x** (`Unsupported Node.js runtime…` error otherwise). Commands in `BOOTSTRAP_FREE_COMMANDS` (`generate, module, deploy, db, init, agentic:init, eject, test*, umes:*, help`) run without generated files; everything else calls `bootstrapFromAppRoot(appDir)` (dynamic TS loader) and `registerCliModules(data.modules)`. Missing generated files print a boxed error telling the user to run `yarn mercato generate` and exit 1.

`run()` in `mercato.ts` first calls `ensureEnvLoaded()`: locates the app dir via the resolver, **copies `.env.example` → `.env` if `.env` is missing and `NODE_ENV !== 'production'`** (logging `📋 Copied .env.example → .env`), then `dotenv.config({ path: appDir/.env })` (quiet when `OM_CLI_QUIET=1` / `DOTENV_CONFIG_QUIET`).

Dispatch model: `mercato <module> <command> [args]`. Module CLIs come from discovered module `cli.ts` files plus built-ins registered inline: `deploy` (railway), `queue` (`worker <name>|--all [--concurrency=N]`, `clear <name>`, `status <name>`), `events` (`emit <event> [json] [--persistent]`, `clear`), `generate` (`all` (default), `watch`, `entity-ids`, `registry`, `entities`, `di`), `db` (`generate`, `migrate`, `greenfield --yes`), `server` (`dev`, `start`), `test` (integration/ephemeral suites). App-level commands can be added via `@/cli`. Output protocol: banner box `🧩 Open Mercato CLI` (suppressed by `OM_CLI_QUIET=1`), `🚀 Running <mod>:<cmd> <args>` before, `⏱️ Done in <ms>ms` after, `💥 Failed: <message>` + exit 1 on error. DB connectivity failures produce actionable messages ("PostgreSQL at host:port/db is not reachable: it refused the connection…").

Shorthand aliases: `mercato generate` → `generate all`; `reindex` → `query_index reindex`; `test:integration*` → `test …`; `seed:defaults` and `init` handled directly.

### 4. `mercato db …` — per-module migrations

Each enabled module gets its **own MikroORM migration table** `mikro_orm_migrations_<sanitized moduleId>` and its own `migrations/` directory (module `data/entities.ts` or `db/schema.ts` discovered per module; app modules under `src/modules/<id>/migrations`, package modules under the package `src|dist/modules/<id>/migrations`). Modules process alphabetically by id.

- `db generate`: for each module, clears the global `MetadataStorage` (so migrations don't leak other modules' tables), inits a throwaway MikroORM (pool 1–3), creates a diff migration with snapshot name `.snapshot-open-mercato`; the initial migration is created only when neither snapshot nor existing `Migration*.{ts,js}` files exist. Generated file is renamed to append `_<moduleId>` (deduped), the class renamed `Migration<ts>_<moduleId>`, and every `alter table … drop constraint X` is rewritten to `drop constraint if exists X` (`makeConstraintDropsIdempotent`).
- `db migrate`: same per-module setup but with `entities: []` and `snapshot: false` — it only *executes* committed migration files (never auto-diffs), applying pending ones one at a time with a progress bar; prints `<mod>: N migrations applied` / `no pending migrations`.
- `db greenfield --yes`: deletes all `Migration*.ts` files and `*snapshot*.json` per module, deletes `*.checksum` files in the generated output dir, `DROP TABLE IF EXISTS mikro_orm_migrations_<mod>` for every module (in one transaction), drops **all** tables in `current_schema()` with `session_replication_role='replica'`, then re-runs `db generate` + `db migrate`. Without `--yes`: prints `This command will DELETE all data. Use --yes to confirm.` and exits 1.
- `ensureDatabaseExists(dbUrl)` (used by `init`): connects to the same server's `/postgres` maintenance DB, checks `pg_database`, and issues `CREATE DATABASE "<name>"` when missing (with manual-recovery instructions on failure).

### 5. `mercato init` — fresh-install initialization sequence

Flags: `--reinstall|-r`, `--no-examples`, `--stresstest [--count=N|-n N] [--lite]`, `--org=<name>`, `--email=`, `--password=`, `--roles=`, `--skip-password-policy` (**default true** — demo passwords bypass policy). Sets `OM_INIT_FLOW=true` (+ `OM_INIT_REINSTALL=true` when reinstalling) in env for downstream seeders.

1. **Reinstall path**: requires `DATABASE_URL`; `ensureDatabaseExists`; drops every table in `current_schema()` (plus forced `vector_search`, `vector_search_migrations`) in one transaction; then **flushes Redis** (`FLUSHALL` on `getRedisUrl()` — silently skipped when no `REDIS_URL`, logged either way).
2. **Non-reinstall guard**: if `public.users` exists and has rows → `❌ Initialization aborted: found N existing user(s) in the database.` + hints (`yarn mercato init --reinstall`, `yarn setup --reinstall`, `yarn reinstall`), exit 1. **Docker's `init-or-migrate.sh` greps for this exact message.**
3. Runs all generators in-process (entity-ids, registries [app/cli], entities, DI, package sources, OpenAPI), quiet.
4. `dbMigrate` (applies all module migrations).
5. `bootstrapFromAppRoot` + `registerCliModules` (dynamic module load).
6. `configs restore-defaults` (vector auto-index default from `OM_DISABLE_VECTOR_SEARCH_AUTOINDEXING`, notifications delivery config).
7. **RBAC/tenant setup**: `auth setup --orgName "Acme Corp" --email superadmin@acme.com --password secret --roles superadmin,admin,employee [--skip-password-policy]` (defaults overridable by `OM_INIT_SUPERADMIN_EMAIL` / `OM_INIT_SUPERADMIN_PASSWORD` and CLI flags). Derived users via `resolveInitDerivedSecrets`: `admin@<email-domain>` and `employee@<email-domain>` (overridable `OM_INIT_ADMIN_EMAIL`/`OM_INIT_EMPLOYEE_EMAIL`), passwords `OM_INIT_ADMIN_PASSWORD`/`OM_INIT_EMPLOYEE_PASSWORD`, else `secret`, or random base64url(9 bytes) when `OM_INIT_GENERATE_RANDOM_PASSWORD=true`.
8. Reads back tenant/org: `SELECT o.id, o.tenant_id FROM organizations o JOIN users u ON u.organization_id = o.id LIMIT 1` — aborts if absent.
9. `feature_toggles seed-defaults` (optional), `auth seed-roles --tenant <id>`, `entities reinstall --tenant` (reinstall only), `entities seed-encryption --tenant --org` (when `TENANT_DATA_ENCRYPTION` parses truthy; **default yes**).
10. **Module defaults**: for every module with `setup.seedDefaults(ctx)` where `ctx = { em, tenantId, organizationId, container }` (single shared request container). Then `ensureCustomRoleAcls(em, tenantId, allModules)` (second ACL pass for roles created during seeding).
11. **Example data**: every module's `setup.seedExamples(ctx)` unless `--no-examples`. Optional stress-test customers.
12. `dashboards seed-defaults --tenant`, `dashboards enable-analytics-widgets --tenant --roles admin,employee` (optional).
13. `search reindex --tenant --org` and `query_index reindex --force --tenant` (optional).
14. Prints a boxed summary with each created user + password. Returns 0; any error → `❌ Initialization failed: <msg>`, return 1.

### 6. `mercato seed:defaults`

Bootstraps, resolves all non-deleted `Organization`s (with tenant), errors `❌ No organizations found. Run yarn initialize first.` (exit 1) when empty, then for each org runs every module's `setup.seedDefaults` (optionally filtered by `--module <id>`) followed by `ensureCustomRoleAcls`. Idempotent by contract (seedDefaults must upsert).

### 7. `mercato server dev|start` — process supervisor

Both commands build the child env with `buildServerProcessEnvironment`: **sets `NODE_ENV=production`** on the spawned processes and manages New Relic preload — strips any `-r newrelic`/`--require=newrelic` from `NODE_OPTIONS`, and re-appends `-r newrelic` **only when `NEW_RELIC_LICENSE_KEY` is non-empty**.

`server dev`:
- Ensures `module-package-sources.css` exists, resolves `next/dist/bin/next` and `@open-mercato/cli/bin/mercato` from `node_modules`.
- Spawns `node <nextBin> dev --turbopack` (cwd = appDir), watching combined output. Ready when output matches `/\bready in\b/i`. Detects **Turbopack cache corruption** (all three patterns: `Failed to restore task data (corrupted database or bug)`, `Unable to open static sorted file`, `TurbopackInternalError`) → removes `.mercato/next/dev` and restarts Next **once**.
- Watches `.env` files (`watchDevEnvFiles`); on change, reloads env (`createDevEnvReloader` resets to the initial-process env + re-reads .env) and restarts the whole runtime loop (`[server] Detected environment file change (<file>). Restarting app runtime...`).
- After Next is ready **and** the dev warmup marker file appears (or `OM_DEV_WARMUP_READY_TIMEOUT_MS`, default 300 000 ms, elapses with a warning), starts background services:
  - **Workers** (`resolveAutoSpawnWorkersMode`): `eager` → spawn `node <mercatoBin> queue worker --all`; `lazy` → `startLazyWorkerSupervisor` (polls queues for pending jobs at `OM_AUTO_SPAWN_WORKERS_LAZY_POLL_MS`, default 1000 ms/min 250, spawns the worker on demand, restarts on unexpected exit unless `OM_AUTO_SPAWN_WORKERS_LAZY_RESTART=false`); `off` → nothing. `applyEventsSingleDeliveryGuard` runs first so persistent events fall back to inline dual-dispatch when no events worker will run in this process.
  - **Scheduler**: only when `QUEUE_STRATEGY === 'local'` and the `scheduler` module exposes a `start` CLI: eager `node <mercatoBin> scheduler start` or lazy supervisor (analogous `OM_AUTO_SPAWN_SCHEDULER_*` envs).
- Runs the in-process generate watcher (structural checksum over module roots + `src/modules.ts`; on change re-runs the full generator suite + structural cache purge).
- Cleanup kills children with SIGTERM, waits for exit, removes the Next dev lock `.mercato/next/dev/lock`.

`server start` (production):
- `applyEventsSingleDeliveryGuard`, then `assertSingleInstanceStrategies(runtimeEnv)` — **throws before anything starts** when `NODE_ENV=production`, a multi-instance topology is declared (`OM_MULTI_INSTANCE` truthy or `OM_INSTANCE_COUNT > 1`), any of `CACHE_STRATEGY`/`QUEUE_STRATEGY`/`RATE_LIMIT_STRATEGY` is not multi-instance-safe (safe sets: cache `redis`, queue `async`, rateLimit `redis`), and `OM_ALLOW_SINGLE_INSTANCE_STRATEGIES` is not set. Production single-instance with unsafe strategies logs a prominent warning instead.
- `acquireServerStartLock(appDir, { port })` — writes `{pid, port, startedAt}` JSON to `.mercato/server-start.lock` with `wx`; if it exists and the recorded pid is alive (`process.kill(pid, 0)`, EPERM counts as alive) it throws `[server] Another Open Mercato production server is already running for <dir> (pid N on port P)…`; stale locks are removed and retried. Released on exit.
- `ensureNextBuildIdInConfiguredDistDir`: reconstructs `.mercato/next/BUILD_ID` from build artifacts (or from a stray `.next/`) so `next start` doesn't fail after partial copies.
- Spawns `node <nextBin> start` + workers + scheduler exactly as in dev (no warmup gate). First unexpected child exit (non-SIGINT/SIGTERM, or non-zero when not stopping) → cleanup, throw `[server] <label> exited unexpectedly with exit code N`.

Worker sizing: `queue worker --all` computes requested per-queue concurrency (max of worker declarations, or `--concurrency=` override), then `planWorkerConcurrency` clamps Σ concurrency to `resolveWorkerConnectionBudget(env, poolMax)` (default `DB_POOL_MAX`, override `OM_WORKERS_DB_CONNECTION_BUDGET`), floor 1 per queue, logging `[worker] DB connection budget: …`. Worker startup log lines are a de-facto contract (`[worker] Starting workers for all queues: a, b`, `[worker] Starting "<q>" with N handler(s), concurrency: C`, `[worker] All workers started. Press Ctrl+C to stop`) — the dev runner parses them.

### 8. Docker & deployment

**`Dockerfile`** (node:24-alpine, corepack/yarn 4):
- `builder`: apk `python3 make g++ ca-certificates openssl`; `yarn install`; `NODE_OPTIONS=--max-old-space-size=4096`; `yarn build` (= `build:packages && generate && build:packages && build:app`).
- `dev`: install + `yarn build:packages` only; `CMD /app/docker/scripts/dev-entrypoint.sh`; EXPOSE 3000.
- `runner`: `yarn workspaces focus @open-mercato/app --production`; copies `.mercato/next`, `.mercato` (generated files), `src`, `public`, configs, `newrelic.js`; creates non-root `omuser` (uid 1001) with passwordless sudo **for `/bin/chown` only** (Railway volume ownership); `WORKDIR /app/apps/mercato`; `CMD ["yarn","start"]` (→ `mercato server start`); `EXPOSE ${CONTAINER_PORT}` (default 3000), `ENV PORT=${CONTAINER_PORT}`.

**`docker-compose.yml`** (infra only, network `mercato-network`):
| service | image | ports (host default) | healthcheck |
|---|---|---|---|
| postgres | `pgvector/pgvector:pg17-trixie` | `POSTGRES_PORT:-5432` | `pg_isready -U ${POSTGRES_USER:-postgres}` 10s/5s/5 |
| redis | `redis:7-alpine` (custom redis.conf, `--maxmemory ${REDIS_MAXMEMORY:-512mb}`) | `REDIS_PORT:-6379` | `redis-cli ping` 10s/5s/5 |
| meilisearch | `getmeili/meilisearch:v1.11` (`MEILI_MASTER_KEY:-meilisearch-dev-key`) | `MEILISEARCH_PORT:-7700` | `curl -fsS http://127.0.0.1:7700/health` |
| localstack | latest, profile `storage-s3` | 4566 | `curl …/_localstack/health` retries 10 |
| verdaccio | v6 (local npm registry) | 4873 | wget `/-/ping` |
| opencode | local build (AI sidecar) | `OPENCODE_PORT:-4096` | — |
Postgres env: `POSTGRES_USER/PASSWORD` default `postgres`, `POSTGRES_DB` default `open-mercato`.

**`docker-compose.fullapp.yml`** (app + infra, DB/Redis/Meili not host-exposed): `app` runs as root (`user: "0"`), command:
```sh
INIT_COMMAND='yarn mercato init' sh /app/docker/scripts/init-or-migrate.sh
yarn start
```
with volumes `init_marker:/tmp/init-marker` and `attachments_storage:/app/apps/mercato/storage`; `depends_on` postgres/redis/meilisearch `condition: service_healthy`. Key app env wiring: `NODE_ENV: development` (intentional for the demo stack), `NODE_OPTIONS:---max-old-space-size=3072`, `PORT=${CONTAINER_PORT:-3000}`, `DATABASE_URL: postgres://…@mercato-postgres-${DEPLOY_ENV:-local}:5432/${POSTGRES_DB:-open-mercato}`, `JWT_SECRET:-JWT`, `APP_URL:-http://localhost:3000`, `CACHE_REDIS_URL: redis://mercato-redis-<env>:6379`, `CACHE_STRATEGY: redis`, `ENABLE_CRUD_API_CACHE:-true`, `MEILISEARCH_HOST`, `OM_INIT_SUPERADMIN_PASSWORD:-password`, `TENANT_DATA_ENCRYPTION:-true` (+ fallback key), `SELF_SERVICE_ONBOARDING_ENABLED:-true`, `DEMO_MODE:-true`, custom-domain vars (`DOMAIN_CHECK_SECRET`, `DOMAIN_RESOLVE_SECRET`, `INTERNAL_APP_ORIGIN:-http://app:3000`, `PLATFORM_DOMAINS`, …). Traefik TLS/ACME is an opt-in overlay file.

**`docker/scripts/init-or-migrate.sh`** — the init-once contract:
- Marker file `${INIT_MARKER_FILE:-/tmp/init-marker/.seeded}`; commands `${INIT_COMMAND:-yarn initialize}` and `${MIGRATE_COMMAND:-yarn db:migrate}`.
- No marker → run INIT_COMMAND. On success: create marker, exit 0. On failure: if the log matches `Initialization aborted: found [0-9]+ existing user\(s\) in the database\.` → run migrations instead, create marker; else propagate exit code.
- Marker present → migrations only.
- Both commands retry once after `yarn install` when the log contains `command not found: mercato`.

**Dev compose** (`fullapp.dev.yml`): builds Dockerfile `target: dev`, mounts the source tree, exposes the dev splash port (`OM_DEV_SPLASH_PORT:-4000`), entrypoint `dev-entrypoint.sh` (yarn install → build:packages → generate → build:packages → init-or-migrate with `yarn mercato init` → `exec yarn dev`; a `/tmp/docker-exec-skip-rebuilt.skip` file short-circuits to `yarn dev` on restarts). Meilisearch runs `start-dev` variant.

**Railway** (`railway.toml` + `railway-entrypoint.sh`): Dockerfile build; deploy start = entrypoint (sudo chown storage volume → init-or-migrate with marker at `/app/apps/mercato/storage/.initialized` → `exec yarn start`); **healthcheckPath = `/`**, timeout 360 s, restart ON_FAILURE ×5.

### 9. Observability — `newrelic.js`

Agent activated purely by env: `NEW_RELIC_LICENSE_KEY` set → CLI injects `-r newrelic` into child `NODE_OPTIONS`; app name `NEW_RELIC_APP_NAME` (default `open-mercato`). Config: distributed tracing on, transaction tracer with `record_sql: 'obfuscated'`, `explain_threshold: 1`, slow SQL on, log forwarding on, `attributes.include: ['request.*']` with an explicit exclude list of sensitive request headers: `cookie, authorization, x-api-key, x-sudo-token, x-domain-check-secret, x-domain-resolve-secret, x-force-host-secret, x-webhook-signature, svix-signature`. `allow_all_headers` deliberately **not** enabled (fail-closed for future custom auth headers).

### 10. Cache package (`packages/cache`)

`createCacheService(options?)` picks a strategy from `options.strategy` → `CACHE_STRATEGY` env → `'memory'` (valid: `memory | redis | sqlite | jsonfile`); default TTL from `CACHE_TTL` (ms); memory LRU cap `CACHE_MEMORY_MAX_ENTRIES` (default 50 000 per docs). Non-memory strategies get a **dependency fallback wrapper**: if the backing module can't load (`CacheDependencyUnavailableError`, e.g. ioredis/better-sqlite3 missing) every call transparently falls back to a memory strategy with one `console.warn('[cache] <strategy> strategy unavailable … Falling back to memory strategy.')`.

**Tenant-aware wrapper** (always applied): tenant comes from `AsyncLocalStorage` (`runWithCacheTenant(tenantId, fn)`, `null` → `'global'`; sanitized `[^a-zA-Z0-9._-] → _`). Logical key/tag → physical storage names:
- value: `tenant:<tenant>:key:k:<sha1hex(key)>`
- metadata: `tenant:<tenant>:key:meta:<sha1hex(key)>` storing `{ key: <original>, expiresAt: <ms|null> }` (this is how `keys(pattern)` and `stats()` recover original names)
- tag: `tenant:<tenant>:tag:t:<sha1hex(tag)>`, plus an implicit scope tag `tenant:<tenant>:tag:__scope__` on every set (so `clear()` = `deleteByTags([scopeTag])`). Deletion counts are halved (`Math.ceil(raw/2)`) to hide the meta twin.

**Redis strategy** (`strategies/redis.ts`): connection URL = explicit arg → `getRedisUrlOrThrow('CACHE')` (`CACHE_REDIS_URL` → `REDIS_URL` → throw). Shared ref-counted ioredis client registry per URL. On-wire layout (note: these prefixes wrap the tenant-aware names above):
- `cache:<storageKey>` → JSON `{"key":…,"value":…,"tags":[…],"expiresAt":<ms|null>,"createdAt":<ms>}`; TTL via `SETEX` with `ceil(ttl/1000)` seconds.
- `tag:<tagName>` → Redis SET of member storage keys; sets update tag membership (SREM old tags, SADD new).
- `deleteByTags` = SMEMBERS per tag → per-key delete; `clear`/`keys`/`stats`/`cleanup` use `KEYS cache:*` scans; `healthcheck()` = `PING`.

So a fully-qualified Redis key looks like: `cache:tenant:<tenantId>:key:k:<sha1>` and `tag:tenant:<tenantId>:tag:t:<sha1>`.

**Structural/nav cache**: logical keys like `nav:sidebar:<locale>:<userId>:<tenantId>:<orgId>` tagged `nav:sidebar:user:<userId>`. `mercato configs cache structural` purges `STRUCTURAL_CACHE_REQUESTS = [{pattern:'nav:*'}, {segment:'admin-nav'}, {segment:'portal-nav'}]` (scoped `--tenant <id>` | `--global` | `--all-tenants`; default global only) and touches generated barrels; the generator suite runs `configs cache structural --all-tenants --quiet` after every regeneration. Other subcommands: `configs cache stats [--json]`, `configs cache purge --all|--segment|--tag|--key|--id|--pattern [--dry-run]`.

### 11. Health / readiness

There is **no dedicated application health endpoint**. Signals available:
- `GET /` (Next page) — used by Railway (`healthcheckPath = "/"`).
- `GET /_next/health` — Next.js internal; `next.config.ts` attaches a marker header (name from `CUSTOMER_DOMAIN_ORIGIN_HEADER`, default `X-Open-Mercato-Origin`, value `1`) so custom-domain DNS verification can confirm "this origin answered".
- Infra healthchecks live in compose (pg_isready / redis-cli ping / Meili `/health`).
- Dev-only: splash `GET :4000/status` JSON (`{mode, phase, detail, failed, failureLines, failureCommand, ready, readyUrl, loginUrl, memoryCurrentBytes, memoryPeakBytes, packageNames, workerQueues:[{queue,handlers,concurrency}], schedulerActive, workerMode, schedulerMode, progressCurrent, progressTotal, progressPercent, progressLabel, activities:[…]}`), and the warmup marker file.
A port should provide a real `/health`-style endpoint as an improvement, but must keep `/` returning 200 for parity with the Railway config and add the `/_next/health` marker-header semantics if custom domains are supported.

## Public contracts

**CLI command surface** (must exist under whatever `mercato`-equivalent the port ships):
```
mercato init [--reinstall] [--no-examples] [--org=] [--email=] [--password=] [--roles=] [--skip-password-policy] [--stresstest --count=N --lite]
mercato seed:defaults [--module <id>]
mercato generate [all|watch|entity-ids|registry|entities|di]
mercato db generate | db migrate | db greenfield --yes
mercato server dev | server start
mercato queue worker <queue>|--all [--concurrency=N] | queue clear <q> | queue status <q>
mercato events emit <event> [json] [--persistent] | events clear
mercato configs cache stats|purge|structural [--tenant|--global|--all-tenants] [--dry-run] [--json]
mercato <module> <command> [args...]        # module cli.ts dispatch
```
Exit codes: 0 success, 1 any failure; runner prints `🚀 Running <mod>:<cmd> …` / `⏱️ Done in Xms` / `💥 Failed: <msg>`.

**`queue status` output** (parseable):
```
Queue "<name>" status:
  Waiting:   N
  Active:    N
  Completed: N
  Failed:    N
```

**Init abort message** (grepped by `init-or-migrate.sh` — byte-exact pattern):
```
❌ Initialization aborted: found <N> existing user(s) in the database.
```
regex used: `Initialization aborted: found [0-9][0-9]* existing user\(s\) in the database\.`

**Default seeded identities** (from `mercato init` with no flags/env):
| user | email | password | roles |
|---|---|---|---|
| Superadmin | `superadmin@acme.com` | `secret` | superadmin |
| Admin | `admin@acme.com` (derived from superadmin domain) | `secret` (or `OM_INIT_ADMIN_PASSWORD` / random) | admin |
| Employee | `employee@acme.com` | `secret` | employee |
Org name: `Acme Corp`. Roles seeded: `superadmin,admin,employee`.

**Postgres structures owned by this subsystem**:
- `mikro_orm_migrations_<moduleId>` — one migration-history table per module (moduleId sanitized `[^a-z0-9_]i → _`; validated `^[a-zA-Z_][a-zA-Z0-9_]*$`).
- Migration snapshot file per module dir: `.snapshot-open-mercato.json`.
- Init queries used verbatim: `SELECT 1 FROM pg_database WHERE datname = $1`; `SELECT to_regclass('public.users')`; `SELECT COUNT(*)::text FROM users`; `SELECT o.id as org_id, o.tenant_id FROM organizations o JOIN users u ON u.organization_id = o.id LIMIT 1`; `SELECT tablename FROM pg_tables WHERE schemaname = current_schema()`.

**Redis structures**:
- Cache entries: key `cache:tenant:<tenant|global>:key:k:<sha1(logicalKey)>`, value JSON `{"key","value","tags","expiresAt","createdAt"}`; twin meta key `…:key:meta:<sha1>` with `{"key":"<logicalKey>","expiresAt":…}`.
- Tag indexes: SET `tag:tenant:<tenant>:tag:t:<sha1(tag)>` and scope SET `tag:tenant:<tenant>:tag:__scope__`.
- Nav cache logical keys: `nav:sidebar:<locale>:<userId>:<tenantId>:<orgId>` (tag `nav:sidebar:user:<userId>`); purged via pattern `nav:*` + segments `admin-nav`, `portal-nav`.
- `mercato init --reinstall` issues `FLUSHALL`.
- (Queues: BullMQ keys per queue when `QUEUE_STRATEGY=async` — covered by the queue subsystem doc.)

**Filesystem contracts** (relative to app dir):
- `.mercato/generated/*` — generated registries (required before any bootstrapped command).
- `.mercato/next/` — Next `distDir`; `.mercato/next/BUILD_ID`; dev lock `.mercato/next/dev/lock`.
- `.mercato/server-start.lock` — production lock, JSON `{"pid":N,"port":"3000"|null,"startedAt":"<iso>"}`.
- `.mercato/dev-warmup-ready.json` — `{"ready":true,"reason":"warmup-complete|warmup-incomplete|warmup-credentials-failed","at":"<iso>"}`.
- `.mercato/dev-splash-child-state.json` — child splash state JSON.
- `.mercato/queue/` — local file-queue base dir (`QUEUE_BASE_DIR`).
- `.mercato/cache/cache.db` / `cache.json` — sqlite/jsonfile cache defaults.
- Docker init markers: `/tmp/init-marker/.seeded` (compose) / `/app/apps/mercato/storage/.initialized` (Railway).

**Dev splash HTTP** (`http://127.0.0.1:4000`): `GET /` HTML, `GET /status` → the JSON state shape listed in §11, else 404.

**Complete env var inventory** (name → default → consumer). Bold = required for a working stack.

*Core connectivity & auth*
| var | default | notes |
|---|---|---|
| **DATABASE_URL** | — (required) | pg connection string; `sslmode=require`/`ssl=true` in URL enables SSL |
| **JWT_SECRET** | — | auth token signing (`AUTH_SECRET` preferred when set; `NEXTAUTH_SECRET` alias) |
| REDIS_URL | — (no localhost fallback!) | base Redis URL |
| QUEUE_REDIS_URL / CACHE_REDIS_URL / EVENTS_REDIS_URL | → REDIS_URL | per-subsystem overrides via `getRedisUrl(prefix)` |
| QUEUE_STRATEGY | `local` | **the async/BullMQ value is `async`** (anything else = local file queue) |
| CACHE_STRATEGY | `memory` (code) / `sqlite` (.env.example) / `redis` (docker) | `memory|redis|sqlite|jsonfile` |
| CACHE_TTL | unset | default TTL ms |
| CACHE_MEMORY_MAX_ENTRIES | 50000 | memory LRU cap |
| CACHE_SQLITE_PATH / CACHE_JSON_FILE_PATH | `.mercato/cache/cache.db` / `.json` | |
| OM_CACHE_SINGLETON | on | process-wide cache singleton toggle |
| RATE_LIMIT_ENABLED / RATE_LIMIT_STRATEGY / RATE_LIMIT_KEY_PREFIX | true / memory / `rl` | plus RATE_LIMIT_LOGIN_* etc. overrides, RATE_LIMIT_TRUST_PROXY_DEPTH=1 |
| APP_URL / NEXT_PUBLIC_APP_URL / NEXTAUTH_URL | `http://localhost:3000` | base URL resolution order in `resolveDevRuntimeBaseUrl`: APP_URL → NEXT_PUBLIC_APP_URL → NEXTAUTH_URL → `http://localhost:${PORT:-3000}` |
| APP_ALLOWED_ORIGINS | — | extra trusted origins (also HMR WS) |
| PORT | 3000 | app port |

*DB pool (code defaults from `resolvePoolConfig`; .env.example ships different values)*
| var | code default | .env.example |
|---|---|---|
| DB_POOL_MIN | 2 | 5 |
| DB_POOL_MAX | 20 | 20 |
| DB_POOL_IDLE_TIMEOUT | 3000 | 10000 |
| DB_POOL_ACQUIRE_TIMEOUT | 6000 | 10000 |
| DB_IDLE_SESSION_TIMEOUT_MS | 600000 (non-prod), unset in prod | — |
| DB_IDLE_IN_TRANSACTION_TIMEOUT_MS | 120000 | — |
| DB_STATEMENT_TIMEOUT_MS / DB_LOCK_TIMEOUT_MS | unset (no timeout) | — |
| DB_SSL / DB_SSL_REJECT_UNAUTHORIZED | false / true | `getSslConfig()` |
| OM_WORKERS_DB_CONNECTION_BUDGET | = DB_POOL_MAX | worker Σconcurrency cap |
| OM_DB_POOL_DEBUG | — | `1` logs pool config |

*Initialization*
| var | default |
|---|---|
| OM_INIT_SUPERADMIN_EMAIL | `superadmin@acme.com` |
| OM_INIT_SUPERADMIN_PASSWORD | `secret` |
| OM_INIT_ADMIN_EMAIL / OM_INIT_EMPLOYEE_EMAIL | `admin@<domain>` / `employee@<domain>` |
| OM_INIT_ADMIN_PASSWORD / OM_INIT_EMPLOYEE_PASSWORD | `secret` |
| OM_INIT_GENERATE_RANDOM_PASSWORD | false (true → random base64url passwords) |
| OM_INIT_FLOW / OM_INIT_REINSTALL | set internally by `init` |
| TENANT_DATA_ENCRYPTION | `yes` (truthy default) |
| TENANT_DATA_ENCRYPTION_FALLBACK_KEY / _KEY / _DEBUG, LOOKUP_HASH_PEPPER | dev fallback key / — / false / — |
| VAULT_ADDR / VAULT_TOKEN / VAULT_KV_PATH / VAULT_REQUEST_TIMEOUT_MS | — / — / — / 1000 |

*Background services*
| var | default |
|---|---|
| AUTO_SPAWN_WORKERS (legacy) / OM_AUTO_SPAWN_WORKERS | true |
| OM_AUTO_SPAWN_WORKERS_LAZY | false (dev orchestrator injects true) |
| OM_AUTO_SPAWN_WORKERS_LAZY_POLL_MS / _LAZY_RESTART | 1000 (min 250) / true |
| AUTO_SPAWN_SCHEDULER / OM_AUTO_SPAWN_SCHEDULER (+_LAZY, _LAZY_POLL_MS, _LAZY_RESTART) | true / false / 1000 / true |
| QUEUE_BASE_DIR | `./.mercato/queue` |

*Topology guard & server*
| var | default |
|---|---|
| OM_MULTI_INSTANCE | false — truthy declares multi-instance topology |
| OM_INSTANCE_COUNT | — (>1 also declares multi-instance) |
| OM_ALLOW_SINGLE_INSTANCE_STRATEGIES | false — override the hard failure |
| NODE_OPTIONS | — (docker sets `--max-old-space-size=3072`) |
| NEW_RELIC_LICENSE_KEY / NEW_RELIC_APP_NAME | — / `open-mercato` (license key presence toggles `-r newrelic`) |

*Dev orchestration (OM_DEV_*)*: OM_DEV_SPLASH_PORT (4000; `random`, `off`), OM_DEV_AUTO_OPEN (`0` disables browser open), OM_DEV_AUTO_MIGRATE (on), OM_DEV_CLASSIC, MERCATO_DEV_OUTPUT=verbose, OM_DEV_LOG_TEE (`0` disables log capture), OM_DEV_LOG_DIR (`.mercato/logs`), OM_DEV_RUN_ID, OM_DEV_GENERATE_WATCH_MODE (`in-process`|`legacy`), OM_DEV_WARMUP_READY_TIMEOUT_MS (300000), OM_DEV_WARMUP_TENANT_ID, OM_DEV_SPLASH_CHILD_STATE_FILE, OM_DEV_WARMUP_READY_FILE, OM_DEV_SPLASH_MODE/_STAGE_CURRENT/_STAGE_TOTAL, OM_WATCH_PACKAGES_MODE (`legacy`), OM_SKIP_LOCAL_PACKAGE_REFRESH, OM_CLI_QUIET, MERCATO_QUIET, DOTENV_CONFIG_QUIET, MERCATO_CLI_DEBUG_IMPORTS.

*Feature/application toggles* (defaults in `.env.example`): SELF_SERVICE_ONBOARDING_ENABLED=false, DEMO_MODE=true, ENABLE_CRUD_API_CACHE=false, OM_ENABLE_ENTERPRISE_MODULES(=false, +_SSO, +_SECURITY), OM_ENABLE_STORAGE_S3=false, MEILISEARCH_HOST/API_KEY/INDEX_PREFIX(`om`)/REQUEST_TIMEOUT_MS(30000), OM_SEARCH_* (ENABLED=true, MIN_LEN=3, ENABLE_PARTIAL=true, HASH_ALGO=sha256, STORE_RAW_TOKENS=false), OM_DISABLE_VECTOR_SEARCH_AUTOINDEXING=true (legacy alias DISABLE_VECTOR_SEARCH_AUTOINDEXING), SCHEDULE_AUTO_REINDEX=true, FORCE_QUERY_INDEX_ON_PARTIAL_INDEXES=true, AUDIT_LOGS_CORE_RETENTION_DAYS=7 / NON_CORE_RETENTION_HOURS=8 / ROTATE_INTERVAL_MS=60000, ADMIN_EMAIL / EMAIL_FROM / NOTIFICATIONS_EMAIL_FROM (sender priority NOTIFICATIONS_EMAIL_FROM → EMAIL_FROM → ADMIN_EMAIL), RESEND_API_KEY, OM_AI_PROVIDER=openai / OM_AI_MODEL=gpt-5-mini (+ OPENCODE_* legacy fallbacks, per-module OM_AI_<MODULE>_*, allowlists), OCR_MODEL=gpt-4o, custom-domain block (PLATFORM_PRIMARY_HOST, DOMAIN_CHECK_SECRET, DOMAIN_RESOLVE_SECRET, INTERNAL_APP_ORIGIN, PLATFORM_DOMAINS, CUSTOMER_DOMAIN_ORIGIN_HEADER=`X-Open-Mercato-Origin`, DOMAIN_CACHE_TTL_SECONDS=60 …), CUSTOMER_SESSION_TTL_DAYS=30, MAX_CUSTOMER_SESSIONS_PER_USER=5, FORCE_HOST_SECRET (test only), OM_INTEGRATION_TEST, OM_INTEGRATION_STRIPE_* / _AKENEO_* / _STORAGE_S3_* preconfiguration blocks.

## Helpers to mirror

```ts
// packages/shared/src/lib/redis/connection.ts
getRedisUrl(prefix?: string): string | null            // <PREFIX>_REDIS_URL → REDIS_URL → null (NEVER default to localhost)
getRedisUrlOrThrow(prefix?: string): string             // throws "Redis URL is not configured. Set <which> …"
parseRedisUrl(url: string): {host,port,password?,db?,tls?} // deprecated; keep URL-form when possible

// packages/shared/src/lib/db/ssl.ts
getSslConfig(): { rejectUnauthorized: boolean } | undefined  // sslmode=require|ssl=true in URL, or DB_SSL=true; DB_SSL_REJECT_UNAUTHORIZED

// packages/shared/src/lib/db/mikro.ts
resolvePoolConfig(env): { poolMin=2, poolMax=20, poolIdleTimeout=3000, poolAcquireTimeout=6000,
  idleSessionTimeoutMs (600000 non-prod), idleInTransactionTimeoutMs=120000, statementTimeoutMs?, lockTimeoutMs? }

// packages/cli/src/lib/auto-spawn-workers.ts / auto-spawn-scheduler.ts
resolveAutoSpawnWorkersMode(env): 'off'|'eager'|'lazy'   // AUTO_SPAWN_WORKERS → OM_AUTO_SPAWN_WORKERS → true; lazy via OM_AUTO_SPAWN_WORKERS_LAZY
resolveAutoSpawnSchedulerMode(env): 'off'|'eager'|'lazy'
resolveLazyPollMs(env): number                            // default 1000, floor 250

// packages/cli/src/lib/worker-connection-budget.ts
resolveWorkerConnectionBudget(env, poolMax): number       // OM_WORKERS_DB_CONNECTION_BUDGET → poolMax
planWorkerConcurrency(queues: {queue,concurrency}[], budget): WorkerConcurrencyPlan // floor 1/queue, deterministic greedy fill

// packages/cli/src/lib/single-instance-strategy-guard.ts
readInfraStrategySnapshot(env): { cacheStrategy, queueStrategy, rateLimitStrategy }
evaluateSingleInstanceGuard(snapshot, env): { action:'ok'|'warn'|'fail', offenders, … }
assertSingleInstanceStrategies(env): result | throws SingleInstanceStrategyError

// packages/cli/src/lib/server-start-lock.ts
acquireServerStartLock(appDir, {port?}): { lockPath, release() }  // wx write, stale-pid recovery via kill(pid,0)

// packages/cli/src/mercato.ts
ensureDatabaseExists(dbUrl): Promise<boolean>            // CREATE DATABASE via /postgres maintenance connection
buildServerProcessEnvironment(env): env                   // NODE_ENV=production; ±`-r newrelic` from NEW_RELIC_LICENSE_KEY
resolveDevRuntimeBaseUrl(env): string                     // APP_URL → NEXT_PUBLIC_APP_URL → NEXTAUTH_URL → http://localhost:$PORT

// packages/cli/src/lib/init-secrets.ts
resolveInitDerivedSecrets({email, env}): { adminEmail, employeeEmail, adminPassword, employeePassword }

// packages/cli/src/lib/db/commands.ts
sanitizeModuleId(id): string; validateTableName(name): void
makeConstraintDropsIdempotent(sql): string                // drop constraint → drop constraint if exists
dbGenerate(resolver) / dbMigrate(resolver) / dbGreenfield(resolver, {yes})

// packages/cache
createCacheService(options?): CacheStrategy               // strategy resolution + tenant wrapper + memory fallback
runWithCacheTenant(tenantId|null, fn)                     // AsyncLocalStorage scope
CacheStrategy = { get(key,{returnExpired?}), set(key,value,{ttl?,tags?}), has, delete, deleteByTags(tags):number,
                  clear():number, keys(pattern?):string[], stats():{size,expired}, healthcheck?, cleanup?, close? }

// packages/core/src/modules/configs/cli.ts
STRUCTURAL_CACHE_REQUESTS = [{kind:'pattern',pattern:'nav:*'},{kind:'segment',segment:'admin-nav'},{kind:'segment',segment:'portal-nav'}]
```

## Behavioral details a port MUST replicate

1. **Init idempotence contract**: `init` on a DB with existing users must fail (exit 1) with the exact abort message above; container entrypoints treat that failure as "already initialized → run migrations only". Marker files decide first-run vs subsequent runs; a lost marker must still converge thanks to this fallback.
2. **Init ordering** matters: generators → migrate → bootstrap → configs restore-defaults → auth setup (tenant/org/users/ACLs) → seed-roles → encryption defaults → per-module `seedDefaults` (single tenant/org ctx) → `ensureCustomRoleAcls` second pass → `seedExamples` → dashboards → search/query-index reindex. `seedDefaults` runs before custom-role ACL fixup because seeding may create roles.
3. **Database auto-creation**: missing target DB is created via the `/postgres` maintenance database before init/reinstall; failures print manual `CREATE DATABASE` instructions and abort (return 1). Connection errors elsewhere are non-fatal to the check (returns true).
4. **Reinstall** drops all tables in `current_schema()` (CASCADE, one transaction) plus forced `vector_search`/`vector_search_migrations`, then `FLUSHALL`s Redis when configured (skips silently with a log line when `REDIS_URL` unset — never spins up a reconnecting client).
5. **Per-module migrations**: separate history tables `mikro_orm_migrations_<mod>`, alphabetical module order, migrate-only never diffs (`entities: []`), generated migrations get `_<moduleId>` suffix and idempotent constraint drops. Greenfield uses `session_replication_role='replica'` while dropping.
6. **Queue strategy naming**: the Redis/BullMQ mode is `QUEUE_STRATEGY=async` (exact string); every other value (including unset and `redis`) means the local file queue. Scheduler auto-spawn is additionally gated to `QUEUE_STRATEGY === 'local'` (with `async`, scheduling is expected to ride BullMQ repeatables).
7. **Auto-spawn defaults**: workers and scheduler auto-spawn **on** by default in both `server dev` and `server start`; legacy `AUTO_SPAWN_*` beats `OM_AUTO_SPAWN_*`; lazy mode only when the `_LAZY` var parses exactly true. `yarn dev` injects lazy=true for both unless the user set the vars.
8. **Worker connection budgeting**: Σ effective concurrency ≤ budget (default `DB_POOL_MAX`), floor 1 per queue, warnings when clamped or below queue floor — a port with one-DB-connection-per-inflight-job semantics must implement the same cap or it can starve the web pool.
9. **Production boot guards** (in this order): events single-delivery guard → single-instance strategy guard (throw in prod+multi-instance with unsafe strategies; warn in prod single-instance) → server start lock (one production server per app dir; stale lock recovery by dead-pid check) → BUILD_ID reconstruction. First unexpected child exit tears the whole supervisor down non-zero.
10. **NODE_ENV handling**: the CLI supervisor forces `NODE_ENV=production` on spawned app/worker/scheduler processes (even under `server dev` — dev-ness comes from `next dev`); New Relic is preloaded via `NODE_OPTIONS='-r newrelic'` iff `NEW_RELIC_LICENSE_KEY` is set, and any pre-existing newrelic require flags are stripped first.
11. **Env loading**: `.env` lives in the app dir; auto-copied from `.env.example` outside production; in `server dev` an `.env` change restarts the runtime with a reloaded environment (values removed from `.env` revert to the original process env).
12. **Warmup semantics** (dev): retryable statuses are exactly 404/408/425/429/≥500; login redirects to `/login` or `/api/auth/session/refresh` are transient; max 3 retry attempts, 2 s apart; 401 → app still marked ready + credentials warning; success/failure both write the warmup ready file, which gates background-service startup (300 s timeout → start anyway with a warning).
13. **Cache key/tag layout**: logical keys are sha1-hashed inside tenant-scoped prefixes with a meta twin per key and an implicit `__scope__` tag; `clear()` only clears the current tenant scope; `deleteByTags`/`clear` counts are halved. Redis physical prefixes `cache:` and `tag:`. Missing native cache deps must degrade to in-memory with a single warning, never crash.
14. **Structural cache purge** (`nav:*` pattern + `admin-nav`/`portal-nav` segments) runs after every generator pass and must be replicated wherever nav/sidebar responses are cached; default CLI scope is global-only unless `--tenant`/`--all-tenants`.
15. **Compose parity**: Postgres must be pgvector-capable (`pgvector/pgvector:pg17`); Redis capped with `maxmemory` and config file; app waits on `service_healthy` for postgres/redis/meilisearch; DB/Redis unexposed to host in fullapp stacks; init runs in the app container command, not a separate one-shot service; `/` must answer 200 for platform healthchecks (Railway timeout 360 s covers first-boot init).
16. **Node engine gate**: CLI refuses to run on Node < 24 with a specific remediation message — a port should have an equivalent runtime version gate.
17. **`seed:defaults`** iterates every non-deleted organization (multi-tenant aware) and re-runs only `seedDefaults` + custom-role ACLs — it must be safe to run repeatedly.
18. **Redis URL resolution never falls back to localhost** — unset Redis means "not configured", and callers choose local strategies or throw (`getRedisUrlOrThrow` message text is user-facing).

## Gotchas

- **`QUEUE_STRATEGY=async`, not `redis`**: the workflow context's claim of `local|redis` is wrong for current upstream — `packages/queue/src/factory.ts` treats only the literal `async` as BullMQ mode. Ports must accept `async` (and may alias `redis` as an improvement, documented as a decision).
- **Two different DB pool defaults**: code defaults (`resolvePoolConfig`: min 2, idle 3000, acquire 6000) differ from `.env.example` (min 5, idle 10000, acquire 10000). Byte-parity testing should pin values via env.
- `CACHE_STRATEGY` default is `memory` in code, but `.env.example` ships `sqlite` and docker stacks use `redis` — "default" depends on which artifact you copy.
- `--skip-password-policy` defaults to **true** in `init` (the flag parser treats its absence as skip=true), so the demo `secret` password always seeds unless a port intentionally hardens this.
- `mercato init` is **not** in `BOOTSTRAP_FREE_COMMANDS` conceptually — it is, and it self-runs generators; but most module commands require generated files first; the CLI's "Generated files not found!" box + exit 1 is the contract for that failure.
- The catch-all API route **ignores `requireRoles`** at runtime (deprecated, warning only) — only `requireFeatures` authorizes. Ports must not resurrect role-name-based auth.
- `docker-compose.fullapp.yml` runs the production image with `NODE_ENV=development` and `user: "0"` — intentional for the demo stack (self-service onboarding + demo mode). Don't "fix" this without noting the behavior change.
- `JWT_SECRET` defaults to literal `JWT` and `OM_INIT_SUPERADMIN_PASSWORD` to `password` in the fullapp compose — insecure by design for local demos; ports should keep the envs but may harden defaults as a documented decision.
- The dev splash server occupies port **4000** and the app port 3000; in containers it binds 0.0.0.0. `EADDRINUSE` on a fixed splash port falls back to a random port (only when not already random).
- Init reads tenant/org ids back via raw SQL joining `organizations`/`users` — it assumes `auth setup` created exactly one org/tenant pair; multi-org DBs would pick an arbitrary row (LIMIT 1, no ORDER BY).
- Cache `clear()` on Redis uses `KEYS` scans (O(N)); fine for admin/CLI paths but a port targeting large keyspaces may substitute `SCAN` (behavior-preserving improvement).
- `instrumentation.ts` being a no-op is deliberate; don't move warmup into the server process — background services key off the *external* warmup marker file.
- There is no `/api/health`; Railway health checks `/` (a full Next page render). A port adding a cheap health endpoint should still keep `/` working unauthenticated.
- `server dev`'s stdout line formats (`[server] …`, `[worker] …`, `✓ Ready in`, `- Local:`) are parsed by the dev wrapper — if a port reuses the wrapper, those exact strings are load-bearing.
- Windows: migration paths are normalized to forward slashes and dynamic imports converted to `file://` URLs (`dynamicImportProvider`) — relevant only to Node ports.
