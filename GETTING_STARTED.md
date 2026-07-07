# 🏁 Getting Started

This walks a newcomer from zero to a running, logged-in Open Mercato dashboard served by a **technology port**. The reference port is **.NET** ([`packages/dotnet`](packages/dotnet/README.md)) — it has three modules working end-to-end: **auth**, **directory**, **dashboards**.

Two paths:

- **Path A** — run the .NET port **standalone** and log in against its own seeded data. Fastest.
- **Path B** — run a **real Open Mercato** UI against the .NET port over one shared database (the [testbench](testbench/README.md)).

## ✅ Prerequisites

- **Docker + Docker Compose** — the one-command path for either flow.
- *or* the **[.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)** plus a reachable **PostgreSQL** and **Redis** — the native path.
- **Path B only:** an [Open Mercato](https://github.com/open-mercato/open-mercato) checkout you can build a Docker image from (this repo pins and analyzes upstream but does not vendor its source).

---

## 🅰️ Path A — run the .NET port standalone

### 1. Start the stack

```bash
cd packages/dotnet
make up                 # postgres:17 + redis:7 + api + worker → http://localhost:8080
```

Native alternative (Postgres + Redis already running):

```bash
cd packages/dotnet
cp .env.example .env
dotnet tool install --global dotnet-ef   # one-time
make migrate
make dev                # API on :8080; run `make worker` in a second terminal
```

Liveness check:

```bash
curl -s http://localhost:8080/healthz
# → {"status":"ok","service":"dotnet-api"}
```

### 2. Seed the OM-identical data

```bash
make greenfield         # drop + migrate + seed   (or: make init to seed without dropping)
```

This reproduces upstream `mercato init` exactly — tenant "Acme Corp Tenant", org "Acme Corp" (slug `acme`), roles `employee`/`admin`/`superadmin`, and three users:

| Email | Role | Password | RBAC |
|---|---|---|---|
| `superadmin@acme.com` | superadmin | `secret` | `is_super_admin` — everything |
| `admin@acme.com` | admin | `secret` | `auth.*` + `directory.organizations.*` (**not** `directory.tenants.manage`) |
| `employee@acme.com` | employee | `secret` | none |

You can also drive the data through the CLI: `make cli ARGS="list-users"`, `make cli ARGS="add-org …"`, `make cli ARGS="list-orgs"`, `make cli ARGS="dashboards seed-defaults"`.

### 3. Log in and read the dashboard

```bash
# Login (form-encoded) → HS256 JWT + auth_token & session_token cookies
curl -s http://localhost:8080/api/auth/login \
  -d 'email=superadmin@acme.com' -d 'password=secret'
# → 200 {"ok":true,"token":"<JWT iss=open-mercato aud=staff>","redirect":"/backend"}

# Wrong password → 401
curl -s http://localhost:8080/api/auth/login \
  -d 'email=superadmin@acme.com' -d 'password=wrongpw'
# → 401 {"ok":false,"error":"Invalid email or password"}
```

Copy the `token` from the login response and use it as a bearer:

```bash
TOKEN="<paste JWT here>"

curl -s http://localhost:8080/api/auth/users -H "Authorization: Bearer $TOKEN"
# → 200 users envelope

curl -s http://localhost:8080/api/auth/feature-check -H "Authorization: Bearer $TOKEN" \
  -X POST -d '…'
# → 200 {"granted":[...]}

curl -s http://localhost:8080/api/dashboards/layout -H "Authorization: Bearer $TOKEN"
# → 200 {"layout":{"items":[]},...,"context":{"userEmail":"superadmin@acme.com"},...}

curl -s http://localhost:8080/api/directory/organization-switcher -H "Authorization: Bearer $TOKEN"
# → 200 (Acme org)
```

All three seeded users log in; `admin@acme.com`'s feature checks correctly **exclude** `directory.tenants.manage`.

---

## 🅱️ Path B — run Open Mercato against the .NET port

The [testbench](testbench/README.md) runs the *real* Open Mercato frontend and the .NET API against **one shared Postgres**. Open Mercato owns and migrates/seeds the schema; the .NET port runs **migrations-off** (`OM_SKIP_MIGRATIONS=1`) and serves only the ported modules. A [Caddy](https://caddyserver.com/) reverse proxy on `:8088` routes the ported `/api/auth/*`, `/api/directory/*`, `/api/dashboards/*` to .NET and everything else to Open Mercato. A shared `JWT_SECRET` **and** email lookup-hash pepper make auth interchangeable, so you log into the real OM UI and the .NET port serves login and the dashboard.

```bash
# 1. Build the Open Mercato app image from your checkout (once)
cd <path-to>/open-mercato && docker build -t open-mercato/app:local .

# 2. Configure shared secrets
cd <this-repo>/testbench && cp .env.example .env     # set JWT_SECRET + LOOKUP_HASH_PEPPER

# 3. Bring up postgres, redis, OM, .NET api+worker, proxy
docker compose up --build

# 4. Open http://localhost:8088 and log in as superadmin@acme.com / secret
#    Auth, the org switcher and the dashboard shell are served by the .NET port.

# 5. Smoke-check the ported seam any time
./smoke.sh http://localhost:8088
```

Full detail, architecture diagram and parity caveats: [`testbench/README.md`](testbench/README.md). Design spec: [`specs/11-testbench.md`](specs/11-testbench.md).

---

## 📋 What works today vs. what's placeholder

| Capability | Status |
|---|---|
| Login / logout, JWT (HS256), sessions, cookies | ✅ served by the port, verified |
| RBAC feature checks (super-admin, admin scopes, employee none) | ✅ served by the port, verified |
| Directory: organizations, org switcher | ✅ served by the port, verified |
| Dashboard shell + layout (`/api/dashboards/layout`) | ✅ served by the port, verified |
| Dashboard **widget data** | 🚧 placeholder — empty state until `sales`/`catalog`/… are ported |
| Email at-rest **decryption for display** | 🚧 tracked parity item (the email **lookup hash** matches — that's what login needs) |

Honesty rule: only modules in the ported set are served by the port; the testbench never claims data it cannot yet produce (see [`specs/11-testbench.md`](specs/11-testbench.md) TESTBENCH-R10).

---

## ➡️ Port the next module

The porting loop is three technology-agnostic skills (same skills for any target):

```
/om-analyze-module <id>            # distill upstream module → port contract (if none yet)
/om-port-module <id> dotnet        # implement the contract 1:1 in packages/dotnet
/om-verify-parity <id> dotnet      # prove request/response, schema, queue parity
```

Every step updates the matrix in [`MODULES.md`](MODULES.md) (⬜ → 🔍 → 🚧 → ✅ → 🧪). Porting order follows the dependency tiers there — infrastructure first (`entities`, `query_index`, `api_keys`), then domain modules.

To surface a newly ported module in the testbench, add it to the seam:

```bash
cd testbench
echo "<module-id>" >> ported-modules.txt   # match the ✅/🧪 .NET column in ../MODULES.md
./gen-proxy.sh                              # regenerate the Caddy route matcher
docker compose up -d caddy                  # reload
```

Same loop targets any technology — swap `dotnet` for `python` or `golang` (see [`README.md`](README.md) and [`specs/09-technology-package-standard.md`](specs/09-technology-package-standard.md)).
</content>
