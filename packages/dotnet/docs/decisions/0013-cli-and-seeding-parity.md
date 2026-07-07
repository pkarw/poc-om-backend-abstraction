# 0013 — Global CLI and OM-parity initial-tenant seeder

Status: accepted · 2026-07-07

## Context

Upstream Open Mercato ships a `mercato` CLI (`packages/cli`) whose `init`
(`yarn initialize`) command bootstraps a fresh install: it runs migrations and
then `auth setup --orgName "Acme Corp" --email superadmin@acme.com --password
secret --roles superadmin,admin,employee`, which calls
`setupInitialTenant` + `ensureRoles` + `ensureDefaultRoleAcls`
(`auth/lib/setup-app.ts`). Module `cli.ts` files also contribute per-module
subcommands (auth `add-user`/`set-password`/`list-users`, directory-facing
`add-org`/`list-orgs`, …).

The .NET port previously had only `AuthBootstrapSeeder` — a minimal, auth-only
superadmin bootstrap — and no CLI. We need (A) a seeder that reproduces the exact
`mercato init` dataset and (B) a global, module-aware CLI.

## Decision

### A. `InitialTenantSeeder` (OM-parity seeder)

`OpenMercato.Modules.Directory.Seeding.InitialTenantSeeder.SetupInitialTenantAsync`
is the 1:1 port of `setupInitialTenant` + `ensureRoles` + `ensureDefaultRoleAcls`.
It produces the byte-for-byte Acme dataset that upstream `mercato init` seeds:

| Data                | Value                                                             |
| ------------------- | ---------------------------------------------------------------- |
| Tenant              | name `Acme Corp Tenant`, is_active=true                          |
| Root organization   | name `Acme Corp`, slug `acme`, is_active=true, depth 0, hierarchy arrays via `OrganizationHierarchy` |
| Roles (tenant)      | `employee`, `admin`, `superadmin` (DEFAULT_ROLE_NAMES)          |
| User superadmin@acme.com | role `superadmin`, password `secret`, bcrypt cost 10, is_confirmed, encrypted email + email_hash |
| User admin@acme.com | role `admin` (derived from primary email domain)                |
| User employee@acme.com | role `employee`                                              |
| RoleAcl superadmin  | is_super_admin=true, features `["directory.tenants.*"]`          |
| RoleAcl admin       | features `["auth.*","directory.organizations.view","directory.organizations.manage"]` |
| RoleAcl employee    | features `[]`                                                     |

Env overrides honored exactly as upstream: `OM_INIT_SUPERADMIN_EMAIL` /
`OM_INIT_SUPERADMIN_PASSWORD` (primary user, resolved by the caller),
`OM_INIT_ADMIN_EMAIL` / `OM_INIT_EMPLOYEE_EMAIL` (derived emails, default
`admin@`/`employee@<primary-domain>`), `OM_INIT_ADMIN_PASSWORD` /
`OM_INIT_EMPLOYEE_PASSWORD` (default `secret`). Idempotent: if the primary user
already exists (matched by the deterministic email lookup hash), the seeder reuses
that tenant and only ensures roles/ACLs. Parents are saved before children so the
DB-level FKs (created by the raw-SQL migrations) are satisfied even though the EF
model declares no relationships.

Admin/superadmin features come from each module's `IModule.DefaultRoleFeatures`
(new; the port of `setup.ts` `defaultRoleFeatures`), merged in registration order
by `ModuleRegistry.MergedDefaultRoleFeatures`. The **raw wildcard patterns** are
stored verbatim into `role_acls.features_json` — matching upstream exactly (which
stores `"auth.*"`, not an expansion); the RBAC layer expands them at check-time via
`FeatureMatch`.

`AuthBootstrapSeeder` is removed. The API host's env-gated boot seeding now calls
`InitialTenantSeeder.RunBootAsync`, so booting with `OM_INIT_SUPERADMIN_EMAIL` +
`OM_INIT_SUPERADMIN_PASSWORD` set produces the full Acme dataset — identical to CLI
`init`/`seed`.

**Home = Directory module.** The seeder must reference both auth entities/crypto
(`User`/`Role`/`RoleAcl`, `PasswordHasher`, `EncryptionService`) and directory
entities/hierarchy (`Tenant`/`Organization`, `OrganizationHierarchy`). The
dependency direction is Directory → Auth → Core, so Directory is the only ported
module that can see all of them; placing it in Auth or Core (as the task suggested
as one option) would invert the dependency. Documented here as the deliberate
deviation from that suggestion.

### B. Global CLI (`OpenMercato.Cli`)

A new console project (`OutputType=Exe`) that references `OpenMercato.Api` to reuse
`ModuleCatalog.CreateRegistry()`, the `AppConfig`/`AppDbContext` wiring (same
`ConfigureWarnings` ignore) and — critically — the `OpenMercato.Api` migrations
assembly. It builds a `ServiceProvider`, aggregates built-in commands with every
module's `IModule.CliCommands`, dispatches by name and prints help on
empty/unknown input. Redis is optional: the multiplexer is a lazy singleton with
`AbortOnConnectFail=false`, so the CLI never blocks when Redis is down.

New contract `ICliCommand` (`OpenMercato.Core.Modules`): `Name`, `Description`,
`Task<int> RunAsync(string[] args, IServiceProvider services)`. `IModule` exposes
an optional `IReadOnlyList<ICliCommand> CliCommands` (default-interface, empty).

- **Built-ins**: `migrate` (apply migrations), `init` (migrate + seed, args
  `--orgName/--email/--password/--orgSlug`, defaults Acme Corp /
  superadmin@acme.com / secret / acme), `greenfield` (`DROP SCHEMA public CASCADE;
  CREATE SCHEMA public;` → migrate → seed; requires `--yes`), `seed` (seeder only).
- **auth**: `add-user`, `set-password`, `list-users` (faithful ports; `add-user`
  resolves the tenant from the `organizations` table via raw SQL, since auth does
  not reference the directory entity type).
- **directory**: `add-org`, `list-orgs`.

## Consequences

- One seeder feeds three entry points (CLI `init`/`greenfield`/`seed` and API boot),
  guaranteeing an identical dataset. Idempotency is covered by tests
  (`InitialTenantSeederTests`, EF InMemory — no live DB).
- Referencing the Web-SDK `OpenMercato.Api` from the console CLI pulls in ASP.NET;
  acceptable because the ported modules already depend on `Microsoft.AspNetCore.App`
  (via `IModule.MapRoutes`). The alternative (replicating a minimal host + a second
  migrations assembly) was rejected as duplication.

## PARITY-TODO (not ported)

Cross-tenant `orgSlug` uniqueness pre-flight (`OrgSlugExistsError`), tenant DEK
provisioning during setup, `--with-examples` `seedExamples` hooks, the demo-superadmin
self-onboarding deactivation, and the auth `seed-roles`/`sync-role-acls`/
`rotate-encryption-key` subcommands. The three required auth subcommands and the two
directory subcommands are ported; the rest arrive with their owning module ports.
</content>
