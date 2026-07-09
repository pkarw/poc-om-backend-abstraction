# Customers module — testbench integration result

Runs the ported .NET **customers** (CRM) module against a **real Open Mercato** instance sharing one
Postgres. **Open Mercato migrates the schema only; the .NET port seeds it and serves the ported
`/api/*`.** This is the key change from the earlier run (where OM's `mercato init` seeded the data and
its per-tenant-DEK-encrypted PII was undecryptable by the port).

## How the data gets there (per-module seeding, on the .NET side)

Seeding mirrors Open Mercato's own API: each module owns its setup (`IModule.OnTenantCreatedAsync` /
`SeedDefaultsAsync` / `SeedExamplesAsync`, the port of upstream `setup.ts` `ModuleSetupConfig`). On
boot the .NET API:

1. runs `InitialTenantSeeder` — the port of core `setupInitialTenant` (Acme tenant/org, roles, ACLs,
   `superadmin@acme.com`/`secret` + admin/employee users);
2. runs `ModuleSeedRunner`, which iterates every registered module in dependency order and calls its
   setup hooks per `(tenant, organization)` scope — exactly like `mercato init`'s per-module loops.

In the testbench this runs even though migrations are off: `OM_SKIP_MIGRATIONS=1` **and**
`OM_SEED_ON_BOOT=1`. The API waits for OM's schema (`om-app` healthcheck + a table probe), then seeds.
OM's `om-app` command is migrate-only (`yarn db:migrate`); `mercato init` never runs.

Per-module data seeded: currencies (10, USD base), dashboard role-widget defaults, and the full
customers set — dictionary defaults, the **Default Pipeline (+8 stages)**, the 5 CE field sets, and the
example dataset (**3 companies, 6 people, 6 deals** with company/person links).

## ✅ What the integration test proves (`./integration-customers.sh`)

- **Real OM is the frontend**: `GET /` and `GET /login` (non-ported) → `200`, served by `om-app`.
- **The ported `/api/customers/*` is served by .NET** through the Caddy proxy, authed by a .NET-issued
  JWT (login is .NET; shared `JWT_SECRET` + `LOOKUP_HASH_PEPPER`).
- **Counts match the seeded dataset**: `people` `total: 6`, `companies` `3`, `deals` `6`; the default
  pipeline is present.
- **Content is READABLE — the encryption gap is gone.** List items carry real values
  (`Mia Johnson`, `Brightside Solar`, `Redwood Residences Solar Rollout`, …), not empty/garbled
  bodies, because the port both writes and reads the data with the same crypto. Verified directly on
  the shared DB: `customer_entities.display_name` is stored **plaintext** (not `iv:ct:tag:v1`).

```
POST /api/auth/login (superadmin@acme.com/secret)        → ok, JWT from .NET
GET  /api/customers/people     → total 6, "Mia Johnson"
GET  /api/customers/companies  → total 3, "Brightside Solar"
GET  /api/customers/deals      → total 6, "Redwood Residences Solar Rollout"
GET  /api/customers/pipelines  → "Default Pipeline"
GET  /api/customers/dictionaries/status , /api/customers/deals/summary → 200
```

## Run it

```bash
cd testbench
docker compose down -v          # fresh shared DB (the seeding flow changed)
docker compose up -d --build    # postgres, redis, om-app (migrate-only), dotnet-api (seeds)+worker, caddy
./smoke.sh                      # auth/directory/dashboards seam
./integration-customers.sh      # customers dataset, readable end-to-end
```

## Why this is the right model

Because the port owns the write and the read, the shared DB never contains data one side can't
interpret. Auth stays interchangeable regardless (login keys on the deterministic `email_hash`, shared
pepper). Making the port byte-compatible with OM's **per-tenant-DEK** PimField encryption
(`encryption_maps` + `iv:ct:tag:v1` under a KMS-derived DEK) remains a separate, still-open parity item
for the scenario where OM itself writes the customer PII — it is no longer on the path for this testbench.

## ✅ Standalone parity (unchanged)

The customers CRM is also fully functional standalone on a .NET-owned database (223 unit tests + live):
all writes via the command bus (execute/undo/redo + `action_logs`), CRUD via the CRUD factory, custom
fields via the entities module (`cf_` on write / bare on read), lists filtered/sorted on custom fields
through query_index.
