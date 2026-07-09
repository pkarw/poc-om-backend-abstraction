# 🧪 Testbench — run Open Mercato against the ported .NET API

Boot the **real Open Mercato** frontend and let the **ported .NET API** serve the
modules that have been ported, so you can log in and use the app against your port.

```
        ┌─────────── caddy (:8088) ───────────┐
 you ──▶│ /api/auth,/api/directory,/api/dashboards ─▶ dotnet-api  │
        │ everything else ─────────────────────────▶ om-app (OM)  │
        └──────────────────────┬───────────────────────┘
                     one shared Postgres + Redis
```

## 🔑 How it works

- **One shared Postgres.** Open Mercato owns and **migrates** the full schema
  (`yarn db:migrate`, schema only). It does **not** seed — `mercato init` never runs.
- The **.NET API runs migrations-off but SEEDS** (`OM_SKIP_MIGRATIONS=1` +
  `OM_SEED_ON_BOOT=1`). After OM has migrated, the port provisions the Acme tenant
  (`superadmin@acme.com` / `secret`) and every ported module's data via each module's own
  setup hooks (the port of upstream `setup.ts` — see `INTEGRATION-customers.md`). Because
  the port both writes and reads the data, content stays self-consistent (no per-tenant-DEK
  PII the port can't decrypt). Its byte-exact ports use the very same `auth` / `directory`
  / `dashboards` / `customers` / … tables.
- Both share `JWT_SECRET` **and** `LOOKUP_HASH_PEPPER`, so a JWT/session and the
  email lookup hash are interchangeable: you log in through OM's UI, the request is
  proxied to .NET, .NET authenticates against OM-seeded users and issues a JWT that
  OM's server components accept.
- **Caddy** only forwards the **ported** modules' `/api/*` to .NET (see
  `ported-modules.txt` / `gen-proxy.sh`); OM serves the rest. As you port more
  modules, add them and regenerate — the seam stays in sync.

## 🚀 Run it

1. **Build the Open Mercato app image** from your open-mercato checkout (once):
   ```bash
   cd <path-to>/open-mercato && docker build -t open-mercato/app:local .
   ```
2. **Configure** shared secrets:
   ```bash
   cd testbench && cp .env.example .env   # edit JWT_SECRET + LOOKUP_HASH_PEPPER
   ```
3. **Start everything** (Postgres, Redis, OM, .NET api+worker, proxy):
   ```bash
   docker compose up --build
   ```
4. Open **http://localhost:8088**, log in as `superadmin@acme.com` / `secret`.
   Auth, the org switcher, and the dashboard are being served by the .NET port.
5. **Smoke-check** the ported seam any time:
   ```bash
   ./smoke.sh http://localhost:8088
   ```

## 🔄 Keeping the ported set in sync

When you port another module to .NET:

```bash
echo "customers" >> ported-modules.txt   # match the ✅/🧪 .NET column in ../MODULES.md
./gen-proxy.sh                            # rewrites the Caddyfile route matcher
docker compose up -d caddy                # reload
```

## ✅ What's validated vs. what needs the OM image

The `.NET side` of this testbench is verified end-to-end (see the repo's getting-started):
the .NET image boots migrations-off against a shared schema, and login / dashboard-layout /
org-switcher all pass **through the Caddy proxy**. The one piece you supply is the
`open-mercato/app` image (built from your OM checkout) — this repo can't build OM for you.

## ⚠️ Parity notes (honest status)

- **Login, RBAC, JWT, sessions, dashboard layout, org switcher** → served by .NET, verified.
- **Customers CRM** → served by .NET and **seeded by .NET** (3 companies, 6 people, 6 deals,
  default pipeline). Content reads back cleanly through the proxy — see
  `./integration-customers.sh` and `INTEGRATION-customers.md`.
- **Widget data** on the dashboard is placeholder until the source modules
  (`sales`/`catalog`/…) are ported — widgets render in their empty state.
- **At-rest encryption**: because the .NET port now **seeds** the shared schema, it both writes
  and reads PII with its own app-key AES-GCM (ADR in `packages/dotnet/docs/decisions`), so the
  data is self-consistent — no garbled content. Being byte-compatible with OM's per-tenant DEK
  scheme (for the case where OM itself writes the PII) is still a tracked parity item; the email
  **lookup hash** already matches (shared pepper), which is all login needs.
- Only modules listed in `ported-modules.txt` are served by .NET; the rest are OM's.
- **Sidebar/nav is OM's.** `GET /api/auth/admin/nav` builds the backend sidebar from OM's
  frontend page registry (its React `page.meta` files), which the API-only .NET port can't
  know — so the proxy keeps that one path on OM even though `auth` is ported (see the
  exception in `Caddyfile` / `gen-proxy.sh`). Without it the sidebar renders no module items.
