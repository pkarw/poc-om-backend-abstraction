# Spec 09 — Technology Package Standard

> Normative for every `packages/<tech>/` directory, present and future. The `om-add-technology` skill scaffolds new packages from this spec; deviations from it are structural defects, not style choices. The canonical rules below are **identical for every technology** — consistency across packages is the point: an agent (or human) who has worked in one package must be instantly oriented in every other.

## Scope

This spec fixes the observable shape of a technology package: directory layout, README and AGENTS.md outlines, environment file, Docker/compose topology, Makefile target names, the minimal source-tree areas (API host, worker host, module area, shared runtime), the reference `health_check` module, and the queue-compatibility honesty rule. It does **not** prescribe language internals — file extensions, project files, framework choices, and naming conventions inside the source tree are tech-idiomatic (that is what `docs/stack.md` and the ADRs document).

Rationale for having a standard at all: the porting loop (spec 00) runs the *same* skills against every technology. Skills locate things by convention (`AGENTS.md` section names, `make` targets, module areas); if each package invented its own shape, every skill would need per-tech branching and drift would be unreviewable. Uniformity also makes cross-package review trivial — `diff` the outlines, not the ideas.

Terminology: MUST / SHOULD / MAY per RFC 2119. `<tech>` = the package's directory name (e.g. `python`, `dotnet`, `golang`).

## Requirements

### Package layout

- **TECHPKG-R1** — Every technology package MUST contain exactly this top-level skeleton (plus tech-idiomatic project files):

  ```
  packages/<tech>/
    README.md            # quickstart — rules in R14–R16
    AGENTS.md            # agent guide — outline in R12–R13
    docs/stack.md        # chosen stack with versions + one-line rationale each
    docs/decisions/      # ADRs, NNNN-<slug>.md
    .env.example         # canonical env vars, R4
    docker-compose.yml   # full stack, R5–R6
    Dockerfile           # multi-stage, R7
    Makefile             # identical target names, R8
    <migrations>         # tech-idiomatic location, R9
    <source tree>        # tech-idiomatic naming, R10–R11
  ```

- **TECHPKG-R2** — `docs/stack.md` MUST list every significant runtime/library choice (web framework, ORM, migration tool, validation library, Redis client, queue client, test framework) with its pinned version and a one-line rationale each. Rationale: reviewers and future agents need to know *why* a library was chosen without archaeology; version pins make parity reports reproducible.
- **TECHPKG-R3** — Architecture decisions MUST be recorded in `docs/decisions/` as ADRs named `NNNN-<slug>.md` (zero-padded, monotonically increasing) with exactly the sections: `# Title` / `Status` / `Context` / `Decision` / `Consequences`. Every "Allowed deviation" a package takes from specs 01–08, and every place it uses a better tech-native solution than the upstream TS approach, MUST have an ADR. Rationale: the compatibility philosophy (spec 00) *encourages* idiomatic divergence in internals — the ADR trail is what keeps that divergence deliberate and auditable instead of accidental.

### Environment & configuration

- **TECHPKG-R4** — `.env.example` MUST define, using **exactly the upstream names**: `DATABASE_URL`, `REDIS_URL`, `QUEUE_STRATEGY`, `QUEUE_REDIS_URL`, `JWT_SECRET`, `PORT`. Additional tech-specific variables MAY be added below a separator comment. Rationale: env-var names are a cross-package contract (AGENTS.md root rule 8; specs 03/04/05/06 bind their semantics) — a port that renames `DATABASE_URL` can never share deployment tooling or docs with the others.

### Docker & compose

- **TECHPKG-R5** — `docker-compose.yml` MUST define the services `postgres` (image `postgres:17-alpine`), `redis` (image `redis:7-alpine`), `api`, and `worker`. `docker compose up --build` MUST be the **only** command needed after clone: the compose file MUST include healthchecks (Postgres/Redis healthy before app services start) and a migration step — either the api entrypoint runs migrations before serving, or a dedicated `migrate` service that api/worker depend on. Rationale: "one command to a running stack" is the package's acceptance test for newcomers and for the `om-verify-parity` harness; hidden manual steps rot instantly.
- **TECHPKG-R6** — The compose stack MUST bring up the full topology the package claims to support: API answering on its documented port and the worker connected to Redis. Ports for Postgres/Redis MAY be published for developer convenience (this is a dev stack, not the production topology of spec 06 R41).
- **TECHPKG-R7** — `Dockerfile` MUST be multi-stage (build stage → slim runtime stage), and **api and worker MUST run from one image** differing only in the container command. Rationale: one image guarantees api and worker can never skew in dependency versions or migrations, halves build time, and mirrors how upstream ships one app that spawns its worker host.

### Makefile (identical across technologies)

- **TECHPKG-R8** — Every package MUST provide a `Makefile` with **exactly these target names** (spellings identical in every technology; underlying commands are tech-idiomatic):

  | Target | Behavior |
  |---|---|
  | `make up` | `docker compose up --build -d` |
  | `make down` | `docker compose down -v` |
  | `make dev` | run the API natively with hot reload where idiomatic |
  | `make worker` | run the queue worker natively |
  | `make migrate` | apply DB migrations natively |
  | `make test` | run tests |

  Packages MAY add extra targets; they MUST NOT rename or repurpose these six. Rationale: `make` is the lowest common denominator across Python/.NET/Go toolchains; identical verbs mean the porting skills and the root README can drive any package without a lookup table.

### Migrations

- **TECHPKG-R9** — Migrations live in the technology's idiomatic location (Alembic `alembic/versions/`, EF Core `Migrations/`, golang-migrate `migrations/`, …) and MUST be real, applied-by-tool migrations against PostgreSQL — no "create schema from model at boot". Migration *behavior* (per-module tracking tables, alphabetical module order, schema parity) is bound by specs 03 and 06; this spec only fixes that the tooling exists and `make migrate` drives it.

### Source tree

- **TECHPKG-R10** — The source tree (tech-idiomatic naming and layout) MUST contain these four areas:
  1. **API host** — serves `GET /healthz` → `200 {"status":"ok","service":"<tech>-api"}` and mounts module routers under `/api/...`. Rationale: `/healthz` is the scaffold-level liveness contract used by compose healthchecks and the root README; the `service` field disambiguates which port answered when several run side by side. (The upstream-parity health behavior of spec 06 R38 — `/` returning 200 — becomes binding when the runtime module tier is ported; `/healthz` is additive and stays.)
  2. **Worker host** — a separately runnable process that connects to Redis, registers the queue processors of all enabled modules, and logs a startup line identifying the queues it consumes.
  3. **`modules/<module_id>/` area** — one directory per ported module, mirroring Open Mercato module concepts: api handlers, data entities, validators, subscribers, workers, acl. The internal file naming is tech-idiomatic, but the concept-to-location mapping MUST be documented in AGENTS.md → Conventions (R12).
  4. **`shared/` area** — the framework runtime: config loading, db, redis, queue abstraction, module registry. Rationale: this is the port's equivalent of upstream `packages/{shared,queue,events,cache}`; keeping it separate from `modules/` preserves the module-vs-infrastructure boundary the whole architecture rests on.
- **TECHPKG-R11** — Every package MUST include **one tiny example module `health_check` wired end-to-end** as the reference pattern: one `GET /api/health_check` route registered *through the module system* (not hardcoded in the host), and one no-op worker registered through the same system. Rationale: the example module is executable documentation — `om-port-module` copies its wiring, and its presence proves the module registry, router mounting, and worker registration actually work before any real module lands.

### AGENTS.md (agent guide)

- **TECHPKG-R12** — Every package's `AGENTS.md` MUST contain exactly these H2 sections, **in this order** (same outline in every technology package):

  ```
  ## Stack
  ## Layout
  ## Conventions            (mapping table: Open Mercato TS concept → this technology equivalent)
  ## Module Porting Rules
  ## API Compatibility Rules
  ## Data Layer             (PostgreSQL + the chosen migration tool, schema-parity rules)
  ## Queues & Events        (Redis + BullMQ compatibility approach)
  ## Configuration          (env vars — same names as upstream)
  ## Commands               (the make targets + underlying raw commands)
  ## Decisions              (index of docs/decisions/)
  ```

  Rationale: `AGENTS.md` is the first file an agent reads before working in a package; a fixed outline means "where do I find the queue approach?" has the same answer everywhere, and outline diffs across packages catch missing guidance mechanically.
- **TECHPKG-R13** — The `## Conventions` section MUST contain a mapping table from Open Mercato TS concepts (route file + `metadata`, `data/entities.ts`, Zod validators, `subscribers/*`, `workers/*`, `acl.ts`, `di.ts`, `setup.ts`) to this technology's equivalents, and `## Decisions` MUST index every ADR in `docs/decisions/` with one line each.

### README.md

- **TECHPKG-R14** — The README title line MUST start with an emoji, and the file MUST contain these sections:
  1. **What is this** — 2 sentences, linking `../../README.md`.
  2. **🚀 Quickstart (Docker)** — exactly **one** command after clone.
  3. **💻 Quickstart (native)** — the absolute minimum steps: install toolchain, one dependency-install command, `make migrate`, `make dev`.
  4. **⚙️ Commands** — table of the make targets.
  5. **📁 Layout** — short tree.
  6. **🔌 Environment** — env var table.
- **TECHPKG-R15** — The README MUST stay under ~120 lines. Rationale: the README is the human on-ramp; everything deeper belongs in AGENTS.md, `docs/stack.md`, or ADRs. Long READMEs duplicate those files and drift.
- **TECHPKG-R16** — The Docker quickstart's one command MUST actually be sufficient (R5); the native quickstart MUST NOT require steps beyond those listed (if it does, fix the tooling, not the README).

### Queue / BullMQ compatibility (be honest, never overclaim)

- **TECHPKG-R17** — The contract is: **jobs enqueued by Node BullMQ must be consumable by the port and vice versa** (envelope and options per spec 04 R6–R7; verification per spec 08 R18).
- **TECHPKG-R18** — **Python** packages MUST use the **official `bullmq` PyPI package** (maintained by taskforcesh) — it provides full protocol compatibility; reimplementing it would be waste and drift.
- **TECHPKG-R19** — **.NET and Go** have no official BullMQ client. Those packages MUST ship a clean queue abstraction (an interface + a Redis implementation) and MUST write an ADR stating the compatibility strategy — vendoring BullMQ's Lua scripts, or implementing the protocol subset: waiting list, active list, job hash, events stream — **with the current implementation status clearly marked** in the ADR. Rationale: the abstraction keeps module code queue-client-agnostic so the client can be swapped for a compatible one later without touching modules; the ADR keeps the compatibility claim honest and reviewable.
- **TECHPKG-R20** — The scaffold's worker MUST at minimum run and process jobs enqueued through its **own** abstraction (proven by the `health_check` no-op worker). **Full BullMQ wire compatibility is tracked as an explicit porting task** (in `MODULES.md`/the queue ADR) until spec 08's queue-interop checks pass in both directions. A package MUST NOT state or imply wire compatibility anywhere (README, AGENTS.md, stack.md) that its ADR marks unimplemented — parity reports record the gap as `BLOCKED`, not hidden (spec 08 R20).

## Contracts

### Health endpoint (scaffold-level)

```
GET /healthz  →  200
{"status":"ok","service":"<tech>-api"}
```

Compact JSON, `content-type: application/json`. Examples: `python-api`, `dotnet-api`, `golang-api`.

### Makefile target vocabulary

`up` · `down` · `dev` · `worker` · `migrate` · `test` — reserved names, identical semantics everywhere (R8).

### ADR skeleton

```markdown
# <Title>

Status: Accepted | Proposed | Superseded by NNNN
Context: <why a decision was needed; upstream behavior at stake>
Decision: <what was chosen>
Consequences: <trade-offs; what parity checks this affects; follow-up tasks>
```

### Compose service names

`postgres` (`postgres:17-alpine`) · `redis` (`redis:7-alpine`) · `api` · `worker` (+ optional `migrate`). Healthchecks on `postgres` and `redis`; `api`/`worker` start only after healthy.

### Reference packages at time of writing

| Package | Stack (see each `docs/stack.md`) | Queue approach |
|---|---|---|
| `packages/python` | FastAPI + SQLAlchemy/Alembic | official `bullmq` PyPI client (R18) |
| `packages/dotnet` | ASP.NET Core minimal APIs + EF Core | queue abstraction + ADR (R19) |
| `packages/golang` | chi + pgx + golang-migrate | queue abstraction + ADR (R19) |

## Allowed deviations

- **Everything tech-idiomatic is free**: source-tree naming, project/solution files, dependency management, test framework, hot-reload mechanism, file extensions — provided the R1 skeleton, R8 targets, R10 areas, and R12 outline are intact.
- **Extra make targets, compose services (e.g. a Meilisearch for later modules), docs** — welcome additions; the required set is a floor, not a ceiling.
- **MUST NOT deviate on**: the six Makefile target names; the AGENTS.md H2 outline and order; the `.env.example` canonical variable names; the `/healthz` body; the compose "one command" rule; the `health_check` example module; ADR naming/sections; and the queue honesty rule (R17–R20). If a genuinely better package shape emerges, change **this spec** first and migrate every package — never fork the convention in one package.

## Verification

`om-add-technology` scaffolds to this spec; a structural conformance check (runnable for any package, and part of reviewing a new one) asserts:

1. **Skeleton** — every R1 path exists; `docs/stack.md` rows carry versions; every ADR matches the naming pattern and contains the four sections (R2–R3).
2. **Env** — `.env.example` defines the six canonical names verbatim (R4).
3. **One-command boot** — from a clean checkout, `docker compose up --build` (or `make up`) alone yields: healthy postgres/redis, migrations applied, `GET /healthz` returning the exact R10 body, `GET /api/health_check` served through the module system, and a worker startup log line (R5–R7, R10–R11).
4. **Makefile** — the six targets exist with the R8 semantics; `make down` leaves no volumes (R8).
5. **Outline parity** — AGENTS.md H2 set and order match R12 byte-for-byte across all packages; README sections and length per R14–R15 (a simple heading-diff across `packages/*/AGENTS.md` catches drift).
6. **Queue honesty** — for .NET/Go-style packages: the queue ADR exists, states the strategy and current status, and no package doc claims wire compatibility beyond it (R17–R20); the `health_check` worker processes a self-enqueued job.
