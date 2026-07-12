# Porting skills

Six **technology-agnostic** Claude Code skills drive every Open Mercato backend port. The same skill ports a module to Python, .NET, Go, or any future target: the target technology is an argument, and all tech-specific conventions live in `packages/<tech>/AGENTS.md` — never in the skills.

| Skill | Args | What it does |
|---|---|---|
| [`om-sync-upstream`](om-sync-upstream/SKILL.md) | `[ref]` | Bump the pinned upstream commit in `upstream/UPSTREAM.md`, regenerate the affected `upstream/analysis/*.md` docs (one subagent per subsystem), flag possibly-stale `specs/*.md` requirement IDs, and mark ported modules in `MODULES.md` that upstream changed. |
| [`om-analyze-module`](om-analyze-module/SKILL.md) | `<module-id>` | Distill one upstream module into its **port contract** at `upstream/analysis/modules/<module-id>.md`: every route (method/path/auth/features/schemas/status codes), entity (exact tables/columns), event, queue/worker, ACL feature, dependency — plus a porting checklist. |
| [`om-port-module`](om-port-module/SKILL.md) | `<module-id> <tech>` | Implement the contract 1:1 in `packages/<tech>/`: identical API surface, Postgres schema (real migrations), event/queue names; idiomatic internals with deviations recorded as ADRs; tests; `MODULES.md` updated. Fans out entities+migrations / routes / workers+subscribers to parallel subagents behind a shared plan. |
| [`om-verify-parity`](om-verify-parity/SKILL.md) | `<module-id> <tech> [--against <url>]` | Black-box compatibility audit: derive probes from the contract (happy path, validation, authz, tenant pollution, 404s), run the port via `make up`, diff normalized responses (optionally against a live TS instance), check `information_schema` and `bull:*` queue-name parity, and write PASS/FAIL to `.ai/parity/<module-id>-<tech>.md`. |
| [`om-add-technology`](om-add-technology/SKILL.md) | `<tech> <stack hints>` | Scaffold `packages/<tech>/` per `specs/09-technology-package-standard.md`: standard layout, AGENTS.md, runtime/ORM/queue ADRs, docker-compose + standard Makefile targets, `/healthz` + example `health_check` module — verified to boot before reporting success. |
| [`om-port-upstream-fixes`](om-port-upstream-fixes/SKILL.md) | `<tech> [--since <ref>] [--module <id>]` | Carry forward fixes upstream merged **after** the pin: enumerate merged PRs since `upstream/UPSTREAM.md` with the `gh` CLI, filter to modules already ported to `<tech>` (`MODULES.md`), classify each (relevant vs. frontend/docs/tests/un-ported), port the relevant ones 1:1 one-at-a-time with build+test checkpoints, then advance the pin — never past an un-ported relevant PR. The surgical alternative to a full sync + re-analyze + re-port wave. |

## The loop

```
om-sync-upstream           # (when upstream moved) refresh pin + analyses, flag stale contracts/ports
      │
om-analyze-module <id>     # produce/refresh the port contract        → MODULES.md 🔍
      │
om-port-module <id> <tech> # implement the contract in packages/<tech> → MODULES.md 🚧 → ✅
      │
om-verify-parity <id> <tech>  # prove 1:1 behavior                     → MODULES.md 🧪
```

`om-add-technology` runs once per target stack, before the first `om-port-module` for that tech. Ports of the same module to different technologies run **in parallel** — packages are independent (only `MODULES.md` is shared).

Ground rules binding all skills: upstream is pinned (`upstream/UPSTREAM.md`), specs are normative (`specs/*.md`), observable behavior is 1:1, internals are idiomatic (ADRs in `packages/<tech>/docs/decisions/`), and every action updates the `MODULES.md` status matrix (⬜ → 🔍 → 🚧 → ✅ → 🧪). See the root [`AGENTS.md`](../../AGENTS.md).

## Vendored skills — Open Mercato agentic PR pipeline

The skills below are **vendored** from [`open-mercato/skills`](https://github.com/open-mercato/skills) (MIT, © 2026 Open Mercato) at commit `aa7045e0d7bbec883b3b01d77af4b092af7f55a2` (2026-07-10). They are a general-purpose **agentic PR / SDLC pipeline** (issue → fix → PR → review → merge → release), independent of the porting loop above. License preserved at [`LICENSE.open-mercato-skills`](LICENSE.open-mercato-skills). Many read a pipeline config at `.ai/agentic.config.json` — run `om-setup-agent-pipeline` once per repo before relying on the tracker-driven ones. To refresh: re-clone the source repo and re-copy the `skills/*` directories (do not hand-edit vendored `SKILL.md` bodies).

| Skill | One-line purpose |
|---|---|
| [`om-setup-agent-pipeline`](om-setup-agent-pipeline/SKILL.md) | One-time configurator: inspect the repo, write `.ai/agentic.config.json` + tracker descriptor + SDLC/review/back-compat docs. |
| [`om-apply-upgrade-notes`](om-apply-upgrade-notes/SKILL.md) | After a skills upgrade, apply `UPGRADE_NOTES.md`: re-sync the tracker descriptor and pipeline artifacts, preserving local edits. |
| [`om-prepare-issue`](om-prepare-issue/SKILL.md) | File a well-formed tracker issue (dedup-checked, spec-linked, step-by-step guidance) without implementing it. |
| [`om-verify-in-repo`](om-verify-in-repo/SKILL.md) | Read-only triage gate: decide whether an issue is a real, still-unfixed defect on the current branch. |
| [`om-root-cause`](om-root-cause/SKILL.md) | Read-only root-cause analysis: locate the bug and the minimal change surface for the next agent. |
| [`om-fix`](om-fix/SKILL.md) | Implement the minimal fix from root-cause + regression tests; claim the issue; run the validation gate (no commit/PR). |
| [`om-open-pr`](om-open-pr/SKILL.md) | Commit, push the branch, open a labeled draft PR, hand the issue back, release the claim lock. |
| [`om-auto-fix-issue`](om-auto-fix-issue/SKILL.md) | Drive the whole autofix chain for one issue (verify → root-cause → fix → open-PR → review loop) in an isolated worktree. |
| [`om-auto-create-pr`](om-auto-create-pr/SKILL.md) | Run an arbitrary task end-to-end and ship it as a PR (plan → commits → validation gate → labels). Resumable. |
| [`om-auto-continue-pr`](om-auto-continue-pr/SKILL.md) | Resume an in-progress `om-auto-create-pr` PR from its plan's first unchecked step. |
| [`om-auto-create-pr-loop`](om-auto-create-pr-loop/SKILL.md) | Resumable, strictly-tracked variant for long multi-step spec work (run folder, per-step commits, checkpoints). |
| [`om-auto-continue-pr-loop`](om-auto-continue-pr-loop/SKILL.md) | Resume an `om-auto-create-pr-loop` run from the first non-done task row. |
| [`om-code-review`](om-code-review/SKILL.md) | The review engine: run the validation gate + checklist, produce categorized findings and an approve/request-changes verdict. |
| [`om-auto-review-pr`](om-auto-review-pr/SKILL.md) | Review/re-review a PR in a worktree; on changes-requested, iterate an autonomous autofix loop until merge-ready. |
| [`om-review-prs`](om-review-prs/SKILL.md) | Review all unreviewed open PRs, newest first, via `om-auto-review-pr`, respecting claim locks. |
| [`om-merge-buddy`](om-merge-buddy/SKILL.md) | Classify open PRs' merge readiness from labels/reviews/CI/mergeability; report what can merge now vs. blocked. |
| [`om-approve-merge-pr`](om-approve-merge-pr/SKILL.md) | Approve + squash-merge a PR by number, refusing when the QA gate or a blocking label forbids it. |
| [`om-stabilize-ci`](om-stabilize-ci/SKILL.md) | Drive a PR/branch to green CI: classify failures, fix the real ones with tests, iterate — never by weakening checks. |
| [`om-check-and-commit`](om-check-and-commit/SKILL.md) | Run every configured validation command, fix straightforward failures, then commit + push the current branch. |
| [`om-prepare-test-env`](om-prepare-test-env/SKILL.md) | Discover how the app runs once, compile a project-specific env-up script, and write a shared test-env descriptor. |
| [`om-integration-tests`](om-integration-tests/SKILL.md) | Run and generate integration/E2E tests (Playwright TS default), reusing a running env; per-test artifact diagnosis. |
| [`om-auto-verify-pr-ui`](om-auto-verify-pr-ui/SKILL.md) | Boot the app and drive its UI with a browser to QA a change (screenshots + pass/fail report), without merging. |
| [`om-spec-writing`](om-spec-writing/SKILL.md) | Write/review feature specs to staff-engineer standards: skeleton-first, Open Questions gate, phased implementation breakdown. |
| [`om-followup-issue-from-pr`](om-followup-issue-from-pr/SKILL.md) | Turn a PR (or PR comment, or added design doc) into a tracked follow-up issue. |
| [`om-sync-merged-pr-issues`](om-sync-merged-pr-issues/SKILL.md) | Post-merge housekeeping: auto-close issues fixed by merged PRs; comment on issues whose PRs closed unmerged. |
| [`om-auto-update-changelog`](om-auto-update-changelog/SKILL.md) | Draft an emoji-format `CHANGELOG.md` entry for every PR since the last release and ship it as a docs PR. |

> Note: these vendored skills assume the source repo's own conventions (a `.ai/` pipeline config, tracker labels, worktree/claim protocol). They are **not** wired into the porting `MODULES.md` loop and were copied verbatim; adapt or ignore the ones that don't fit this lab's workflow.
