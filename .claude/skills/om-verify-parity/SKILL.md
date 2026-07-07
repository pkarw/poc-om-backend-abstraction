---
name: om-verify-parity
description: Prove a ported module is 1:1 compatible with upstream. Use after om-port-module, or to re-check a port after upstream/spec changes. Args: <module-id> <tech> [--against <reference-url>] — module id, technology package, and optionally the base URL of a RUNNING upstream TypeScript instance to diff live responses against. Writes a PASS/FAIL report to .ai/parity/<module-id>-<tech>.md.
---

# om-verify-parity <module-id> <tech> [--against <reference-url>]

Execute a black-box compatibility audit of the port against its contract: HTTP behavior, Postgres schema, and queue/event names. The port passes only when its observable output is indistinguishable from the contract (and, when given, from the live upstream reference).

## Ground rules

- The contract `upstream/analysis/modules/<module-id>.md` is the assertion source; the spec Verification sections (`specs/01`–`05`, each ends with a `## Verification` chapter) define *how* to probe cross-cutting behavior. Derive checks from those — do not invent expectations.
- Test the running system over HTTP/psql/redis — never by reading the port's source and declaring it correct.
- Report honestly: a FAIL is a FAIL. Never mark a requirement PASS that you did not execute; unexecutable checks are reported as SKIPPED with a reason.

## Procedure

### 1. Load inputs and derive the test matrix

1. Read the contract; confirm it is not flagged stale. Read `MODULES.md` for current status. Read `packages/<tech>/AGENTS.md` for ports, seed/init commands, and test credentials.
2. Derive the test matrix. **Fan out one subagent per contract route** (in parallel) to write test cases; each returns a machine-executable case list (method, path, headers, body, expected status, expected body shape with volatile-field markers). Minimum per route:
   - **Happy path** — valid request; assert exact status and body shape (list envelope `{items,total,page,pageSize,totalPages}` for factory GETs, exact mutation bodies for POST/PUT/DELETE).
   - **Validation errors** — one probe per constrained field from the contract's request field table (missing required, wrong type, out-of-range), plus malformed JSON ⇒ 400 (never 500); assert exact error envelope.
   - **Authz failures** — anonymous ⇒ `401 {"error":"Unauthorized"}` (unless `requireAuth:false`); authenticated user *without* the route's `requireFeatures` ⇒ `403 {"error":"Forbidden","requiredFeatures":[...]}` with the exact feature list; tenant-pollution probe (foreign `?tenantId=` and body `tenantId`) ⇒ guard failure without handler effects.
   - **Not found** — unknown id / undeclared method ⇒ 404 with the contract's exact body (mind the `"Not Found"` vs `"Not found"` casing distinction from `specs/02-api-compatibility.md`).
   - Plus route-specific cases the contract calls out (pagination clamping `pageSize=999 ⇒ 100`, sorting aliases, `withDeleted`, exports, rate limits).
3. Add non-HTTP checks: DB schema (every contract entity), queue names (every contract worker), event subscriber registration if the package exposes it.

### 2. Boot the port

1. From `packages/<tech>/`: `make up`; wait for `/healthz` to answer 200.
2. Apply migrations and seed: `make migrate`, then the package's init/seed target (superadmin from `OM_INIT_SUPERADMIN_EMAIL`/`OM_INIT_SUPERADMIN_PASSWORD`). Create the fixtures the matrix needs: a tenant, a user with the module's features, a user *without* them, and a foreign tenant for pollution probes (use the package CLI or direct API calls).
3. Obtain tokens via `POST /api/auth/login` (or the package's documented auth path) for both users.

### 3. Execute against the port

Run the matrix (scripted — write a small runner in the scratchpad; curl or an HTTP lib). For each case record: status, headers of interest (`content-type`, `x-om-*`, `Retry-After`, `X-RateLimit-*`, `set-cookie` on auth failures), and body. **Normalize volatile fields before comparison**: UUIDs/ids, timestamps (`createdAt`/`updatedAt`/ISO strings), JWT/token values, request ids — replace with type-tagged placeholders (`<uuid>`, `<iso-datetime>`), then compare canonical JSON (sorted keys) against the expected shape. Item ordering inside `items` IS significant — assert it.

### 4. Execute against the live reference (only with `--against`)

If a reference URL was given: repeat the same matrix against it (same fixtures created the same way, or the package's documented shared seed), normalize both sides identically, and diff port-response vs reference-response per case. A shape/status/header divergence is a FAIL even if the port matches the written contract — then also flag the contract as possibly wrong. Skip destructive cases against a reference you do not own; mark them SKIPPED.

### 5. Database schema parity

Dump the port's schema for every contract table via `psql` on the package's `DATABASE_URL`:

```sql
SELECT table_name, column_name, data_type, is_nullable, column_default
FROM information_schema.columns WHERE table_name IN (<contract tables>) ORDER BY table_name, ordinal_position;
```

plus indexes (`pg_indexes`) and FKs (`information_schema.table_constraints`/`key_column_usage`). Compare against the contract's Entities section: exact table names, column names, compatible Postgres types, matching nullability/defaults/uniques/soft-delete/tenancy columns. Any name mismatch is a FAIL (shared-database interop).

### 6. Queue & event parity

1. Trigger at least one enqueue per contract queue (via the route/subscriber that produces it, or the package CLI).
2. Inspect Redis: `redis-cli --scan --pattern 'bull:*'` — every contract queue must appear as `bull:<queueName>:*` with **no custom prefix** (EVENTSQUEUES-R4); pull one job hash and assert the envelope `{id,payload,createdAt}` with BullMQ job name == envelope id and the standard job options (attempts 3, exponential backoff 1000, removeOnComplete true, removeOnFail 1000) per `specs/04-events-and-queues.md`.
3. Verify event names emitted (via the package's event log/bus inspection or persistent-event table) match the contract's Events section verbatim.

### 7. Write the report

Write `.ai/parity/<module-id>-<tech>.md` (create dirs as needed):

```markdown
# Parity report — <module-id> × <tech>
> Contract: upstream/analysis/modules/<module-id>.md @ <pinned SHA>. Run: <date>. Reference: <url|none>.

## Verdict: PASS | FAIL (<n> failures, <n> warnings, <n> skipped)

## HTTP — table: case id | route | probe | expected | actual | PASS/FAIL/SKIP
## Schema — table per entity: check | expected | actual | PASS/FAIL
## Queues & events — table: name | check | PASS/FAIL
## Failures — one subsection per FAIL: exact request, expected vs actual diff, suspected cause, fix pointer.
## Warnings — SHOULD-level spec items not met (report, don't fail).
```

MUST-level contract/spec items ⇒ PASS/FAIL; SHOULD-level ⇒ warning only (mirrors the spec Verification convention).

### 8. Track and clean up

1. `MODULES.md`: on full PASS set `<module-id>` × `<tech>` → 🧪 (parity verified); on FAIL keep ✅/🚧 and link the report.
2. `make down` in the package.

### 9. Report

Return: verdict, counts (pass/fail/warn/skip), report path, and — on FAIL — the top failures with one-line causes and whether the bug is in the port, the contract, or the spec.
