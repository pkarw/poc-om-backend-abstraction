# Upstream Reference — Open Mercato

This directory pins a single upstream commit of [Open Mercato](https://github.com/open-mercato/open-mercato) and holds the subsystem analyses derived from it. **Everything in this repo — specs, port contracts, ported code, parity checks — targets the pinned commit below**, not upstream `main`.

## Pinned commit

| | |
|---|---|
| **Repository** | https://github.com/open-mercato/open-mercato |
| **Pinned commit** | `adc9da27759e357febe9ed8d4b7182040d127349` |
| **Commit date** | 2026-07-01 |
| **Local read-only clone (analysis sessions)** | `/tmp/om-analyze` (never modify) |
| **Refreshable clone cache** | `.upstream-cache/` at repo root (gitignored, managed by `scripts/sync-upstream.sh`) |

> The pinned commit line above is machine-parsed by `scripts/sync-upstream.sh` (first 40-hex-char SHA in this file). Keep it as a full SHA.

## What lives in `upstream/analysis/`

Seven subsystem analyses, each generated at the pinned commit (every doc states its commit in a header note). They are the source of truth that the tech-agnostic specs in `specs/` were distilled from.

| Doc | Covers |
|---|---|
| [`analysis/01-module-system.md`](analysis/01-module-system.md) | Module system & app composition: auto-discovered module files (`api/`, `data/`, `subscribers/`, `workers/`, `acl.ts`, `di.ts`, `setup.ts`, `ce.ts`, `events.ts`, `cli.ts`), registry generation, module lifecycle |
| [`analysis/02-api-http.md`](analysis/02-api-http.md) | HTTP API surface & conventions: route file mapping, `openApi` exports, auth/feature metadata, error shapes, headers |
| [`analysis/03-data-layer.md`](analysis/03-data-layer.md) | Data layer: MikroORM entities, migrations, multi-tenancy scoping, custom fields, query engine, CommandBus/undo |
| [`analysis/04-events-queues.md`](analysis/04-events-queues.md) | Events, queues & scheduler: event bus, persistent subscribers, BullMQ queue strategy (`QUEUE_STRATEGY`), workers, cron jobs |
| [`analysis/05-auth-rbac.md`](analysis/05-auth-rbac.md) | Auth & RBAC: staff auth/JWT, API keys, ACL features, roles, customer/portal auth |
| [`analysis/06-runtime-startup.md`](analysis/06-runtime-startup.md) | Runtime, startup & ops: `apps/mercato`, dev orchestration, `mercato` CLI, Docker, cache, canonical env vars |
| [`analysis/07-shared-services.md`](analysis/07-shared-services.md) | Shared services & cross-cutting helpers (`packages/shared`, translations, notifications, attachments, audit_logs, configs, …) **plus the full module inventory** that feeds [`MODULES.md`](../MODULES.md) |

## How to refresh

1. Run [`scripts/sync-upstream.sh`](../scripts/sync-upstream.sh) — it clones/updates upstream into `.upstream-cache/` at a given ref (default `origin/main`), then prints the candidate commit, its date, and a `git diff --stat` summary against the pinned commit so you can see which subsystems moved.

   ```bash
   ./scripts/sync-upstream.sh              # compare pinned ↔ origin/main
   ./scripts/sync-upstream.sh v2.3.0       # compare pinned ↔ a tag/branch/SHA
   ```

2. Run the **`om-sync-upstream` skill** (`/om-sync-upstream`). It bumps the pinned commit in this file, regenerates the stale docs in `upstream/analysis/`, and flags affected specs and ported modules.

## Pinning rules

- **Ports target the pinned commit.** Never analyze or port from upstream `main` directly; drift between agents working at different commits breaks 1:1 parity claims.
- **Bump deliberately.** A pin bump is an explicit, reviewed change: update the SHA + date here, regenerate affected `upstream/analysis/` docs, then **re-review every module marked ported/verified in [`MODULES.md`](../MODULES.md)** against the new commit (re-run `om-verify-parity`; downgrade statuses that no longer hold).
- **One pin for everything.** All analysis docs, port contracts and parity reports must state the commit they were generated at; if it differs from the pin above, they are stale and must be regenerated before being trusted.
