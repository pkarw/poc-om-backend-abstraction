# 0004 — Queue Abstraction and BullMQ Compatibility Strategy

## Status

Accepted (compatibility adapter: **not implemented yet — tracked porting task**)

## Context

Upstream queues (packages/queue) use a strategy pattern (`QUEUE_STRATEGY`:
`local` | `redis`) with BullMQ on Redis. The cross-technology contract is that
jobs enqueued by Node BullMQ must be consumable by this port and vice versa.
Unlike Python (official `bullmq` PyPI package), there is **no official BullMQ
client for .NET**.

## Decision

Ship a clean queue abstraction now and pursue BullMQ wire compatibility as an
explicit, tracked task:

1. `IJobQueue` / `IJobHandler` (src/OpenMercato.Core/Queue/) is the only queue
   API modules may use — never raw Redis.
2. The current implementation (`RedisJobQueue` + `QueueWorkerService`) uses its
   own Redis layout: `om:queue:{q}:id` counter, `om:queue:{q}:jobs:{id}` hash
   (`name`, `data` JSON, `timestamp`), `om:queue:{q}:wait`/`:active`/`:failed`
   lists, with RPOPLPUSH wait→active and completion markers on the job hash.
   This deliberately shadows BullMQ's shape (`bull:{q}:wait`, job hashes with
   `name`/`data`/`timestamp`) to keep the adapter gap small.
3. BullMQ compatibility plan: implement a `BullMqJobQueue`/worker behind the
   same interfaces by vendoring BullMQ's Lua scripts (addJob, moveToActive,
   moveToFinished) or implementing the protocol subset — waiting list, active
   list, job hash fields, `bull:{q}:events` stream, lock keys and stalled-job
   handling — selected via `QUEUE_STRATEGY`.

## Consequences

- **Honest current status:** jobs are interchangeable only between this port's
  own API and worker. Node BullMQ jobs are NOT consumable yet and vice versa.
- Modules are insulated from the swap: when the BullMQ adapter lands, no module
  code changes.
- The polling worker (StackExchange.Redis cannot block on BRPOPLPUSH) adds up
  to ~500 ms latency on idle queues; acceptable for the scaffold, revisited
  with the BullMQ adapter.
