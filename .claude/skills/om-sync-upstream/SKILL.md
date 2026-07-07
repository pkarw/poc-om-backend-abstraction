---
name: om-sync-upstream
description: Refresh the pinned Open Mercato upstream commit. Use when upstream has moved, when analysis docs cite an old commit, or before starting a new porting wave. Args: [ref] — a git ref of the upstream repo (branch, tag, or SHA); defaults to latest origin/main. Technology-agnostic — touches only upstream/, specs/ flags, and MODULES.md.
---

# om-sync-upstream [ref]

Bump the pinned upstream commit in `upstream/UPSTREAM.md`, regenerate the analysis docs affected by the diff, and flag specs and ported modules that may now be stale.

## Ground rules

- **NEVER** modify any read-only upstream checkout (e.g. `/tmp/om-analyze`). Work only in a fresh clone under a temp/cache dir.
- The pin is the single source of truth: every analysis doc, spec header, and port contract cites it. Do not update any doc to the new commit unless you actually re-verified its content against the new tree.
- If the resolved ref equals the current pin, report "already up to date" and stop.

## Procedure

### 1. Resolve the current and target commits

1. Read `upstream/UPSTREAM.md` → extract the pinned commit SHA (call it `OLD`) and its date.
2. Get a working clone of `https://github.com/open-mercato/open-mercato`:
   - If `scripts/sync-upstream.sh` exists, run it (it maintains a gitignored `.upstream-cache/` clone).
   - Otherwise `git clone` into the scratchpad directory.
3. `git fetch --all --tags`, then resolve the target: `git rev-parse <ref>` (default `origin/main`) → `NEW`, and `git show -s --format=%ci NEW` → its date.
4. If `NEW == OLD`: report and stop.

### 2. Compute the diff and map it to subsystems

1. In the clone: `git log --oneline OLD..NEW` and `git diff --stat OLD..NEW` and `git diff --name-only OLD..NEW`.
2. Build the path→subsystem map from the analysis docs themselves: each `upstream/analysis/0N-*.md` has a **"Key source locations"** table listing the upstream paths it covers. A subsystem is *affected* when any changed path falls under (or imports-adjacent to) one of its key source locations. Rough default mapping if a table is ambiguous:

   | Analysis doc | Typical upstream paths |
   |---|---|
   | `01-module-system.md` | `packages/shared/src/modules/**`, `packages/shared/src/lib/{di,bootstrap,modules}/**`, `packages/cli/**`, `apps/mercato/src/{modules.ts,bootstrap.ts,di.ts}` |
   | `02-api-http.md` | `apps/mercato/src/app/api/**`, `packages/shared/src/lib/crud/**`, `packages/shared/src/modules/registry.ts`, `packages/core/src/modules/api_docs/**` |
   | `03-data-layer.md` | `packages/core/src/modules/{entities,query_index,directory}/**`, `packages/shared/src/lib/{query,data,commands}/**`, any `data/entities.ts` / `migrations/**` |
   | `04-events-queues.md` | `packages/queue/**`, `packages/events/**`, `packages/core/src/modules/{core,planner}/workers`, scheduler code, `packages/core/src/bootstrap.ts` |
   | `05-auth-rbac.md` | `packages/core/src/modules/{auth,api_keys,customer_accounts}/**`, `packages/shared/src/lib/auth/**` |
   | `06-runtime-startup.md` | `packages/cli/**`, `packages/cache/**`, init/seed code, health endpoints, env resolution |
   | `07-shared-services.md` | `packages/shared/**` (misc helpers), `packages/core/src/modules/{attachments,audit_logs,dictionaries,configs,feature_toggles,notifications,translations}/**` |

3. Also collect the set of **changed module ids**: every changed path matching `packages/core/src/modules/<id>/**` or `apps/mercato/src/modules/<id>/**`.

### 3. Regenerate affected analysis docs — fan out

For each affected `upstream/analysis/0N-<subsystem>.md`, spawn **one subagent in parallel** (all subagents at once). Each subagent gets:

- the path to the clone checked out at `NEW` (read-only for them too — tell them),
- the existing analysis doc,
- the relevant slice of `git diff OLD..NEW` for its key source locations,
- instruction: *re-verify every claim in the doc against the `NEW` tree; rewrite sections invalidated by the diff; keep the doc's existing section structure (Purpose / Key source locations / How it works / Public contracts / Helpers to mirror / Behavioral details a port MUST replicate / Gotchas); update the header line to `Analyzed at upstream commit <NEW> (<date>)`; cite only real files at `NEW`.*

Unaffected docs: leave content untouched, but do NOT bump their header commit (they were not re-verified).

### 4. Update the pin

Edit `upstream/UPSTREAM.md`: new SHA, new commit date, today's date as sync date, and append a short changelog entry (`OLD..NEW`, N commits, affected subsystems).

### 5. Flag potentially stale specs

For each affected subsystem, open the matching `specs/*.md` (spec numbering matches analysis numbering; the spec header cites the commit it was derived from). Skim its Requirements against the diff summary and list requirement IDs (e.g. `APIHTTP-R20`) whose upstream basis changed. Do **not** silently rewrite specs — instead:

- add/refresh a `> ⚠️ Staleness review needed (upstream OLD..NEW): <IDs or "none">` note under the spec's header, and
- include the list in your final report so a human/agent can run a deliberate spec revision.

### 6. Flag ported modules needing re-port review

1. Read `MODULES.md`.
2. For every module whose status is 🔍 (analyzed) or beyond, check whether its id is in the changed-module set from step 2.3.
3. For each hit: mark the module's row/cells in `MODULES.md` with a ⚠️ (keep the existing status emoji) and note "upstream changed in OLD..NEW — re-run om-analyze-module, then review ports". Contracts in `upstream/analysis/modules/<id>.md` for these modules are now stale — say so in the report; do not regenerate them here (that is `om-analyze-module`'s job, against the new pin).

### 7. Report

Output a summary: `OLD → NEW` (dates), commit count, affected subsystems (regenerated docs), specs flagged with requirement IDs, changed modules and which of them have contracts/ports needing review, and the recommended follow-up commands (`/om-analyze-module <id>` per stale contract).
