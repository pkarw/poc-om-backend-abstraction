# 0015 — Command bus, action logs, and undo/redo

## Status

Accepted

## Context

Upstream Open Mercato routes every mutating operation through a **command bus**
(`packages/shared/src/lib/commands/*`) that runs a handler, persists an
`action_logs` row (audit_logs module) with a redo envelope + undo token, and
supports atomic undo/redo. The CRM (customers) port and every future module
depends on this write infrastructure, so it is ported into `OpenMercato.Core`
(namespace `OpenMercato.Core.Commands`) ahead of the CRUD factory.

## Decision

- **Handlers as DI services.** A command is an `ICommand<TInput,TResult>` (plus
  the non-generic `ICommand` marker). Modules register handlers with
  `services.AddScoped<ICommand, XCommand>()` — the idiomatic .NET equivalent of
  upstream's side-effect `registerCommand(handler)`. The `CommandBus` resolves
  `IEnumerable<ICommand>` and indexes them by `CommandId`
  (`'<module>.<domain>.<action>'`).
- **Optional capabilities via extra interfaces**, mirroring upstream's optional
  `undo`/`redo`/`buildLog`: `IUndoableCommand` (Undo/RedoAsync) and
  `ICommandLogMetadataBuilder<TInput,TResult>` (BuildLog → resource kind/id,
  snapshots, changes, context).
- **action_logs is byte-exact.** The `ActionLog` entity + raw-SQL migration
  `20260707040000_AddActionLogs` reproduce the upstream table/column/index names
  and types (execution_state text default 'done', command_payload/snapshot_*/
  changes_json/context_json jsonb, changed_fields text[], the 9 indexes). jsonb
  columns are stored as raw JSON strings with typed `GetRedoInput<T>()` /
  `GetSnapshot*<T>()` accessors. Mapped by a small `AuditLogsModule` (id
  `audit_logs`) that also registers `ActionLogService` + `CommandBus` (scoped).
- **Atomic state transitions.** `execution_state` moves `done → undoing →
  undone` on undo and `undone → redoing → done` on redo via compare-and-set. On
  a relational provider the CAS is a single conditional `UPDATE … WHERE
  execution_state = …` (upstream `nativeUpdate`); on the in-memory provider used
  by tests it falls back to load-check-set.
- **Optimistic locking** (`OptimisticLock`) ports `enforceCommandOptimisticLock`
  faithfully: header `x-om-ext-optimistic-lock-expected-updated-at`, env
  `OM_OPTIMISTIC_LOCK` (default all; `off/…` disables; comma-list allow-list),
  and the exact `409 { error:'record_modified', code:'optimistic_lock_conflict',
  currentUpdatedAt, expectedUpdatedAt }` body via `CommandHttpException`.

## Consequences / deviations (PARITY-TODO)

- **Redo re-arms the same row** (undone → done with a fresh undo token) instead
  of upstream's "mark the old log `redone` and write a NEW log via the execute
  path". The observable behavior a client cares about (a record can be
  undone → redone → undone …) is preserved; the extra history rows are not yet
  emitted. Revisit when porting the audit_logs history projections.
- **Deferred pipeline stages** are left as clean extension points with
  `// PARITY-TODO` markers in `CommandBus`: before/after command interceptors,
  CRUD cache invalidation, and the post-commit side-effect flush (CRUD events +
  query-index upsert). These land with the events/query-index and interceptor
  ports.
- **Transaction wrapping** uses `AppDbContext.Database.BeginTransactionAsync` on
  relational providers only (the in-memory provider does not support
  transactions); this is the `withAtomicFlush({ transaction:true })` analog for
  single-DbContext handlers. Multi-phase per-phase flush semantics are not
  needed under EF's change-tracker (a single `SaveChanges` commits the unit of
  work).
- **audit_logs scope** here is limited to the command-write path. The
  `access_logs` table, the HTTP API (list/undo/redo/access routes),
  projections/encryption, and the `CommandContext.SystemActor`/strict-scope
  logging nuances are deferred to the audit_logs API port. `AuditLogsModule`
  therefore maps only `action_logs` and its `MapRoutes` is a no-op.
- `OpenMercato.Core` now references `Microsoft.EntityFrameworkCore.Relational`
  (needed for `ToTable`/`HasColumnName`/`HasColumnType` in
  `AuditLogsModule.ConfigureModel`).
