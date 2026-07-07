# 0009 — Auth RBAC + roles/users route deviations

Status: accepted
Date: 2026-07-07
Scope: `OpenMercato.Modules.Auth` — RBAC + roles + users domain slice (RbacService, /api/auth/roles,
/api/auth/roles/acl, /api/auth/users, /api/auth/users/acl, /api/auth/users/consents,
/api/auth/users/resend-invite, /api/auth/admin/nav).

## Context

Upstream (`packages/core/src/modules/auth`) resolves authorization through `services/rbacService.ts`
and a set of hand-written + `makeCrudRoute` handlers that depend on the **directory** module
(Tenant/Organization entities, organization-scope resolution), the shared **crud command bus**
(undo/redo, optimistic lock), **search_tokens** full-text indexing, **api_keys**, **notifications**,
and a **@open-mercato/cache** strategy. None of those are ported yet. This port reproduces the
observable HTTP contract (paths, methods, status codes, JSON envelopes/field names, documented
quirks) using EF Core against the shared `AppDbContext`, and records the following deviations.

## Decisions

1. **RBAC evaluation is uncached (correct, not memoized).** Upstream layers a 5-minute
   `CacheStrategy` with tag invalidation (`rbac:user:*`, `rbac:tenant:*`, …). `RbacService`
   evaluates against the DB on every call. The boolean/ACL results are byte-identical; only latency
   differs. `Invalidate*Cache` methods are retained as no-ops so route handlers that mirror upstream
   invalidation calls compose unchanged.

2. **`api_key:<id>` subjects are treated as "no grants".** The API-key ACL path depends on the
   unported `api_keys` module. Staff (`Guid`) subjects — the entire ported surface — are faithful.
   Marked `// PARITY-TODO`.

3. **enabled-modules filtering is omitted.** Upstream intersects grants/super-admin with
   `getEnabledModuleIds()`. That registry is out of scope; grants are matched as-is. With all modules
   enabled (the normal case) behaviour is identical.

4. **Directory not ported → tenant/organization _names_ fall back to their ids.** The roles/users
   list items expose `tenantName`/`organizationName` equal to the id string (upstream falls back to
   the id when a name is absent). Super-admin `om_selected_tenant` cookie scoping and org-scope
   resolution are skipped; a super admin lists across all tenants. On **user create/update** the
   target tenant is taken from the actor (upstream derives it from `organization.tenant`). Marked
   `// PARITY-TODO`.

5. **Search is role-name only.** Upstream `search` matches encrypted email via `search_tokens`,
   organization name, and role name. Without `search_tokens` and the directory tables, only the
   role-name branch is ported; an all-empty candidate set still yields the `{items:[],…}` 200 as
   upstream. Marked `// PARITY-TODO`.

6. **CRUD is inlined; no command bus.** Create/update/delete for roles and users run directly against
   `AppDbContext` reproducing the command validation, guards, reserved-name (`superadmin`/`admin`)
   400, per-tenant duplicate-email 400 envelope, "assigned users" guards, cascade deletes and event
   payloads. Undo/redo, audit-log entries, custom-field values and the query_index indexer are not
   ported (best-effort no-ops / omitted).

7. **Optimistic lock is a no-op.** `enforceCommandOptimisticLock` is skipped (no-op when the
   `x-om-ext-optimistic-lock-expected-updated-at` header is absent, which is the default). Header
   enforcement is `// PARITY-TODO`.

8. **Emails / rate limits are no-ops.** No mailer or Redis limiter is wired: invite/resend token
   rows are created faithfully but no email is sent (so `_warning:'invite_email_failed'` never
   fires), and the in-handler `resend-invite` fail-open limiter is skipped. Marked `// PARITY-TODO`.

9. **`admin/nav` returns the faithful envelope with empty chrome.** The sidebar chrome
   (`lib/backendChrome.tsx`) is UI-derived and out of scope. The route returns
   `{brand:null,groups:[],settingsSections:[],settingsPathPrefixes:[],profileSections:[],
   profilePathPrefixes:[],grantedFeatures:<RBAC>,roles:<JWT>}` — real granted features + roles,
   empty chrome. Marked `// PARITY-TODO`.

## Preserved exactly

Wildcard matching (`*`, `prefix.*` incl. exact-prefix) and `hasAllFeatures`; per-user `UserAcl` wins
**exclusively** over role aggregation; `is_super_admin ⇒ ['*']` / all-orgs; `__all__` org sentinel;
organization-scope denial in `userHasAllFeatures`; the roles/users **empty-envelope quirk** (200
`{items:[],total:0,totalPages:1}` on unauth/invalid-query); reserved-role-name 400; per-tenant
duplicate-email 400 body (`{error,fieldErrors,details}`); `roles/acl` PUT `{ok:true,sanitized:false}`;
`users/acl` non-SA sanitization (strip `*`/`directory.*`/`directory.tenants*`, `requestedIsSuperAdmin`
default false, empty ACL ⇒ row delete, `sanitized` flag); consents integrity HMAC verification;
super-admin visibility hiding in roles/users lists.

## Integrator note

`RbacService` must be registered so the foundation `RequireFeatures(...)` filter can resolve it:

```csharp
// AuthModule.ConfigureServices
services.AddScoped<IRbacService, RbacService>();
```

Registered as **scoped** (it holds the request-scoped `AppDbContext`).
