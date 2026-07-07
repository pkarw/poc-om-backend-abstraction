# AGENTS.md — poc-om-backend-abstraction

Rules for AI agents working in this repository. Read this before touching anything.

## What this repo is

A porting lab for [Open Mercato](https://github.com/open-mercato/open-mercato) (TypeScript/Next.js modular commerce/ERP framework). We re-implement its **backend modules** in other technologies (Python, .NET, Go, …) with **1:1 API compatibility**. The TS codebase stays the source of truth; this repo holds the specs, the porting skills, and one package per target technology.

## Repository map

```
specs/                  Normative tech-agnostic specs (00-09). Requirements have IDs (e.g. APIHTTP-R3).
upstream/UPSTREAM.md    Pinned upstream commit — the reference every port targets.
upstream/analysis/      Subsystem analyses of upstream (descriptive ground truth).
upstream/analysis/modules/  Per-module port contracts (produced by om-analyze-module).
.claude/skills/         The 5 technology-agnostic porting skills.
packages/<tech>/        One package per technology. Each has its own AGENTS.md — read it before working there.
MODULES.md              Porting tracker: module × technology status matrix. Keep it updated.
scripts/sync-upstream.sh  Refresh the upstream clone (.upstream-cache/, gitignored).
```

## Non-negotiable rules

1. **Specs are normative.** `specs/*.md` requirements (MUST/SHOULD) bind every port. If reality forces a deviation, change the spec first (or record the exception in an ADR) — never silently diverge.
2. **Upstream is pinned.** Ports target the commit in `upstream/UPSTREAM.md`. Never analyze or port against an unpinned checkout. Bumping the pin goes through the `om-sync-upstream` skill.
3. **Observable behavior is 1:1.** API paths, methods, status codes, JSON envelopes, auth semantics, Postgres table/column names, queue names, event names — identical to upstream. Anything an API client, a shared database, or a Redis-connected BullMQ instance can observe must match.
4. **Internals are idiomatic.** Inside a technology package, prefer the language-native best solution (validation, ORM, DI) over transliterating TypeScript. Record notable choices as ADRs in `packages/<tech>/docs/decisions/`.
5. **Every technology package keeps the same shape.** Layout, `Makefile` target names (`up/down/dev/worker/migrate/test`), AGENTS.md section outline, README structure — all defined in `specs/09-technology-package-standard.md`. Consistency across packages beats local preference.
6. **PostgreSQL + Redis + BullMQ-compatible queues everywhere.** With real migration tooling per tech. Never claim BullMQ wire compatibility that isn't implemented — the honest status lives in each tech's queue ADR.
7. **Track everything in MODULES.md.** Any analyze/port/verify action updates the matrix (⬜ → 🔍 → 🚧 → ✅ → 🧪).
8. **Env var names are shared.** `DATABASE_URL`, `REDIS_URL`, `QUEUE_STRATEGY`, `QUEUE_REDIS_URL`, `JWT_SECRET` — same names in every package, matching upstream.
9. **Module contract parity.** Every module declares the *same four pieces the same way in every technology* — **RBAC feature declarations, notification types, custom-field sets / custom entities, and declared typed events** (mirroring upstream `acl.ts` / `notifications.ts` / `ce.ts`+`data/fields.ts` / `events.ts`). Each tech's module abstraction and registry aggregation MUST support them, and every ported module MUST declare its full surface — **declare-now even when the delivery/storage engine is deferred** (only the acting engine may be stubbed, never the declaration). The canonical shape is [`specs/10-module-contract-parity.md`](specs/10-module-contract-parity.md).

## How to do the common tasks

| Task | Do this |
|---|---|
| Port a module | `/om-analyze-module <id>` (if no contract yet) → `/om-port-module <id> <tech>` → `/om-verify-parity <id> <tech>` |
| Add a technology | `/om-add-technology <tech> <stack hints>` — then review its AGENTS.md against the other packages for outline parity |
| Upstream moved | `/om-sync-upstream [ref]` — regenerates stale analyses, flags affected ports |
| Understand a subsystem | Read `upstream/analysis/<n>-<subsystem>.md`, then the matching `specs/` doc |
| Work inside `packages/<tech>/` | Read that package's `AGENTS.md` first; it maps upstream concepts to tech idioms |

## Porting order

Infrastructure tier first (shared runtime, `auth`, `directory`, `entities`, `query_index`), then domain modules — the dependency tiers are laid out in `MODULES.md`. Modules can be ported to multiple technologies **in parallel** (independent packages, no shared files except MODULES.md).

## Style

- Markdown: GitHub-flavored, tables over prose lists where comparing things.
- ADRs: `NNNN-slug.md` with `Status / Context / Decision / Consequences`.
- Cite upstream code as repo-relative paths (`packages/core/src/modules/auth/...`) at the pinned commit.
- Commit messages: conventional, scoped (`feat(python): port customers module`).
