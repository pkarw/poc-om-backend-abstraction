# Customers module — testbench integration result

Ran the ported .NET **customers** (CRM) module against a **real Open Mercato** instance
sharing one Postgres (OM owns + migrates + seeds the schema; .NET runs migrations-off and
serves the ported `/api/*` through the Caddy proxy).

## ✅ What the integration test proved

- **Schema parity**: OM migrated all 25 `customer_*` tables; the .NET port reads/writes the
  same tables with no migration of its own (`OM_SKIP_MIGRATIONS=1`).
- **Wiring + auth**: login is served by .NET; every customers endpoint answers `200` through
  the proxy — `/api/customers/{people,companies,deals,deals/summary,pipelines,interactions,
  dashboard/widgets/new-customers,dictionaries/status}`.
- **Counts**: `GET /api/customers/people` → `total: 6`, companies `3`, deals `6` — i.e. the
  .NET queries see exactly the rows OM seeded.
- **Writes land in the shared DB**: `POST /api/customers/people` via .NET → `201`, and the row
  is present when Postgres is queried directly (OM and .NET share it). The new record is also
  picked up by the .NET query-index custom-field filter.

## ⚠️ Known gap — field encryption parity (blocks reading OM-written PII)

Open Mercato encrypts customer PII at rest (`customer_entities.display_name`,
`customer_people.first_name`, primary emails, …) with **per-tenant DEKs**: a random 32-byte key
per tenant, wrapped by the master key (`TENANT_DATA_ENCRYPTION_KEY`) and stored in the
`encryption_maps` table (`iv:ct:tag:v1` AES-256-GCM payloads).

The .NET port currently encrypts with a single **app-level key** (SHA-256 of `JWT_SECRET`) — a
documented ADR deviation. Consequences on the **shared** DB:

- **Login works** — the email **lookup hash** matches (shared `LOOKUP_HASH_PEPPER`), and bcrypt
  password verification is standard. This is why auth is fully interchangeable.
- **List item content does not** — `GET /api/customers/people` returns the correct `total` but
  empty/garbled item bodies, because the .NET port cannot decrypt values OM wrote with the
  tenant DEK (and vice-versa). `entity_indexes` is likewise populated by OM under its own
  document shape.

**This is the remaining work to make the customers content fully interoperable on a shared DB:**
port Open Mercato's tenant-DEK encryption (`packages/shared/src/lib/encryption/tenantData
EncryptionService.ts` + the local-KMS DEK wrap/unwrap) into the .NET `entities` module so it
reads/writes the same `encryption_maps`-wrapped DEKs and the `iv:ct:tag:v1` format. The
`encryption_maps` table is already ported byte-exact; only the DEK derivation/unwrap + the
AES-GCM payload format need to match.

## ✅ Definitive "it works" proof — standalone (.NET-owned data)

Against a .NET-owned database (the port both writes and indexes), the customers CRM is fully
functional end-to-end (223 unit tests + live):

```
POST /api/customers/people {"firstName":"Dana","cf_buying_role":"champion"}  → 201
GET  /api/customers/people/{id}   → displayName + buying_role (bare cf key) + newsletter_opt_in
GET  /api/customers/people?cf_buying_role=champion  → filtered via the query index
POST /api/customers/deals {...,"cf_competitive_risk":"high"}  → 201
GET  /api/customers/deals?cf_competitive_risk=high  → filtered via the query index
```

All writes go through the **command bus** (execute/undo/redo + `action_logs`); all CRUD uses the
**CRUD factory**; custom fields use the **entities** module (cf_ on write / bare on read); lists
filter/sort on custom fields through the **query_index** module — exactly the framework the port
was built to mirror.
