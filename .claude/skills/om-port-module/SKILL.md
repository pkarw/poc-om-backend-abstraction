---
name: om-port-module
description: Port one Open Mercato module 1:1 into a technology package. Use after om-analyze-module has produced the module's port contract. Args: <module-id> <tech> — the upstream module id and the target technology package name under packages/ (e.g. python, dotnet, golang). The same skill drives any technology; tech-specific conventions come from packages/<tech>/AGENTS.md.
---

# om-port-module <module-id> <tech>

Implement the module's port contract in `packages/<tech>/`, with observable behavior identical to upstream and idiomatic internals.

## Ground rules

- **Observable behavior is 1:1** (AGENTS.md rule 3): API paths, methods, status codes, JSON envelopes, auth semantics, Postgres table/column names, queue names, event names — exactly as the contract states. No "improvements" on the wire.
- **Internals are idiomatic** (rule 4): use the target language's best validation/ORM/DI patterns per `packages/<tech>/AGENTS.md`. Record every notable deviation from the TS structure as an ADR.
- **Declare the full module surface — declare-now** (`specs/10-module-contract-parity.md`): a ported module MUST declare its complete contract surface — **RBAC feature declarations, notification types, custom-field sets / custom entities, and declared typed events** — using the target package's module-contract mechanism (see `packages/<tech>/AGENTS.md`), mirroring upstream's `acl.ts` / `notifications.ts` / `ce.ts`+`data/fields.ts` / `events.ts`. These are **declared even when the delivery/storage engine is deferred**: the declarations (ids, titles, severities, expiry, templates, field-set shapes, event names + payload shapes) are part of the wire/registry contract and must land now; only the runtime that *acts* on them (notification delivery, EAV persistence, event fan-out) may be stubbed and tracked. Never drop a declaration because its engine isn't built yet.
- **Use the package's CRUD factory — don't hand-write CRUD** (`specs/02` R68): where the upstream module builds an endpoint with `makeCrudRoute`, the port MUST implement it through the target package's shared CRUD factory/base-class (the `makeCrudRoute` equivalent — e.g. .NET `OpenMercato.Core.Crud.CrudRoute.Map<TEntity>(routes, CrudConfig<TEntity>)`), NOT bespoke per-route list/get/create/update/delete logic. The list envelope, pagination clamping, sort aliasing, `ids` intersection, custom-field decoration, error envelopes and pipeline ordering all live in the factory so every module inherits them identically. If the package has no CRUD factory yet, that is a blocker — stop and get it (and the command bus below) built as shared infrastructure first; it is not per-module code.
- **Writes go through the command bus** (`specs/02` R69, `specs/03` R57/R57a): every mutating operation (including CRUD-factory POST/PUT/DELETE) MUST dispatch a named command `'<module>.<domain>.<action>'` whose `execute`/`undo`/`redo` handlers do the write, through the package's shared command bus (e.g. .NET `OpenMercato.Core.Commands.CommandBus`, handlers `ICommand<TInput,TResult>` + optional `IUndoableCommand`). Handlers MUST NOT write to a business table outside the command pipeline — the bus owns the transaction, the `action_logs` row (backing the `x-om-operation` undo header + undo/redo), and the post-commit side-effect flush. Implement `undo`/`redo` wherever upstream's command does (redo reuses the original record id).
- **Entities with custom fields declare CE field sets AND index them** (`specs/03` R37/R49a): a ported entity that carries custom fields MUST both declare its custom-field set / custom entity via the package's module-contract mechanism (`ce.ts` + `data/fields.ts` equivalent, declare-now) AND index those `cf:<key>` values into the shared query index (`entity_indexes`) on every write via the package's query-index maintenance primitive (e.g. .NET `ICrudIndexer.UpsertOneAsync`/`DeleteOneAsync`, awaited before the write returns). Declaring the field set without indexing it leaves the module's own list endpoints unable to filter/sort on those fields (`cf_<key>` / `cf_<key>In` / `cf_<key>` sort) and breaks cross-module reads. If the indexer/CE engine is deferred, the declaration + a `// PARITY-TODO` extension point still land now.
- The contract is the requirement source; `specs/*.md` bind cross-cutting behavior; the upstream TS source is reference only (consult it when the contract is ambiguous — and then fix the contract too).

## Procedure

### 1. Prerequisite checks (stop early on failure)

1. **Contract exists**: `upstream/analysis/modules/<module-id>.md`. If missing or flagged stale (⚠️ from om-sync-upstream), run `om-analyze-module <module-id>` first (invoke the skill), then continue.
2. **Tech package exists**: `packages/<tech>/AGENTS.md`. If missing, stop and instruct: run `om-add-technology <tech> <hints>` first.
3. **Dependencies ported**: read the contract's Dependencies section and `MODULES.md`; every "port first" dependency must be at least ✅ (ported) for `<tech>`. If not, stop and report the required order — do not port dependencies implicitly unless the caller asked.

### 2. Load the context

Read, in this order: the contract; the specs the contract references (at minimum `specs/01-module-system.md`, `specs/02-api-compatibility.md`, `specs/03-data-layer.md`, plus `04`/`05` if the module has workers/subscribers or auth surface); `packages/<tech>/AGENTS.md` end-to-end (module layout, naming, ORM/migration tool, queue client, validation, testing conventions); and one already-ported module in `packages/<tech>/` as a style reference (if any exists).

### 3. Write the shared plan

Write a plan file to the scratchpad (not the repo) that all subagents will receive. It must fix every cross-cutting decision up front so parallel work composes:

- module directory path and internal layout in `packages/<tech>/` (per its AGENTS.md),
- entity/class names ↔ exact Postgres table/column names from the contract,
- migration file name(s) and the migration tool invocation,
- service/DI registration names,
- route registration mechanism and the exact path+method+auth metadata table, plus **which routes use the package's CRUD factory** (upstream `makeCrudRoute`) vs. are hand-written,
- **command surface**: the named commands (`'<module>.<domain>.<action>'`) each write op dispatches through the command bus, and which of them are undoable (`undo`/`redo`) — verbatim from upstream `commands/*.ts`,
- **custom-field indexation**: for each entity carrying custom fields, the CE field-set declaration AND the query-index (`entity_indexes`) maintenance wiring so list endpoints filter/sort on `cf:<key>` (`specs/03` R37/R49a),
- event names, queue names, subscriber/worker ids (verbatim from the contract),
- validation approach (language-native lib; error responses must still match the contract's status+body),
- **module-contract declarations (declare-now, per `specs/10`)** — where and how the package's module-contract mechanism carries them (see `packages/<tech>/AGENTS.md`):
  - ACL/RBAC feature ids + titles,
  - notification types (type id, severity, `expiresAfterHours`, template keys) — verbatim from the contract's Notifications section,
  - custom-field sets and custom entities attached to entities (field-set keys, field names/types, target entity) — from the contract's "Custom entities & field sets",
  - declared typed events (event name + payload shape) — the module's `events.ts` surface, distinct from where each event is *emitted*,
  - for each: note whether the acting engine is deferred (declared-now, engine stubbed) so it lands in the registry regardless,
- test plan: which contract rows get unit tests vs route tests, plus a check that every declaration surfaces in the package's aggregated registry.

Update `MODULES.md`: set `<module-id>` × `<tech>` to 🚧 (porting).

### 4. Fan out implementation subagents

After the plan is written, spawn **parallel subagents**, each receiving the plan, the contract, and `packages/<tech>/AGENTS.md`:

- **Subagent A — entities + migrations**: ORM entities/models with exact table/column names; a real migration (the package's migration tool, per its data-layer ADR) reproducing the contract DDL byte-for-byte on names/types/nullability/indexes/FKs; `make migrate` must apply cleanly on a fresh DB.
- **Subagent B — routes + validation + commands**: every contract route with identical path/method/status/envelope. Routes that upstream builds with `makeCrudRoute` MUST be built with the package's **CRUD factory** (not hand-written — SKILL ground rules / `specs/02` R68), wiring the entity's custom-field codec and query-index maintenance through the factory's extension points (e.g. .NET `ICrudCustomFields` / `ICrudIndexer`). Every **write** op MUST be a named **command** (`'<module>.<domain>.<action>'`) with `execute` and, where upstream has them, `undo`/`redo` handlers, dispatched via the package's **command bus** so it produces an `action_logs` row + `x-om-operation` header (`specs/02` R69, `specs/03` R57). Auth + `requireFeatures` metadata wired into the package's dispatcher; request validation matching the contract's field tables (including error status+body).
- **Subagent C — workers + subscribers + events**: subscribers bound to the exact event names; workers on the exact queue names consuming/producing the standard job envelope `{id,payload,createdAt}` with BullMQ-compatible options (see `specs/04-events-and-queues.md`); every event emission the contract lists.

Each subagent also writes the tests for its slice. Subagent C also declares the module's **typed event surface** (`events.ts` equivalent — event names + payload shapes) via the package's module-contract mechanism, independent of whether every event's delivery is wired yet (declare-now, per `specs/10`).

Sequence-sensitive leftovers are integrated by **you** after the subagents return — these touch shared files and must not be parallelized:

- ACL/RBAC **feature declaration**,
- **notification-type declaration** (`notifications.ts` equivalent — declare the types even if delivery is deferred),
- **custom-field-set / custom-entity declaration** (`ce.ts` + `data/fields.ts` equivalent — declare the field sets even if EAV storage is deferred),
- confirming the **declared event surface** is registered in the module contract,
- setup/seed hooks, DI registration, CLI commands, and enabling the module in the package's composition config.

Every declaration MUST use the package's module-contract mechanism (`packages/<tech>/AGENTS.md` + `specs/10`) so the package's registry aggregation picks it up. A declaration whose acting engine is deferred is still required now — record the deferred engine, never the declaration itself, in the deferral notes.

### 5. Integrate and verify locally

1. Merge the subagents' output; register the module in the package's enabled-modules config; wire DI, ACL, setup hooks, CLI.
2. Run the package's standard targets from `packages/<tech>/`: `make migrate` (fresh DB), `make test`, then `make up` and smoke-test one route per verb (auth'd and anonymous) plus `/healthz`.
3. Fix until green. If a contract requirement is impossible or clearly wrong, STOP: re-check upstream source at the pin; if the contract is wrong, fix the contract (and note it); if the spec must bend, record the exception as an ADR — never silently diverge (AGENTS.md rule 1).

### 6. Record decisions

For each notable idiomatic deviation (different validation lib, ORM pattern, DI approach, code layout), add an ADR in `packages/<tech>/docs/decisions/NNNN-<slug>.md` with `Status / Context / Decision / Consequences`. Only decisions with consequences worth remembering — not routine translations.

### 7. Track and hand off

1. Update `MODULES.md`: `<module-id>` × `<tech>` → ✅ (ported), with a link to the module dir; note test status.
2. Tick the contract's Porting checklist items that are done (in a copy/notes if the contract should stay pristine — follow repo convention).
3. Finish by running (or instructing the caller to run) **`om-verify-parity <module-id> <tech>`** — a port is not "done" (🧪) until parity passes. Include the exact command in your report.

### 8. Report

Return: files created/changed (paths), migration name, routes implemented (count + any contract rows deliberately deferred, with reasons), the **declared module-contract surface** (feature ids, notification types, custom-field sets, declared events — with any deferred *engines* flagged), ADRs written, test results, and the follow-up parity command.
