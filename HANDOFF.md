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
| A | List rendering: snake_case field keys | TODO | OM DataTable `mapApiItem` reads snake_case (`display_name`,`first_name`,`primary_email`,`legal_name`,`brand_name`…); .NET list returns camelCase → blank cells. First slice of DataQuery parity. |
| F | Sidebar shows only ported modules | TODO | Nav now served by OM (`/api/auth/admin/nav`) → shows ALL modules. OM enablement is build-time (`src/modules.ts`), so filter the nav to ported groups (group id prefix == module id). Likely a .NET nav proxy+filter gated for testbench. |
| B | Per-module EF migrations | TODO | Central `OpenMercato.Api/Migrations/*` → per-module, mirroring OM's per-module MikroORM migrations. |
| C | Hybrid DataQuery 1:1 | TODO | index+DB blend, search_tokens, cf select/filter/sort, snake_case output; generic service used by all list routes. (A is its first slice.) |
| D | Encryption maps + per-tenant DEK 1:1 | TODO | TenantDataEncryptionService (DerivedKms PBKDF2 310000/sha512, root=sha256(secret)), encryption_maps-driven field enc on write/read for ported modules; seed maps + tenant DEK. Enables bidirectional PII parity on shared DB. |
| E | CrudFactory-style route layer | TODO | Generic crud route factory (OM crudFactory contract) so per-entity routes are declarative config, no duplicated list/create/update/delete. |
| G | Playwright verification | TODO | login → people list, company list, their detail pages; assert data displayed + actions work. |

## Key findings so far
- **List bug root cause**: OM people list `packages/core/src/modules/customers/backend/customers/people/page.tsx` `mapApiItem` reads `item.display_name`/`first_name`/`primary_email`/`company_entity_id`/`lifecycle_stage` (snake_case). Companies page.tsx same (`display_name`,`legal_name`,`brand_name`,`website_url`,`size_bucket`,`annual_revenue`). .NET `PeopleRoutes.ProjectBase`/company projections emit camelCase → mapper yields ''. Fix = snake_case list output (1:1 with OM DataQuery). Detail pages already work (different shape) — don't break them.
- **Sidebar**: fixed earlier to route `/api/auth/admin/nav` → OM (Caddyfile exception) because the API-only .NET port returns empty `groups`. OM nav group ids are `"<module>.nav.group"` (e.g. `customers.nav.group`, `sales.nav.group`) so filtering by ported module id is feasible.
- **Ported modules**: auth, directory, dashboards, entities, query_index, dictionaries, currencies, customers (see `testbench/ported-modules.txt`).
- Analysis subagents dispatched for C (DataQuery), D (encryption), E+B (crudFactory+migrations) — fold their plans in when done.

## Log
- (init) Wrote HANDOFF, created task list, dispatched 3 analysis subagents.
