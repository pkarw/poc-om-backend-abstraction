# 🔀 Open Mercato — Backend Abstraction & Porting Lab

> Port any [Open Mercato](https://github.com/open-mercato/open-mercato) backend module to **Python**, **.NET**, **Go** (and beyond) with **1:1 API compatibility** — driven by technology-agnostic AI skills.

Open Mercato's core is TypeScript + Next.js. Users keep asking: *"can the API run on .NET? Go? Python?"* — this repo is the answer. It holds:

1. 📐 **Specs** — technology-agnostic requirement specs distilled from the upstream codebase (module system, API contracts, data layer, queues, auth, runtime).
2. 🤖 **AI porting skills** — the *same* skills port any module to any target technology, using per-technology convention docs.
3. 📦 **Technology packages** — one runnable API + worker skeleton per technology, all speaking PostgreSQL + Redis + BullMQ-compatible queues.
4. 🔭 **Upstream tracking** — a pinned upstream commit and analysis docs, refreshable as Open Mercato core evolves.

## 🗺️ Repository map

| Path | What lives there |
|---|---|
| [`specs/`](specs/) | Normative, tech-agnostic specs (`00`–`09`). Start at [`specs/00-overview.md`](specs/00-overview.md) |
| [`upstream/`](upstream/) | Pinned upstream reference: [`UPSTREAM.md`](upstream/UPSTREAM.md) + subsystem analyses in [`upstream/analysis/`](upstream/analysis/) |
| [`.claude/skills/`](.claude/skills/) | The 5 porting skills (see below) |
| [`packages/python/`](packages/python/) | 🐍 FastAPI + SQLAlchemy/Alembic + official BullMQ client |
| [`packages/dotnet/`](packages/dotnet/) | 🟣 ASP.NET Core minimal APIs + EF Core |
| [`packages/golang/`](packages/golang/) | 🐹 chi + pgx + golang-migrate |
| [`MODULES.md`](MODULES.md) | 📊 Porting tracker — module × technology status matrix |
| [`scripts/sync-upstream.sh`](scripts/sync-upstream.sh) | Refresh the upstream clone and diff against the pinned commit |
| [`AGENTS.md`](AGENTS.md) | Rules of the road for AI agents working in this repo |

## 🚀 Run an API server

Every technology package boots the same way — PostgreSQL 17, Redis 7, API host, queue worker:

```bash
# Docker (one command)
cd packages/python && make up      # 🐍  → http://localhost:8000/healthz
cd packages/dotnet && make up      # 🟣  → http://localhost:8080/healthz
cd packages/golang && make up      # 🐹  → http://localhost:8090/healthz
```

Native (no Docker) quickstarts live in each package's `README.md` — always the same `make` targets: `dev`, `worker`, `migrate`, `test`.

## 🤖 The porting loop

Technology-agnostic skills — the *same* skill drives a port to Python, .NET, Go, or any future target:

| Skill | What it does |
|---|---|
| `om-sync-upstream` | 🔄 Refresh the pinned upstream commit + regenerate stale analyses |
| `om-analyze-module` | 🔬 Distill one upstream module into a **port contract** (routes, schemas, events, queues, ACL) |
| `om-port-module` | 🛠️ Implement the contract 1:1 in a target technology package |
| `om-verify-parity` | ✅ Prove request/response, DB-schema and queue-name parity against the contract |
| `om-add-technology` | ➕ Scaffold a new `packages/<tech>/` following the [package standard](specs/09-technology-package-standard.md) |

Typical flow — port a module to two technologies **simultaneously**:

```
/om-analyze-module customers
/om-port-module customers python     # in parallel with…
/om-port-module customers dotnet
/om-verify-parity customers python
/om-verify-parity customers dotnet
```

## 🧭 Compatibility philosophy

- **Observable behavior is sacred** 🔒 — same paths, methods, status codes, JSON shapes, auth semantics, Postgres schema, queue/event names.
- **Internals are idiomatic** 🎨 — if the target language has a better solution than the TS original (validation, DI, ORM patterns), use it and record an ADR in `packages/<tech>/docs/decisions/`.
- **Everything shares infrastructure** 🧱 — PostgreSQL with real migrations, Redis, and BullMQ-compatible queues in every technology.
- **Upstream is pinned** 📌 — ports target the commit recorded in [`upstream/UPSTREAM.md`](upstream/UPSTREAM.md); bump deliberately with `om-sync-upstream`.

## 📊 Status

See [`MODULES.md`](MODULES.md) for the live module × technology matrix. Current phase: **foundation** — specs, skills and runnable skeletons are in place; module porting starts with the infrastructure tier (auth, directory, entities, query engine).
