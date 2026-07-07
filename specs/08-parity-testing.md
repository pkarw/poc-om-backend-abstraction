# Spec 08 — Parity Testing: Proving 1:1 Compatibility

> Derived from upstream commit adc9da27759e357febe9ed8d4b7182040d127349. This spec is normative for the `om-verify-parity` skill and for any hand-run parity check. It defines *how* the requirements of specs 01–07 are verified and what evidence counts.

## Scope

This spec binds the verification methodology for every ported module in every technology package:

1. **Harness setup** — how the reference (upstream Node) and the port are run side by side.
2. **Golden request/response testing** — request corpora, response comparison, and the normalization rules for volatile fields (UUIDs, timestamps, tokens, order-insensitive arrays).
3. **Schema parity** — `information_schema`-based DDL comparison.
4. **Queue interop** — BullMQ round-trips between Node and the port, in both directions.
5. **Event name parity** — declared events, subscriber ids, flags.
6. **Error envelope parity** — the error catalogue, byte-compared.
7. **Authorization matrix testing** — each route × each relevant feature grant.
8. **The parity report** — the `.ai/parity/<module>-<tech>.md` format, verdict rules, and tracker updates.

Terminology: MUST / SHOULD / MAY per RFC 2119. "Reference" = the upstream TypeScript app at the pinned commit. "Port" = the implementation under test in `packages/<tech>/`. "Module contract" = the per-module analysis in `upstream/analysis/modules/<module>.md`. A *check* is one executable probe; a *category* is a group of checks (the eight areas above).

## Requirements

### Harness & ground rules

- **PARITY-R1** — Parity MUST be measured against the reference at the pinned upstream commit (`upstream/UPSTREAM.md`), never against documentation alone. Where running the reference is infeasible for a given check, recorded golden fixtures produced *from* the reference at that commit MAY substitute, and the report MUST say which mode was used per category.
- **PARITY-R2** — Interop categories (schema, queue, cache/Redis, cross-runtime read/write) MUST run with the reference and the port attached to the **same** PostgreSQL server and the **same** Redis instance. HTTP categories MUST run both stacks against equivalently seeded databases (same init command, same `OM_INIT_*` values, same `JWT_SECRET`).
- **PARITY-R3** — Both stacks MUST be configured with identical values for every shared environment variable (`DATABASE_URL` may differ by database name for HTTP categories; `JWT_SECRET`, `QUEUE_STRATEGY=async`, `QUEUE_REDIS_URL`/`REDIS_URL`, `OM_INIT_*` MUST match). Cross-runtime credential checks (JWT minted by one stack verified by the other) depend on this.
- **PARITY-R4** — Every check MUST map to at least one requirement ID from specs 01–07 (or a per-module contract clause). Checks without a requirement anchor are advisory and MUST be reported as `INFO`, never as failures.
- **PARITY-R5** — MUST-level requirement failures fail the run. SHOULD-level requirement failures are reported as `WARN` and do not fail the run. A check that could not execute (missing infra, unimplemented dependency) is `BLOCKED` — it MUST be listed, never silently skipped, and BLOCKED MUST-level checks prevent an overall `PASS`.
- **PARITY-R6** — Checks MUST be deterministic and re-runnable: fixed seeds where randomness exists, per-run scratch tenants/queues (`parity-<runid>-…` names for scratch resources), and cleanup that leaves shared infrastructure as found.

### Golden request/response testing

- **PARITY-R7** — For every route of the ported module (from the module contract's route table), the harness MUST replay a request corpus covering at minimum: the happy path per method, each documented error branch, list-endpoint parameter variations (`page`/`pageSize` clamping, sort aliases, `ids` intersection, `withDeleted`, `filter[...]`, `cf_*`), and each hand-written-route quirk recorded in the module contract.
- **PARITY-R8** — For each corpus entry the harness MUST compare, between reference and port: HTTP status code (exact), response body (canonicalized then normalized per R10–R14), and the header set named by the module contract (at minimum `content-type` byte-exact — `application/json` with no charset for JSON — plus conditional `x-om-*` headers, `content-disposition`, rate-limit headers, and `Set-Cookie` names/attributes where the route sets cookies).
- **PARITY-R9** — JSON canonicalization before comparison: parse, re-serialize with lexicographically sorted object keys and compact separators. Array **order is significant by default** — list `items` ordering is part of the contract (sorting semantics) and MUST NOT be normalized away except where R13 applies.

#### Normalization of volatile fields

The goal of normalization is to remove *values that legitimately differ between two live systems* while still asserting their **shape**. Normalization MUST be minimal and declared; over-normalization hides real regressions.

- **PARITY-R10** — **UUIDs.** Fields documented as server-generated UUIDs (`id`, `jobId`, request ids, generated record ids) MUST be replaced by positional placeholders (`<uuid-1>`, `<uuid-2>`, …) assigned in first-occurrence order *consistently within each response*, after asserting the raw value matches the UUID format. Equal raw UUIDs MUST map to the same placeholder, so referential identity inside a response (e.g. `items[0].id` reappearing in `_meta`) is still compared. UUIDs that are fixture inputs (seeded ids sent in the request) MUST NOT be normalized — they must round-trip exactly.
- **PARITY-R11** — **Timestamps.** Fields documented as server clocks (`createdAt`, `updatedAt`, `executedAt`, `iat`/`exp`, epoch `timestamp` fields) MUST be replaced by `<ts>` after asserting (a) the format matches upstream's serialization exactly (ISO-8601 with milliseconds and `Z` for JSON dates; integer seconds for JWT claims; integer milliseconds where upstream uses epoch ms) and (b) the value falls within the test run's time window. Relative-ordering assertions (e.g. `updatedAt >= createdAt`) SHOULD be kept where the contract implies them.
- **PARITY-R12** — **Opaque tokens and derived blobs.** JWTs, refresh tokens, undo tokens, and the `x-om-operation` header MUST NOT be byte-compared. Instead the harness MUST assert structure: JWTs decode with the shared secret and their claim sets are compared (with R10/R11 applied to `sub`/`sid`/`iat`/`exp`); `x-om-operation` decodes (per its documented codec) to a JSON object whose key set and normalized values are compared; refresh/undo tokens match their documented length/alphabet.
- **PARITY-R13** — **Order-insensitive arrays.** Only arrays the specs or module contract declare unordered MAY be compared as multisets (sort canonicalized elements, then compare). The standing unordered set is: RBAC feature lists and `granted` arrays, `requiredFeatures` in 403 bodies, role-ACL feature sets, tag lists, `_meta.enrichedBy`/`enricherErrors`, and validation-issue `details` arrays (compared as multisets of issues keyed by `path`+`code`). Everything else stays ordered.
- **PARITY-R14** — Per-module contracts MAY extend the volatile-field list (e.g. externally-assigned ids), but every extension MUST be listed in the parity report's normalization table. The harness MUST fail a corpus entry when a field *not* on the volatile list differs, even if the difference "looks volatile".

### Schema parity (information_schema)

- **PARITY-R15** — For every table owned by the ported module (per its contract) plus the shared contract tables it touches (spec 03 Contracts) and its `mikro_orm_migrations_<module>` tracking table, the harness MUST run the port's migrations on a clean database and the reference's migrations on another clean database **on the same Postgres server version**, then diff:
  - `information_schema.tables` — table presence;
  - `information_schema.columns` — column names, data types, nullability, defaults, `is_generated`/`generation_expression` (the `organization_id_coalesced` stored generated column MUST match);
  - `pg_indexes` — index names and definitions (unique/partial indexes byte-relevant: upstream code targets them in `ON CONFLICT`);
  - table/check/FK constraint names via `information_schema.table_constraints` where the contract fixes them.
- **PARITY-R16** — The diff MUST be empty for contract tables. Default expressions MUST be compared as rendered by the same Postgres server (`pg_get_expr`), eliminating formatting noise; a port MUST NOT paper over a semantic default difference (e.g. `now()` vs a client-side default — a missing DB default is a finding, per spec 03 R2/R4 the split between DB-side uuid defaults and app-side timestamps is contractual.)
- **PARITY-R17** — The harness MUST additionally verify migration bookkeeping: after apply, each module's tracking table exists, contains one row per applied migration, and recorded names match the applied files (spec 03 R10–R12). A shared-DB smoke test SHOULD apply reference migrations first and then run the port's migrate command, asserting it reports "no pending migrations" rather than re-applying or diffing.

### Queue interop (BullMQ)

- **PARITY-R18** — For every queue name the module uses (from its contract; reserved names per spec 04), the harness MUST run both directions on a shared Redis:
  1. **Node → port:** enqueue via upstream Node BullMQ (reference code path or a BullMQ ^5 producer using the spec-04 envelope); assert the port's worker consumes it, receives the full envelope plus ctx `{jobId, attemptNumber, queueName}`, and Redis is left per the retention options.
  2. **Port → Node:** enqueue via the port; inspect the raw `bull:<queue>:<jobId>` hash and assert job name == `data.id` == UUID v4, `data` is exactly `{id, payload, createdAt}` with ISO-8601 `createdAt`, and `opts` equal `{attempts:3, backoff:{type:"exponential",delay:1000}, removeOnComplete:true, removeOnFail:1000}` (+ `delay` only when requested); then assert an upstream Node BullMQ worker consumes it to completion.
- **PARITY-R19** — A retry-semantics probe MUST run at least once per port: a deliberately failing handler shows `attemptsMade` progressing with exponential backoff and the job landing in the failed set per `removeOnFail`.
- **PARITY-R20** — When the technology's queue implementation is not yet BullMQ wire-compatible (a legitimate interim state for .NET/Go per spec 09), queue-interop checks MUST be recorded as `BLOCKED` with a pointer to the package's queue ADR — never as passes, never omitted. The overall verdict rule (R5) then caps the report below full `PASS` for modules that use queues.
- **PARITY-R21** — Scheduler-using modules MUST additionally verify repeatable-job mirroring (`bull:scheduler-execution:repeat:*` name/repeat-opts/retention per spec 04 R33) and the `events` queue payload shape `{event, payload, options}` for persistent emits (spec 04 R25).

### Event name parity

- **PARITY-R22** — The harness MUST enumerate, from both stacks, the module's declared events (`{id, clientBroadcast?, excludeFromTriggers?, …}` — e.g. via `GET /api/events` and the port's equivalent registry dump) and diff: event id sets MUST be identical; `clientBroadcast` flags MUST match (they drive the SSE bridge); persistent/non-persistent classification of emissions MUST match observed queue contents.
- **PARITY-R23** — Subscriber and worker registrations MUST be enumerated and diffed: subscriber ids (`<module>:<path>:<basename>` derivation), event patterns, `persistent`/`sync` flags; worker ids, queue names, concurrency defaults (spec 01 R28–R29).
- **PARITY-R24** — An emission probe MUST verify runtime naming: trigger a representative write through the port and assert the CRUD event `<module>.<entity>.created|updated|deleted` and `query_index.upsert_one` reach a bus tap with the exact payload key sets of spec 03 Contracts.

### Error envelope parity

- **PARITY-R25** — Every error row in the module contract's error catalogue — seeded from the spec 02 Contracts table plus module-specific errors — MUST be triggered at least once against both stacks and byte-compared after R9 canonicalization (error bodies contain no volatile fields except where documented, e.g. the optimistic-lock timestamps which follow R11).
- **PARITY-R26** — The catalogue MUST include the deliberately confusable pairs as distinct checks: dispatcher `404 {"error":"Not Found"}` vs factory `404 {"error":"Not found"}`; dispatcher `401 {"error":"Unauthorized"}` vs endpoint-level `401 {"ok":false,"error":"…"}` shapes; `422 "Operation blocked"` vs `"Operation blocked by guard"`; the uniform anti-oracle 401 across all credential-failure branches (spec 05 R8, R48 — identical bodies asserted across branches).
- **PARITY-R27** — 5xx checks MUST assert the sanitized envelope (`500 {"error":"Internal server error","message":"Something went wrong. Please try again later."}`) and that no stack trace or internal message leaks in any tested error response.

### Authorization matrix testing

- **PARITY-R28** — For each route × method of the module, the harness MUST derive the expected guard behavior from route metadata (`requireAuth`, `requireFeatures`) and execute at minimum this probe matrix against both stacks, comparing status + body exactly:

  | Probe | Expected (unless route metadata says otherwise) |
  |---|---|
  | Anonymous | `401 {"error":"Unauthorized"}`; `200`-class only when `requireAuth: false` |
  | Invalid/expired JWT | 401 + `Set-Cookie` expiring `auth_token`, `session_token` |
  | Authenticated, **no** required features | `403 {"error":"Forbidden","requiredFeatures":[…]}` echoing the full list |
  | For each required feature `f`: all features **except** `f` | 403 (all-features semantics — one probe per feature) |
  | Exact feature grant | 2xx/route success |
  | Wildcard grant (`<module>.*`) | Same success (wildcard covers module prefix) |
  | Superadmin subject | Success (spec 05 R31) |
  | Customer-type JWT on staff route | Treated as unauthenticated → 401 |
  | API-key subject with equivalent role grants | Same outcome as the user subject |
  | Tenant pollution: repeated `?tenantId=` + body `tenantId` for a foreign tenant | `403 {"error":"Not authorized to target this tenant."}`, handler not invoked |
  | `requireRoles`-only metadata | MUST pass (deprecated metadata authorizes nothing) |

- **PARITY-R29** — Feature grants for the matrix MUST be provisioned through the real ACL tables (role_acls/user_acls) — not by stubbing the RBAC service — so ACL resolution, wildcard matching, and the RBAC cache are exercised. After a grant change, the harness MUST assert cache invalidation took effect (fresh verdict without waiting out the TTL).
- **PARITY-R30** — Multi-tenant data-visibility probes MUST accompany the matrix for CRUD routes: cross-tenant record ids yield `404 {"error":"Not found"}`; empty org scope yields the 200-empty list envelope for GET and `403 {"error":"Forbidden"}` for mutations (spec 02 R27/R32, spec 03 R21/R26–R27).

### The parity report

- **PARITY-R31** — Every `om-verify-parity` run MUST write (create or overwrite) the report `.ai/parity/<module>-<tech>.md` in the repository root, in the format given in Contracts. One file per module × technology; history lives in git.
- **PARITY-R32** — The report MUST contain: the run metadata block (module, technology, upstream pin, port revision, date, harness mode per R1); the category summary table; a per-requirement result table covering every requirement ID the module contract binds (`PASS`/`FAIL`/`WARN`/`BLOCKED`/`N/A` + one-line evidence); a failures section with reproduction (request or command, expected, actual, normalized diff); the normalization extensions used (R14); and references to ADRs justifying accepted deviations.
- **PARITY-R33** — Verdict rules: `PASS` = every MUST-level check passed and none BLOCKED; `PASS-WITH-WARNINGS` = only SHOULD-level findings; `BLOCKED` = MUST-level checks could not run (with the blocking dependency named); `FAIL` = any MUST-level check failed. The verdict MUST appear on the report's first line after the title.
- **PARITY-R34** — On `PASS`/`PASS-WITH-WARNINGS`, the run MUST update the module's cell in `MODULES.md` to 🧪 (parity-verified); on `FAIL` it MUST leave the tracker at ✅ or 🚧 as appropriate and MUST NOT mark 🧪. A stale report (older than the current upstream pin or port revision) confers no status.
- **PARITY-R35** — Reports MUST be honest about scope: checks derived from fixtures rather than a live reference (R1), BLOCKED queue interop (R20), and any category run partially MUST be visible in the summary table. A report MUST NOT present a subset run as a full verification.

## Contracts

### Report format `.ai/parity/<module>-<tech>.md`

````markdown
# Parity report — <module> × <tech>

**Verdict: PASS | PASS-WITH-WARNINGS | BLOCKED | FAIL**

| | |
|---|---|
| Module | <module> |
| Technology | packages/<tech> |
| Upstream pin | adc9da27… (upstream/UPSTREAM.md) |
| Port revision | <git sha of this repo> |
| Date | YYYY-MM-DD |
| Reference mode | live | golden-fixtures (per category below) |

## Summary

| Category | Checks | Pass | Warn | Fail | Blocked |
|---|---|---|---|---|---|
| Golden request/response | … | … | … | … | … |
| Schema parity | … | … | … | … | … |
| Queue interop | … | … | … | … | … |
| Event name parity | … | … | … | … | … |
| Error envelopes | … | … | … | … | … |
| Authz matrix | … | … | … | … | … |

## Requirement results

| Requirement | Result | Evidence |
|---|---|---|
| APIHTTP-R19 | PASS | list envelope byte-equal across 14 corpus entries |
| EVENTSQUEUES-R6 | BLOCKED | no BullMQ wire client yet — see packages/go/docs/decisions/0002-queue-compat.md |
| … | … | … |

## Failures

### <requirement id> — <one-line title>
- Probe: `<method> <path>` (or command)
- Expected: <status/body or artifact>
- Actual: <status/body or artifact>
- Normalized diff:
  ```diff
  …
  ```

## Normalization extensions
| Field | Rule | Justification |
|---|---|---|

## Deviations & ADRs
- <spec id / requirement> → packages/<tech>/docs/decisions/NNNN-….md
````

Sections with no content keep their heading with `None.` (machine-checkable completeness).

### Result vocabulary

`PASS` · `WARN` (SHOULD-level miss) · `FAIL` (MUST-level miss) · `BLOCKED` (could not execute; MUST-level blocks overall PASS) · `N/A` (requirement not bound by this module, justification required) · `INFO` (advisory, no requirement anchor).

### Standing volatile-field table (baseline for R10–R14)

| Field class | Detection | Normalization |
|---|---|---|
| Server-generated UUID | contract-listed field + UUID regex | `<uuid-N>` positional, identity-preserving |
| ISO-8601 timestamp | contract-listed field + format check | `<ts>` after format + window assert |
| Epoch ms/s | contract-listed field + integer + window | `<ts-ms>` / `<ts-s>` |
| JWT | route contract | decode-and-compare claims |
| `x-om-operation` header | header presence | decode-and-compare JSON keys/values |
| Refresh/undo/API-key secrets | route contract | length + alphabet assert only |
| Unordered arrays | R13 list + contract extensions | multiset compare |

## Allowed deviations

- **Harness implementation is free** — any language/tooling may implement the checks; the probes, comparison semantics, and report format are what is fixed. A port MAY ship its parity harness inside `packages/<tech>/` (as `make test` targets) provided it emits the R31 report.
- **Golden fixtures vs live reference** (R1): fixtures are acceptable where noted, but MUST be regenerated whenever the upstream pin moves (`om-sync-upstream` invalidates them).
- **Extra checks welcome** — additional probes beyond a spec's Verification section are encouraged; they report as `INFO` unless anchored to a requirement.
- **MUST NOT**: weaken a comparison (adding volatile fields without R14 documentation, relaxing byte-comparison to "similar"), skip BLOCKED checks silently, or mark 🧪 in `MODULES.md` without a current `PASS`-grade report.

## Relationship to specs 01–07

Each of specs 01–07 ends with a Verification section describing *what* to probe for its requirements; this spec defines *how* those probes are executed, normalized, scored, and reported. Where a spec's Verification section and this spec differ in mechanics, this spec wins; where a spec defines an exact expected value, that spec wins.
