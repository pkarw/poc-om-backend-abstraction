# 0005 — Solution Layout: Core / Api / Worker / One Project per Module

## Status

Accepted

## Context

Upstream is a monorepo where `packages/core/src/modules/<module>/` folders are
auto-discovered and cross-cutting runtime lives in `packages/{shared,queue,events,...}`.
.NET has no filesystem-based module discovery; the idiomatic unit of isolation
is a project.

## Decision

Classic src/tests solution:

- `src/OpenMercato.Core` — framework runtime: config (`AppConfig`, `DotEnv`),
  `AppDbContext`, module contract (`IModule`) + `ModuleRegistry`, queue
  (`IJobQueue`, `RedisJobQueue`, `QueueWorkerService`), events (`IEventBus`,
  `LocalEventBus`). Equivalent of upstream `packages/{shared,queue,events}`.
- `src/OpenMercato.Modules.<ModuleName>` — one project per ported module
  (PascalCase of the snake_case module id), containing `Api/`, `Data/`,
  `Validators/`, `Workers/`, `Subscribers/` and the `IModule` implementation.
- `src/OpenMercato.Api` — composition root: `ModuleCatalog`, EF Core migrations,
  design-time factory, `/healthz`.
- `src/OpenMercato.Worker` — queue worker host sharing the same module list.
- `tests/OpenMercato.Tests` — xunit.

Module registration is explicit (add to `ModuleCatalog` in Api and to the list
in Worker `Program.cs`) instead of upstream's build-time discovery.

## Consequences

- Modules cannot reference each other accidentally; dependencies flow
  module -> Core only.
- Adding a module means: new project, add to solution, register in both hosts,
  add a migration. Two registration points is the accepted cost of explicitness.
- A single Docker image serves api and worker with different commands.
