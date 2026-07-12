---
name: om-port-upstream-fixes
description: After the upstream pin was set, port newly merged upstream fixes/PRs into an already-ported technology package. Use when open-mercato/open-mercato has merged bug fixes, behavior changes, or schema changes since the pin that touch modules already ported to <tech>, and you want those changes reflected in packages/<tech> without a blind full re-analysis. Args - <tech> [--since <commit-or-date>] [--module <id>]. Enumerates merged PRs since the pin with the gh CLI, filters to ported modules, ports each relevant change 1:1, and advances the pin only as far as the ports reach.
---

# om-port-upstream-fixes <tech> [--since <commit-or-date>] [--module <id>]

Carry forward the fixes that upstream merged *after* the pin. This is the surgical alternative to a full `om-sync-upstream` + re-`om-analyze-module` + re-`om-port-module` wave: it finds the merged PRs since the pin that touch modules already ported to `<tech>`, ports the relevant ones (bug fixes / behavior / schema / validation / API changes), skips the irrelevant ones (frontend-only, docs, tests-only, un-ported modules) with a stated reason, and advances the pin — but never past an un-ported relevant PR.

`--since` overrides the starting point (a commit SHA or an ISO date); default is the current pin. `--module <id>` restricts the whole run to a single ported module.

## Ground rules

- **Observable-behavior parity is sacred.** The port must reproduce the upstream PR's *observable* effect exactly — wire behavior, status codes, JSON envelopes, table/column names, queue/event names, validation messages. Idiomatic internals are fine; changed behavior on the wire is not (AGENTS.md rule 3; `specs/02`, `specs/03`).
- **Verify by building and running, not by reading.** A port is not done because the diff "looks equivalent." Build the package, run the module's tests, and — where feasible — bring up `testbench/` and re-verify the behavior the PR changed. Reading the diff is how you *plan*; running is how you *confirm*.
- **Honest reporting.** Every PR in range gets an explicit decision: ported or skipped, with a reason. If a port is partial, failed, or blocked, say so in the report — do not round up to "done."
- **One PR at a time, with a checkpoint.** Port PRs oldest-first, one at a time. After each: build + test green, then a checkpoint (commit-ready state / note in the run log) before starting the next. Do not batch several upstream PRs into one indistinguishable change.
- **Never bump the pin past an un-ported relevant PR.** The pin means "everything up to here is reflected in the ports." If PR #120 (relevant, un-ported) merged before PR #125 (ported), the pin may advance only to #120's parent — never to #125. A relevant PR you chose to skip *without porting* is a hard stop for the pin.
- **The pin is the single source of truth.** Update `upstream/UPSTREAM.md` only for merge commits whose changes you actually ported and verified for `<tech>`.

## Procedure

### 1. Resolve the pin and the ported-module set

1. Read `upstream/UPSTREAM.md` → extract the pinned commit SHA (`PIN`) and its date. This is the default `--since`.
2. Read `MODULES.md` → build `PORTED`, the set of module ids whose `<tech>` column is ✅ or 🧪 (ported / parity-verified). 🔍 (analyzed only) and 🚧 (in progress) are **not** ported — exclude them; note any 🚧 module in the report as "porting in flight, fixes deferred." If `--module <id>` is given, intersect `PORTED` with `{<id>}` and stop with a clear message if that module is not ported for `<tech>`.
3. Resolve the effective start:
   - default: `PIN` (and its commit date, via the pinned clone or `git show -s --format=%ci`).
   - `--since <sha>`: use that commit's date as the PR search floor.
   - `--since <date>`: use the date directly.
4. Confirm the tech package exists: `packages/<tech>/AGENTS.md`. If missing, stop — there is nothing ported to fix.

### 2. Enumerate merged PRs since the pin (gh CLI)

Use the `gh` CLI against the upstream repo. The PR search floor is the pin's commit date (`<date>` below):

```bash
gh pr list --repo open-mercato/open-mercato --state merged \
  --search "merged:>=<date>" --limit 200 \
  --json number,title,mergedAt,mergeCommit,labels
```

For each candidate PR, pull its file list and metadata:

```bash
gh pr view <n> --repo open-mercato/open-mercato \
  --json number,title,body,files,mergeCommit,mergedAt,labels
```

Sort the candidates **oldest merge first** (`mergedAt` ascending) — you port and advance the pin in merge order. Drop any PR whose `mergeCommit` is an ancestor of `PIN` (already included) — verify with the pinned clone if in doubt (`git merge-base --is-ancestor <mergeCommit> <PIN>`).

> If `gh` is unavailable or unauthenticated, stop and report it — do not guess at what merged. `gh auth status` to check.

### 3. Filter to ported modules and classify each PR

Map each PR's changed files to module ids: a path `packages/core/src/modules/<id>/**` (or `apps/mercato/src/modules/<id>/**`) → module `<id>`. A PR is **in scope** when at least one changed file maps to a module in `PORTED` (respecting `--module`).

For every in-scope PR, classify with a stated reason:

- **relevant** — touches backend behavior a port must mirror: route handlers, entities/migrations/schema, validation, commands, subscribers/workers, events, ACL, envelopes, status codes.
- **skip** — with the reason:
  - *frontend-only* — only `**/frontend/**`, `*.tsx`, backoffice UI, components.
  - *docs-only* — only `*.md`, `docs/**`.
  - *tests-only* — only test files, no source behavior change.
  - *un-ported module* — all mapped modules are outside `PORTED` (note them for a future porting wave).
  - *no observable change* — refactor/rename with identical wire + schema behavior (justify explicitly).

Out-of-scope PRs (touch no ported module) are recorded once as "out of scope" and otherwise ignored.

Produce the working list: the ordered set of **relevant** PRs, each tagged with its module(s).

### 4. Port each relevant PR — one at a time, oldest first

For each relevant PR, in merge order:

1. **Read the change**: `gh pr diff <n> --repo open-mercato/open-mercato`. Understand the *observable* delta — what a caller/DB/queue would see differently before vs. after. Cross-check against the module's port contract (`upstream/analysis/modules/<id>.md`) and the specs it cites; if the PR invalidates a contract claim, note it (the contract may need refreshing via `om-analyze-module` — flag it, don't silently diverge).
2. **Locate the target(s)** in `packages/<tech>/`: the module directory (per `packages/<tech>/AGENTS.md`), then the specific file(s) — route/handler, entity, migration, validator, command, subscriber/worker — corresponding to the upstream files the PR changed.
3. **Port 1:1, idiomatically**: reproduce the observable behavior exactly; write internals per `packages/<tech>/AGENTS.md`. Keep CRUD via the package's CRUD factory and writes via the command bus (never hand-roll around them — `specs/02` R68/R69). Match error status+body and table/column/queue/event names verbatim.
4. **Schema-affecting PRs — mind the testbench.** This repo runs the port against an **OM-migrated schema** in `testbench/` (OM owns migrations; the .NET side is migrate-only and seeds via `IModule.SeedDefaults/SeedExamples`). A PR that changes upstream migrations/columns usually needs the **testbench OM image bumped** to the version carrying that migration — *not* a new `<tech>` migration that would fight OM's schema. Decide per PR: (a) pure app-behavior change → port the code; (b) schema change → bump the testbench OM image (see `testbench/README.md`, `specs/11-testbench.md`, and the image-sync memory note) and adjust entities/seed to match; record which. If both the code and schema moved, do both.
5. **Add/adjust tests**: add a unit/route test that fails before the port and passes after, exercising the exact behavior the PR changed. Update any test the change legitimately alters.
6. **Checkpoint**: build + run the module's tests (step 5 below) green before moving to the next PR. Keep each PR's port as its own coherent change (commit-ready) so the pin can stop precisely between PRs.

If a relevant PR cannot be ported now (missing dependency, needs an un-ported module, ambiguous contract), **stop porting at that PR**: it becomes the pin ceiling (§6). Record it as "relevant, not ported — blocked: <reason>."

### 5. Build and run tests; re-verify in the testbench where feasible

From `packages/<tech>/`, after each PR and again at the end of the run:

```bash
# from packages/<tech>/ — use the package's real targets (see its AGENTS.md / Makefile)
make test        # or the package's unit-test target (e.g. dotnet test)
make migrate     # only if this package owns migrations for the change
```

For behavior/schema PRs, bring up `testbench/` and re-verify the specific behavior the PR changed (drive the affected route / inspect the affected table), per `testbench/README.md`. For schema PRs, confirm the testbench OM image actually carries the upstream migration before trusting the run (the OM image can drift from the OM test source — see the image-sync memory note). Fix until green; if a change is impossible to reproduce faithfully, stop and reconcile against upstream at that PR's merge commit rather than diverging.

### 6. Advance the pin — only as far as the ports reach

Let `LAST_PORTED` be the merge commit of the newest relevant PR you fully ported **and** verified, such that **every relevant PR merged at or before it is also ported** (no un-ported relevant PR sits below it). Then:

1. Edit `upstream/UPSTREAM.md`: set the pinned commit to `LAST_PORTED`'s SHA and its commit date, today's date as the sync date, and append a changelog line: `PIN..LAST_PORTED — ported upstream fixes for <tech>: #<a>, #<b>, … (modules: …)`.
2. If a relevant PR was skipped-without-porting or blocked *below* the newest ported PR, **do not** advance past it — pin to that PR's parent instead and say so loudly in the report (the pin is intentionally held back).
3. If no relevant PR was ported, leave the pin unchanged and report why.

> The pin bump here is narrower than `om-sync-upstream`: it does **not** regenerate analysis docs. If the ported PRs materially changed a module's contract, flag it in the report and recommend `om-analyze-module <id>` — do not bump analysis-doc headers you did not re-verify.

### 7. Record ports in the tracker

1. In `MODULES.md`: for each module that received ports, append a short note (in its status cell or the module notes block) — `upstream fixes ported for <tech>: #<n> (<one-line>)` with the new pin. Keep the status emoji; a fix port does not change ✅/🧪 unless it broke and was left partial (then downgrade and say so).
2. If the repo keeps a changelog/handoff note (e.g. `HANDOFF.md`), add a dated entry listing the ported PRs, the new pin, and any deferred/blocked PRs.

### 8. Report

Return a table plus the pin outcome:

| PR# | Title | Module(s) | Decision | Files touched (in packages/<tech>) | Test result |
|---|---|---|---|---|---|
| 118 | fix currency rounding | currencies | ported | Api/CurrenciesRoutes.cs, tests/… | 12/12 pass |
| 121 | redesign settings UI | customers | skipped — frontend-only | — | — |
| 124 | add deal.stage column | customers | ported (+ testbench OM image bump) | Data/Deal.cs, seed | testbench re-verified |
| 127 | fix export in un-ported catalog | catalog | skipped — module not ported to <tech> | — | — |

Also report: `PIN → NEW_PIN` (SHAs + dates), the list of PRs ported / skipped / blocked, whether the pin was intentionally held back (and behind which PR), any module contracts now stale (recommend `om-analyze-module`), and the follow-up `om-verify-parity <module-id> <tech>` command for every module that received behavior/schema ports.
