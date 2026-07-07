# AGENTS.md — .NET Port of Open Mercato Backend

Guide for AI agents porting Open Mercato modules (upstream:
`packages/core/src/modules/<module>/`) into this .NET package. Read
`docs/stack.md` and `docs/decisions/` before making structural changes.

## Stack

.NET 9 · ASP.NET Core minimal APIs · EF Core 9 + Npgsql (EF Core migrations via
`dotnet ef`) · StackExchange.Redis 2.8 · FluentValidation 11 (Zod equivalent) ·
Microsoft.Extensions.DependencyInjection (Awilix equivalent) · System.Text.Json
(camelCase web defaults) · xunit. Versions are pinned in the csproj files; see
`docs/stack.md` for rationale.

## Layout

```
OpenMercato.sln
src/
  OpenMercato.Core/                  # framework runtime (upstream packages/{shared,queue,events})
    Configuration/                   #   AppConfig (env), DotEnv, connection-string helpers
    Data/AppDbContext.cs             #   single DbContext fed by module ConfigureModel
    Modules/                         #   IModule contract + ModuleRegistry
    Queue/                           #   IJobQueue, IJobHandler, RedisJobQueue, QueueWorkerService
    Events/EventBus.cs               #   IEventBus, IEventSubscriber, LocalEventBus
  OpenMercato.Modules.HealthCheck/   # reference module — copy this shape for every port
    HealthCheckModule.cs             #   IModule implementation (id, acl, di, model, routes)
    Api/  Data/  Validators/  Workers/  Subscribers/
  OpenMercato.Api/                   # API host: ModuleCatalog.cs, Program.cs, Migrations/
  OpenMercato.Worker/                # queue worker host
tests/OpenMercato.Tests/             # xunit
docs/  Dockerfile  docker-compose.yml  Makefile  .env.example
```

## Conventions

Mapping from upstream TS concepts to this package:

| Open Mercato (TypeScript)                        | This package (.NET)                                                                 |
| ------------------------------------------------ | ----------------------------------------------------------------------------------- |
| `modules/<m>/api/<method>/<path>.ts`             | `MapGet`/`MapPost("/api/<m>/<path>", ...)` in `Modules.<M>/Api/*Endpoints.cs`, wired via `IModule.MapRoutes` |
| `modules/<m>/data/entities.ts` (MikroORM)        | POCO classes in `Modules.<M>/Data/` + mapping in `IModule.ConfigureModel(ModelBuilder)` |
| `modules/<m>/data/validators.ts` (Zod)           | `AbstractValidator<T>` classes in `Modules.<M>/Validators/` (FluentValidation)      |
| `modules/<m>/subscribers/*.ts`                   | `IEventSubscriber` implementations in `Modules.<M>/Subscribers/`, registered in `ConfigureServices` |
| `modules/<m>/workers/*.ts`                       | `IJobHandler` implementations in `Modules.<M>/Workers/`, registered in `ConfigureServices` |
| `modules/<m>/acl.ts` (features)                  | `IModule.AclFeatures` string list                                                    |
| `modules/<m>/di.ts` (Awilix)                     | `IModule.ConfigureServices(IServiceCollection)`                                      |
| `modules/<m>/events.ts`                          | Event-name string constants inside the module project                                |
| `modules/<m>/setup.ts` / migrations              | EF Core migration in `src/OpenMercato.Api/Migrations/` (+ seed logic if needed)      |
| Build-time module auto-discovery                 | Explicit registration: `Api/ModuleCatalog.cs` AND `Worker/Program.cs`                |

Naming conversions:

- Module ids stay **snake_case** everywhere observable: URLs (`/api/health_check`),
  queue names (`health_check`), event names (`health_check.pinged`), ACL features
  (`health_check.view`), table names (`om_health_ping`).
- .NET identifiers are PascalCase: module id `health_check` → project
  `OpenMercato.Modules.HealthCheck`, namespace `OpenMercato.Modules.HealthCheck`.
- DB columns are snake_case via explicit `HasColumnName` in `ConfigureModel`
  (`CreatedAt` → `created_at`). Never rely on EF default names.
- JSON output is camelCase automatically (System.Text.Json web defaults) —
  matches upstream; do not add naming attributes unless upstream deviates.

## Module Porting Rules

1. Create `src/OpenMercato.Modules.<PascalCaseId>/` with csproj referencing
   `OpenMercato.Core` (copy the HealthCheck csproj: FrameworkReference
   `Microsoft.AspNetCore.App` + FluentValidation).
2. Implement `IModule`: `Id` (upstream snake_case id), `AclFeatures` (from
   `acl.ts`), `ConfigureServices` (from `di.ts`; also register every
   `IJobHandler` and `IEventSubscriber` here), `ConfigureModel` (from
   `data/entities.ts`), `MapRoutes` (one route per upstream api file).
3. Register the module in **both** `src/OpenMercato.Api/ModuleCatalog.cs` and
   `src/OpenMercato.Worker/Program.cs`, and add the project to `OpenMercato.sln`
   and the Dockerfile restore layer.
4. Add a migration: `dotnet ef migrations add Add<Module> --project
   src/OpenMercato.Api --startup-project src/OpenMercato.Api`, then review the
   generated SQL for schema parity with the upstream module's migrations.
5. Port validators rule-by-rule from `data/validators.ts`; keep upstream field
   names in error details.
6. Never call Redis directly from a module — go through `IJobQueue`/`IEventBus`.
7. Add xunit tests under `tests/OpenMercato.Tests/` (at minimum: validators and
   any pure logic).

## API Compatibility Rules

- Paths, HTTP methods, status codes and JSON property names must match upstream
  byte-for-byte where feasible. Check the upstream route file's response
  construction before writing a handler.
- camelCase JSON comes free from System.Text.Json; verify enums/dates serialize
  in the same format upstream emits (ISO 8601 for dates — matches default
  `DateTimeOffset` serialization).
- Auth semantics (JWT via `JWT_SECRET`, same claims and 401/403 behavior) must
  be preserved when the auth module is ported; until then `JWT_SECRET` is loaded
  but unused.
- `GET /healthz` is a liveness endpoint returning
  `{"status":"ok","service":"dotnet-api"}` and must never touch Postgres/Redis.
  `GET /api/health_check` is the readiness pattern with real dependency pings.

## Data Layer

- PostgreSQL only; `DATABASE_URL` accepts upstream URL form
  (`postgres://user:pass@host:5432/db`) or Npgsql keyword form —
  `ConnectionStrings.FromDatabaseUrl` converts.
- One `AppDbContext` (in Core); modules contribute mappings via
  `ConfigureModel`. Access entities with `db.Set<TEntity>()`.
- Migrations live in `src/OpenMercato.Api/Migrations/` (composition root sees
  all modules). The API applies pending migrations at startup with retry;
  `make migrate` applies them natively via `dotnet ef database update`.
- Schema parity: replicate upstream table/column names, types and nullability
  exactly (snake_case, `om_`-prefixed tables where upstream does so). Compare
  against the upstream module's `migrations/` output.

## Queues & Events

- Queue contract: `IJobQueue.EnqueueAsync(queue, jobName, payload)` and
  `IJobHandler { Queue; HandleAsync }`. Redis implementation uses `om:queue:*`
  keys; `QueueWorkerService` polls (500 ms idle) because StackExchange.Redis
  cannot issue blocking commands.
- **BullMQ compatibility is NOT implemented yet.** Jobs currently interchange
  only between this port's API and worker. The adapter plan (vendor BullMQ Lua
  scripts / implement waiting+active lists, job hash, events stream) is
  specified in `docs/decisions/0004-queue-bullmq-compatibility.md` and is a
  tracked porting task. Modules will not need changes when it lands.
- Events: `IEventBus.PublishAsync(name, payload)` with in-process
  `LocalEventBus` (= upstream `EVENTS_STRATEGY=local`). Subscribers implement
  `IEventSubscriber` with dot-notation event names (`health_check.pinged`).
  A distributed Redis bus is a tracked task.

## Configuration

Env var names are identical to upstream. Loaded by `AppConfig.FromEnvironment()`
after `DotEnv.Load()` (real env vars override `.env`).

| Variable          | Default                                              | Notes                                  |
| ----------------- | ---------------------------------------------------- | -------------------------------------- |
| `DATABASE_URL`    | `postgres://mercato:mercato@localhost:5432/mercato`  | URL or Npgsql keyword form             |
| `REDIS_URL`       | `redis://localhost:6379`                             | General Redis (health checks, cache)   |
| `QUEUE_STRATEGY`  | `redis`                                              | Only `redis` implemented in this port  |
| `QUEUE_REDIS_URL` | value of `REDIS_URL`                                 | Queue connection (separate mux if it differs) |
| `JWT_SECRET`      | `dev-secret-change-me`                               | Reserved for the auth module port      |
| `PORT`            | `8080`                                               | API listen port                        |

## Commands

| Make target    | Raw command                                                                          |
| -------------- | ------------------------------------------------------------------------------------ |
| `make up`      | `docker compose up --build -d`                                                       |
| `make down`    | `docker compose down -v`                                                             |
| `make dev`     | `dotnet watch --project src/OpenMercato.Api run`                                     |
| `make worker`  | `dotnet run --project src/OpenMercato.Worker`                                        |
| `make migrate` | `dotnet ef database update --project src/OpenMercato.Api --startup-project src/OpenMercato.Api` |
| `make test`    | `dotnet test OpenMercato.sln`                                                        |

Adding a migration:
`dotnet ef migrations add <Name> --project src/OpenMercato.Api --startup-project src/OpenMercato.Api`
(requires `dotnet tool install --global dotnet-ef`; the design-time factory in
`OpenMercato.Api/DesignTimeDbContextFactory.cs` reads `.env`).

## Decisions

| ADR                                                            | Decision                                    |
| -------------------------------------------------------------- | ------------------------------------------- |
| [0001](docs/decisions/0001-dotnet9-aspnet-minimal-apis.md)      | .NET 9 + ASP.NET Core minimal APIs          |
| [0002](docs/decisions/0002-efcore-npgsql-migrations.md)         | EF Core 9 + Npgsql, EF Core migrations      |
| [0003](docs/decisions/0003-fluentvalidation.md)                 | FluentValidation as the Zod equivalent      |
| [0004](docs/decisions/0004-queue-bullmq-compatibility.md)       | Queue abstraction + BullMQ compat strategy  |
| [0005](docs/decisions/0005-solution-layout.md)                  | Solution layout (Core/Api/Worker/Modules)   |
