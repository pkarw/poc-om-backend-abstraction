# 0002 — EF Core 9 + Npgsql with EF Core Migrations

## Status

Accepted

## Context

Upstream uses MikroORM entities (`data/entities.ts`) with generated SQL
migrations per module against PostgreSQL. The port needs schema parity
(snake_case tables/columns, e.g. `om_health_ping`) and real migration tooling.

## Decision

Use EF Core 9 with the Npgsql provider. A single `AppDbContext`
(src/OpenMercato.Core/Data/AppDbContext.cs) aggregates entity mappings that each
module contributes via `IModule.ConfigureModel(ModelBuilder)` — mirroring how
every upstream module's `data/entities.ts` feeds one MikroORM instance.
Migrations are code-first, generated with `dotnet ef migrations add`, stored in
`src/OpenMercato.Api/Migrations/` (the composition root that references all
modules), and applied automatically by the API entrypoint (`Database.MigrateAsync`
with retry) as well as natively via `make migrate`.

Table and column names are mapped explicitly to snake_case in `ConfigureModel`
to preserve upstream schema naming; entity classes stay idiomatic PascalCase.

## Consequences

- One linear migration history for the whole app instead of per-module
  migration folders; module ownership is documented by the mapping code.
- `DATABASE_URL` (upstream URL form) is converted to an Npgsql keyword string by
  `ConnectionStrings.FromDatabaseUrl`; keyword strings also work unchanged.
- Adding a module's entities requires regenerating a migration from the Api
  project (`--project src/OpenMercato.Api`).
