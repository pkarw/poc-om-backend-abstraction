# 🟣 Open Mercato Backend — .NET Port

## What is this

A .NET 9 scaffold for porting [Open Mercato](../../README.md) backend modules with 1:1 API compatibility: same routes, same JSON shapes, same PostgreSQL/Redis infrastructure. It ships a module registry mirroring upstream module concepts and one reference module (`health_check`) wired end-to-end (route → validation → EF Core entity → queue job → event subscriber).

## 🚀 Quickstart (Docker)

```bash
docker compose up --build
```

Then: `curl http://localhost:8080/api/health_check` → `{"status":"ok","module":"health_check","checks":{"database":true,"redis":true}}`

## 💻 Quickstart (native)

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and running Postgres + Redis (e.g. `docker compose up -d postgres redis`).

```bash
dotnet tool install --global dotnet-ef   # one-time, for migrations
cp .env.example .env
dotnet restore
make migrate
make dev
```

Run the queue worker in a second terminal: `make worker`.

## ⚙️ Commands

| Target         | What it does                                        |
| -------------- | --------------------------------------------------- |
| `make up`      | `docker compose up --build -d` (full stack)         |
| `make down`    | `docker compose down -v`                            |
| `make dev`     | Run the API natively with hot reload (dotnet watch) |
| `make worker`  | Run the queue worker natively                       |
| `make migrate` | Apply EF Core migrations (`dotnet ef database update`) |
| `make test`    | Run xunit tests (`dotnet test`)                     |

## 📁 Layout

```
src/
  OpenMercato.Api/                  API host, module catalog, EF Core migrations
  OpenMercato.Worker/               Queue worker host
  OpenMercato.Core/                 Framework runtime: config, db, redis, queue, events, module registry
  OpenMercato.Modules.HealthCheck/  Reference module (api, data, validators, workers, subscribers)
tests/OpenMercato.Tests/            xunit tests
docs/                               Stack + architecture decision records
```

## 🔌 Environment

| Variable         | Default                                        | Purpose                              |
| ---------------- | ---------------------------------------------- | ------------------------------------ |
| `DATABASE_URL`   | `postgres://mercato:mercato@localhost:5432/mercato` | PostgreSQL connection (URL form) |
| `REDIS_URL`      | `redis://localhost:6379`                       | Redis for caching/health             |
| `QUEUE_STRATEGY` | `redis`                                        | Queue backend strategy               |
| `QUEUE_REDIS_URL`| value of `REDIS_URL`                           | Redis used by the job queue          |
| `JWT_SECRET`     | `dev-secret-change-me`                         | Token signing secret                 |
| `PORT`           | `8080`                                         | API listen port                      |
