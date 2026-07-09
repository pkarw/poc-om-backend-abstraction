# HANDOFF — customers/testbench 1:1 parity program

Living doc to continue this multi-part effort across sessions. Update the **Status** table and
**Log** as you go. Atomic commits (one logical change each).

## Environment (how to run)
See memory `testbench-runtime-env`. Quick: `export PATH="$HOME/.dotnet:$PATH"; export DOTNET_ROOT="$HOME/.dotnet"`.
Docker CLI: `/mnt/c/Program Files/Docker/Docker/resources/bin/docker.exe`. Testbench at `testbench/`
(`docker.exe compose up -d --build`; `./smoke.sh`, `./integration-customers.sh`; URL http://localhost:8088,
login `superadmin@acme.com`/`secret`). OM checkout (source of truth): `/mnt/c/Users/pkarw/Projects/open-mercato`.
Editing a bind-mounted file (Caddyfile) then `restart` fails on WSL — use `compose up -d --force-recreate <svc>`.

## Goal
Make the .NET port's customers experience work 1:1 with Open Mercato in the testbench (real OM frontend +
.NET-served ported APIs on a shared Postgres), verified with Playwright on the people/company list + detail pages.

## Tasks & status
| # | Task | Status | Notes |
|---|------|--------|-------|
| A | List rendering: snake_case field keys | **DONE** (commit) | Added `PeopleRoutes.ProjectListItem` + snake_case list overlays for people/companies; detail stays camelCase. Verified live + Playwright. cf-on-list is `cf_`-prefixed in OM (deferred to C). |
| G | Playwright verification | **DONE** (commit) | `testbench/e2e/verify.mjs` — all green: people list (6), person detail (name/title/company/Deals 2), companies list (3), company detail (name/industry). |
| F | Sidebar shows only ported modules | **DONE** (commit) | `.NET AdminNavRouteGroup` fetches OM nav (`OM_NAV_UPSTREAM`) + filters by `/backend/<path>` prefix allowlist; nav routed to .NET. Catalog/Sales/Staff/Storage/Workflows/Messages/Checkout all hidden. RESIDUAL: OM SSR-bakes the full nav (client fetch prunes it) and a demo "Example" group is injected via OM's widget-injection registry (not the nav endpoint) — fully removing needs the OM image rebuilt with `enabledModules` trimmed to ported. |
| B | Per-module EF migrations | TODO | See "Analysis: migrations" below. Recommended: one empty-model migrations DbContext per module (own `MigrationsAssembly` + `__ef_migrations_<mod>` history), move raw-SQL migration files into module projects, boot loop applies in `ModuleCatalog` order. Cheap because migrations are raw SQL (no snapshot). |
| C | Hybrid DataQuery 1:1 | TODO | See "Analysis: DataQuery". Slices: 1 generic `IDataQuery` (snake_case docs+`cf_`), 2 full cf/filter/sort+`customFieldSources`/joins, 3 `search_tokens` (write in `QueryIndexCrudIndexer`, read AND-of-hashes EXISTS), 4 true index+DB hybrid + coverage fallback + jsonb pushdown, 5 roll out to all list routes. A was slice 0. |
| D | Encryption maps + per-tenant DEK 1:1 | TODO | See "Analysis: encryption". DerivedKms PBKDF2(sha256(secret), tenantId-utf8, 310000, SHA512, 32→b64); TenantDataEncryptionService (encryption_maps 3-level scope fallback, `iv:ct:tag:v1`); **fix email_hash to plain sha256(lower.trim)** (currently `v2:` HMAC — parity bug); EF SaveChanges interceptor encrypts, explicit read-decrypt helpers; seed maps+DEK in InitialTenantSeeder; customers currently PLAINTEXT. Only auth+customers have default maps. |
| E | CrudFactory-style route layer | MOSTLY EXISTS | .NET **already has** `CrudRoute`+`CrudConfig` (port of OM `makeCrudRoute`), used by People/Companies/Deals/Comments/Tags/Addresses/Currencies. Remaining: unify per-module HTTP helpers (`CustomersHttp`/`EntitiesHttp`/… duplicate the factory's private auth/body) into a public `CrudHttp`; add nested-resource support; close PARITY-TODO seams (exports, list cache, mutation guards, enrichers, access logging, rate limit — `CrudRoute.cs:31-33`); migrate remaining CRUD-shaped hand-rolled routes. Not a from-scratch build. |

### Known data-completeness gaps (surfaced by Playwright, for "all data displayed")
- **DONE (commit)**: seeder now sets real primary_email/primary_phone/source + profile job_title/domain/size/etc. from OM CUSTOMER_EXAMPLES → list Email/Source columns populated (verified). Requires a fresh DB (`down -v`) since seed is idempotent (no backfill).
- Still PARITY-TODO in the seeder: example activities/interactions/notes/addresses and custom-field VALUES (need the entities cf codec) — the Deals/Activities tabs and cf columns stay empty until then.
- List custom-field columns render blank until C makes list cf keys `cf_`-prefixed (OM `mapApiItem` collects `cf_*`).

## Analysis (from subagents — full plans)
- **DataQuery** (OM `packages/core/src/modules/query_index/lib/engine.ts` HybridQueryEngine; `packages/shared/src/lib/query/*`; `search-tokens.ts`): base table `b` LEFT JOIN `entity_indexes ei` on `entity_id=b.id::text`; base filters→SQL, non-base→`ei.doc->>field`, cf→`coalesce(doc->>'cf:key',doc->>'key')`; `like/ilike`→`search_tokens` AND-of-hashes EXISTS; select base as snake_case + `cf_<key>`; coverage-gap falls back to BasicQueryEngine. .NET now: in-memory index-only engine, no search_tokens write/read, camelCase (A fixed customers list). Change: `Lib/QueryIndexEngine.cs`, `Crud/QueryIndexCrudListQuery.cs`, `Crud/QueryIndexCrudIndexer.cs`, `Lib/IndexDocument.cs`, `Core/Crud/CrudRoute.cs`.
- **Encryption** (OM `packages/shared/src/lib/encryption/{aes,kms,tenantDataEncryptionService,subscriber,find}.ts`; per-module `encryption.ts`): DerivedKms root=sha256(secret) secret order FALLBACK_KEY→ENCRYPTION_KEY→AUTH_SECRET→NEXTAUTH_SECRET→dev; `deriveKey=pbkdf2(root, tenantId, 310000, 32, sha512)` b64 (**salt = tenantId UTF-8 string — make-or-break**). Maps: only auth (`user`:email+email_hash,name; `user_consent`) and customers (7 entities) declare fields. .NET now: app-key SHA256(JWT_SECRET), customers plaintext, no interceptor, email_hash `v2:` (bug). Testbench: if .NET derives DEK from shared TENANT_DATA_ENCRYPTION_KEY → bidirectional parity with OM.
- **CrudFactory / migrations**: see tasks E and B above.

## Progress verification commands
`cd packages/dotnet && dotnet test OpenMercato.sln` (224 pass). Testbench redeploy after .NET change: `cd testbench && docker.exe compose build dotnet-api && docker.exe compose up -d --force-recreate --no-deps dotnet-api`. Then `./integration-customers.sh` and `cd e2e && node verify.mjs`.

## Key findings so far
- **List bug root cause**: OM people list `packages/core/src/modules/customers/backend/customers/people/page.tsx` `mapApiItem` reads `item.display_name`/`first_name`/`primary_email`/`company_entity_id`/`lifecycle_stage` (snake_case). Companies page.tsx same (`display_name`,`legal_name`,`brand_name`,`website_url`,`size_bucket`,`annual_revenue`). .NET `PeopleRoutes.ProjectBase`/company projections emit camelCase → mapper yields ''. Fix = snake_case list output (1:1 with OM DataQuery). Detail pages already work (different shape) — don't break them.
- **Sidebar**: fixed earlier to route `/api/auth/admin/nav` → OM (Caddyfile exception) because the API-only .NET port returns empty `groups`. OM nav group ids are `"<module>.nav.group"` (e.g. `customers.nav.group`, `sales.nav.group`) so filtering by ported module id is feasible.
- **Ported modules**: auth, directory, dashboards, entities, query_index, dictionaries, currencies, customers (see `testbench/ported-modules.txt`).
- Analysis subagents dispatched for C (DataQuery), D (encryption), E+B (crudFactory+migrations) — fold their plans in when done.

## Log
- (init) Wrote HANDOFF, created task list, dispatched 3 analysis subagents.
