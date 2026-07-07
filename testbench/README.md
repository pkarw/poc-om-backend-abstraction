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

- **One shared Postgres.** Open Mercato owns and **migrates** the full schema and
  **seeds** it (`mercato init` → Acme Corp, `superadmin@acme.com` / `secret`).
- The **.NET API runs migrations-off** (`OM_SKIP_MIGRATIONS=1`). Its byte-exact ports
  read/write the very same `auth` / `directory` / `dashboards` tables OM created.
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
- **Widget data** on the dashboard is placeholder until the source modules
  (`sales`/`catalog`/…) are ported — widgets render in their empty state.
- **Email at-rest encryption**: .NET uses a single app-key AES-GCM (ADR in
  `packages/dotnet/docs/decisions`), while OM uses per-tenant DEKs. The **email lookup
  hash matches** (shared pepper), which is what login needs; decrypting an OM-written
  email for *display* in a .NET response is a tracked parity item.
- Only modules listed in `ported-modules.txt` are served by .NET; the rest are OM's.
