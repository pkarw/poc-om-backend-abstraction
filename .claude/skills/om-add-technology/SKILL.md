---
name: om-add-technology
description: Scaffold a new technology package under packages/<tech> so modules can be ported to it. Use when targeting a language/stack that has no package yet. Args: <tech> <stack hints> — the package name (e.g. python, dotnet, golang, rust) and free-form stack hints (e.g. "FastAPI + SQLAlchemy/Alembic + bullmq python client"). Produces the full standard layout, AGENTS.md, ADRs, docker-compose, Makefile, and a booting healthz + health_check example module.
---

# om-add-technology <tech> <stack hints>

Create `packages/<tech>/` conforming to the technology package standard, prove it boots, and register it in the repo's tracking docs. After this skill, `om-port-module <any-module> <tech>` must be runnable.

## Ground rules

- `specs/09-technology-package-standard.md` is normative for layout, Makefile target names, AGENTS.md section outline, and README structure. Read it FIRST; if it does not exist, stop and report — do not invent a divergent standard.
- **Consistency across packages beats local preference** (AGENTS.md rule 5): when the spec leaves room, copy the shape of an existing `packages/*/` package rather than innovating.
- Hard stack requirements for every tech: PostgreSQL 17 with a real migration tool, Redis 7, and a **BullMQ-compatible** queue client (jobs interchangeable with Node BullMQ ^5 on shared Redis, `bull:<queue>:*` keys) plus a worker host. Never claim BullMQ wire compatibility that is not implemented — state the honest status in the queue ADR.
- Shared env var names, verbatim: `DATABASE_URL`, `REDIS_URL`, `QUEUE_STRATEGY`, `QUEUE_REDIS_URL`, `JWT_SECRET`, `OM_INIT_SUPERADMIN_EMAIL`, `OM_INIT_SUPERADMIN_PASSWORD`.

## Procedure

### 1. Read the standard and the neighbors

1. Read `specs/09-technology-package-standard.md` in full — extract the required directory layout, the standard Makefile targets (at minimum `up`, `down`, `dev`, `worker`, `migrate`, `test`), the AGENTS.md section outline, and the README structure.
2. List existing `packages/*/` and skim one complete package (its AGENTS.md, Makefile, docker-compose, health_check module) as the shape reference.
3. Read `specs/06-runtime-and-startup.md` (healthz, init, cache) and `specs/04-events-and-queues.md` (queue strategy, envelope, worker host) — the skeleton must honor them.

### 2. Decide the stack

From the stack hints plus (if needed) a quick ecosystem check, decide and justify:

- **Runtime & web framework** — must support: convention-based route registration per module, middleware-style dispatch pipeline (auth → tenant guard → features → rate limit), JSON byte-control (exact envelopes).
- **ORM + migration tool** — real, versioned, up/down migrations; exact control over table/column names.
- **Queue client** — a BullMQ-protocol-compatible library (official `bullmq` ports where they exist, else a vetted protocol implementation); plus the worker-host model (one consumer per queue, concurrency option).
- **Validation, DI, test framework** — language-native best choices.

Pick the API host port: existing conventions are python→8000, dotnet→8080, golang→8090; choose the next free conventional port for a new tech and record it in the README.

### 3. Fan out scaffolding subagents

First write a short stack-decision brief to the scratchpad (stack choices + port + layout). Then spawn **three subagents in parallel**, each with the brief and spec 09:

- **Subagent A — infra**: `docker-compose.yml` (postgres:17, redis:7, api service, worker service; healthchecks; env wired to the shared var names), `Makefile` with the standard targets exactly as named in spec 09, `.env.example`, dockerfile(s), `.gitignore`.
- **Subagent B — app skeleton**: the runtime source tree per spec 09 — module registry/composition config, dispatch pipeline stub honoring `specs/01`/`02` guard order and default-deny auth, queue abstraction with `QUEUE_STRATEGY` selection (`async` ⇒ BullMQ/Redis, else local) and the `{id,payload,createdAt}` envelope, worker host entrypoint, migration tool wiring, `/healthz` endpoint (200 JSON, no auth), and one **example `health_check` module** exercising every convention slot: one public GET route with an openApi-equivalent description, one entity + migration, one event + subscriber, one worker + queue, one ACL feature, DI registration, and tests for each.
- **Subagent C — docs**: `AGENTS.md` following the REQUIRED section outline from spec 09 (it must map every upstream concept — module layout, routes, entities/migrations, validators, subscribers, workers, ACL, DI, setup, CLI — to this tech's idiom, since om-port-module reads it as law); `README.md` per the standard structure (native + Docker quickstart, port, make targets); ADRs in `docs/decisions/`: `0001-runtime-and-framework.md`, `0002-orm-and-migrations.md`, `0003-queue-bullmq-compatibility.md` (each `Status / Context / Decision / Consequences`; the queue ADR states the honest interop level).

Integrate the three outputs yourself; resolve naming collisions in favor of the AGENTS.md conventions.

### 4. Verify it boots

1. `cd packages/<tech> && make up` — all containers healthy.
2. `curl http://localhost:<port>/healthz` — expect 200 and the documented JSON body.
3. `make migrate` on the fresh DB (applies the health_check migration), `make test` green.
4. Queue smoke: trigger the health_check worker's queue once; confirm `redis-cli --scan --pattern 'bull:*'` shows the queue under the default `bull:` prefix and the worker consumes the job.
5. `make down`. Fix and repeat until all four pass — do not report success on a package that has not booted.

### 5. Register the package

1. `MODULES.md`: add a `<tech>` column to the status matrix (all ⬜, health_check ✅/🧪 as verified).
2. Root `README.md`: add the package row (icon, stack summary, port) to the repository map and the `make up` example.
3. Cross-check the new AGENTS.md outline against the other packages' AGENTS.md for section parity (AGENTS.md rule 5).

### 6. Report

Return: package path, chosen stack (one line per ADR), port, verification results (boot / healthz / migrate / test / queue smoke), and the suggested first real port (`om-port-module auth <tech>` — infrastructure tier first, per AGENTS.md porting order).
