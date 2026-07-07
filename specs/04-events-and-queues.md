# Events, Queues & Scheduler — Requirements Spec

> Derived from upstream commit adc9da27759e357febe9ed8d4b7182040d127349. Source analysis: ../upstream/analysis/04-events-queues.md

## Scope

This spec covers three cooperating subsystems a port must provide:

1. **Job queue** — a two-strategy queue abstraction (`local` file-based dev mode, `async` = BullMQ-compatible Redis mode) plus a worker host that runs module-declared job handlers.
2. **Event bus** — per-request in-process event dispatch with optional durable ("persistent") delivery through the `events` queue, a Postgres `NOTIFY` bridge for SSE broadcast events, and a declared-event registry.
3. **Scheduler** — cron/interval schedules stored in Postgres (`scheduled_jobs`), mirrored to BullMQ repeatable jobs in async mode, executed by a local polling engine in local mode.

The overriding constraint: **a port shares Redis and Postgres with Node processes.** Any queue job produced by the port must be consumable by an upstream Node BullMQ worker, and vice versa. Anything observable on the wire (Redis keys, job payloads, DB rows, HTTP responses, NOTIFY payloads) is normative. Internal structure (DI, module layout, language idioms) is not.

Out of scope: the CRUD sync-subscriber store (covered by the data-layer spec), HTTP framework details (spec 02), module discovery mechanics (spec 01).

## Requirements

### Queue strategy & configuration

- **EVENTSQUEUES-R1** — The port MUST select the queue strategy from `QUEUE_STRATEGY`: the value `async` (exact string) selects the BullMQ-compatible Redis strategy; any other value (including unset) selects the local strategy. The port MAY additionally accept the legacy token `redis` as an alias for async, matching upstream `packages/core/src/bootstrap.ts`.
- **EVENTSQUEUES-R2** — In async mode the Redis URL MUST be resolved as `QUEUE_REDIS_URL`, falling back to `REDIS_URL`. If neither is set the port MUST fail with an error. It MUST NOT default to localhost.
- **EVENTSQUEUES-R3** — Redis URLs MUST be honored in full: scheme (`redis://`/`rediss://`), username/password, db index, and query parameters.
- **EVENTSQUEUES-R4** — Queue names MUST be used verbatim with BullMQ's default key prefix: all Redis keys are `bull:<queueName>:*`. The port MUST NOT set a custom prefix.
- **EVENTSQUEUES-R5** — Boolean environment variables in this subsystem MUST be parsed case-insensitively after trimming: true = `1,true,yes,y,on,enable,enabled`; false = `0,false,no,n,off,disable,disabled`; unrecognized tokens fall back to the variable's documented default.

### Job envelope & enqueue semantics

- **EVENTSQUEUES-R6** — Every enqueue MUST wrap the caller payload in the envelope `{ "id": "<uuid v4>", "payload": <caller payload>, "createdAt": "<ISO-8601 timestamp>" }`. In async mode the BullMQ job **name** MUST equal the envelope `id`, and the BullMQ job **data** MUST be the whole envelope.
- **EVENTSQUEUES-R7** — Every async enqueue MUST use exactly these BullMQ job options: `attempts: 3`, `backoff: { type: "exponential", delay: 1000 }`, `removeOnComplete: true`, `removeOnFail: 1000`, and `delay: <delayMs>` only when a delay > 0 was requested (omitted otherwise). (Exception: scheduler repeatable jobs, see R33.)
- **EVENTSQUEUES-R8** — Job handlers MUST receive the full envelope (they unwrap `payload` themselves) plus a context `{ jobId, attemptNumber, queueName }` where `attemptNumber` is 1-based (BullMQ `attemptsMade + 1`).
- **EVENTSQUEUES-R9** — The queue interface MUST expose enqueue (returning the job id), process, clear, close, and job counts (`waiting`, `active`, `completed`, `failed`). In async mode `clear` MUST obliterate the queue (BullMQ `obliterate({ force: true })` semantics) and counts MUST come from BullMQ job counts.
- **EVENTSQUEUES-R10** — The local strategy MUST store jobs as JSON files under `<QUEUE_BASE_DIR|.mercato/queue>/<queueName>/queue.json` with state in `state.json`, retry failed jobs up to 3 attempts with exponential backoff `1000 * 2^(attempt-1)` ms, and drop jobs after the 3rd failure while incrementing `failedCount`. The local strategy SHOULD only be used for development; it is not multi-process safe.

### Workers & the worker host

- **EVENTSQUEUES-R11** — Modules MUST be able to declare workers as files with metadata `{ queue, id?, concurrency? }` and a handler. Default id is `<moduleId>:workers:<path>:<filename>`; default concurrency is 1; declarations without a `queue` MUST be ignored.
- **EVENTSQUEUES-R12** — The port MUST provide a worker-host command equivalent to `mercato queue worker <queueName> | --all [--concurrency=N]` that runs one BullMQ-compatible consumer per queue.
- **EVENTSQUEUES-R13** — When multiple module workers target the same queue, the host MUST run exactly one consumer for that queue whose handler executes every registered worker for that queue **sequentially per job**. If any worker's handler throws, the whole job MUST fail (so a retry re-runs all workers, including ones that succeeded). Worker handlers MUST therefore be idempotent.
- **EVENTSQUEUES-R14** — Each job MUST be processed in a fresh request-scoped service container (per-job isolation), with best-effort cleanup of ORM/session state afterwards.
- **EVENTSQUEUES-R15** — Per-queue consumer concurrency MUST default to the max of the member workers' declared concurrencies, and the sum across queues SHOULD be clamped to a DB-connection budget (`OM_WORKERS_DB_CONNECTION_BUDGET`, default = the DB pool max) with a floor of 1 per queue.
- **EVENTSQUEUES-R16** — The worker host MUST shut down gracefully on SIGTERM/SIGINT, closing all queue connections before exit.
- **EVENTSQUEUES-R17** — The server process SHOULD support worker auto-spawn controlled by `AUTO_SPAWN_WORKERS`/`OM_AUTO_SPAWN_WORKERS` (default on/eager). Lazy mode, if implemented, MUST probe for pending jobs using **read-only** operations (BullMQ `getJobCounts`-level reads; never by instantiating a consumer) and spawn the worker only when ready jobs exist; probe errors MUST be fail-soft (do not spawn).

### Event bus

- **EVENTSQUEUES-R18** — The port MUST provide an event bus with `emit(event, payload, options)` where options include `persistent?`, `deliverInline?`, `tenantId?`, `organizationId?`. Subscribers register with an event pattern and optional `persistent`, `id`, `sync`, `priority` metadata; default subscriber id is `<moduleId>:<path>:<filename>` (no `subscribers` segment).
- **EVENTSQUEUES-R19** — `emit` MUST execute the pipeline in this order: (1) global taps, (2) inline delivery to matching subscribers, (3) cross-process Postgres NOTIFY broadcast (broadcast events only), (4) enqueue to the `events` queue when `persistent: true`.
- **EVENTSQUEUES-R20** — Errors thrown by global taps or inline subscribers MUST be logged and swallowed — they MUST NOT propagate to the emitter or affect other subscribers.
- **EVENTSQUEUES-R21** — Event pattern matching MUST support: literal `*` matching everything, exact string equality, and glob-style `*` wildcards that do **not** cross `.` segment boundaries (e.g. `orders.*.created` matches `orders.line.created` but not `orders.line.item.created`).
- **EVENTSQUEUES-R22** — Single-delivery mode (`OM_EVENTS_SINGLE_DELIVERY`, default **on**): on a `persistent: true` emit, subscribers marked `persistent: true` MUST be skipped inline and dispatched only by the events worker; non-persistent subscribers still run inline. With single-delivery off (legacy), persistent subscribers run inline **and** in the worker.
- **EVENTSQUEUES-R23** — `deliverInline: false` on a persistent emit MUST suppress inline delivery entirely (enqueue-only), including for non-persistent subscribers.
- **EVENTSQUEUES-R24** — Single-delivery fail-safe: at server startup, if single-delivery is requested but no events worker will exist (auto-spawn off and `OM_EVENTS_EXTERNAL_WORKER` not set), the port MUST rewrite the effective flag to `false` for the process and any spawned children and log a prominent warning, so persistent subscribers are never silently lost.
- **EVENTSQUEUES-R25** — A `persistent: true` emit MUST enqueue `{ "event": <event id>, "payload": <emit payload>, "options": <emit options> }` (wrapped in the R6 envelope) onto the queue named `events`.
- **EVENTSQUEUES-R26** — The events-queue producer connection SHOULD be memoized process-wide per Redis URL (one producer connection per process, not per request), disable-able via `OM_EVENTS_SHARED_PRODUCER=0`, and closed on shutdown. A port MUST NOT open a new Redis connection per request for event enqueues.
- **EVENTSQUEUES-R27** — The events worker (queue `events`, concurrency from `WORKERS_EVENTS_CONCURRENCY`, default 1) MUST: under single-delivery, dispatch to every subscriber whose pattern matches (wildcards included) **and** is `persistent: true`; under legacy mode, dispatch to exact-match subscribers only. It MUST run subscribers concurrently (settle-all), pass ctx `{ resolve, tenantId, organizationId }` from the emit options (null when absent), and **throw** if ≥ 1 subscriber failed (message reporting `<n>/<total>` failures and failing ids) so the job retries. Delivery is at-least-once; retries re-run previously successful subscribers.
- **EVENTSQUEUES-R28** — Declared events: modules declare events with `{ id, label, description?, category?, entity?, excludeFromTriggers?, clientBroadcast?, portalBroadcast? }`. Emitting an undeclared id through the typed emitter MUST log an error but still emit (unless a strict mode was opted in, which throws).

### Cross-process bridge & SSE

- **EVENTSQUEUES-R29** — Only events declared `clientBroadcast: true` whose payload contains a non-empty string `tenantId` MUST be published cross-process, via Postgres `NOTIFY om_event_bridge` with JSON envelope `{ "event", "payload", "options"?, "originPid" }`. Envelopes over **7000 bytes** MUST be dropped with a warning. Publish errors are logged, never thrown.
- **EVENTSQUEUES-R30** — Bridge listeners MUST `LISTEN om_event_bridge`, reconnect after ~1000 ms on connection loss, ignore envelopes whose `originPid` equals their own process id, and re-dispatch received events **only** to SSE/tap fan-out — inline subscribers are not re-run cross-process.
- **EVENTSQUEUES-R31** — `GET /api/events/stream` MUST: require auth (401 with body `Unauthorized` when tenant/subject missing); respond `text/event-stream` with `Cache-Control: no-cache, no-transform` and `X-Accel-Buffering: no`; send an initial `: connected` comment; send `:heartbeat` comments every 30 s; deliver only `clientBroadcast` events, audience-filtered server-side by tenant (required) and optional organization/recipient-user/recipient-role narrowing; message data is JSON `{ "id": <event name>, "payload", "timestamp": <ms epoch>, "organizationId" }`; payloads over 4096 bytes MUST be replaced by a truncated fallback `{ "truncated": true, "id"?, "entityId"?, "entityType"? }` (or skipped when even that is impossible).
- **EVENTSQUEUES-R32** — `GET /api/events` MUST require auth and return `{ "data": EventDefinition[], "total": number }` (200), filterable by `category`, `module`, and `excludeTriggerExcluded` (default `true`: events with `excludeFromTriggers` are hidden unless the query sets it to `false`).

### Scheduler

- **EVENTSQUEUES-R33** — Postgres table `scheduled_jobs` (columns in Contracts) is the **source of truth**. In async mode, enabled schedules MUST be mirrored to BullMQ repeatable jobs on queue `scheduler-execution`: job name `schedule-<uuid>`, repeat options `{ tz: <timezone|'UTC'>, pattern: <cron> }` or `{ tz, every: <intervalMs> }`, job data in the R6 envelope shape with `id: "schedule-<uuid>"` and `payload: { scheduleId, tenantId, organizationId, scopeType }`, options `removeOnComplete: { age: 2592000, count: 1000 }`, `removeOnFail: { age: 7776000, count: 5000 }`, no explicit jobId.
- **EVENTSQUEUES-R34** — Mirroring MUST be best-effort: BullMQ sync failures are logged, never thrown (the DB write still succeeds). A reconciliation (`syncAll`) MUST run once per process cold start: register enabled DB schedules missing in BullMQ and remove repeatables with no enabled DB row.
- **EVENTSQUEUES-R35** — Schedule validation MUST enforce scope and target rules with these exact error messages: `System-scoped schedules cannot have organizationId or tenantId`; `Organization-scoped schedules must have both organizationId and tenantId`; `Tenant-scoped schedules must have tenantId and no organizationId`; `Queue target must have targetQueue`; `Command target must have targetCommand`.
- **EVENTSQUEUES-R36** — Cron expressions MUST be validated as exactly 5 whitespace-separated fields and evaluated timezone-aware. Intervals MUST match `^(\d+)(s|m|h|d)$`; anything else MUST fail with `Invalid interval format: ...`.
- **EVENTSQUEUES-R37** — The execute-schedule worker (queue `scheduler-execution`, concurrency 5) MUST per job: reload the schedule from DB (return silently if missing/deleted); **throw** on any mismatch between the job payload's `scopeType`/`tenantId`/`organizationId` and the DB row (anti-tampering); emit `scheduler.job.skipped` and return if disabled; skip when `requireFeature` is set and the tenant lacks the feature; emit `scheduler.job.started` before dispatch.
- **EVENTSQUEUES-R38** — For `targetType = "queue"` the worker MUST enqueue to `targetQueue` the payload `{ ...targetPayload, tenantId, organizationId, _idempotencyKey: "scheduler-<scheduleId>-<epochMs>" }` (standard R6/R7 envelope and options), close the queue afterwards, set `lastRunAt`, and emit `scheduler.job.completed` with `{ queueJobId, queueName }`. For `targetType = "command"` it MUST execute the command with `{ ...targetPayload, tenantId, organizationId }` and emit completed with `{ commandId, commandResult }`. Any other target configuration MUST throw.
- **EVENTSQUEUES-R39** — In local mode a polling engine (every `SCHEDULER_POLL_INTERVAL_MS`, default 30000 ms) MUST select due enabled schedules (`nextRunAt <= now`, limit 100, ordered by `nextRunAt`) and execute each under a Postgres transaction-scoped advisory lock (`pg_try_advisory_xact_lock`); if the lock is not acquired the schedule is skipped. It MUST recalculate `nextRunAt` **from now** after every execution, including failures and feature-skips (drift-free), and emit `scheduler.job.started/completed/skipped/failed`.
- **EVENTSQUEUES-R40** — `POST /api/scheduler/trigger` MUST return: 401 unauthenticated; 404 when the schedule is not found under tenant/org-scoped lookup; 403 for system-scoped schedules unless the caller `isSuperAdmin === true`; **400 when `QUEUE_STRATEGY` is not `async`** (message stating async is required); on success 200 `{ "ok": true, "jobId", "message" }`, having enqueued a one-off job whose payload additionally carries `triggerType: "manual"` and `triggeredByUserId`.
- **EVENTSQUEUES-R41** — The scheduler MUST declare the events `scheduler.job.started`, `scheduler.job.completed`, `scheduler.job.failed`, `scheduler.job.skipped`. The async execute worker does NOT emit `scheduler.job.failed` (failures propagate as job retries); only the local engine emits it.
- **EVENTSQUEUES-R42** — Remaining scheduler HTTP routes MUST exist with upstream auth features: `GET/POST/PUT/DELETE /api/scheduler/jobs` (`scheduler.jobs.view` / `scheduler.jobs.manage`), `GET /api/scheduler/jobs/[id]/executions`, `GET /api/scheduler/targets`, `GET /api/scheduler/queue-jobs/[jobId]` (all view-gated), `POST /api/scheduler/trigger` (`scheduler.jobs.trigger`).
- **EVENTSQUEUES-R43** — Local-scheduler auto-spawn SHOULD follow upstream: spawned only when `QUEUE_STRATEGY=local` and `AUTO_SPAWN_SCHEDULER` (default true); lazy probing uses read-only SQL counts on `scheduled_jobs` and tolerates a missing table (Postgres error `42P01`).

### CLI / operational surface

- **EVENTSQUEUES-R44** — The port MUST provide operational commands equivalent to: `queue worker <name>|--all [--concurrency=N]`, `queue clear <name>`, `queue status <name>`, `events emit <event> [json] [--persistent]`, `events clear`, `scheduler list|status|run <id>|start`. Names MAY be adapted to the port's CLI conventions; behavior MUST match.

## Contracts

### Redis / BullMQ wire format (byte-level interop)

All keys use BullMQ's default prefix: `bull:<queueName>:*` (e.g. `bull:events:wait`, `bull:events:delayed`, `bull:events:meta`, per-job hashes `bull:events:<jobId>`, repeatables under `bull:scheduler-execution:repeat:*`). BullMQ protocol version: **^5.0.0**.

**Producer contract for any queue `Q`:**

```
Queue.add(
  name = "<envelope uuid>",
  data = { "id": "<uuid>", "payload": <caller payload>, "createdAt": "<ISO-8601>" },
  opts = { "removeOnComplete": true, "removeOnFail": 1000, "attempts": 3,
           "backoff": { "type": "exponential", "delay": 1000 }, "delay"?: <ms> }
)
```

**Events queue** (`events`) — `data.payload`:

```json
{ "event": "<event id>", "payload": { "...": "emit payload" },
  "options": { "persistent": true, "tenantId": "...", "organizationId": "...", "deliverInline": false } }
```

(`tenantId`/`organizationId`/`deliverInline` present only when passed.)

**Scheduler repeatables** (`scheduler-execution`):

```
name = "schedule-<uuid>"
data = { "id": "schedule-<uuid>",
         "payload": { "scheduleId": "...", "tenantId": "...", "organizationId": "...", "scopeType": "tenant" },
         "createdAt": "<ISO-8601>" }
opts = { "repeat": { "tz": "UTC", "pattern": "*/5 * * * *" } | { "tz": "UTC", "every": 900000 },
         "removeOnComplete": { "age": 2592000, "count": 1000 },
         "removeOnFail": { "age": 7776000, "count": 5000 } }
```

**Scheduler queue-target dispatch payload (async):** `{ ...targetPayload, "tenantId", "organizationId", "_idempotencyKey": "scheduler-<scheduleId>-<epochMs>" }` — wrapped in the standard envelope. (Local mode uses a different shape — `{ scheduleId, scheduleName, scopeType, tenantId, organizationId, payload: targetPayload, triggeredAt }` — downstream workers must handle the async shape in production.)

**Consumer contract:** one BullMQ `Worker(queueName, processor, { connection, concurrency })` per queue per worker process; handler context `attemptNumber = attemptsMade + 1`. `clear` = `obliterate({ force: true })`; `status` = `getJobCounts('waiting','active','completed','failed')`.

### Postgres

**Table `scheduled_jobs`:** `id uuid pk default gen_random_uuid()`, `organization_id uuid null`, `tenant_id uuid null`, `scope_type text default 'tenant'` (`system|organization|tenant`), `name text`, `description text null`, `schedule_type text` (`cron|interval`), `schedule_value text`, `timezone text default 'UTC'`, `target_type text` (`queue|command`), `target_queue text null`, `target_command text null`, `target_payload jsonb null`, `require_feature text null`, `is_enabled bool default true`, `last_run_at`, `next_run_at`, `source_type text default 'user'` (`user|module`), `source_module text null`, `created_at`/`updated_at` (`now()`), `deleted_at null`, `created_by_user_id`/`updated_by_user_id uuid null`. Indexes: `(organization_id, tenant_id)`, `(next_run_at)`, `(scope_type, is_enabled)`.

**Advisory locks (local scheduler):** `pg_try_advisory_xact_lock(<key>)` where `<key> = abs(32-bit Java-style string hash of "schedule:<id>")`. The exact hash function only matters for lock compatibility when a port coexists with a running Node local scheduler (see Allowed deviations).

**Event bridge:** `NOTIFY om_event_bridge, '<json>'` / `LISTEN om_event_bridge`; envelope `{ "event", "payload", "options"?, "originPid" }`; payload ≤ 7000 bytes (larger dropped).

### HTTP

| Route | Method | Auth | Contract |
|---|---|---|---|
| `/api/events` | GET | authenticated | 200 `{ "data": EventDefinition[], "total": n }`; query `category`, `module`, `excludeTriggerExcluded` (default `true`) |
| `/api/events/stream` | GET | authenticated; 401 text `Unauthorized` | SSE per R31 (heartbeat 30 s, 4096 B cap, audience filtering) |
| `/api/scheduler/jobs` | GET/POST/PUT/DELETE | `scheduler.jobs.view` / `scheduler.jobs.manage` | Schedule CRUD; validation messages per R35 |
| `/api/scheduler/jobs/[id]/executions` | GET | `scheduler.jobs.view` | Execution history from BullMQ job state |
| `/api/scheduler/trigger` | POST | `scheduler.jobs.trigger` | 401 / 404 / 403 / **400 non-async** / 200 `{ "ok": true, "jobId", "message" }` |
| `/api/scheduler/targets` | GET | `scheduler.jobs.view` | Available queue/command targets |
| `/api/scheduler/queue-jobs/[jobId]` | GET | `scheduler.jobs.view` | BullMQ job details |

### Environment variables

| Var | Contract |
|---|---|
| `QUEUE_STRATEGY` | `async` → Redis/BullMQ; anything else → local |
| `QUEUE_REDIS_URL` → `REDIS_URL` | Resolution order; missing → error, never localhost |
| `QUEUE_BASE_DIR` | Local strategy dir, default `.mercato/queue` |
| `OM_EVENTS_SINGLE_DELIVERY` | Default true |
| `OM_EVENTS_EXTERNAL_WORKER` | Default false |
| `OM_EVENTS_SHARED_PRODUCER` | Default true |
| `WORKERS_EVENTS_CONCURRENCY` | events worker concurrency, default 1 |
| `AUTO_SPAWN_WORKERS` / `OM_AUTO_SPAWN_WORKERS`, `OM_AUTO_SPAWN_WORKERS_LAZY`, `OM_AUTO_SPAWN_WORKERS_LAZY_POLL_MS` (1000, min 250), `OM_AUTO_SPAWN_WORKERS_LAZY_RESTART` (true) | Worker auto-spawn |
| `OM_WORKERS_DB_CONNECTION_BUDGET` | Σconcurrency budget, default DB pool max |
| `AUTO_SPAWN_SCHEDULER` (true), `SCHEDULER_POLL_INTERVAL_MS` (30000) | Local scheduler |

### Reserved queue names

`events` and `scheduler-execution` are infrastructure queues with fixed semantics. Module queue names at the pinned commit (a ported module must keep its module's queue names verbatim): `notifications`, `messages-email`, `checkout-email`, `checkout-transaction-expiry`, `workflow-activities`, `webhook-deliveries`, `vector-indexing`, `fulltext-indexing`, `data-sync-import`, `data-sync-export`, `data-sync-scheduled`, `catalog-product-bulk-delete`, `customers-deals-bulk-update-stage`, `customers-deals-bulk-update-owner`, `customer-accounts-cleanup-sessions`, `customer-accounts-cleanup-tokens`, `domain-verification`, `domain-tls-retry`, `integration-health-probe`, `integration-log-pruner`, `payment-gateways-webhook`, `payment-gateways-status-poller`, `shipping-carriers-webhook`, `shipping-carriers-status-poller`, `stripe-webhook`, `sync-akeneo-first-import`, `sync-akeneo-delete-products`, `ai-token-usage-prune`, `ai-pending-action-cleanup`, `communication-channels-{inbound,outbound,reactions,poll,poll-tick,...}`.

## Concept mapping

| Upstream TS concept | Technology-agnostic concept a port implements |
|---|---|
| `createQueue(name, strategy)` / `createModuleQueue` (`packages/queue/src/factory.ts`) | Queue factory producing a BullMQ-wire-compatible producer/consumer for a named queue |
| `QueuedJob` envelope (`packages/queue/src/types.ts`) | The `{ id, payload, createdAt }` JSON job envelope (R6) |
| `strategies/async.ts` (BullMQ ^5) | Any BullMQ-protocol-compatible Redis client library (e.g. `bullmq` Python, a Go/.NET BullMQ port) speaking the `bull:` key protocol |
| `strategies/local.ts` (JSON files) | File-based dev queue with the same retry/backoff semantics (or a documented dev-only substitute) |
| `workers/*.ts` (`metadata` + default export) | Module worker declaration: queue name + concurrency + handler, discoverable by the port's module registry |
| `mercato queue worker` + `createPerJobWorkerHandler` | Worker host process: one consumer per queue, sequential multi-handler execution, per-job scoped service container |
| `planWorkerConcurrency` / connection budget | Concurrency-vs-DB-pool clamping policy |
| `createEventBus` (`packages/events/src/bus.ts`) | Per-request event bus implementing the 4-step emit pipeline (R19) |
| `subscribers/*.ts` metadata | Subscriber declaration: pattern + `persistent`/`sync`/`priority` flags |
| `matchEventPattern` single-segment wildcards | Segment-aware glob matcher (R21) |
| `OM_EVENTS_SINGLE_DELIVERY` + `reconcileSingleDelivery` | Single-delivery mode with startup fail-safe downgrade (R22/R24) |
| `events.worker.ts` | The `events`-queue consumer dispatching persistent subscribers (R27) |
| `createModuleEvents` (`packages/shared/src/modules/events/factory.ts`) | Declared-event registry + validating typed emit (R28) |
| pg `NOTIFY om_event_bridge` bridge (`packages/events/src/bridge.ts`) | Cross-process broadcast channel over Postgres LISTEN/NOTIFY (R29/R30) |
| `/api/events/stream` SSE route | Authenticated, audience-filtered SSE endpoint (R31) |
| `ScheduledJob` MikroORM entity | `scheduled_jobs` table managed by the port's migration tooling (same columns/indexes) |
| `SchedulerService` | Schedule CRUD service with scope/target validation (R35) and best-effort mirror |
| `BullMQSchedulerService` + `ScheduledJobSubscriber` + `syncAll` | Repeatable-job mirroring on entity change + cold-start reconciliation (R33/R34) |
| `execute-schedule.worker.ts` | `scheduler-execution` consumer with scope-integrity, feature gate, target dispatch (R37/R38) |
| `LocalSchedulerService` + `pg_try_advisory_xact_lock` | DB-polling execution engine with advisory locking (R39) |
| `cron-parser` + `validateCron` / `parseInterval` | 5-field timezone-aware cron parsing + `\d+(s\|m\|h\|d)` intervals (R36) |
| `getRedisUrlOrThrow('QUEUE')` | `QUEUE_REDIS_URL → REDIS_URL → error` resolution (R2) |
| `parseBooleanToken` | Env boolean token table (R5) |
| `getQueuePendingProbe` | Read-only pending-job probe for lazy supervisors (R17) |

## Allowed deviations

Idiomatic replacements are welcome when the wire/DB/HTTP surface is unchanged. Document each as a decision.

**MAY deviate:**

- **Language/runtime internals**: DI container, module discovery, lazy loading, code generation — any mechanism is fine if worker/subscriber declarations resolve to the same queue names, ids, and handler semantics.
- **BullMQ client library**: any implementation that is byte-compatible with BullMQ ^5 on Redis (official ports like `bullmq` for Python, or a native reimplementation of the Lua-script protocol). What matters is that Node BullMQ workers can consume the port's jobs and vice versa.
- **Local (dev) strategy storage**: an in-memory or SQLite dev queue is acceptable instead of JSON files, provided retry counts/backoff and the CLI status semantics match; the local strategy is never shared cross-technology. (`QUEUE_BASE_DIR` layout only matters if the port claims file compatibility with a Node dev instance.)
- **Validation library**: language-native validation instead of Zod, as long as R35's exact error messages and HTTP status codes are preserved.
- **Advisory-lock hash function**: only needs to replicate upstream's 32-bit string hash if the port's local scheduler must coexist with a Node local scheduler on the same DB; otherwise any stable hash of `schedule:<id>` is fine (document the choice).
- **Cron library**: any 5-field, timezone-aware cron evaluator; next-run results must agree with `cron-parser` for standard expressions.
- **Process supervision**: threads/goroutines/systemd instead of child-process auto-spawn, provided the single-delivery fail-safe (R24) still knows whether a worker exists.
- Upstream bugs need not be reproduced: the events worker's forever-cached listener map (a port MAY support live refresh), and the broken `mercato scheduler run` DI wiring (the supported trigger path is the HTTP API).

**MUST NOT change:**

- The job envelope, job name = envelope uuid, and enqueue options (R6/R7) — these are the interop contract.
- Queue names, the `bull:` key prefix, and the `events` / `scheduler-execution` payload shapes.
- `scheduled_jobs` column names/types/defaults and the DB-is-source-of-truth rule.
- Event dispatch ordering, single-delivery semantics, error-swallowing, and at-least-once persistent delivery (R19–R27).
- The NOTIFY channel name `om_event_bridge`, its envelope, and the 7000-byte cap.
- HTTP paths, methods, status codes, and JSON shapes in the Contracts table (including the 400-on-local manual trigger).
- Env var names, resolution order, and defaults (R2, R5, env table).

## Verification

How `om-verify-parity` checks these requirements (all interop tests run against a shared Redis + Postgres with both the Node upstream and the port attached):

1. **Envelope & options parity (R6–R7)** — the port enqueues a job to a scratch queue; the harness reads the raw `bull:<q>:<jobId>` Redis hash and asserts: job name == `data.id` == uuid v4, `data` has exactly `{id,payload,createdAt}` with ISO-8601 `createdAt`, and `opts` equal `{attempts:3, backoff:{type:'exponential',delay:1000}, removeOnComplete:true, removeOnFail:1000}` (+`delay` only when requested). Repeat in reverse: Node enqueues, the port's worker consumes and echoes `{jobId, attemptNumber, queueName, payload}` for comparison.
2. **Cross-runtime consumption (R4, R12–R13)** — Node producer → port worker and port producer → Node BullMQ worker round-trips on the same queue; assert both complete and Redis is left clean (`removeOnComplete`). A deliberately failing handler must show `attemptsMade` progressing 0→1→2 with exponential delays and land in `bull:<q>:failed` retained per `removeOnFail:1000`.
3. **Events pipeline (R19–R27)** — emit matrix tests via the port's `events emit` CLI/API: persistent emit with single-delivery on → assert persistent subscriber did NOT run inline (spy log) and an `events` job with payload `{event,payload,options}` appeared; run the events worker and assert wildcard persistent subscribers fired with correct ctx tenant/org; one failing subscriber → job throws with the `n/total subscriber(s) failed` message and retries. Flip `OM_EVENTS_SINGLE_DELIVERY=false` → assert dual dispatch. Start server with auto-spawn off and no external-worker flag → assert effective flag downgraded and warning logged (R24).
4. **Pattern matcher (R21)** — table-driven fixture shared between runtimes: (`orders.*`, `orders.created`) → true; (`orders.*`, `orders.line.created`) → false; (`*`, anything) → true; exact matches; prefix-mode cases. Port must produce identical booleans.
5. **Bridge & SSE (R29–R31)** — port emits a `clientBroadcast` event with `tenantId`; harness `LISTEN om_event_bridge` asserts the envelope JSON and that a >7000 B payload is not notified. SSE: open `/api/events/stream` authenticated → assert headers, `: connected` first frame, heartbeat cadence (tolerance ±2 s), message JSON shape, 4096 B truncation fallback, and 401 text for anonymous.
6. **Scheduler mirroring (R33–R36)** — create schedules via `POST /api/scheduler/jobs` (cron + interval); inspect `bull:scheduler-execution:repeat:*` for name `schedule-<uuid>`, repeat opts `{tz,pattern|every}`, retention `{age:2592000/7776000}`; disable the schedule → repeatable removed; delete the BullMQ entry manually and cold-start the port → `syncAll` restores it. Invalid scope/target payloads must return the exact R35 messages. 6-field cron and `10x` interval must be rejected.
7. **Execute worker (R37–R38)** — enqueue a `scheduler-execution` job with a tampered `tenantId` → job fails; disabled schedule → `scheduler.job.skipped` observed, job succeeds; queue-target run → downstream queue receives `{...targetPayload, tenantId, organizationId, _idempotencyKey: /^scheduler-<id>-\d+$/}` and `scheduled_jobs.last_run_at` updated.
8. **HTTP parity (R31–R32, R40, R42)** — golden request/response suite diffing status codes and JSON bodies against upstream for every route in the Contracts table, including the 400 manual-trigger response under `QUEUE_STRATEGY=local` and 403 for non-superadmin system schedules.
9. **Config semantics (R1–R2, R5)** — start the port with `QUEUE_STRATEGY=async` and no Redis env → assert startup error (no localhost connection attempted); with only `REDIS_URL` → connects there; with both → `QUEUE_REDIS_URL` wins. Boolean env table fuzzed over all listed tokens.
10. **Schema parity (R33)** — `pg_dump --schema-only` diff of `scheduled_jobs` (columns, defaults, indexes) between the port's migrations and upstream's.
