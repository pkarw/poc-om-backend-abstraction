# Spec 11 — Testbench: Running Upstream Open Mercato Against a Port

> Normative for the **testbench** — the harness that runs a *real* upstream Open Mercato deployment against a technology port so a human can log into OM's UI and have ported `/api/*` routes served by the port. Tech-agnostic: it constrains any port, with `testbench/` (OM ↔ `packages/dotnet`) as the reference implementation.

## Scope

This spec fixes the observable shape of a testbench: a shared PostgreSQL database owned by upstream, a reverse-proxy seam that routes only the ported modules' `/api/*` to the port, the port's migrations-off requirement, and the secret-parity requirement (JWT signing secret + email lookup-hash pepper) that makes authentication interchangeable between the two runtimes. It does **not** prescribe the proxy software, compose topology internals, or the port's language — only the seams that must hold for the two runtimes to co-serve one browser session.

Rationale: parity specs 05/08 prove a port is 1:1 in isolation. The testbench proves it *in situ* — that the real upstream frontend, its server components, and its database cannot tell the ported `/api/*` handlers from the originals. It is the highest-fidelity acceptance test short of replacing upstream outright.

Terminology: MUST / SHOULD / MAY per RFC 2119. **upstream** = the pinned TypeScript/Next.js Open Mercato deployment; **port** = a `packages/<tech>/` runtime; **ported set** = the modules with a ✅/🧪 status for that tech in [`../MODULES.md`](../MODULES.md).

## Requirements

### Shared database ownership

- **TESTBENCH-R1** — Upstream and the port MUST share **one** PostgreSQL database. Upstream **owns the schema**: it runs all migrations (`mercato migrate`/equivalent) and seeds baseline data (`mercato init` → tenant, org, roles, users). Rationale: two migration owners on one schema race and drift; a single owner keeps the DDL authoritative and lets the port read/write the exact tables upstream created.
- **TESTBENCH-R2** — The port MUST run **migrations-off** against the shared schema, gated by an explicit env flag (`OM_SKIP_MIGRATIONS=1` in the reference). It MUST NOT create, alter, or drop tables it did not create. Its ported modules read and write the same `auth` / `directory` / `dashboards` / … tables upstream migrated. Rationale: the port's own migrations would collide with upstream's ownership (R1); migrations-off makes the port a pure API layer over the shared schema.
- **TESTBENCH-R3** — Redis MAY be shared as well (cache, queues, sessions). If shared, key layouts remain contract per specs 04/05 — no divergence introduced by co-tenancy.

### The reverse-proxy seam

- **TESTBENCH-R4** — A reverse proxy MUST sit in front of both runtimes on a single published port and route by path prefix: the **ported set's** `/api/<module>/*` routes to the port, and **everything else** — all other `/api/*`, the Next.js UI, static assets, server components — to upstream. Rationale: upstream's frontend uses same-origin `/api/*`; a single origin proxy is the only way to serve a browser session partly from each runtime without CORS or cookie-domain breakage.
- **TESTBENCH-R5** — The proxy's ported-route matcher MUST be **driven by the ported-module list**, not hand-maintained. The reference keeps `ported-modules.txt` (one module id per line) and `gen-proxy.sh`, which regenerates the proxy config's route matcher. Rationale: the seam must stay in lockstep with `MODULES.md`; a generated matcher makes "port another module → serve it from the port" a two-line change and prevents the proxy from claiming modules the port cannot yet serve (R9).

### Secret parity (interchangeable auth)

- **TESTBENCH-R6** — Both runtimes MUST share the **same `JWT_SECRET`**. A JWT/session token issued by either MUST verify in the other (HS256, same `iss`/`aud` per spec 05). Rationale: upstream server components verify the `auth_token` JWT via the shared secret with no DB hit; a user who logs in through a port-served `/api/auth/login` must receive a token upstream's `/backend` accepts, and vice versa.
- **TESTBENCH-R7** — Both runtimes MUST share the **same email lookup-hash pepper** (`LOOKUP_HASH_PEPPER` in the reference) so the deterministic email→lookup-hash used to find a user by email matches byte-for-byte across runtimes. Rationale: login resolves a user by hashed email; a mismatched pepper means the port cannot find upstream-seeded users (and vice versa) even though the row exists.
- **TESTBENCH-R8** — Where upstream and a port choose different **at-rest encryption** schemes for reversible fields (e.g. per-tenant DEKs vs. a single app-key AEAD), decrypting the *other* runtime's ciphertext for display is a **tracked parity item**, not a testbench requirement. The deterministic lookup hash (R7) is the only cross-runtime cryptographic dependency login needs. Rationale: login and RBAC ride on the lookup hash and the JWT secret; reversible-field display parity is a separate, module-level concern that must not be silently claimed as working.

### Honest status

- **TESTBENCH-R9** — The proxy MUST route to the port **only** modules in the ported set (R5). Non-ported `/api/*` stays with upstream. A testbench MUST NOT route a module's routes to a port that has not ported it. Rationale: routing an unported module to the port yields 404s or wrong behavior that looks like a parity failure; the seam must reflect reality.
- **TESTBENCH-R10** — The testbench's docs MUST state, honestly, what is **served by the port and verified** versus what is **placeholder**. In particular, a ported dashboard/aggregation module renders its shell and layout from the port, but **widget data sourced from not-yet-ported modules is placeholder** (empty-state) until those source modules are ported. A testbench MUST NOT imply data it cannot yet produce. Rationale: the compatibility philosophy's "honesty over claims" (spec 00) applies doubly here — the demo is persuasive, so its limits must be explicit.
- **TESTBENCH-R11** — The port side of the testbench MUST be reproducible from this repository alone (compose brings up postgres/redis + the port's api/worker + the proxy, migrations-off against a shared schema). The **upstream image** is supplied by the user from their own Open Mercato checkout — this repo does not vendor or build upstream. Rationale: this repo pins and analyzes upstream but does not host its source; the testbench proves the port's half and documents the one external artifact the user provides.

## Contracts

### Proxy routing (reference: Caddy on `:8088`)

```
/api/<ported-module>/*   → port        (e.g. /api/auth/*, /api/directory/*, /api/dashboards/*)
everything else          → upstream    (UI, server components, all other /api/*)
```

The ported-module set is the ✅/🧪 `<tech>` column of `MODULES.md`, materialized in `ported-modules.txt` and compiled into the proxy config by `gen-proxy.sh`.

### Shared configuration (identical values in both runtimes)

| Variable | Purpose | Requirement |
|---|---|---|
| `DATABASE_URL` | shared Postgres | same DB; upstream owns schema (R1) |
| `JWT_SECRET` | HS256 token signing | identical → tokens interchangeable (R6) |
| `LOOKUP_HASH_PEPPER` | email→lookup-hash pepper | identical → user lookup matches (R7) |
| `OM_SKIP_MIGRATIONS` | port migrations-off flag | set on the port only (R2) |

### Verified reference flow

`make up` upstream image built → compose up (postgres, redis, upstream, port api+worker, proxy) → open the proxy port → log in as an upstream-seeded user (`superadmin@acme.com` / `secret`) → login, org switcher, and the dashboard layout are served by the port through the proxy; a smoke script re-checks the ported seam.

## Allowed deviations

- **Proxy software, compose service names, published port** are free (reference uses Caddy on `:8088`; any single-origin reverse proxy works).
- **Redis sharing** (R3) is optional; a testbench MAY give the port its own Redis if no cross-runtime queue/cache/session interop is exercised.
- **MUST NOT deviate on**: single shared Postgres with upstream owning the schema (R1); the port running migrations-off (R2); routing only the ported set to the port (R4, R9); shared `JWT_SECRET` + lookup-hash pepper (R6–R7); and the honest-status rule for placeholder data (R10).

## Verification

A testbench is conformant when, against a shared schema upstream migrated and seeded:

1. **Shared schema** — the port boots migrations-off (R2) and its ported modules read/write upstream-created tables; no port-authored DDL runs.
2. **Proxy seam** — every ported-set `/api/<module>/*` is answered by the port; a non-ported `/api/*` is answered by upstream; the matcher matches `ported-modules.txt` (R4–R5, R9).
3. **Interchangeable auth** — a login served by the port issues a token upstream's server components accept (R6), and the port finds upstream-seeded users by email (R7) — verified by logging into the real UI through the proxy and reaching a port-served page (dashboard layout, org switcher).
4. **Honest status** — the testbench docs distinguish port-served/verified from placeholder; no unported module is routed to the port and no unbacked widget data is claimed (R9–R10).
5. **Reproducibility** — the port half comes up from this repo alone; only the upstream image is user-supplied (R11).
</content>
</invoke>
