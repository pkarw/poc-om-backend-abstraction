# Porting skills

Five **technology-agnostic** Claude Code skills drive every Open Mercato backend port. The same skill ports a module to Python, .NET, Go, or any future target: the target technology is an argument, and all tech-specific conventions live in `packages/<tech>/AGENTS.md` — never in the skills.

| Skill | Args | What it does |
|---|---|---|
| [`om-sync-upstream`](om-sync-upstream/SKILL.md) | `[ref]` | Bump the pinned upstream commit in `upstream/UPSTREAM.md`, regenerate the affected `upstream/analysis/*.md` docs (one subagent per subsystem), flag possibly-stale `specs/*.md` requirement IDs, and mark ported modules in `MODULES.md` that upstream changed. |
| [`om-analyze-module`](om-analyze-module/SKILL.md) | `<module-id>` | Distill one upstream module into its **port contract** at `upstream/analysis/modules/<module-id>.md`: every route (method/path/auth/features/schemas/status codes), entity (exact tables/columns), event, queue/worker, ACL feature, dependency — plus a porting checklist. |
| [`om-port-module`](om-port-module/SKILL.md) | `<module-id> <tech>` | Implement the contract 1:1 in `packages/<tech>/`: identical API surface, Postgres schema (real migrations), event/queue names; idiomatic internals with deviations recorded as ADRs; tests; `MODULES.md` updated. Fans out entities+migrations / routes / workers+subscribers to parallel subagents behind a shared plan. |
| [`om-verify-parity`](om-verify-parity/SKILL.md) | `<module-id> <tech> [--against <url>]` | Black-box compatibility audit: derive probes from the contract (happy path, validation, authz, tenant pollution, 404s), run the port via `make up`, diff normalized responses (optionally against a live TS instance), check `information_schema` and `bull:*` queue-name parity, and write PASS/FAIL to `.ai/parity/<module-id>-<tech>.md`. |
| [`om-add-technology`](om-add-technology/SKILL.md) | `<tech> <stack hints>` | Scaffold `packages/<tech>/` per `specs/09-technology-package-standard.md`: standard layout, AGENTS.md, runtime/ORM/queue ADRs, docker-compose + standard Makefile targets, `/healthz` + example `health_check` module — verified to boot before reporting success. |

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
