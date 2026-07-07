# 0004 — src layout with explicit module registry

## Status

Accepted

## Context

Upstream discovers module files by filesystem convention (`api/<method>/<path>.ts`, `workers/*.ts`, `subscribers/*.ts`, `acl.ts`, ...) and aggregates them via code generation (`yarn generate`). Python has no equivalent build-time generation step, and implicit filesystem scanning is fragile and unidiomatic.

## Decision

- Standard Python src layout: `src/om/` package with `shared/` (framework runtime) and `modules/<module_id>/` (one directory per ported module), plus top-level hosts `om/api.py` and `om/worker.py`.
- Explicit registration instead of scanning: each module builds one frozen `Module` dataclass (router, entities, workers, subscribers, acl_features) in its `__init__.py`; `src/om/modules/__init__.py` lists enabled modules (`MODULES`) — the direct analogue of upstream `apps/mercato/src/modules.ts`.
- Module ids are snake_case and identical to upstream module directory names.

## Consequences

- Adding a module is a two-step change: create `modules/<id>/` and append it to `MODULES`. Forgetting the second step means the module is silently inactive — the AGENTS.md checklist calls this out.
- No code generation step exists or is needed; `Module` objects are plain imports, so tests can introspect the registry directly.
- The per-module file naming (`api.py`, `entities.py`, `validators.py`, `workers.py`, `subscribers.py`, `acl.py`) mirrors upstream concepts one-to-one, keeping ports mechanical.
