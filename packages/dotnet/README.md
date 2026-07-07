# 🟣 Open Mercato Backend — .NET Port

## What is this

A .NET 9 (ASP.NET Core minimal APIs + EF Core/Npgsql + StackExchange.Redis) port of [Open Mercato](../../README.md) backend modules with 1:1 API compatibility: same routes, same JSON shapes, same PostgreSQL/Redis infrastructure. Ported modules: **auth**, **directory**, **dashboards** (plus the `health_check` reference module) — 105 tests pass. Seeding reproduces upstream `mercato init` exactly (Acme tenant/org/roles/users).

## 🚀 Quickstart (Docker)

```bash
make up        # postgres:17 + redis:7 + api + worker → http://localhost:8080
```

Health: `curl http://localhost:8080/healthz` → `{"status":"ok","service":"dotnet-api"}`

Seed the Acme data (drop + migrate + seed), then log in as a seeded user:

```bash
make greenfield                                   # or: make init (seed without dropping)

curl -s http://localhost:8080/api/auth/login \
  -d 'email=superadmin@acme.com' -d 'password=secret'
# → 200 {"ok":true,"token":"<HS256 JWT>","redirect":"/backend"} + auth_token & session_token cookies

curl -s http://localhost:8080/api/dashboards/layout -H "Authorization: Bearer <token>"
# → 200 {"layout":{"items":[]},...,"context":{"userEmail":"superadmin@acme.com"},...}
```

## 💻 Quickstart (native)

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and running Postgres + Redis (e.g. `docker compose up -d postgres redis`).

```bash
dotnet tool install --global dotnet-ef   # one-time, for migrations
cp .env.example .env
dotnet restore
make migrate
make init        # seed the Acme tenant/org/roles/users
make dev
```

Run the queue worker in a second terminal: `make worker`.

## 👤 Seeded users

`mercato init`-identical: tenant "Acme Corp Tenant", org "Acme Corp" (slug `acme`), roles `employee`/`admin`/`superadmin`.

| Email | Role | Password | RBAC |
|---|---|---|---|
| `superadmin@acme.com` | superadmin | `secret` | `is_super_admin` (everything) |
| `admin@acme.com` | admin | `secret` | `auth.*` + `directory.organizations.*` (not `directory.tenants.manage`) |
| `employee@acme.com` | employee | `secret` | none |

## ⚙️ Commands

| Target         | What it does                                        |
| -------------- | --------------------------------------------------- |
| `make up`      | `docker compose up --build -d` (full stack)         |
| `make down`    | `docker compose down -v`                            |
| `make dev`     | Run the API natively with hot reload (dotnet watch) |
| `make worker`  | Run the queue worker natively                       |
| `make migrate` | Apply EF Core migrations                            |
| `make test`    | Run xunit tests (`dotnet test`)                     |
| `make init`    | Seed the OM-identical Acme data                     |
| `make greenfield` | Drop + recreate schema + migrate + seed (passes `--yes`) |
| `make seed`    | Run seeding only                                    |
| `make cli ARGS="…"` | Run the CLI (see below)                        |

## 🛠️ CLI

`dotnet run --project src/OpenMercato.Cli -- <cmd>` (or `make cli ARGS="<cmd>"`). Commands:

| Command | Source | What it does |
|---|---|---|
| `migrate` | built-in | Apply EF Core migrations |
| `init` | built-in | Seed the OM-identical Acme data |
| `greenfield --yes` | built-in | Drop + migrate + seed |
| `seed` | built-in | Seed only |
| `add-user` / `set-password` / `list-users` | auth | Manage users |
| `add-org` / `list-orgs` | directory | Manage organizations |
| `dashboards seed-defaults` / `dashboards enable-analytics-widgets` | dashboards | Seed dashboard defaults |

## 📁 Layout

```
src/
  OpenMercato.Api/                  API host, module catalog, EF Core migrations
  OpenMercato.Worker/               Queue worker host
  OpenMercato.Cli/                  CLI host (built-in + module-contributed commands)
  OpenMercato.Core/                 Framework runtime: config, db, redis, queue, events, module registry
  OpenMercato.Modules.Auth/         auth module (users, sessions, roles, JWT, RBAC)
  OpenMercato.Modules.Directory/    directory module (tenants, organizations)
  OpenMercato.Modules.Dashboards/   dashboards module (layout, widgets)
  OpenMercato.Modules.HealthCheck/  Reference module
tests/OpenMercato.Tests/            xunit tests (105 pass)
docs/                               Stack + architecture decision records
```

## 🔌 Environment

| Variable         | Default                                        | Purpose                              |
| ---------------- | ---------------------------------------------- | ------------------------------------ |
| `DATABASE_URL`   | `postgres://mercato:mercato@localhost:5432/mercato` | PostgreSQL connection (URL form) |
| `REDIS_URL`      | `redis://localhost:6379`                       | Redis for caching/health             |
| `QUEUE_STRATEGY` | `redis`                                        | Queue backend strategy               |
| `QUEUE_REDIS_URL`| value of `REDIS_URL`                           | Redis used by the job queue          |
| `JWT_SECRET`     | `dev-secret-change-me`                         | Token signing secret (HS256)         |
| `PORT`           | `8080`                                         | API listen port                      |
| `OM_INIT_SUPERADMIN_EMAIL` / `OM_INIT_SUPERADMIN_PASSWORD` | — | Env-gated boot seeding of a superadmin |
| `OM_SKIP_MIGRATIONS` | —                                          | `1` = migrations-off mode (for the testbench) |

## 🧪 Testbench

Run a **real Open Mercato UI** against this port over one shared Postgres (OM owns the schema, the port runs migrations-off, a proxy routes ported `/api/*` to .NET). See [`../../testbench/README.md`](../../testbench/README.md) and spec [`../../specs/11-testbench.md`](../../specs/11-testbench.md).
</content>
