# Deploying Open Mercato on the .NET backend

This folder productionises the **real Open Mercato experience — full UI, the AI assistant, dashboards,
CRM — served on top of the .NET‑ported backend APIs**, sharing one Postgres + Redis, behind TLS.

```
                         ┌───────────────────────────────────────────────┐
   HTTPS (443)           │  caddy   TLS + reverse proxy                   │
  ───────────────▶       │    ├── /api/{auth,directory,dashboards,        │
   your domain           │    │        entities,query_index,dictionaries, │
                         │    │        currencies,customers}  ─▶ dotnet-api│
                         │    └── everything else (UI + AI + other /api)  │
                         │             ─▶ om-app (Open Mercato Next.js)   │
                         │  dotnet-api      .NET ported backend (+ seeds) │
                         │  dotnet-worker   .NET background worker        │
                         │  postgres  redis  shared by both runtimes      │
                         └───────────────────────────────────────────────┘
```

- **`om-app`** — the Open Mercato Next.js app: the UI, the **AI assistant**, vector search, OCR, and
  every *non‑ported* `/api/*`. It **migrates** the schema (no seed).
- **`dotnet-api`** — the .NET port. Serves only the **ported** `/api/*` and **seeds** the ported‑module
  data (so all PII is written/read with the port's own crypto). Migrations‑off.
- **`dotnet-worker`** — the .NET background/queue worker.
- Both runtimes share `JWT_SECRET`, `LOOKUP_HASH_PEPPER` and `TENANT_DATA_ENCRYPTION_KEY`, which is what
  makes sessions and encrypted columns interchangeable between them.

Files here:

| File | Purpose |
|------|---------|
| `docker-compose.prod.yml` | The full stack. |
| `Caddyfile` | TLS + routing (Caddy owns 80/443, auto‑HTTPS). Used on a plain VPS. |
| `Caddyfile.internal` | HTTP‑only splitter for platforms that terminate TLS for you (Dokploy). |
| `docker-compose.dokploy.yml` | Override that makes Caddy HTTP‑only for Dokploy. |
| `.env.example` | Every setting. Copy to `.env`. |

---

## Prerequisites (all deploys)

1. **A domain** with an `A` record pointing at the server's public IP.
2. The **Open Mercato frontend image** (`open-mercato/app:local`). There is no public image yet, so build
   it once from an Open Mercato checkout:
   ```bash
   git clone https://github.com/open-mercato/open-mercato.git
   cd open-mercato && docker build -t open-mercato/app:local .
   ```
   > The Next.js build needs ≈2 GB RAM. On a 2 GB server add swap first (see below) or build it on a
   > bigger machine / in CI and `docker push` it to a registry, then set `OM_APP_IMAGE` to that ref.
3. `deploy/.env` filled in — copy `deploy/.env.example` and set the domain, the three secrets
   (`openssl rand -hex 32` each), a strong DB + admin password, and your `OPENAI_API_KEY` if you want AI.

The `.NET` image (`dotnet-api` / `dotnet-worker`) is built for you from `packages/dotnet` by
`up -d --build`.

---

## Section 1 — Deploy to a Hetzner Ubuntu VPS

A minimal, from‑scratch deploy on a fresh **Hetzner Cloud** server (Ubuntu 24.04). A **CX22**
(2 vCPU / 4 GB) is a comfortable minimum; **CX11** (2 GB) works if you add swap and build the OM image
elsewhere.

### 1. Create the server & DNS

- Hetzner Cloud console → *Add Server* → Ubuntu 24.04, type **CX22**, add your SSH key.
- Point your domain's `A` record at the server IP (e.g. `erp.example.com → 203.0.113.10`).

### 2. Base setup (as root over SSH)

```bash
ssh root@203.0.113.10

# Docker Engine + compose plugin
curl -fsSL https://get.docker.com | sh

# Firewall: allow SSH + HTTP/HTTPS only
ufw allow OpenSSH && ufw allow 80/tcp && ufw allow 443/tcp && ufw --force enable

# (2 GB servers only) add 2 GB swap so the OM image build / Node don't OOM
fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile && swapon /swapfile
echo '/swapfile none swap sw 0 0' >> /etc/fstab
```

### 3. Get the code + build the OM image

```bash
# this repo (the .NET port + deploy files)
git clone https://github.com/pkarw/poc-om-backend-abstraction.git
cd poc-om-backend-abstraction

# the Open Mercato frontend image (see Prerequisites; skip if you use a registry image)
git clone https://github.com/open-mercato/open-mercato.git /opt/open-mercato
docker build -t open-mercato/app:local /opt/open-mercato
```

### 4. Configure

```bash
cp deploy/.env.example deploy/.env
# generate secrets
for k in JWT_SECRET LOOKUP_HASH_PEPPER TENANT_DATA_ENCRYPTION_KEY; do
  sed -i "s|^$k=.*|$k=$(openssl rand -hex 32)|" deploy/.env
done
# then edit deploy/.env: DOMAIN, LETSENCRYPT_EMAIL, POSTGRES_PASSWORD,
# OM_INIT_SUPERADMIN_PASSWORD, and OPENAI_API_KEY (for the AI assistant)
nano deploy/.env
```

### 5. Launch

```bash
docker compose --env-file deploy/.env -f deploy/docker-compose.prod.yml up -d --build
```

First boot: `om-app` migrates the schema, then `dotnet-api` seeds the demo tenant. Watch it:

```bash
docker compose --env-file deploy/.env -f deploy/docker-compose.prod.yml logs -f dotnet-api
```

Caddy obtains a Let's Encrypt certificate automatically on first HTTPS hit. Then open
`https://your-domain/login` and sign in as `superadmin@acme.com` with the password you set.

### 6. Operating it

```bash
CF="--env-file deploy/.env -f deploy/docker-compose.prod.yml"
docker compose $CF ps                 # status
docker compose $CF logs -f om-app     # tail the OM app (incl. AI)
docker compose $CF pull && docker compose $CF up -d   # update images
# to ship a .NET port change: git pull, then
docker compose $CF up -d --build dotnet-api dotnet-worker
```

**Backups:** the state lives in the `pgdata` volume — back it up with
`docker compose $CF exec -T postgres pg_dump -U mercato mercato | gzip > backup-$(date +%F).sql.gz`.

---

## Section 2 — Deploy to Dokploy

[Dokploy](https://dokploy.com) is a self‑hosted PaaS. It ships its own **Traefik** ingress that owns
ports 80/443 and provisions TLS, so here the bundled Caddy runs as an **HTTP‑only internal splitter**
(routing ported `/api/*` → .NET, the rest → OM) and Dokploy terminates HTTPS in front of it. The
`docker-compose.dokploy.yml` override wires that up.

### 1. Install Dokploy (once, on a fresh Ubuntu VPS)

```bash
curl -sSL https://dokploy.com/install.sh | sh
```

Open `http://<server-ip>:3000`, create the admin account. Point your app domain's `A` record at the
server. (Build/push the `open-mercato/app:local` image as in Prerequisites, or make it available on the
server / a registry Dokploy can pull.)

### 2. Create a Compose application

- Dokploy → *Create Project* → add a **Compose** service.
- **Source**: connect this Git repo (`poc-om-backend-abstraction`), branch `main`.
- **Compose path / files**: use both files —
  `deploy/docker-compose.prod.yml` and `deploy/docker-compose.dokploy.yml`
  (in Dokploy's compose field: `-f deploy/docker-compose.prod.yml -f deploy/docker-compose.dokploy.yml`).

### 3. Environment

In the service's **Environment** tab, paste the contents of `deploy/.env.example` and fill in the
values (DOMAIN, the three `openssl rand -hex 32` secrets, `POSTGRES_PASSWORD`,
`OM_INIT_SUPERADMIN_PASSWORD`, `OPENAI_API_KEY`). Dokploy injects these into the compose.

### 4. Domain + TLS

- In the service's **Domains** tab → *Add Domain*:
  - **Host**: your domain (e.g. `erp.example.com`)
  - **Service**: `caddy`  •  **Container port**: `80`
  - **HTTPS**: on (Let's Encrypt). Dokploy's Traefik gets the cert and forwards plain HTTP to the
    internal Caddy splitter.

### 5. Deploy

Hit **Deploy**. Dokploy builds the `dotnet-*` images and starts the stack. Watch the `dotnet-api` logs
for the seed to finish, then open `https://your-domain/login`.

> **Note on the OM image:** Dokploy builds the `dotnet-api`/`dotnet-worker` images from the repo, but
> `om-app` uses a prebuilt image (`OM_APP_IMAGE`). Either build `open-mercato/app:local` on the Dokploy
> host once, or push it to a registry and set `OM_APP_IMAGE` to that reference in the Environment tab.

---

## Troubleshooting

- **`om-app` unhealthy / restarts** — check `DATABASE_URL`, the shared secrets, and that migrations ran:
  `docker compose $CF logs om-app`.
- **UI shows data but PII looks garbled** — `TENANT_DATA_ENCRYPTION_KEY` differs between `om-app` and the
  .NET services; they must be identical (the override reuses the same value for both).
- **Login works but a ported page 404s** — the Caddy `@ported` matcher and the modules the .NET service
  serves drifted apart; keep them in sync with `testbench/ported-modules.txt`.
- **AI assistant does nothing** — set `OPENAI_API_KEY` (used for the assistant, embeddings and OCR) and
  redeploy.
- **OM image build OOMs** — add swap (Section 1 step 2) or build the image on a larger machine and push it.
