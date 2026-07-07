# 0004 — Project layout: cmd/ + internal/platform + internal/modules

## Status

Accepted

## Context

Upstream organizes code as auto-discovered module directories
(`packages/core/src/modules/<module>/` with `api/`, `data/`, `subscribers/`,
`workers/`, `acl.ts`, `di.ts`) plus cross-cutting packages (`packages/shared`,
`packages/queue`, ...). Go has no filesystem-based auto-discovery and its
community standard is `cmd/` for binaries and `internal/` for private code.

## Decision

```
cmd/api, cmd/worker, cmd/migrate   → the three binaries (one Docker image)
internal/platform/…                → upstream's cross-cutting packages
  config, db, redisconn, queue, events, validation, registry
internal/modules/<module_id>/      → one Go package per Open Mercato module
internal/modules/modules.go        → explicit registry (replaces auto-discovery)
migrations/                        → flat golang-migrate dir, files prefixed
                                     NNNN_<module>_<desc>.{up,down}.sql
```

A module is a `registry.Module` value: `ID`, `Routes(chi.Router)`,
`Workers`, `Subscribers`, `Features` — constructed by a `Module(deps)` function
that closes over the DI container (`registry.Deps`).

## Consequences

- Adding a module = one package + one line in `internal/modules/modules.go`;
  forgetting the line is caught immediately (route 404s), unlike reflection
  magic that fails silently.
- `internal/` prevents other repos importing scaffolding as API.
- Module ids keep upstream snake_case (`health_check`) while Go package names
  are collapsed (`healthcheck`) per Go conventions; the mapping is recorded in
  each module's `ModuleID` constant.
- Migrations live in one flat dir (a golang-migrate constraint); module
  ownership is encoded in the filename.
