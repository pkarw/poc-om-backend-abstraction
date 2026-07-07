# Events, Queues & Scheduler

> Analyzed at upstream commit adc9da27759e357febe9ed8d4b7182040d127349 (2026-07-01). Regenerate via the om-sync-upstream skill.

## Purpose

Open Mercato's background-processing spine consists of three cooperating subsystems:

1. **`@open-mercato/events`** (`packages/events`) — an in-process event bus with optional durable ("persistent") delivery through the job queue, a Postgres `NOTIFY`-based cross-process bridge for SSE broadcast events, and a typed per-module event declaration registry (`createModuleEvents`).
2. **`@open-mercato/queue`** (`packages/queue`) — a two-strategy job queue abstraction: `local` (JSON files on disk, dev only) and `async` (**BullMQ on Redis**, production). Module `workers/*.ts` files are auto-discovered and become BullMQ workers.
3. **`@open-mercato/scheduler`** (`packages/scheduler`, module id `scheduler`) — cron/interval scheduling. Source of truth is the Postgres `scheduled_jobs` table; under `QUEUE_STRATEGY=async` schedules are mirrored to **BullMQ repeatable jobs** on the `scheduler-execution` queue; under `local` a DB-polling engine with Postgres advisory locks executes them.

A port must reproduce the queue job envelope, queue names, BullMQ options, and the events-queue dispatch semantics byte-for-byte so Node BullMQ workers and any other-language BullMQ client can interoperate on the same Redis.

## Key source locations

| Path (upstream repo root) | Contents |
|---|---|
| `packages/events/src/bus.ts` | `createEventBus` — in-memory listeners, persistent enqueue, single-delivery skip logic, global event taps, shared BullMQ producer memoization |
| `packages/events/src/types.ts` | `EventBus`, `EmitOptions` (`persistent`, `deliverInline`, `tenantId`, `organizationId`), `SubscriberDescriptor`, `SubscriberHandler`, `SubscriberContext` |
| `packages/events/src/single-delivery.ts` | `OM_EVENTS_SINGLE_DELIVERY` / `OM_EVENTS_EXTERNAL_WORKER` parsing + `reconcileSingleDelivery` fail-safe |
| `packages/events/src/bridge.ts` | Cross-process event bridge over Postgres `LISTEN/NOTIFY` (channel `om_event_bridge`) |
| `packages/events/src/modules/events/workers/events.worker.ts` | The `events` queue worker — dispatches queued persistent events to module subscribers |
| `packages/events/src/modules/events/api/route.ts` | `GET /api/events` — declared-events registry endpoint |
| `packages/events/src/modules/events/api/stream/route.ts` | `GET /api/events/stream` — SSE DOM Event Bridge (clientBroadcast events) |
| `packages/shared/src/modules/events/factory.ts` | `createModuleEvents`, global event bus reference, declared-event registry, `isBroadcastEvent` |
| `packages/shared/src/modules/events/types.ts` | `EventDefinition` (id, label, category, entity, clientBroadcast, portalBroadcast, excludeFromTriggers) |
| `packages/shared/src/lib/events/patterns.ts` | `matchEventPattern` (single-segment wildcard / prefix modes) |
| `packages/shared/src/lib/patterns/wildcard.ts` | `matchWildcardPattern` core matcher |
| `packages/shared/src/lib/redis/connection.ts` | `getRedisUrl(prefix)` / `getRedisUrlOrThrow(prefix)` env resolution |
| `packages/shared/src/lib/boolean.ts` | Boolean env token parsing (`TRUE_VALUES`/`FALSE_VALUES`) |
| `packages/queue/src/types.ts` | `Queue`, `QueuedJob`, `JobContext`, `JobHandler`, `WorkerMeta`, `WorkerDescriptor`, `EnqueueOptions` |
| `packages/queue/src/factory.ts` | `createQueue`, `createModuleQueue`, `resolveQueueStrategy` |
| `packages/queue/src/strategies/async.ts` | BullMQ strategy — enqueue options, worker creation, job counts |
| `packages/queue/src/strategies/local.ts` | File-based strategy — `queue.json`/`state.json`, retries, polling |
| `packages/queue/src/worker/runner.ts` | `runWorker` (graceful shutdown, background mode), `createRoutedHandler` |
| `packages/queue/src/worker/registry.ts` | In-process worker registry (`registerModuleWorkers`, `getWorkersByQueue`) |
| `packages/queue/src/pending-probe.ts` | Read-only pending-job probes used by the lazy worker supervisor |
| `packages/cli/src/mercato.ts` (~lines 1533–1695) | Built-in `mercato queue worker|clear|status` and `mercato events emit|clear` CLI |
| `packages/cli/src/lib/worker-job-handler.ts` | `createPerJobWorkerHandler` — per-job DI container isolation |
| `packages/cli/src/lib/worker-connection-budget.ts` | Fits Σ(worker concurrency) into the DB pool budget |
| `packages/cli/src/lib/queue-worker-supervisor.ts` | Lazy per-queue worker auto-spawn supervisor |
| `packages/cli/src/lib/auto-spawn-workers.ts` | `AUTO_SPAWN_WORKERS` mode resolution (off/eager/lazy) |
| `packages/cli/src/lib/events-single-delivery.ts` | Server-bootstrap single-delivery guard (env rewrite) |
| `packages/cli/src/lib/scheduler-supervisor.ts` | Lazy local-scheduler auto-spawn (probes `scheduled_jobs` via SQL) |
| `packages/cli/src/lib/generators/module-registry.ts` (~1304–1336, 1672–1731) | Discovery of `subscribers/*.ts` and `workers/*.ts` metadata into generated registries |
| `packages/shared/src/modules/registry.ts` | `ModuleSubscriber`/`ModuleWorker` types, `createLazyModuleSubscriber`/`createLazyModuleWorker` |
| `packages/core/src/bootstrap.ts` (~106–175) | Event bus creation per request container, subscriber auto-registration, `setGlobalEventBus` |
| `packages/scheduler/src/modules/scheduler/data/entities.ts` | `ScheduledJob` entity → `scheduled_jobs` table |
| `packages/scheduler/src/modules/scheduler/services/schedulerService.ts` | DB-level schedule CRUD (`register`/`update`/`unregister`) + scope/target validation |
| `packages/scheduler/src/modules/scheduler/services/bullmqSchedulerService.ts` | BullMQ repeatable-job mirroring, `syncAll` reconciliation |
| `packages/scheduler/src/modules/scheduler/services/localSchedulerService.ts` | Local DB-polling execution engine (advisory locks) |
| `packages/scheduler/src/modules/scheduler/workers/execute-schedule.worker.ts` | Worker on `scheduler-execution` — validates scope, runs queue/command target |
| `packages/scheduler/src/modules/scheduler/lib/{cronParser,intervalParser,nextRunCalculator,localLockStrategy}.ts` | Cron (via `cron-parser`), interval strings, next-run calc, `pg_try_advisory_xact_lock` |
| `packages/scheduler/src/modules/scheduler/lib/scheduledJobSubscriber.ts` | MikroORM flush subscriber that auto-syncs schedule changes to BullMQ |
| `packages/scheduler/src/modules/scheduler/cli.ts` | `mercato scheduler list|status|run|start` |
| `packages/scheduler/src/modules/scheduler/di.ts` | DI wiring per strategy + one-time cold-start `syncAll()` |
| `packages/scheduler/src/modules/scheduler/events.ts` | Declared `scheduler.job.{started,completed,failed,skipped}` events |

## How it works

### 1. Queue package — strategy pattern

`createQueue<T>(name, strategy, options)` (`packages/queue/src/factory.ts`) returns a `Queue<T>` backed by one of exactly two strategies:

- **`local`** (`strategies/local.ts`) — file-based, dev-only. Directory layout: `<QUEUE_BASE_DIR|.mercato/queue>/<queueName>/queue.json` (JSON array of stored jobs) and `state.json` (`{ lastProcessedId?, completedCount?, failedCount? }`). All read-modify-write sequences are serialized through a per-queue promise-chain mutex. `enqueue` appends `{ id: uuid, payload, createdAt, availableAt? }`. `process(handler)` without `limit` starts a continuous 1000 ms polling loop (like a BullMQ Worker) and returns the sentinel `{ processed: -1, failed: -1 }`; with `{ limit }` it processes a single batch synchronously and returns real counts. Failed jobs are retried up to **3 attempts** with exponential backoff `1000 * 2^(attempt-1)` ms implemented by rewriting `availableAt` and `attemptCount`; after the 3rd failure the job is dropped ("dead letter" = removed from `queue.json`, `failedCount` incremented). A corrupted `queue.json` is backed up to `queue.corrupted.<epochMs>.json` and reset to `[]`.
- **`async`** (`strategies/async.ts`) — BullMQ (`bullmq` ^5.0.0, optional peer dep, lazily imported). Queue name is used **verbatim** with **no custom prefix**, so all Redis keys use BullMQ's default `bull:` prefix. Connection is resolved as a full URL object `{ url }` (preserving `rediss://`, username, db, query params) from explicit options or `getRedisUrlOrThrow('QUEUE')` → `QUEUE_REDIS_URL` else `REDIS_URL` else throw.

Strategy selection everywhere: `process.env.QUEUE_STRATEGY === 'async' ? 'async' : 'local'` (`resolveQueueStrategy()`); `packages/core/src/bootstrap.ts` additionally accepts the legacy values `EVENTS_STRATEGY` env var and `'redis'` token as aliases for async.

`createModuleQueue<T>(name, { concurrency })` is the boilerplate helper modules use: async + `getRedisUrlOrThrow('QUEUE')` when `QUEUE_STRATEGY=async`, else local.

### 2. The job envelope (critical for cross-language interop)

Every enqueue — both strategies — wraps the caller payload in a **`QueuedJob` envelope**. Under BullMQ, `queue.add()` is called as:

```ts
// packages/queue/src/strategies/async.ts, enqueue()
const jobData = { id: crypto.randomUUID(), payload: data, createdAt: new Date().toISOString() }
await queue.add(jobData.id, jobData, {
  delay: options?.delayMs && options.delayMs > 0 ? options.delayMs : undefined,
  removeOnComplete: true,
  removeOnFail: 1000,
  attempts: 3,
  backoff: { type: 'exponential', delay: 1000 },
})
```

So in Redis: the BullMQ **job name = the envelope UUID**, and **`job.data` = the whole envelope** `{ id, payload, createdAt }`. The worker side hands `job.data` (the envelope) to the handler as the `QueuedJob`, with context `{ jobId: job.id ?? data.id, attemptNumber: job.attemptsMade + 1, queueName }`. A port's producer must write this envelope; a port's consumer must unwrap `data.payload`.

### 3. Worker discovery, registration and the worker host

Module worker files live at `src/modules/<module>/workers/*.ts` and export:

```ts
export const metadata: WorkerMeta = { queue: 'my-queue', id?: string, concurrency?: number }
export default async function handle(job: QueuedJob<T>, ctx: JobContext & { resolve }) { ... }
```

The code generator (`packages/cli/src/lib/generators/module-registry.ts`) scans `workers/` dirs, loads `metadata`, **skips files without `metadata.queue`**, and emits `ModuleWorker` descriptors into `.mercato/generated/modules.cli.generated.ts`:
`{ id: metadata.id ?? '<moduleId>:workers:<subdirs>:<filename>', queue, concurrency: metadata.concurrency ?? 1, handler: createLazyModuleWorker(...) }`. Handlers are lazy: the module file is dynamically imported on first job and its **default export** is the handler. `registerModuleWorkers` (queue package registry) applies app-level overrides via `applyWorkerOverridesToDescriptors` (override/disable by worker id).

**Worker host — `mercato queue worker <queueName> | --all [--concurrency=N]`** (`packages/cli/src/mercato.ts`):
- Collects all generated `ModuleWorker`s; for each queue the requested concurrency is `--concurrency` override or `max(worker.concurrency)` across that queue's workers.
- Requested concurrencies are then **fitted to a DB connection budget** (`resolveWorkerBudgetPlan` / `planWorkerConcurrency` in `lib/worker-connection-budget.ts`): budget = `OM_WORKERS_DB_CONNECTION_BUDGET` or resolved DB pool max; every queue keeps a floor of 1; totals are clamped deterministically.
- `runWorker({ queueName, connection: { url: getRedisUrl('QUEUE') }, concurrency, handler, background })` creates the strategy queue and calls `queue.process(handler)`, which for async instantiates one `new Worker(queueName, processor, { connection, concurrency })`. Graceful shutdown closes all managed queues on SIGTERM/SIGINT and `process.exit(0|1)`.
- The composite handler is `createPerJobWorkerHandler(queueWorkers, createRequestContainer)` (`lib/worker-job-handler.ts`): for **each job** it builds a fresh Awilix request container, then runs **every registered worker for that queue sequentially** with `ctx.resolve` bound to that container, and finally best-effort `em.clear()`. One failing handler fails the whole job (BullMQ retries it and all handlers re-run).

**Auto-spawn from `mercato server start|dev`** (mercato.ts ~2090–2160): mode from `AUTO_SPAWN_WORKERS`/`OM_AUTO_SPAWN_WORKERS` (default **true/eager**) and `OM_AUTO_SPAWN_WORKERS_LAZY`:
- *eager*: spawns one child `node <mercatoBin> queue worker --all`.
- *lazy*: `startLazyWorkerSupervisor` polls every `OM_AUTO_SPAWN_WORKERS_LAZY_POLL_MS` (default 1000, min 250) using `getQueuePendingProbe(queueName, strategy)` — a read-only probe (local: parse `queue.json`, count `ready` vs `delayedFuture`; async: `Queue.getJobCounts('waiting','delayed','active')`, never creating a Worker). First probe with `ready > 0` spawns `node <mercatoBin> queue worker <queueName>`; unexpected child exit triggers a re-probe and restart when jobs remain (`OM_AUTO_SPAWN_WORKERS_LAZY_RESTART`, default true). Probe errors are fail-soft ("do not start worker yet").

### 4. Event bus

`createEventBus({ resolve, queueStrategy? })` (`packages/events/src/bus.ts`) is built **per request container** in `packages/core/src/bootstrap.ts`, registered as DI key `eventBus`, and mirrored globally with `setGlobalEventBus` so `createModuleEvents(...).emit` works outside DI. All discovered module subscribers are registered on each bus instance.

`emit(event, payload, options)` executes, in order:
1. **Global taps** — `registerGlobalEventTap` handlers stored on `globalThis.__openMercatoEventBusGlobalTaps__` (used by the SSE stream endpoint). Errors logged, swallowed.
2. **Inline delivery** — unless `options.persistent && options.deliverInline === false` ("enqueue-only"). Iterates all registered listener patterns; a pattern matches via `matchEventPattern` (see below). Under **single-delivery** (default on) a *persistent* emit **skips handlers registered with `persistent: true`** inline (the events worker will run them). Each handler is awaited; errors are logged and swallowed (one failing subscriber never affects others or the emitter).
3. **Cross-process broadcast** — if `isBroadcastEvent(event)` (declared with `clientBroadcast: true`) *and* the payload has a non-empty string `tenantId`, publish via Postgres: `SELECT pg_notify('om_event_bridge', json)` with envelope `{ event, payload, options, originPid }`; messages over **7000 bytes** are dropped with a warning. Other processes `LISTEN om_event_bridge` (auto-reconnect after 1000 ms) and re-dispatch to local listeners (SSE fan-out), skipping envelopes whose `originPid === process.pid`.
4. **Persistent enqueue** — if `options.persistent`, enqueue `{ event, payload, options }` onto queue **`events`**. Under async, the producer `Queue` is memoized process-wide on `globalThis.__openMercatoEventsProducerQueues__` keyed by `async:<redisUrl>` (one Redis connection per process instead of per request; disable with `OM_EVENTS_SHARED_PRODUCER=0`), with a SIGTERM/SIGINT close hook.

**Event pattern matching** (`packages/shared/src/lib/events/patterns.ts`): default mode is `single-segment` — `'*'` matches everything, exact string matches, otherwise glob-style `*` wildcards that **do not cross `.` segment boundaries** (`matchWildcardPattern(..., { singleSegmentWildcard: true })`). Webhooks use `prefix` mode instead (`foo.*` = prefix `foo.`; trailing `*` = raw prefix).

**Subscribers**: module files `src/modules/<module>/subscribers/*.ts` export `metadata` and a default handler:

```ts
// packages/core/src/modules/business_rules/subscribers/crud-rule-trigger.ts
export const metadata = { event: '*', persistent: true, id: 'business_rules:crud-rule-trigger' }
export default async function handle(payload, ctx) { ... }
```

Recognized metadata fields (generator `normalizeSubscriberMetadata`): `id?`, `event`, `persistent?`, `sync?`, `priority?`. Default id: `<moduleId>:<subdirs>:<filename>` (note: **no** `subscribers` segment, unlike worker ids). Generated descriptor: `{ id, event, persistent, sync, priority, handler: createLazyModuleSubscriber(...) }`. Bootstrap flat-maps all module subscribers into `eventBus.registerModuleSubscribers(subs)`; subscribers with `sync: true` are additionally registered into a synchronous-subscriber store (`@open-mercato/shared/lib/crud/sync-subscriber-store`) used by the CRUD engine (`priority` orders them there — the async bus itself ignores `priority`).

**Meaning of `persistent`**:
- On a **subscriber**: "this subscriber is dispatched by the events worker from the durable `events` queue" — under single-delivery it is *skipped inline* on a persistent emit and executed only in the worker (which pattern-matches, so wildcard persistent subscribers are reached).
- On an **emit** (`EmitOptions.persistent: true`): the event is also enqueued to the `events` queue for the worker. Delivery guarantee: inline ephemeral delivery is best-effort at-most-once (in-memory); persistent delivery is **at-least-once** via BullMQ (3 attempts, exponential backoff). There is no exactly-once; subscribers must tolerate redelivery.

**Single-delivery reconciliation** (`packages/events/src/single-delivery.ts`, mirrored in `packages/cli/src/lib/events-single-delivery.ts`): `OM_EVENTS_SINGLE_DELIVERY` defaults **on**. At server startup, `applyEventsSingleDeliveryGuard` computes `workersAvailable = autoSpawnWorkersMode !== 'off' || OM_EVENTS_EXTERNAL_WORKER`. If single-delivery is requested but no worker will exist, the env is **rewritten to `'false'`** in both the process env and spawned-child env (fall back to legacy dual-dispatch: persistent subscribers run inline *and* the event is enqueued) and a loud warning is printed. Bus and worker read the same reconciled env, so they always agree within a process.

### 5. The events worker

`packages/events/src/modules/events/workers/events.worker.ts`:

```ts
export const EVENTS_QUEUE_NAME = 'events'
export const metadata: WorkerMeta = {
  queue: 'events',
  concurrency: process.env.WORKERS_EVENTS_CONCURRENCY ? parseInt(..., 10) : 1,
}
```

Handler: `job.payload` is `{ event, payload, options? }`. Builds a cached listener map from **all generated module subscribers** (`getCliModules()`), then resolves the subscriber set:
- **single-delivery on** (default): every subscriber whose `event` pattern matches (via `matchEventPattern`, wildcards included) **and** has `persistent: true`.
- **flag off (legacy)**: exact-match `listeners.get(event)` — *all* subscribers for that literal event name (this double-runs exact persistent subscribers relative to inline, and never reaches wildcard subscribers).

All matched subscribers run via `Promise.allSettled` with ctx `{ resolve, tenantId: options?.tenantId ?? null, organizationId: options?.organizationId ?? null }`. If **any** rejected, the worker throws `` `${errors.length}/${subscribers.length} subscriber(s) failed for event "${event}": ${failedIds}` `` — failing the BullMQ job so it retries (attempts=3), which **re-runs the successful subscribers too**.

### 6. `createModuleEvents` — typed event declarations

Modules declare emittable events in `src/modules/<module>/events.ts`:

```ts
// packages/scheduler/src/modules/scheduler/events.ts
const events = [
  { id: 'scheduler.job.started', label: 'Scheduled Job Started', entity: 'scheduled_job', category: 'lifecycle' },
  ...
] as const
export const eventsConfig = createModuleEvents({ moduleId: 'scheduler', events })
export const emitSchedulerEvent = eventsConfig.emit
```

`createModuleEvents` (`packages/shared/src/modules/events/factory.ts`) registers each `EventDefinition` (`{ id, label, description?, category?: 'crud'|'lifecycle'|'system'|'custom', module, entity?, excludeFromTriggers?, clientBroadcast?, portalBroadcast? }`) into module-global registries and returns a typed `emit(eventId, payload, options)` that (a) validates the id was declared — `strict: true` throws, default logs `console.error` **and still emits** — and (b) delegates to `getGlobalEventBus()` (globalThis key `__openMercatoGlobalEventBus__`); if no bus is set it warns and returns. `isBroadcastEvent`/`isPortalBroadcastEvent` consult this registry for SSE filtering. `registerEventModuleConfigs` seeds the registry at app bootstrap from `events.generated.ts`.

### 7. Scheduler

**Data model** — table `scheduled_jobs` (`data/entities.ts`): `id uuid pk default gen_random_uuid()`, `organization_id uuid null`, `tenant_id uuid null`, `scope_type text default 'tenant'` (`system|organization|tenant`), `name text`, `description text null`, `schedule_type text` (`cron|interval`), `schedule_value text`, `timezone text default 'UTC'`, `target_type text` (`queue|command`), `target_queue text null`, `target_command text null`, `target_payload jsonb null`, `require_feature text null`, `is_enabled bool default true`, `last_run_at`, `next_run_at`, `source_type text default 'user'` (`user|module`), `source_module text null`, `created_at`/`updated_at` (`now()`), `deleted_at null`, `created_by_user_id`/`updated_by_user_id uuid null`. Indexes: `(organization_id, tenant_id)`, `(next_run_at)`, `(scope_type, is_enabled)`.

**SchedulerService** (DB upsert API used by modules and admin UI) validates scope (`system` → no org/tenant; `organization` → both org and tenant required; `tenant` → tenant only) and target (`queue` requires `targetQueue`; `command` requires `targetCommand`), computes `nextRunAt` via `calculateNextRun`, and best-effort syncs to BullMQ (DB is source of truth; sync errors are logged, never thrown).

**Async strategy (`QUEUE_STRATEGY=async`)** — `BullMQSchedulerService`:
- Own queue `new Queue('scheduler-execution', { connection: { url: getRedisUrlOrThrow('QUEUE') } })`.
- `register(schedule)` adds a **repeatable job**: name `schedule-<schedule.id>`, repeat options `{ tz: timezone||'UTC', pattern: cronExpr }` for cron or `{ tz, every: intervalMs }` for intervals, job data in the QueuedJob envelope shape `{ id: 'schedule-<id>', payload: { scheduleId, tenantId, organizationId, scopeType }, createdAt: ISO }`, opts `removeOnComplete: { age: 86400*30, count: 1000 }`, `removeOnFail: { age: 86400*90, count: 5000 }`, **no jobId** (BullMQ generates per-instance repeat ids).
- `unregister(id)` scans `getRepeatableJobs()` for `id`/`name === 'schedule-<id>'` and `removeRepeatableByKey(key)`.
- `syncAll()` reconciles: registers DB-enabled schedules missing in BullMQ (batched 500/page), removes repeatables whose id has no enabled DB row. Run once per process at DI registration via `setImmediate` (`di.ts`) and by `mercato scheduler start`.
- A MikroORM flush subscriber (`ScheduledJobSubscriber`) mirrors any entity change to BullMQ immediately.

**Execution worker** — `workers/execute-schedule.worker.ts`, `metadata = { queue: 'scheduler-execution', concurrency: 5 }`. Per job: loads a **fresh** schedule row; returns silently if missing/deleted; **throws** on any scope mismatch between payload and DB (`scopeType`, `tenantId`, `organizationId` — anti-tampering); emits `scheduler.job.skipped` + returns if disabled; emits `scheduler.job.started`; if `requireFeature` set, checks `rbacService.tenantHasFeature(...)` and skips when absent. Then:
- `targetType === 'queue'`: `createQueue(targetQueue, QUEUE_STRATEGY)` and enqueue `{ ...targetPayload, tenantId, organizationId, _idempotencyKey: 'scheduler-<scheduleId>-<Date.now()>' }`; always `queue.close()` in `finally`; sets `lastRunAt`; emits `scheduler.job.completed` with `{ queueJobId, queueName }`.
- `targetType === 'command'`: `new CommandBus().execute(targetCommand, { input: { ...targetPayload, tenantId, organizationId }, ctx: { auth: null, selectedOrganizationId, organizationIds, ... } })`; sets `lastRunAt`; emits completed with `{ commandId, commandResult }`.
- Otherwise throws `Invalid target configuration`. Worker throws propagate to BullMQ retry; `scheduler.job.failed` is NOT emitted here (only the local engine emits it).

**Local strategy (`QUEUE_STRATEGY=local`)** — `LocalSchedulerService`, started by `mercato scheduler start`: polls every `SCHEDULER_POLL_INTERVAL_MS` (default 30000) for `{ isEnabled: true, deletedAt: null, nextRunAt <= now }` (limit 100, order `nextRunAt ASC`); each due schedule runs under a Postgres **transaction-scoped advisory lock** `pg_try_advisory_xact_lock(hash)` where `hash = Math.abs(32-bit JS string hash of 'schedule:<id>')`; if not acquired, skip. Runs the same feature check and target dispatch (note: the local queue-target payload shape differs — `{ scheduleId, scheduleName, scopeType, tenantId, organizationId, payload: targetPayload, triggeredAt }`), then sets `lastRunAt` and recalculates `nextRunAt` **from now** (drift-free, `recalculateNextRun`). Emits `scheduler.job.started/completed/skipped/failed`. Next-run also updated after failures and feature-skips.

**Cron/interval parsing**: cron via the `cron-parser` npm package with `{ currentDate, tz }`; `validateCron` additionally enforces exactly **5 whitespace-separated fields**. Intervals match `^(\d+)(s|m|h|d)$` (seconds/minutes/hours/days → ms).

**Scheduler auto-spawn**: `mercato server start` spawns the local polling engine only when `QUEUE_STRATEGY=local` and `AUTO_SPAWN_SCHEDULER` is on (default true); lazy mode (`startLazySchedulerSupervisor`) probes `scheduled_jobs` with raw SQL (`count(*) ... where is_enabled and deleted_at is null`, tolerating a missing table, code `42P01`) and spawns `node <mercatoBin> scheduler start` once an enabled schedule exists.

## Public contracts

### Environment variables

| Var | Meaning / default |
|---|---|
| `QUEUE_STRATEGY` | `'async'` → BullMQ; anything else → `local`. Bootstrap also accepts legacy `'redis'` and legacy `EVENTS_STRATEGY` var |
| `QUEUE_REDIS_URL` → `REDIS_URL` | Redis URL resolution order for the `QUEUE` prefix; **no localhost fallback — missing config throws** |
| `QUEUE_BASE_DIR` | Local strategy base dir, default `.mercato/queue` |
| `OM_EVENTS_SINGLE_DELIVERY` | Default **true**; false → legacy dual-dispatch |
| `OM_EVENTS_EXTERNAL_WORKER` | Default false; operator acknowledgment that an out-of-process events worker runs |
| `OM_EVENTS_SHARED_PRODUCER` | Default true; false → per-bus producer queues |
| `WORKERS_EVENTS_CONCURRENCY` | events worker concurrency, default 1 |
| `AUTO_SPAWN_WORKERS` / `OM_AUTO_SPAWN_WORKERS` | Default true (eager) |
| `OM_AUTO_SPAWN_WORKERS_LAZY`, `OM_AUTO_SPAWN_WORKERS_LAZY_POLL_MS` (1000, min 250), `OM_AUTO_SPAWN_WORKERS_LAZY_RESTART` (true) | Lazy supervisor knobs |
| `OM_WORKERS_DB_CONNECTION_BUDGET` | Worker Σconcurrency budget, default = DB pool max |
| `AUTO_SPAWN_SCHEDULER`, `SCHEDULER_POLL_INTERVAL_MS` (30000) | Local scheduler |
| Boolean parsing | true: `1,true,yes,y,on,enable,enabled`; false: `0,false,no,n,off,disable,disabled` (case-insensitive, trimmed) |

### Redis / BullMQ structures (exact, for other-language interop)

- Library: **BullMQ ^5.0.0**, default prefix — all keys are `bull:<queueName>:*` (`bull:events:wait`, `bull:events:delayed`, `bull:events:meta`, per-job hashes `bull:events:<jobId>`, repeatables under `bull:scheduler-execution:repeat:*`, etc.). Upstream never sets a custom `prefix`.
- **Producer contract** for any queue `Q`: `Queue.add(name = <envelope uuid>, data = { "id": "<uuid>", "payload": <caller payload>, "createdAt": "<ISO-8601>" }, opts = { removeOnComplete: true, removeOnFail: 1000, attempts: 3, backoff: { type: "exponential", delay: 1000 }, delay?: <ms> })`.
- **Events queue** (`events`): `data.payload = { "event": "<event id>", "payload": <emit payload>, "options": { "persistent": true, "tenantId"?, "organizationId"?, "deliverInline"? } }`.
- **Scheduler repeatables** (`scheduler-execution`): job name `schedule-<uuid>`, `data = { "id": "schedule-<uuid>", "payload": { "scheduleId", "tenantId", "organizationId", "scopeType" }, "createdAt" }`, `opts = { repeat: { tz, pattern | every }, removeOnComplete: { age: 2592000, count: 1000 }, removeOnFail: { age: 7776000, count: 5000 } }`. Manual triggers (API `/api/scheduler/trigger`) enqueue a normal envelope whose payload adds `triggerType: 'manual'`, `triggeredByUserId`.
- **Worker contract**: one BullMQ `Worker(queueName, processor, { connection, concurrency })` per queue per worker process; handler ctx `attemptNumber = attemptsMade + 1`.
- `clear` uses `queue.obliterate({ force: true })`; `status` uses `getJobCounts('waiting','active','completed','failed')`.

### Postgres structures

- `scheduled_jobs` table (columns above) + advisory locks `pg_try_advisory_xact_lock(<32-bit hash>)` for the local engine.
- Event bridge: `NOTIFY om_event_bridge, '<json>'` / `LISTEN om_event_bridge`; envelope `{ "event", "payload", "options"?, "originPid" }`, payload ≤ 7000 bytes (larger dropped). SSL query params (`sslmode` etc.) are stripped from `DATABASE_URL` for the bridge connections; publisher is a `pg.Pool` with `max: 2`.

### Known queue names (discovered `workers/*.ts` metadata at this commit)

`events`, `scheduler-execution` (concurrency 5), `notifications`, `messages-email`, `checkout-email`, `checkout-transaction-expiry`, `workflow-activities`, `webhook-deliveries`, `vector-indexing`, `fulltext-indexing`, `data-sync-import`, `data-sync-export`, `data-sync-scheduled`, `catalog-product-bulk-delete`, `customers-deals-bulk-update-stage`, `customers-deals-bulk-update-owner`, `customer-accounts-cleanup-sessions`, `customer-accounts-cleanup-tokens`, `domain-verification`, `domain-tls-retry`, `integration-health-probe`, `integration-log-pruner`, `payment-gateways-webhook`, `payment-gateways-status-poller`, `shipping-carriers-webhook`, `shipping-carriers-status-poller`, `stripe-webhook`, `sync-akeneo-first-import`, `sync-akeneo-delete-products`, `ai-token-usage-prune`, `ai-pending-action-cleanup`, plus `communication-channels-{inbound,outbound,reactions,poll,poll-tick,...}` (see `packages/core/src/modules/communication_channels/lib`).

### HTTP routes in this subsystem

| Route | Method | Auth | Behavior |
|---|---|---|---|
| `/api/events` | GET | `requireAuth: true` | Declared events registry; query filters `category`, `module`, `excludeTriggerExcluded` (default `true`, i.e. events with `excludeFromTriggers` are hidden unless `=false`); returns `{ data: EventDefinition[], total: number }`, 200 |
| `/api/events/stream` | GET | `requireAuth: true`; 401 `'Unauthorized'` text when no `tenantId`/`sub` | SSE (`text/event-stream`, `Cache-Control: no-cache, no-transform`, `X-Accel-Buffering: no`); initial `: connected` comment; `:heartbeat` every 30 s; only `clientBroadcast` events; message JSON `{ id: eventName, payload, timestamp: msEpoch, organizationId }`; audience filtering by tenant (required), `organizationId(s)`, `recipientUserId(s)`, `recipientRoleId(s)`; payloads > 4096 bytes fall back to a truncated `{ truncated: true, id?, entityId?, entityType? }` payload, else skipped |
| `/api/scheduler/jobs` | GET/POST/PUT/DELETE | features `scheduler.jobs.view` (GET) / `scheduler.jobs.manage` | Schedule CRUD |
| `/api/scheduler/jobs/[id]/executions` | GET | `scheduler.jobs.view` | Execution history (from BullMQ job state) |
| `/api/scheduler/trigger` | POST | `scheduler.jobs.trigger` | Manual trigger; 401 no auth; 404 not found (tenant/org-scoped lookup); 403 system schedule w/o `isSuperAdmin`; **400 when `QUEUE_STRATEGY !== 'async'`**; success `{ ok: true, jobId, message }` |
| `/api/scheduler/targets` | GET | `scheduler.jobs.view` | Available queue/command targets |
| `/api/scheduler/queue-jobs/[jobId]` | GET | `scheduler.jobs.view` | BullMQ job details |

### CLI commands

- `mercato queue worker <queueName> | --all [--concurrency=N]` — the queue worker host (see §3).
- `mercato queue clear <queueName>` / `mercato queue status <queueName>`.
- `mercato events emit <event> [jsonPayload] [--persistent|-p]` / `mercato events clear`.
- `mercato scheduler list [--tenant --scope --enabled] | status | run <schedule-id> | start`.

## Helpers to mirror

| Helper (upstream signature) | Port needs |
|---|---|
| `createQueue<T>(name: string, strategy: 'local'\|'async', options?): Queue<T>` | Queue factory with the exact envelope + BullMQ opts above |
| `createModuleQueue<T>(name, { concurrency? }): Queue<T>` | Env-strategy convenience factory |
| `resolveQueueStrategy(): 'local'\|'async'` | `QUEUE_STRATEGY === 'async'` check |
| `Queue<T>`: `enqueue(data, { delayMs? }): Promise<string>`, `process(handler, { limit? }): Promise<ProcessResult>`, `clear(): Promise<{removed}>`, `close()`, `getJobCounts(): {waiting,active,completed,failed}` | Full interface; `process` sentinel `{processed:-1,failed:-1}` in continuous mode |
| `JobHandler<T> = (job: QueuedJob<T>, ctx: { jobId, attemptNumber, queueName }) => Promise<void>` | Handler shape |
| `getQueuePendingProbe(queueName, strategy?, options?): Promise<{ queueName, strategy, ready, delayedFuture, active, error, errorMessage? }>` | Read-only probe, fail-soft |
| `runWorker({ queueName, handler, connection?, concurrency=1, gracefulShutdown=true, background=false, strategy? })` | Worker host runtime |
| `createRoutedHandler(handlers: Record<string, JobHandler>): JobHandler` | Routes on `job.payload.type`; missing type → warn + succeed |
| `createPerJobWorkerHandler(workers, createContainer): JobHandler` | Per-job DI scope, run all queue workers sequentially |
| `registerModuleWorkers(list)` / `getWorkersByQueue(queue)` / `getRegisteredQueues()` | Worker registry (duplicate id → warn + overwrite) |
| `planWorkerConcurrency(queues, budget)` / `resolveWorkerConnectionBudget(env, poolMax)` | Concurrency-vs-DB-pool fitting |
| `createEventBus({ resolve, queueStrategy? }): EventBus` | Bus with `emit/on/registerModuleSubscribers/clearQueue` + deprecated alias `emitEvent` |
| `EventBus.emit(event, payload, { persistent?, deliverInline?, tenantId?, organizationId? })` | Full dispatch pipeline in §4 order |
| `registerGlobalEventTap(handler): () => void` | Process-wide tap set on `globalThis` |
| `matchEventPattern(eventName, pattern, { mode?: 'single-segment'\|'prefix' }): boolean` | Wildcard semantics: `*` never crosses `.` in default mode |
| `isSingleDeliveryRequested(env)` / `reconcileSingleDelivery({ requested, workersAvailable })` | Env default-on + fail-safe |
| `createModuleEvents({ moduleId, events, strict=false })` → `{ moduleId, events, emit }` | Typed declaration + validating emit via global bus |
| `setGlobalEventBus(bus)` / `getGlobalEventBus()` / `isEventDeclared(id)` / `getDeclaredEvents()` / `isBroadcastEvent(id)` / `isPortalBroadcastEvent(id)` | Declaration registry |
| `publishCrossProcessEvent(event, payload, options?)` / `registerCrossProcessEventListener(listener): () => void` | pg NOTIFY bridge |
| `getRedisUrl(prefix?) : string \| null` / `getRedisUrlOrThrow(prefix?)` | `<PREFIX>_REDIS_URL → REDIS_URL → null/throw` |
| `parseBooleanToken` / `parseBooleanWithDefault` | Env token semantics table above |
| `SchedulerService.register/update/unregister/enable/disable/exists/findByModule` | DB upsert + scope/target validation errors (exact messages in §7) |
| `BullMQSchedulerService.register/unregister/syncAll/getRepeatableJobs/destroy` | Repeatable mirroring |
| `LocalSchedulerService.start/stop` + `LocalLockStrategy.runWithLock(key, fn)` | Advisory-lock polling engine |
| `calculateNextRun(type, value, tz='UTC', fromDate?)` / `recalculateNextRun(type, value, tz)` | Always from *now* after execution |
| `parseCronExpression(expr, tz, currentDate?)` / `validateCron` (5 fields) / `parseInterval('15m') → ms` / `calculateNextRunFromInterval` | Schedule parsing |

## Behavioral details a port MUST replicate

1. **Job envelope**: BullMQ `job.data` is `{ id: uuid, payload, createdAt: ISO }`; job *name* is the same uuid. Handlers receive the envelope, unwrap `payload` themselves. `ctx.attemptNumber = attemptsMade + 1` (1-based).
2. **Enqueue opts on every producer path**: `attempts: 3`, `backoff: { type: 'exponential', delay: 1000 }`, `removeOnComplete: true`, `removeOnFail: 1000`, `delay` only when `delayMs > 0`.
3. **Queue names are used verbatim, default `bull:` prefix**, one Redis connection config resolved as a URL (`QUEUE_REDIS_URL` → `REDIS_URL`, never a localhost default).
4. **Persistent event dispatch (single-delivery default)**: on `emit(..., { persistent: true })`, ephemeral subscribers run inline (errors swallowed per-handler), persistent subscribers run only in the events worker; `deliverInline: false` skips inline delivery entirely (enqueue-only). With single-delivery off, persistent subscribers run inline **and** in the worker (exact-match only in the worker). The worker aggregates failures with `Promise.allSettled` and throws when ≥ 1 subscriber fails → the whole event job retries, re-running previously-successful subscribers (at-least-once; ordering within one dispatch is concurrent, not sequential).
5. **Single-delivery fail-safe**: a server process with no worker (auto-spawn off, no `OM_EVENTS_EXTERNAL_WORKER`) must rewrite the effective single-delivery flag to false for itself *and* any children, and log the exact warning semantics (silent loss prevention).
6. **Inline handler errors never propagate to the emitter**; global-tap errors likewise. Cross-process publish errors are caught and logged.
7. **Cross-process bridge only for `clientBroadcast` events with a string `tenantId` payload field**; > 7000-byte envelopes dropped with warning; listeners ignore own-pid envelopes (SSE fan-out only — inline subscribers are NOT re-run cross-process).
8. **Subscriber/worker discovery defaults**: worker id `<module>:workers:<path>:<file>`, concurrency default 1, files without `metadata.queue` ignored; subscriber id `<module>:<path>:<file>`; handler = default export; lazy import on first use; app-level overrides by id may replace or disable entries.
9. **Multiple workers on one queue**: single BullMQ Worker per queue whose handler runs each registered module worker **sequentially per job**; per-job DI container; queue concurrency = max of member concurrencies, clamped to the DB connection budget (floor 1 per queue).
10. **`mercato queue status/clear`** semantics: async counts from `getJobCounts`; async `clear` = `obliterate({force:true})` and reports `removed: -1`; local counts `{ waiting: queue length, active: 0, completed/failed: cumulative counters }`.
11. **Local strategy retry parity**: 3 attempts, exponential backoff base 1000 ms, dead-lettered (deleted) after exhaustion; jobs enqueued while a batch is running must survive the batch's rewrite of `queue.json`.
12. **Scheduler**: BullMQ repeatable job name/id `schedule-<uuid>`; repeat `{ tz, pattern }` (cron) or `{ tz, every }` (interval ms); retention `removeOnComplete {age: 30d, count: 1000}` / `removeOnFail {age: 90d, count: 5000}`; worker concurrency 5; **scope-integrity check throws** on payload/DB mismatch; disabled/missing schedules are silent skips (skipped event / return); `requireFeature` gate; queue-target payload gains `tenantId`, `organizationId`, `_idempotencyKey: scheduler-<id>-<epochMs>`; `nextRunAt` recalculated **from now**, also on failure and skip.
13. **Scheduler API status codes**: manual trigger 400 under local strategy with the "requires QUEUE_STRATEGY=async" error; 403 for system-scoped schedules unless `isSuperAdmin === true` (never role-name comparison); 404 with tenant/org-scoped lookup.
14. **Schedule validation errors** (exact messages): `System-scoped schedules cannot have organizationId or tenantId`; `Organization-scoped schedules must have both organizationId and tenantId`; `Tenant-scoped schedules must have tenantId and no organizationId`; `Queue target must have targetQueue`; `Command target must have targetCommand`.
15. **DB is the scheduler source of truth** — BullMQ sync failures are logged, never thrown; `syncAll` reconciliation runs once per process cold start and removes orphaned repeatables.
16. **SSE stream**: 401 for missing tenant/sub; immediate `: connected` flush; 30 s heartbeat comments; 4096-byte payload cap with structured truncation fallback; server-side audience filtering (tenant required, org/user/role optional narrowing).
17. **`createModuleEvents` non-strict default**: undeclared event ids log an error **but the event is still emitted**.
18. **Cron format**: 5-field only for `validateCron`; timezone-aware via `tz`. Interval format `^(\d+)(s|m|h|d)$` — anything else throws `Invalid interval format: ...`.

## Gotchas

- **Strategy token is `'async'`, not `'redis'`** — `QUEUE_STRATEGY=redis` is honored *only* in `packages/core/src/bootstrap.ts` (event bus creation), not by `resolveQueueStrategy()`/CLI/scheduler DI, which treat anything other than `'async'` as local. Canonical value: `async`.
- **`EmitOptions` has two definitions**: the events package (`packages/events/src/types.ts`, includes `deliverInline`, tenant/org scope) and a narrower one in `packages/shared/src/modules/events/types.ts` (only `persistent`). The bus honors the richer one.
- **`SubscriberMeta` in `packages/events/src/types.ts` documents only `{ event, id? }`** but the generator actually consumes `{ event, id?, persistent?, sync?, priority? }` — `persistent` matters to the worker/single-delivery, `sync`/`priority` only feed the CRUD sync-subscriber store.
- **Worker retries re-run all subscribers/handlers of a job**, including ones that already succeeded (events worker aggregates via `allSettled` then throws; multi-worker queues run handlers sequentially and fail wholesale). Ported subscribers must be idempotent.
- The events worker's listener map is **cached forever after first job** (`cachedListenerMap`) — subscriber changes require a worker restart.
- **Local scheduler advisory-lock hash is a 32-bit string hash with `Math.abs`** — a port must reproduce the same function only if it needs lock compatibility with a running Node instance; collisions are theoretically possible and treated as acceptable.
- The **local queue strategy is not multi-process safe** and its `concurrency` option is cosmetic (sequential processing). Don't build port behavior on it beyond dev parity.
- `enqueue-only` (`deliverInline: false`) suppresses inline delivery for **ephemeral subscribers too** — only safe for events whose subscribers are all persistent.
- The scheduler's **local vs async queue-target payloads differ** (local wraps `payload:` and adds `scheduleName/triggeredAt`; async spreads `targetPayload` at the top level and adds `_idempotencyKey`). Downstream workers must handle the async shape in production.
- `mercato scheduler run` (CLI) resolves a DI key `queueService` that is not registered anywhere at this commit — the supported manual-trigger path is `POST /api/scheduler/trigger`. Its `job.payload || job.data` defensive check in the execute worker exists for this raw-add path.
- `.env.example` mentions `EVENTS_REDIS_URL`, but the events bus actually resolves Redis via the **`QUEUE`** prefix (`QUEUE_REDIS_URL`/`REDIS_URL`); there is no `EVENTS_REDIS_URL` consumer in the analyzed code.
- The shared events producer cache means **one BullMQ producer connection per process per Redis URL**; a port with per-request DI must replicate this or leak connections (upstream hit `maxclients` exhaustion before this fix).
- BullMQ `Worker` instances also poll Redis; the **pending probe deliberately avoids creating Workers** — a port's supervisor must use passive count reads (`LLEN`/`ZCARD`-level or `getJobCounts`) only.
- `getJobCounts` local semantics (`completed`/`failed` are cumulative counters persisted in `state.json`, `clear()` preserves them) differ from BullMQ's retention-window counts; API consumers should not assume parity across strategies.
