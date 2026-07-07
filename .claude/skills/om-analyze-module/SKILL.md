---
name: om-analyze-module
description: Distill one upstream Open Mercato module into a PORT CONTRACT at upstream/analysis/modules/<module-id>.md. Use before porting any module (om-port-module requires the contract), or to refresh a contract flagged stale by om-sync-upstream. Args: <module-id> — the upstream module directory name (e.g. auth, currencies, customers). Technology-agnostic — reads only the pinned upstream source.
---

# om-analyze-module <module-id>

Produce the definitive, implementation-ready contract for one upstream module. Everything `om-port-module` implements and `om-verify-parity` checks comes from this document — if it is not in the contract, it will not be ported. Be exhaustive and cite files.

## Ground rules

- Analyze ONLY at the pinned commit in `upstream/UPSTREAM.md`. Use the read-only checkout (e.g. `/tmp/om-analyze` or `.upstream-cache/`) — verify `git -C <checkout> rev-parse HEAD` matches the pin; never modify the checkout.
- Everything you state must come from reading upstream source. Never guess a schema, status code, or queue name.
- UI artifacts (`backend/`, `frontend/`, `components/`, `widgets/`, pages) are **out of scope** — list them under "Not ported" and move on.

## Procedure

### 1. Locate and inventory the module

1. Read `upstream/UPSTREAM.md` for the pin; confirm the checkout matches.
2. Find the module: `packages/core/src/modules/<module-id>/` (most modules) or `apps/mercato/src/modules/<module-id>/` (app-level, e.g. `example`). If absent in both, stop and report the available module list.
3. `find` the full file tree. Classify by convention (see `upstream/analysis/01-module-system.md`): `index.ts` (metadata), `acl.ts`, `di.ts`, `setup.ts`, `ce.ts`, `events.ts`, `cli.ts`, `encryption.ts`, `notifications.ts`, `api/**`, `data/entities.ts`, `data/validators.ts`, `data/extensions.ts`, `data/fields.ts`, `data/enrichers.ts`, `subscribers/*`, `workers/*`, `commands/**`, `services/**`, `lib/**`, `migrations/**`, `i18n/*`, plus UI dirs (out of scope).

### 2. Fan out analysis subagents

Spawn **four subagents in parallel**, each given the module path, the pin, and pointers to the relevant `upstream/analysis/0N-*.md` background doc. Each returns structured findings (markdown fragments) — they do not write the final file.

- **Subagent A — HTTP surface** (background: `02-api-http.md`, `05-auth-rbac.md`): every file under `api/**`. For each route derive: final path (`/api/<module-id>/<segments>` from file location, or explicit `metadata.path` override; bracket params `[id]`, `[...slug]`, `[[...slug]]`), methods, per-method `requireAuth` / `requireFeatures` / `rateLimit`, the `openApi` export (summaries, schemas, response status map), and the Zod schemas from `data/validators.ts` (or inline) — translate each to a language-neutral field table: name, type, required/optional, default, constraints, coercions. For routes built with `makeCrudRoute` (`packages/shared/src/lib/crud/factory.ts`), expand what the factory implies: list envelope `{items,total,page,pageSize,totalPages}`, standard query params, mutation response bodies, error envelopes — reference the shared spec instead of re-deriving, but record the factory *configuration* (entity, schemas, hooks, events, indexer options) exactly. Include `api/interceptors.ts` if present.
- **Subagent B — Data** (background: `03-data-layer.md`): `data/entities.ts` + `migrations/**` + `data/extensions.ts` + `data/fields.ts` + `ce.ts` + `encryption.ts`. For each entity: exact Postgres **table name**, every **column** (exact name, Postgres type, nullable, default, unique), primary key, indexes, FKs, soft-delete (`deleted_at`), tenancy columns (`tenant_id`/`organization_id`). Cross-check decorators against the module's committed migrations — migrations win on exact DDL. Also record custom-field sets and custom entities declared in `ce.ts`/`data/fields.ts`.
- **Subagent C — Async surface** (background: `04-events-queues.md`): `events.ts` (declared event names **and their payload shapes** — the typed event surface, via `createModuleEvents({moduleId, events})`, that every port re-declares), `subscribers/*` (per subscriber: event name, derived id `<module-id>:<subdirs>:<basename>`, `persistent`/`sync`/`priority`, handler behavior summary, what it writes/enqueues), `workers/*` (per worker: **queue name**, derived id `<module-id>:workers:<subdirs>:<basename>`, concurrency, payload shape inside the standard envelope `{id,payload,createdAt}`, side effects), plus every `eventBus.emit*` / enqueue call found anywhere in the module (grep `services/`, `commands/`, `api/`) with exact event/queue names and payload shapes.
- **Subagent D — Wiring & lifecycle** (background: `01-module-system.md`, `06-runtime-startup.md`, `07-shared-services.md`): `index.ts` metadata (`requires`!), `acl.ts` (every feature id + title), `di.ts` (every registered service name and its role), `setup.ts` (`onTenantCreated`, `seedDefaults`, `seedExamples`, `defaultRoleFeatures`, `defaultCustomerRoleFeatures` — with the concrete seeded data), `cli.ts` (commands, args, behavior), `notifications.ts` (**every `NotificationTypeDefinition`** — type id, severity, `expiresAfterHours`, template keys/channels — these are declared by every port even if delivery is deferred), env vars read anywhere in the module, and internal `services/**`/`commands/**` behavior that routes depend on (business rules that affect observable output).

### 3. Determine dependencies

Combine: `metadata.requires` from `index.ts`, DI services consumed from other modules (grep imports of `@open-mercato/core/modules/<other>` and container resolutions), events consumed that other modules emit, FKs into other modules' tables. Produce an ordered "port these first" list.

### 4. Write the contract

Write `upstream/analysis/modules/<module-id>.md` (create the directory if needed) with exactly this structure:

```markdown
# Port contract — <module-id>
> Upstream commit <SHA> (<date>). Source: packages/core/src/modules/<module-id>/. Regenerate via om-analyze-module.

## Overview            — 3–6 sentences: what the module does, its role in the dependency graph.
## Dependencies        — table: module id | why needed | must be ported first (yes/no).
## HTTP routes         — summary table: METHOD | path | auth | requiredFeatures | rateLimit | source file.
### <METHOD> <path>    — one subsection per route+method: request schema (field table),
                          response schema per status code (field table or exact JSON shape),
                          error cases (status + exact body), behavior notes, events emitted.
## Entities            — one subsection per entity: table name; column table
                          (column | pg type | nullable | default | notes); indexes; FKs; tenancy; soft delete.
## Custom entities & field sets   — from ce.ts / data/fields.ts (or "none"). First-class: for each
                          custom-field set — set key/id | target entity it attaches to | field table
                          (field name | type | required | default | constraints); for each custom entity —
                          entity id | fields. These are declared by every port even if EAV storage is deferred.
## Notifications       — from notifications.ts (or "none"). Table: type id | severity |
                          expiresAfterHours | template keys/channels | triggered by. Declared by every
                          port even if delivery is deferred (declare-now, per specs/10).
## Events              — declared (events.ts surface): event name | payload shape (the typed
                          contract every port declares). Emitted: name | payload shape | emitted from.
                          Consumed: name | subscriber id | sync/persistent | effect.
## Workers & queues    — queue name | worker id | concurrency | payload shape | behavior. (Queue names are wire contract.)
## ACL features        — feature id | title | used by (routes/checks).
## Setup & seeding     — tenant-created hooks, seedDefaults/seedExamples content, defaultRoleFeatures.
## DI services         — service name | role | consumed by.
## CLI commands        — command | args | behavior (or "none").
## Configuration       — env vars | config keys | defaults.
## Not ported          — UI files and anything intentionally excluded.
## Porting checklist   — ordered checkbox list an implementing agent works through:
                          migrations → entities → services → routes → subscribers → workers →
                          declare surface (ACL features → notification types → custom-field sets/custom
                          entities → declared events; declare-now even if the engine is deferred, per
                          specs/10) → setup/seed → DI → CLI → tests → parity run.
```

Every route, column, event, queue, and feature id must be **byte-exact** — these are the parity assertions. Where behavior comes from shared machinery (dispatcher guards, CRUD factory, queue envelope), reference the spec requirement IDs (`specs/01`–`07`) rather than duplicating, but keep module-specific values inline.

### 5. Track

Update `MODULES.md`: set the module's analysis status to 🔍 (analyzed / contract ready) and link the contract. Add the row if missing (place it in the correct dependency tier).

### 6. Report

Return: contract path, route/entity/event/queue/feature counts, the dependency list (and which dependencies lack contracts or ports), and any upstream ambiguities you could not resolve from source (call these out explicitly — never paper over them).
