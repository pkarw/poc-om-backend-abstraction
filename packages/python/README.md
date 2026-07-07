# 🐍 Open Mercato Backend — Python Port

## What is this

A Python (FastAPI) scaffold for porting [Open Mercato](../../README.md) backend modules with 1:1 API compatibility. It ships the framework runtime (config, PostgreSQL, Redis, BullMQ-compatible queues, module registry) plus one reference module, `health_check`, wired end-to-end.

## 🚀 Quickstart (Docker)

```bash
docker compose up --build
```

Then: `curl http://localhost:8000/healthz` and `curl http://localhost:8000/api/health_check`. Migrations run automatically before the API starts.

## 💻 Quickstart (native)

1. Install [uv](https://docs.astral.sh/uv/) (Python 3.12 is fetched automatically) and start PostgreSQL + Redis (`docker compose up -d postgres redis`).
2. `cp .env.example .env`
3. `uv sync`
4. `make migrate`
5. `make dev`

## ⚙️ Commands

| Command        | What it does                                    |
| -------------- | ----------------------------------------------- |
| `make up`      | `docker compose up --build -d` (full stack)     |
| `make down`    | `docker compose down -v`                        |
| `make dev`     | Run the API natively with hot reload (uvicorn)  |
| `make worker`  | Run the BullMQ queue worker natively            |
| `make migrate` | Apply Alembic migrations                        |
| `make test`    | Run pytest                                      |

## 📁 Layout

```
packages/python/
├── docs/                 # stack.md + ADRs (docs/decisions/)
├── migrations/           # Alembic migrations (versions/)
├── src/om/
│   ├── api.py            # API host (/healthz + /api/... module routes)
│   ├── worker.py         # Worker host (BullMQ processors)
│   ├── shared/           # config, db, redis, queue, events, module registry
│   └── modules/          # one dir per ported module
│       └── health_check/ # reference module (api, entities, validators,
│                         #   workers, subscribers, acl)
└── tests/
```

## 🔌 Environment

| Variable          | Purpose                                            | Default (dev)                                     |
| ----------------- | -------------------------------------------------- | ------------------------------------------------- |
| `DATABASE_URL`    | PostgreSQL connection (upstream-compatible scheme) | `postgres://postgres:postgres@localhost:5432/mercato` |
| `REDIS_URL`       | Redis connection                                   | `redis://localhost:6379`                          |
| `QUEUE_STRATEGY`  | `local` (inline) or `redis` (BullMQ)               | `local`                                           |
| `QUEUE_REDIS_URL` | Redis for queues (falls back to `REDIS_URL`)       | —                                                 |
| `JWT_SECRET`      | Token signing secret                               | dev placeholder                                   |
| `PORT`            | API listen port                                    | `8000`                                            |
