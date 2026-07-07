# 0002 — pgx/v5 (no ORM) + golang-migrate SQL migrations

## Status

Accepted

## Context

Upstream uses MikroORM entities (`data/entities.ts`) and its migration
generator. The port contract requires the *observable* Postgres schema (table
and column names, types, constraints) to be identical — clients and shared
databases must not be able to tell the implementations apart. Go options:
GORM/ent (ORMs with their own schema opinions) vs pgx + hand-written SQL.

## Decision

- `github.com/jackc/pgx/v5` with `pgxpool` as the only database access layer;
  entities are plain Go structs documenting their table/columns
  (see `internal/modules/healthcheck/entities.go`).
- `github.com/golang-migrate/migrate/v4` with plain `NNNN_<module>_<desc>.up.sql`
  / `.down.sql` files in `migrations/`, applied via the library
  (`internal/platform/db/migrate.go`) by `cmd/migrate` and by the api
  container entrypoint.

## Consequences

- Schema parity is trivially auditable: the migration SQL *is* the schema, and
  can be diffed against upstream's generated MikroORM migrations.
- No ORM naming magic can silently diverge from upstream snake_case tables.
- More hand-written SQL per module — accepted cost; ports translate MikroORM
  entity definitions into explicit DDL once per module.
- Down migrations are mandatory for every up migration.
