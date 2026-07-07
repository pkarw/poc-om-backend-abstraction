# 0003 — Official `bullmq` PyPI package for queues (full Node interop)

## Status

Accepted

## Context

Upstream queueing (packages/queue) is a strategy pattern selected by `QUEUE_STRATEGY` (`local` | `redis`), with BullMQ on Redis for the `redis` strategy. The cross-technology contract for this repo is hard: jobs enqueued by Node BullMQ must be consumable by the port and vice versa.

## Decision

- Use the official `bullmq` package from PyPI, maintained by taskforcesh (the BullMQ authors). It implements the actual BullMQ protocol (same Lua scripts, same Redis data structures), so interop with Node producers/consumers is full, not emulated.
- Keep upstream's strategy pattern in `src/om/shared/queue.py`: `QUEUE_STRATEGY=redis` → `BullMQQueueBackend` (bullmq `Queue.add`); `QUEUE_STRATEGY=local` → `LocalQueueBackend` executing registered handlers inline (dev/test convenience, mirroring upstream's local strategy).
- The worker host (`src/om/worker.py`) creates one `bullmq.Worker` per `WorkerSpec` (queue name, handler, concurrency) collected from the module registry.

## Consequences

- No compatibility shim to maintain — unlike the Go/.NET ports, Python gets BullMQ wire compatibility for free and this ADR carries no "implementation status" caveat.
- Queue names, job names and JSON payload shapes are the shared contract with the TS stack; ported workers must reuse the upstream queue ids verbatim.
- The bullmq package is asyncio-based, matching the rest of the stack; `QUEUE_REDIS_URL` (falling back to `REDIS_URL`) selects the broker exactly as upstream does.
- With `QUEUE_STRATEGY=local` the worker host has nothing to consume (jobs run inline in the enqueuing process); it logs this and idles so container topology stays uniform.
