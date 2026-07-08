# 0019 — Dictionaries module: hand-written routes, command-bus writes

Status: Accepted · Date: 2026-07-08 · Scope: `packages/dotnet` `OpenMercato.Modules.Dictionaries`

## Context

Upstream `packages/core/src/modules/dictionaries` exposes org-scoped enumerations
(`dictionaries` + `dictionary_entries`). Unlike most CRUD surfaces, upstream **does not use
`makeCrudRoute`** for dictionaries — it hand-writes every route (`api/route.ts`,
`api/[dictionaryId]/route.ts`, `entries/*`) with bespoke response shapes:

- List endpoints return `{ items }` with **no** pagination envelope (`total/page/pageSize/totalPages`).
- Dictionary reads apply **org inheritance** (selected org + its ancestors → `readableOrganizationIds`,
  with an `isInherited` flag), which the generic factory scope filter does not model.
- Entry lists are sorted by the dictionary's configured `entry_sort_mode`, not a generic sort param.
- Dictionary writes were **inline `em` mutations** (not on the command bus); entry writes went through
  the command bus but via hand-written nested routes, not the factory.

## Decision

1. **Routes are hand-written** (not `CrudRoute.Map`). Using the factory would change the observable
   contract (envelope shape, inheritance, entry sorting), breaking 1:1 parity. This is consistent with
   `AGENTS.md`: the factory is mandated only for endpoints upstream builds with `makeCrudRoute`.
2. **All writes dispatch through the command bus** (per the .NET write convention in `AGENTS.md` §96),
   including the dictionary create/update/delete that upstream did inline. New commands:
   `dictionaries.dictionary.{create,update,delete}` + `dictionaries.entries.{create,update,delete,reorder,set_default}`.
   This centralizes scope guards, optimistic locking, action-log rows, and (for entries) undo/redo.
3. **Migration folds the 3 upstream migrations into one** raw-SQL migration
   `20260707080000_AddDictionariesModule` (base tables → position/is_default + partial default index +
   backfill/seed → entry_sort_mode), byte-exact to the upstream DDL.

## Consequences / PARITY-TODO

- Undo/redo is implemented for entry `create/update/delete`; `reorder`/`set_default` undo is left as a
  `// PARITY-TODO` (complex partial-unique-index restore ordering) — the routes still return `{ ok: true }`.
- `findOneWithDecryption` field-value encryption, the CRUD mutation-guard, translation locale overlay
  (the `dictionaries:dictionary_entry.label` translatable field), and full zod error-message parity are
  clean seams left as `// PARITY-TODO`.
- The "no selected org ⇒ read across all tenant orgs" branch of upstream's readable-org resolution is
  simplified to the selected org + ancestors; the empty-selection branch returns `{}`.
</content>
