# 0003 — Queue abstraction and BullMQ compatibility strategy

## Status

Accepted (compatibility work in progress — see honest status below)

## Context

Upstream queues jobs with BullMQ on Redis (`packages/queue`, strategy pattern
via `QUEUE_STRATEGY=local|redis`). The repo-wide contract: jobs enqueued by
Node BullMQ must be consumable by this port and vice versa. **There is no
official BullMQ client for Go** (taskforcesh ships Node and Python only).
Third-party Go ports exist but are unmaintained/incomplete, so we own the
implementation.

## Decision

1. Ship a clean `JobQueue` interface (`internal/platform/queue/queue.go`) with
   `Enqueue` / `Process`, plus `local` (in-process channels) and `redis`
   strategies selected by `QUEUE_STRATEGY` — mirroring upstream.
2. The Redis strategy (`internal/platform/queue/redis.go`) adopts BullMQ's
   data model from day one: `bull:<queue>:id` counter, `bull:<queue>:<jobId>`
   job hash (`name`, `data`, `opts`, `timestamp`, `attemptsMade`,
   `finishedOn`, `failedReason`, `returnvalue`), `wait`/`active` lists
   (LPUSH → BLMOVE RIGHT/LEFT), `completed`/`failed` sorted sets.
3. Full wire compatibility is reached by vendoring/re-implementing BullMQ's
   Lua scripts (`addStandardJob`, `moveToActive`, `moveToFinished`) and the
   remaining keys: `meta`, `events` stream, `delayed`/`prioritized` sets,
   `stalled` checks and job locks (`bull:<queue>:<id>:lock`).

## Honest compatibility status

| Capability | Status |
|---|---|
| Jobs enqueued and processed through this package's own abstraction | ✅ works (this scaffold) |
| BullMQ-style key naming and job-hash fields | ✅ implemented |
| Node BullMQ worker consuming jobs enqueued here | ❌ not yet — requires meta/events/lock keys and Lua-script semantics |
| This worker consuming jobs enqueued by Node BullMQ | ❌ not yet — BLMOVE subset ignores locks, delayed/prioritized jobs, `marker` key |
| Delayed / repeatable / prioritized jobs, retries with backoff | ❌ not implemented |

Full BullMQ wire compatibility is an explicit tracked porting task; do **not**
claim interchangeability until a cross-runtime integration test (Node enqueue →
Go process, Go enqueue → Node process) passes.

## Consequences

- Module code depends only on `JobQueue`; the compatibility work happens once
  in the platform layer without touching modules.
- Key-naming parity means no data migration when the full protocol lands.
- Until then, mixed Node/Go deployments must route each queue to a single
  runtime.
