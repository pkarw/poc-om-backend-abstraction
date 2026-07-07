# 🐹 Open Mercato — Go port

Go implementation of Open Mercato backend modules with 1:1 API compatibility
against the pinned TypeScript upstream — see the [repo overview](../../README.md).
It ships an API host, a queue worker host and a module registry mirroring
upstream's module system, with `health_check` as the wired-end-to-end reference module.

## 🚀 Quickstart (Docker)

```bash
docker compose up --build
```

That's it: Postgres 17 + Redis 7 start, migrations apply, the API serves on
[http://localhost:8090](http://localhost:8090) and the worker consumes the
`health_check` queue. Try it:

```bash
curl http://localhost:8090/healthz            # {"status":"ok","service":"golang-api"}
curl http://localhost:8090/api/health_check   # {"status":"ok","module":"health_check","checks":{"database":true,"redis":true}}
```

## 💻 Quickstart (native)

Requires Go 1.23+ and running Postgres + Redis (e.g. `docker compose up -d postgres redis`).

```bash
cp .env.example .env   # adjust if your Postgres/Redis differ
go mod tidy            # resolve deps, generate go.sum
make migrate           # apply DB migrations
make dev               # run the API (make worker in a second terminal)
```

## ⚙️ Commands

| Target | What it does |
|---|---|
| `make up` | `docker compose up --build -d` — full stack |
| `make down` | `docker compose down -v` — stop, drop volumes |
| `make dev` | run the API natively |
| `make worker` | run the queue worker natively |
| `make migrate` | apply DB migrations natively |
| `make test` | run tests (`go test ./...`) |

## 📁 Layout

```
cmd/{api,worker,migrate}/     the three binaries (one Docker image)
internal/platform/            runtime: config, db, redisconn, queue, events,
                              validation, registry
internal/modules/             one package per module; modules.go registers them
internal/modules/healthcheck/ reference module (route+worker+subscriber+acl)
migrations/                   golang-migrate SQL files
docs/                         stack.md + ADRs (decisions/)
```

## 🔌 Environment

Same names as upstream (see `.env.example`):

| Var | Default | Meaning |
|---|---|---|
| `DATABASE_URL` | — (required) | Postgres DSN |
| `REDIS_URL` | — (required) | Redis URL |
| `QUEUE_STRATEGY` | `redis` | `local` or `redis` |
| `QUEUE_REDIS_URL` | `REDIS_URL` | dedicated queue Redis |
| `JWT_SECRET` | — | auth signing secret (future auth module) |
| `PORT` | `8090` | API listen port |

Agent-facing conventions and porting rules: [AGENTS.md](AGENTS.md).
BullMQ compatibility status (read before mixing Node and Go workers):
[docs/decisions/0003-bullmq-queue-compatibility.md](docs/decisions/0003-bullmq-queue-compatibility.md).
