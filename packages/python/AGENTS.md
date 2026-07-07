# AGENTS.md — Python Port of Open Mercato Backend

Agent guide for porting Open Mercato backend modules (upstream clone at `/tmp/om-analyze`, modules under `packages/core/src/modules/<module>/`) into this package.

## Stack

Python 3.12 · FastAPI + uvicorn · uv (package manager; always `uv sync` / `uv run`, never pip) · SQLAlchemy 2 async + asyncpg · Alembic migrations · redis-py · official `bullmq` PyPI package (full Node BullMQ interop) · pydantic v2 · pytest + httpx. Versions and rationale: [docs/stack.md](docs/stack.md).

## Layout

```
packages/python/
├── alembic.ini, migrations/          # Alembic config + versions/
├── docker-compose.yml, Dockerfile    # postgres:17 + redis:7 + api + worker
├── Makefile                          # up/down/dev/worker/migrate/test
├── docs/{stack.md,decisions/}        # stack + ADRs
├── src/om/
│   ├── api.py                        # API host: /healthz + mounts module routers under /api
│   ├── worker.py                     # Worker host: bullmq.Worker per WorkerSpec
│   ├── shared/                       # config.py, db.py, redis.py, queue.py, events.py, registry.py
│   └── modules/
│       ├── __init__.py               # MODULES list (= upstream apps/mercato/src/modules.ts)
│       └── health_check/             # reference module — copy this pattern
└── tests/
```

## Conventions

Mapping from upstream TS concepts to this package (per module `src/om/modules/<module_id>/`):

| Open Mercato (packages/core/src/modules/`<module>`/) | Python equivalent | Notes |
| --- | --- | --- |
| `api/<method>/<path>.ts` (must export `openApi`) | `api.py` — `APIRouter` with `@router.<method>("/<path>")` | Host mounts under `/api`; pydantic `response_model` supplies OpenAPI. |
| `data/entities.ts` (MikroORM) | `entities.py` — SQLAlchemy declarative classes on `om.shared.db.Base` | Identical table/column names (snake_case, as upstream DB schema). |
| `data/validators.ts` (Zod) | `validators.py` — pydantic v2 models | Field names in JSON payloads stay camelCase where upstream APIs use camelCase (use pydantic `alias`). |
| `subscribers/*.ts` (`metadata = { event, persistent? }`) | `subscribers.py` handlers + `SubscriberSpec(event=..., id=...)` | Registered in the module's `Module` object. |
| `workers/*.ts` (`metadata = { queue, id?, concurrency? }`) | `workers.py` handlers + `WorkerSpec(queue=..., id=..., concurrency=...)` | Queue/job names must match upstream verbatim (shared Redis contract). |
| `acl.ts` (`features` export) | `acl.py` — `features: list[str]` → `Module(acl_features=...)` | Same `<module>.<action>` ids. |
| `notifications.ts` (`notificationTypes`) | `notifications.py` — `list[NotificationType]` → `Module(notification_types=...)` | Declared now; delivery engine is a PORT-TODO until `notifications` is ported. |
| `data/fields.ts` (`fieldSets`) | `fields.py` — `list[CustomFieldSet]` → `Module(custom_field_sets=...)` | EAV storage is a PORT-TODO until `entities` is ported. |
| `ce.ts` (custom entities) | `ce.py` — `list[CustomEntity]` → `Module(custom_entities=...)` | Entities with `fields` also feed custom-field sets. |
| `events.ts` (`createModuleEvents`) | `events.py` — `list[DeclaredEvent]` → `Module(declared_events=...)` | Event `name` byte-exact; `persistent`/`client_broadcast` flags. |
| `di.ts` (Awilix `register(container)`) | Module-level factories + FastAPI `Depends` | No DI container; shared singletons live in `om/shared/*`. |
| `data/migrations/` (per module) | `migrations/versions/` (global Alembic history) | Prefix the revision message with the module id. |
| `apps/mercato/src/modules.ts` | `src/om/modules/__init__.py` (`MODULES`) | Explicit registration replaces `yarn generate`. |

### Module declaration surfaces (spec 10 — module contract parity)

The `Module` dataclass (`om/shared/registry.py`) exposes **one consistent
place per module** for the four upstream declaration surfaces, mirroring the
.NET `IModule` and Go `registry.Module`. Every module declares **all four** —
as empty lists when it declares none, never omitted — so the shape is identical
across modules and the registry aggregators are total. "Declare now, engine
later": the declaration is mandatory even where the consuming engine
(notifications, EAV) is still a PORT-TODO.

```python
from om.shared.registry import (
    Module, NotificationType, CustomField, CustomFieldSet, CustomEntity, DeclaredEvent,
)

MODULE = Module(
    id="widgets",
    router=router,
    entities=[Widget],
    acl_features=["widgets.view", "widgets.manage"],          # acl.ts
    notification_types=[                                        # notifications.ts
        NotificationType(type="widgets.assigned", module="widgets",
                         severity="info", title_key="widgets.assigned.title",
                         expires_after_hours=72),
    ],
    custom_field_sets=[                                         # data/fields.ts
        CustomFieldSet(entity_id="widgets:widget", source="widgets",
                       fields=[CustomField(key="priority", kind="integer")]),
    ],
    custom_entities=[                                           # ce.ts
        CustomEntity(id="widgets:tag", label="Tag"),
    ],
    declared_events=[                                           # events.ts
        DeclaredEvent(name="widgets.widget.created", persistent=True),
    ],
)
```

The runtime aggregates each surface across enabled modules, in enabled-module
order, via `all_acl_features()`, `all_notification_types()`,
`all_custom_field_sets()`, `all_custom_entities()`, `all_declared_events()`
(all in `om/shared/registry.py`) — the analogue of upstream's `Module[]` →
registry fold.

Naming conversions: TS camelCase identifiers → Python snake_case (`findUserById` → `find_user_by_id`); kebab-case route segments stay byte-identical in URL paths (`/api/customer-accounts` keeps the hyphen — only Python symbols convert); module ids stay snake_case exactly as upstream directory names.

## Module Porting Rules

1. Read the upstream module end-to-end first: `api/`, `data/entities.ts`, `data/validators.ts`, `subscribers/`, `workers/`, `acl.ts`, `notifications.ts`, `ce.ts`, `data/fields.ts`, `events.ts`, `di.ts`, `setup.ts`.
2. Create `src/om/modules/<module_id>/` with `__init__.py`, `api.py`, `entities.py`, `validators.py`, `workers.py`, `subscribers.py`, `acl.py`, and (when upstream declares them) `notifications.py`, `fields.py`, `ce.py`, `events.py` (omit files with no upstream counterpart; keep `acl.py` whenever routes are guarded upstream — never ship guarded routes without features).
3. Build the frozen `Module` object in `__init__.py` (see `health_check/__init__.py`) declaring **all** upstream surfaces via `acl_features`, `notification_types`, `custom_field_sets`, `custom_entities`, `declared_events` (empty lists when none — spec 10), and append it to `MODULES` in `src/om/modules/__init__.py` — a module not listed there is silently inactive. Declaration is mandatory even where the consuming engine (notifications/EAV) is still a PORT-TODO.
4. Add an Alembic migration: `uv run alembic revision -m "<module_id>: <what>"` (or `--autogenerate` after entities import), then verify the SQL matches the upstream MikroORM migration's effect.
5. Add tests under `tests/` (httpx `ASGITransport` for routes; direct handler calls for workers/subscribers).
6. Verify: `make test`, then `make up` and curl the new routes against upstream response fixtures.
7. When a language-idiomatic replacement is used for an upstream library (pydantic for Zod, etc.), the observable behavior must stay identical; document non-obvious choices as a new ADR in `docs/decisions/`.

## API Compatibility Rules

- Paths, methods, status codes and JSON bodies must match upstream byte-for-byte where feasible: same `/api/...` paths (hyphens and all), same JSON key casing (upstream APIs are typically camelCase — use pydantic aliases, never expose snake_case keys upstream doesn't have).
- Auth semantics must match: JWT signed with `JWT_SECRET`, same claim names and error status codes as upstream `packages/core/src/modules/auth`.
- FastAPI's default 422 validation error shape differs from upstream's; when porting a real module, replicate the upstream error contract with custom exception handlers on the module router.
- `GET /healthz` → `200 {"status":"ok","service":"python-api"}`, no DB/Redis access (liveness). `GET /api/health_check` performs real DB + Redis pings.
- Every route declares a pydantic `response_model` — this is the analogue of upstream's mandatory `openApi` export; docs are served at `/docs` and `/openapi.json`.

## Data Layer

- PostgreSQL only; async SQLAlchemy sessions come from the `get_session` FastAPI dependency (`om/shared/db.py`).
- `DATABASE_URL` keeps upstream's `postgres://` scheme; normalization to `postgresql+asyncpg://` happens in `Settings.sqlalchemy_database_url` (ADR 0002).
- Schema parity is a hard rule: table names, column names/types/nullability/defaults must equal the upstream MikroORM schema so both stacks can share a database.
- Adding a migration: ensure the module is in `MODULES` (env.py imports the registry so `Base.metadata` is complete), then `uv run alembic revision --autogenerate -m "<module_id>: <change>"`, review the generated SQL, apply with `make migrate`. Docker applies `alembic upgrade head` on api start.

## Queues & Events

- Strategy pattern mirrors upstream `packages/queue`: `QUEUE_STRATEGY=redis` uses the official `bullmq` PyPI package — full wire compatibility with Node BullMQ (same Lua scripts/Redis structures), no shim (ADR 0003). `QUEUE_STRATEGY=local` runs handlers inline in the enqueuing process (dev/test); the worker host then idles by design.
- Enqueue via `om.shared.queue.get_queue_backend().enqueue(queue, name, data)`; process by declaring a `WorkerSpec` — the worker host creates one `bullmq.Worker` per spec. Handlers receive a job exposing `.name` and `.data`.
- Queue names and job payload JSON are the cross-stack contract: reuse upstream ids verbatim.
- Events: `om.shared.events.emit(name, data)` dispatches in-process to `SubscriberSpec` handlers. Upstream's persistent (queue-backed) subscribers are NOT implemented yet — porting task; route persistent subscribers through the queue backend when needed.

## Configuration

Same names as upstream, loaded from env / `.env` by pydantic-settings (`om/shared/config.py`):

| Variable | Meaning |
| --- | --- |
| `DATABASE_URL` | PostgreSQL DSN (upstream `postgres://` scheme) |
| `REDIS_URL` | Redis connection |
| `QUEUE_STRATEGY` | `local` \| `redis` |
| `QUEUE_REDIS_URL` | Queue broker (falls back to `REDIS_URL`) |
| `JWT_SECRET` | Token signing secret |
| `PORT` | API listen port (default 8000) |

Add new upstream env vars as `Settings` fields with the identical upstream name; never invent new names for existing upstream concepts.

## Commands

| Target | Raw command |
| --- | --- |
| `make up` | `docker compose up --build -d` |
| `make down` | `docker compose down -v` |
| `make dev` | `uv sync && uv run uvicorn om.api:app --reload --port ${PORT:-8000}` |
| `make worker` | `uv sync && uv run python -m om.worker` |
| `make migrate` | `uv sync && uv run alembic upgrade head` |
| `make test` | `uv sync && uv run pytest` |

## Decisions

| ADR | Decision |
| --- | --- |
| [0001](docs/decisions/0001-runtime-fastapi.md) | Python 3.12 + FastAPI + uvicorn + uv |
| [0002](docs/decisions/0002-sqlalchemy-alembic.md) | SQLAlchemy 2 async + Alembic; upstream `DATABASE_URL` scheme kept |
| [0003](docs/decisions/0003-bullmq-python.md) | Official `bullmq` PyPI package — full Node BullMQ interop |
| [0004](docs/decisions/0004-project-layout.md) | src layout + explicit module registry (no filesystem scanning) |
