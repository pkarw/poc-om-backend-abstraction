# Spec 05 — Auth & RBAC

> Derived from upstream commit adc9da27759e357febe9ed8d4b7182040d127349. Source analysis: ../upstream/analysis/05-auth-rbac.md

## Scope

Requirements for porting Open Mercato's three authentication planes and its authorization engine to any technology:

1. **Staff auth** (upstream `auth` module): form login → HS256 JWT bound to a DB session, opaque refresh token, logout, password reset, feature-check endpoints.
2. **Machine auth** (upstream `api_keys` module): bcrypt-hashed `omk_…` secrets, header-based, RBAC subject `api_key:<uuid>`.
3. **Customer/portal auth** (upstream `customer_accounts` module): JSON login, separate JWT audience, separate session table and cookies.

Plus the cross-cutting pieces every route depends on: the dispatcher auth/feature guards (upstream `packages/create-app/template/src/app/api/[...slug]/route.ts`), wildcard feature matching, role/user ACLs, multi-tenant scoping (`directory` module tables), auth rate limiting, and the Redis-backed RBAC cache.

Out of scope: UI pages, sidebar preferences, user consents beyond table shape, AI-chat session keys (SHOULD-level only).

## Requirements

### JWT

- **AUTHRBAC-R1 (MUST)** Staff and customer tokens are HS256 JWTs: header `{"alg":"HS256","typ":"JWT"}`, unpadded base64url segments, signature = HMAC-SHA256 over `header.payload`. Verification MUST compare signatures in constant time.
- **AUTHRBAC-R2 (MUST)** Signing keys are audience-derived, never the raw `JWT_SECRET`: for audience `A`, normalize (lowercase, non-alphanumeric → `_`), use env `JWT_${A_UPPERCASE}_SECRET` if set, else the lowercase hex digest of `HMAC-SHA256(key = JWT_SECRET, message = 'open-mercato:jwt:v1:' + normalizedAudience)` used as the HMAC key string. Staff audience is `staff`, customer audience is `customer`. Tokens signed by one audience MUST NOT verify under the other.
- **AUTHRBAC-R3 (MUST)** Issued tokens carry `iat`, `exp` (default TTL 28800 s), `iss: "open-mercato"`, `aud`. Default verification enforces `aud === 'staff'` and `iss === 'open-mercato'` with strict equality and rejects tokens whose `exp` is in the past; a missing `exp` is accepted.
- **AUTHRBAC-R4 (MUST)** Legacy grace: when default verification fails and `JWT_LEGACY_GRACE_MINUTES` is not `0`/`false`/`off` (default: enabled), retry verification with the raw `JWT_SECRET` and no aud/iss enforcement; on success mark the payload `_legacyToken: true`.
- **AUTHRBAC-R5 (MUST)** Staff login issues JWTs with claims `{sub: <user uuid>, sid: <session uuid>, tenantId: <uuid|null>, orgId: <uuid|null>, email, roles: string[]}` (plus R3 standard claims).

### Staff login, sessions, refresh, logout

- **AUTHRBAC-R6 (MUST)** `POST /api/auth/login` accepts `application/x-www-form-urlencoded` (or generic form data) — never JSON. Fields: `email`, `password`, `remember` (truthy tokens `on|1|true|yes`), `tenantId` (alias `tenant`), `requireRole` (alias `role`, comma-separated), `redirect`.
- **AUTHRBAC-R7 (MUST)** Login responses use exactly: 400 `{"ok":false,"error":"Invalid credentials"}` (validation failure: non-email, password < 6 chars, non-UUID tenantId); 401 `{"ok":false,"error":"Invalid email or password"}` (any credential failure); 403 `{"ok":false,"error":"Not authorized for this area"}` (`requireRole` mismatch); 429 `{"error":"Too many requests. Please try again later."}`.
- **AUTHRBAC-R8 (MUST)** Credential failures are indistinguishable: unknown email, wrong password, and an email that matches users in more than one tenant (when no `tenantId` was given) all yield the identical 401 body, and password verification always runs a bcrypt compare — against the fixed dummy hash `$2b$10$OcZrhmZpIzJOjkfwUrk7d.Nl0eHNzOvalBcBlt5Ran.4lj8R3HZg6` when there is no user or stored hash — so latency is uniform.
- **AUTHRBAC-R9 (MUST)** Successful login (200) returns `{"ok":true,"token":"<jwt>","redirect":"<sanitized path, default /backend>"}` plus `"refreshToken":"<64-char hex>"` **only** when `remember` was set, and sets cookies `auth_token=<jwt>` (Max-Age 28800) and `session_token=<raw refresh token>` (with `remember`: `Expires = now + REMEMBER_ME_DAYS` days, default 30; without: Max-Age 28800). All auth cookies are `HttpOnly; Path=/; SameSite=Lax` and `Secure` in production.
- **AUTHRBAC-R10 (MUST)** Each login creates a `sessions` row: raw token = 32 random bytes as 64 lowercase hex chars; stored value = lowercase hex `HMAC-SHA256(secret, raw)` where secret is the first set of `AUTH_TOKEN_SECRET | AUTH_SECRET | NEXTAUTH_SECRET | JWT_SECRET` (production MUST refuse to start with none set); `expires_at = now + (remember ? REMEMBER_ME_DAYS days : 8 h)`. Only the hash is persisted.
- **AUTHRBAC-R11 (MUST)** `POST /api/auth/session/refresh` (no auth required) takes JSON `{"refreshToken": string}`. Missing/empty → 400 `{"ok":false,"error":"Missing or invalid refresh token"}`; unknown/expired → 401 `{"ok":false,"error":"Invalid or expired refresh token"}`; both failures clear `auth_token` and `session_token` cookies. Success → 200 `{"ok":true,"accessToken":"<jwt>","expiresIn":28800}` and sets a fresh `auth_token` cookie. The refresh token is NOT rotated.
- **AUTHRBAC-R12 (MUST)** `GET /api/auth/session/refresh?redirect=<path>` (no auth required) reads the `session_token` cookie; invalid/missing → 302 to `/login?redirect=<encoded>` clearing both cookies; valid → 302 to the sanitized redirect (default `/`) setting a fresh `auth_token` (Max-Age 28800).
- **AUTHRBAC-R13 (MUST)** `POST /api/auth/logout` (auth required) deletes the session identified by the JWT `sid` and/or the `session_token` hash, responds 302 to `/login`, and clears both staff cookies (Max-Age 0).
- **AUTHRBAC-R14 (MUST)** Successful login updates `users.last_login_at` and resets the compound (`ip:emailhash`) login rate-limit counter.
- **AUTHRBAC-R15 (MUST)** Login emits `auth.login.success` on success and `auth.login.failed` on credential failure (fire-and-forget; event delivery failures never affect the HTTP response).

### Request authentication & dispatcher guards

- **AUTHRBAC-R16 (MUST)** Staff token extraction order: `Authorization: Bearer <jwt>` (case-insensitive scheme), else the URL-decoded `auth_token` cookie. A verified payload with `type === 'customer'` is rejected as invalid (no API-key fallback) — customer JWTs never authenticate staff routes.
- **AUTHRBAC-R17 (MUST)** Canonical revalidation runs on every authenticated request: (a) `sub` and tenant/org claims must be null/empty or UUID-shaped; (b) if the JWT carries `sid`, a session row with `id = sid AND user_id = sub AND deleted_at IS NULL AND expires_at > now` must exist (tokens without `sid` are valid only via the R4 legacy path); (c) the user must exist and not be soft-deleted; (d) the user's **current** `tenant_id`/`organization_id` must equal the JWT claims; (e) roles and `isSuperAdmin` are recomputed from the DB, ignoring the JWT `roles` claim. Any check failing → status `invalid`.
- **AUTHRBAC-R18 (MUST)** Logout, password-reset confirmation, and session deletion revoke live JWTs immediately (consequence of R17b — no grace window).
- **AUTHRBAC-R19 (MUST)** When no valid staff JWT is presented, fall back to API-key auth: `X-Api-Key: <secret>` header, else `Authorization: ApiKey <secret>` (case-insensitive scheme).
- **AUTHRBAC-R20 (MUST)** Routes require authentication by default; only an explicit per-method `requireAuth: false` opts out. Unauthenticated access to a protected route → 401 `{"error":"Unauthorized"}`.
- **AUTHRBAC-R21 (MUST)** When a presented staff token was invalid (rather than absent) and the final response is 401, the response clears the `auth_token` and `session_token` cookies.
- **AUTHRBAC-R22 (MUST)** Per-method `requireFeatures: string[]` gates the route: unauthenticated → 401; authenticated but failing `userHasAllFeatures` → 403 `{"error":"Forbidden","requiredFeatures":[...]}` echoing the required feature list. The organization used for the check resolves as: selected-org cookie (if allowed) → auth `orgId` (if allowed) → first allowed org.
- **AUTHRBAC-R23 (MUST)** `requireRoles` route metadata is deprecated: it MUST be accepted and ignored (authorize nothing), logging a warning once per route+method. Only `requireFeatures` authorizes.
- **AUTHRBAC-R24 (MUST)** Tenant parameter-pollution guard: every distinct `tenantId` candidate — each repeated `?tenantId=` query parameter plus a top-level body `tenantId` (JSON field or form field, on non-GET/HEAD/OPTIONS) — is validated. A non-superadmin targeting a tenant other than their own → 403 `{"error":"Not authorized to target this tenant."}`. Literal `"null"`/`"undefined"` strings normalize to null. Superadmins may target any tenant, and `null` from a superadmin means "global". The tri-state absent/null/uuid is load-bearing: an absent candidate resolves to the actor's own tenant.
- **AUTHRBAC-R25 (MUST)** When the resolved context is superadmin, cookies `om_selected_tenant` / `om_selected_org` override `tenantId`/`orgId` (the originals preserved as `actorTenantId`/`actorOrgId`); org value `__all__` or empty → `orgId = null`; the role list gains `superadmin` if absent. Non-superadmin contexts ignore `om_selected_tenant`.
- **AUTHRBAC-R26 (MUST)** Unmatched route or method → 404 `{"error":"Not Found"}`.

### RBAC: features, ACLs, resolution

- **AUTHRBAC-R27 (MUST)** Features are plain strings (`<module>.<action>` / `<module>.<area>.<action>`) declared per module. Wildcard matching is exactly: granted `*` matches everything; granted `mod.*` matches `mod` itself and anything starting with `mod.`; anything else matches by string equality. `hasAllFeatures`: empty required → allow; empty grants with non-empty required → deny; otherwise every required feature must match some grant.
- **AUTHRBAC-R28 (MUST)** ACL storage: `role_acls` and `user_acls` rows carry `features_json` (string array, may contain wildcards — free-form, unvalidated at write time), `is_super_admin` (default false), and `organizations_json` (string array or null; null, empty, or containing `'__all__'` means all organizations). Roles are strictly tenant-scoped (`roles.tenant_id NOT NULL`, unique `(tenant_id, name)`); creating a role without a tenant MUST fail.
- **AUTHRBAC-R29 (MUST)** ACL resolution order for user subjects: (1) global superadmin shortcut — any `user_acls.is_super_admin = true` row, else any `role_acls.is_super_admin = true` on any linked role, tenant-agnostic → `{isSuperAdmin: true, features: ['*'], organizations: null}`; (2) if a `user_acls` row exists for `(user, tenant)`, it replaces role aggregation entirely; (3) otherwise aggregate all `role_acls` of the user's tenant-scoped roles: OR `is_super_admin`, union features (insertion-order dedupe), union org lists — any null/`'__all__'` list widens `organizations` to null.
- **AUTHRBAC-R30 (MUST)** Organization restriction denies before features: if the resolved ACL's `organizations` is a list (not null) and the check scope's `organizationId` is not in it, `userHasAllFeatures` returns false regardless of granted features.
- **AUTHRBAC-R31 (MUST)** Superadmin subjects pass every feature check, except when a module allowlist is configured: a feature whose owning module is disabled fails even for superadmins.
- **AUTHRBAC-R32 (MUST)** Resolved ACLs are cached under key `rbac:{userId}:{tenantId|'null'}:{organizationId|'null'}` with TTL 300 000 ms and invalidation tags `rbac:user:{id}`, `rbac:tenant:{id}`, `rbac:org:{id}`, `rbac:all` (customer analogue: `customer_rbac:{userId}:{tenantId}:{orgId}` with `customer_rbac:*` tags). The cache backend MUST support tag-based deletion and be shareable (Redis when configured). Role-ACL edits MUST invalidate the tenant tag; user/role assignment changes MUST invalidate the user tag.
- **AUTHRBAC-R33 (MUST)** `POST /api/auth/feature-check` (auth required) takes JSON `{"features": [string ≤128] ≤50 items}` and returns 200 `{"ok":<bool>,"granted":[...],"userId":"..."}`, where on overall failure `granted` is the per-feature-evaluated subset of the **requested strings** (never expanded wildcards). 400 `{"ok":false,"error":"Invalid request"}` on invalid body.
- **AUTHRBAC-R34 (MUST)** `GET /api/auth/features` (guard `auth.acl.manage`) returns 200 `{"items":[{"id","title","module","dependsOn"?}],"modules":[{"id","title"}]}` sorted by module then id, listing declared features only.
- **AUTHRBAC-R35 (MUST)** Default role seeding is merge-only: built-in roles `employee`, `admin`, `superadmin`; each module may declare `defaultRoleFeatures` per role name; syncing (setup hook or CLI `auth sync-role-acls [--tenant <id>] [--no-superadmin]`) upserts one RoleAcl per role per tenant, unioning features (never removing) and setting — never unsetting — `is_super_admin` on the superadmin role's ACL.
- **AUTHRBAC-R36 (MUST)** Staff admin CRUD endpoints exist with these feature guards: `/api/auth/users` (`auth.users.list/create/edit/delete`), `/api/auth/roles` (`auth.roles.list` for GET, `auth.roles.manage` otherwise), `/api/auth/roles/acl` and `/api/auth/users/acl` (`auth.acl.manage`).

### Password reset & policy

- **AUTHRBAC-R37 (MUST)** `POST /api/auth/reset` (no auth, form field `email`) always returns 200 `{"ok":true}` whether or not the account exists. It creates a `password_resets` row: 64-hex raw token, HMAC-hashed at rest with the R10 secret, `expires_at = now + 1 h`.
- **AUTHRBAC-R38 (MUST)** `POST /api/auth/reset/confirm` (form: `token` ≥10 chars, `password` per policy) enforces single use via an atomic compare-and-set on `used_at` (update … where `used_at IS NULL`), sets a new bcrypt-cost-10 hash, deletes **all** of the user's sessions, and returns 200 `{"ok":true,"redirect":"/login"}`. Invalid/expired/reused token → 400 `{"ok":false,"error":"Invalid or expired token"}`; malformed request → 400 `{"ok":false,"error":"Invalid request"}`.
- **AUTHRBAC-R39 (MUST)** Password policy is env-driven: `OM_PASSWORD_MIN_LENGTH` (default 6), `OM_PASSWORD_REQUIRE_DIGIT/UPPERCASE/SPECIAL` (all default on). User passwords are hashed with bcrypt cost 10.

### API keys (machine auth)

- **AUTHRBAC-R40 (MUST)** Secret format: `omk_{8 lowercase hex}.{48 lowercase hex}` (4 + 24 random bytes). `key_prefix` = the first 12 characters of the secret (including `omk_`), stored plaintext with a unique index; the full secret is stored only as bcrypt cost 10 in `key_hash`.
- **AUTHRBAC-R41 (MUST)** Verification: select non-deleted candidates by `key_prefix`, skip expired (`expires_at <= now`), bcrypt-compare each. The RBAC subject id is literally `api_key:<key uuid>`; the ACL branch for API-key subjects skips the global-superadmin shortcut and aggregates the RoleAcls of the key's `roles_json`.
- **AUTHRBAC-R42 (MUST)** Context building: keys bound to a user (`session_user_id ?? created_by`) require that user to exist, be live, and match the key's tenant AND org exactly, else authentication fails; user-less keys require the key's tenant/org to exist, be active, and be mutually consistent. The resulting context is `{sub: "api_key:<id>", tenantId, orgId, roles, isApiKey: true, isSuperAdmin, keyId, keyName, userId?}`.
- **AUTHRBAC-R43 (MUST)** Management API `/api/api_keys/keys`: GET (guard `api_keys.view`) → paginated `{items,total,page,pageSize,totalPages}` with item fields `id,name,description,keyPrefix,organizationId,organizationName,createdAt,lastUsedAt,expiresAt,roles:[{id,name}]`; POST (guard `api_keys.create`) validates `name` 1–120, `description` ≤1000, optional `tenantId`/`organizationId`, `roles` (UUIDs or names), optional future `expiresAt`, checks the actor may grant each role, and returns the secret exactly once as `{id,name,keyPrefix,secret,tenantId,organizationId,roles}` — the secret MUST NOT be retrievable afterwards; missing tenant context → 400 `{"error":"Tenant context required"}`; DELETE `?id=<uuid>` (guard `api_keys.delete`) soft-deletes and invalidates caches.
- **AUTHRBAC-R44 (SHOULD)** An in-process API-key auth cache with success TTL 30 s (`OM_API_KEY_AUTH_TTL_MS`), negative TTL 5 s (`OM_API_KEY_AUTH_NEGATIVE_TTL_MS`), keyed by a per-process-random HMAC fingerprint of the secret, LRU-bounded (1000). It MUST be node-local (never shared via Redis) and success entries MUST NOT outlive `expires_at`. `last_used_at` writes SHOULD be throttled to once per 60 s per key.

### Multi-tenancy & email hashing

- **AUTHRBAC-R45 (MUST)** Directory tables: `tenants(id, name, is_active, timestamps, deleted_at)`; `organizations(id, tenant_id, name, slug unique-per-tenant, logo_url, is_active, parent_id, root_id, tree_path, depth, ancestor_ids, child_ids, descendant_ids, timestamps)`. Staff users belong to exactly one nullable `(tenant_id, organization_id)` pair on the `users` row; cross-org visibility comes only from ACL `organizations_json` plus org-tree descendant expansion.
- **AUTHRBAC-R46 (MUST)** Staff email uniqueness is per-tenant via the partial unique index `(tenant_id, email_hash) WHERE deleted_at IS NULL AND email_hash IS NOT NULL` on `users`.
- **AUTHRBAC-R47 (MUST)** `email_hash` = `'v2:' + lowercase hex HMAC-SHA256(pepper, normalized email)` (trimmed, lowercased), pepper = first set of `LOOKUP_HASH_PEPPER | TENANT_DATA_ENCRYPTION_FALLBACK_KEY | TENANT_DATA_ENCRYPTION_KEY`; with no pepper, legacy plain `SHA-256(value)` hex. Email lookups MUST match `email` equality OR `email_hash IN (v2 candidate, legacy candidate)`, filtered `deleted_at IS NULL`; new writes use the `v2:` form when a pepper is configured.

### Customer/portal auth

- **AUTHRBAC-R48 (MUST)** `POST /api/customer_accounts/login` is JSON-only: `{email, password, tenantId?, organizationId?}` (tenant resolvable from request host). Every failure branch — unknown email, missing hash, inactive account, locked out, wrong password, unverified email — returns the identical bcrypt-timing-equalized 401 `{"ok":false,"error":"Invalid email or password"}`. The lockout counter increments only on wrong password.
- **AUTHRBAC-R49 (MUST)** Customer login success → 200 `{"ok":true,"user":{"id","email","displayName","emailVerified"},"resolvedFeatures":[...]}` plus cookies `customer_auth_token` (JWT, aud `customer`, Max-Age 28800) and `customer_session_token` (raw token, Max-Age 30 days), HttpOnly/Lax/Secure-in-prod. Customer JWT payload: `{sub, sid, type:'customer', tenantId, orgId, email, displayName, customerEntityId, personEntityId, resolvedFeatures}`.
- **AUTHRBAC-R50 (MUST)** Customer sessions (`customer_user_sessions`): raw token = 32 random bytes base64url, stored as plain lowercase-hex `SHA-256` (no HMAC pepper — deliberately different from staff), TTL `CUSTOMER_SESSION_TTL_DAYS` (default 30), per-user cap `MAX_CUSTOMER_SESSIONS_PER_USER` (default 5) revoking oldest-first.
- **AUTHRBAC-R51 (MUST)** Customer request auth requires `type === 'customer'`, a live `sid` session (fail-closed on infrastructure errors), a live active user, `iat` not older than the user's `sessions_revoked_at`, and re-resolves portal features fresh (per-user `customer_user_acls` overrides aggregated `customer_role_acls`; `isPortalAdmin` ⇒ features `['*']`). Guard failures: 401 `{"ok":false,"error":"Authentication required"}` and 403 `{"ok":false,"error":"Insufficient permissions"}`.
- **AUTHRBAC-R52 (MUST)** `POST /api/customer_accounts/portal/sessions-refresh` reads the `customer_session_token` cookie; success → 200 `{"ok":true,"resolvedFeatures":[...]}` + fresh `customer_auth_token`; failures → 401 with `error` one of `"No session token"`, `"Invalid or expired session"`, `"Account not active"`, `"Session refresh failed"`.

### Rate limiting

- **AUTHRBAC-R53 (MUST)** Auth endpoints are rate-limited in two layers before validation: per-IP, then compound key `"{ip}:{emailHash}"`. Defaults: login 5/60 s (compound) and 20/60 s (IP), refresh 15/60 s and 60/60 s, all with 60 s block; env knobs `RATE_LIMIT_{PREFIX}_{POINTS,DURATION,BLOCK_DURATION}` for prefixes `login`, `login-ip`, `refresh`, `refresh-ip`, `reset`, `reset-ip`, `reset-confirm`, plus `RATE_LIMIT_ENABLED` and `RATE_LIMIT_STRATEGY=memory|redis` (Redis via `REDIS_URL`).
- **AUTHRBAC-R54 (MUST)** Rate limiting is fail-open: limiter/Redis errors never block a request. 429 responses carry `Retry-After`, `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset` headers and body `{"error":"<message>"}`.
- **AUTHRBAC-R55 (MUST)** Client IP resolution: honor `X-Forwarded-For` only when `RATE_LIMIT_TRUST_PROXY_DEPTH` > 0 (default 1; take the Nth-from-last entry), else `X-Real-IP`.
- **AUTHRBAC-R56 (SHOULD)** Test hooks: rate limits disabled under `OM_INTEGRATION_TEST`; with `OM_TEST_MODE=1` and `OM_TEST_AUTH_RATE_LIMIT_MODE=opt-in`, limits apply only when the request carries `x-om-test-rate-limit: on`.

### Bootstrap & CLI

- **AUTHRBAC-R57 (MUST)** A setup command (`auth setup --orgName --email --password [--orgSlug] [--roles] …`) provisions tenant + organization + built-in roles + a superadmin user, seeds default role ACLs, and defaults credentials from `OM_INIT_SUPERADMIN_EMAIL`/`OM_INIT_SUPERADMIN_PASSWORD`.
- **AUTHRBAC-R58 (SHOULD)** Additional CLI parity: `add-user`, `seed-roles`, `sync-role-acls`, `set-password`, list commands — same observable DB effects as upstream `packages/core/src/modules/auth/cli.ts`.
- **AUTHRBAC-R59 (SHOULD)** Auth-domain events beyond R15: `auth.logout`, `auth.password.*`, and user/role CRUD events per upstream `packages/core/src/modules/auth/events.ts`, emitted with the same names.

## Contracts

### HTTP endpoints (staff plane)

| Method & path | Auth | Request | Success | Errors |
|---|---|---|---|---|
| `POST /api/auth/login` | none | form: `email`, `password`, `remember?`, `tenantId?`/`tenant?`, `requireRole?`/`role?`, `redirect?` | 200 `{"ok":true,"token":"<jwt>","redirect":"/backend","refreshToken"?:"<64 hex>"}` + cookies | 400/401/403/429 per R7 |
| `POST /api/auth/logout` | required | cookies | 302 → `/login`, clears cookies | 401 `{"error":"Unauthorized"}` |
| `GET /api/auth/session/refresh?redirect=` | none | `session_token` cookie | 302 → redirect, sets `auth_token` | 302 → `/login?redirect=…` + cookie clear |
| `POST /api/auth/session/refresh` | none | `{"refreshToken":"…"}` | 200 `{"ok":true,"accessToken":"<jwt>","expiresIn":28800}` | 400/401 per R11 (clear cookies); 429 |
| `POST /api/auth/reset` | none | form: `email` | 200 `{"ok":true}` always | 422 validation; 429 |
| `POST /api/auth/reset/confirm` | none | form: `token`, `password` | 200 `{"ok":true,"redirect":"/login"}` | 400 per R38; 429 |
| `POST /api/auth/feature-check` | required | `{"features":[…]}` | 200 `{"ok":bool,"granted":[…],"userId":"…"}` | 400 `{"ok":false,"error":"Invalid request"}`; 401 |
| `GET /api/auth/features` | `auth.acl.manage` | — | 200 `{"items":[…],"modules":[…]}` | 401/403 |
| `/api/auth/users`, `/api/auth/roles`, `/api/auth/roles/acl`, `/api/auth/users/acl` | per R36 | CRUD | — | dispatcher envelopes |
| `GET/POST/DELETE /api/api_keys/keys` | `api_keys.view/create/delete` | per R43 | list / create-with-secret / `{"success":true}` | 400 `{"error":"Tenant context required"}`, 403, 404 |
| `POST /api/customer_accounts/login` | none | JSON per R48 | 200 per R49 | uniform 401 |
| `POST /api/customer_accounts/portal/sessions-refresh` | none | `customer_session_token` cookie | 200 `{"ok":true,"resolvedFeatures":[…]}` | 401 variants per R52 |

### Dispatcher-level envelopes (every route)

```json
401 {"error":"Unauthorized"}
403 {"error":"Forbidden","requiredFeatures":["…"]}
403 {"error":"Not authorized to target this tenant."}
404 {"error":"Not Found"}
429 {"error":"<message>"}   // + Retry-After, X-RateLimit-Limit/Remaining/Reset
```

Note the two coexisting 401 shapes: dispatcher `{"error":"Unauthorized"}` vs endpoint-level `{"ok":false,"error":"…"}` (login/refresh/customer guards). Both are part of the contract.

### Cookies & headers

| Name | Plane | Content | Attributes |
|---|---|---|---|
| `auth_token` | staff | JWT (aud `staff`) | HttpOnly, Path=/, SameSite=Lax, Secure(prod), Max-Age 28800 |
| `session_token` | staff | 64-hex refresh token | same; Expires now+`REMEMBER_ME_DAYS` d or Max-Age 28800 |
| `customer_auth_token` | customer | JWT (aud `customer`) | same; Max-Age 28800 |
| `customer_session_token` | customer | base64url token (43 chars) | same; Max-Age 2592000 |
| `om_selected_tenant` / `om_selected_org` | staff | tenant/org UUID; org may be `__all__` | superadmin scope switch (R25) |
| `Authorization: Bearer <jwt>` | staff & customer | precedence over cookies | |
| `X-Api-Key: <secret>` / `Authorization: ApiKey <secret>` | machine | consulted only after JWT path fails | |

### JWT payload (staff, as issued by login)

```json
{
  "iat": 1767225600, "exp": 1767254400, "iss": "open-mercato", "aud": "staff",
  "sub": "<user uuid>", "sid": "<session uuid>",
  "tenantId": "<uuid|null>", "orgId": "<uuid|null>",
  "email": "user@example.com", "roles": ["admin", "employee"]
}
```

### PostgreSQL tables (names/columns are wire contract; all soft-deletable via `deleted_at`)

- `users(id uuid pk, tenant_id uuid?, organization_id uuid?, email text, email_hash text, name text?, password_hash text?, is_confirmed bool default true, last_login_at, created_at, updated_at, deleted_at)` + index on `email_hash` + partial unique `(tenant_id, email_hash) WHERE deleted_at IS NULL AND email_hash IS NOT NULL`
- `roles(id, name, tenant_id NOT NULL, timestamps, deleted_at)` unique `(tenant_id, name)`; `user_roles(id, user_id, role_id, created_at, deleted_at)`
- `sessions(id, user_id, token text UNIQUE /* HMAC-SHA256 hex */, expires_at, created_at, last_used_at, deleted_at)`
- `password_resets(id, user_id, token text UNIQUE /* HMAC-SHA256 hex */, expires_at, used_at, created_at, deleted_at)`
- `role_acls(id, role_id, tenant_id NOT NULL, features_json json, is_super_admin bool default false, organizations_json json?, timestamps)`; `user_acls` same shape keyed by `user_id`
- `api_keys(id, name, description?, tenant_id?, organization_id?, key_hash text /* bcrypt */, key_prefix text UNIQUE, roles_json json, created_by uuid?, session_token text?, session_user_id uuid?, session_secret_encrypted text?, last_used_at, expires_at?, timestamps, deleted_at)`
- `tenants`, `organizations` per R45; `user_consents(id, user_id, tenant_id?, organization_id?, consent_type, is_granted, granted_at, withdrawn_at, source, ip_address, integrity_hash, …)` unique `(user_id, tenant_id, consent_type)`
- Customer plane: `customer_users`, `customer_user_sessions` (token_hash = plain SHA-256), `customer_role_acls`, `customer_user_acls`

### Redis / cache keys

- RBAC cache: `rbac:{userId}:{tenantId|'null'}:{orgId|'null'}` → `{"isSuperAdmin":bool,"features":[…],"organizations":[…]|null}`, TTL 300 000 ms, tags `rbac:user:{id}`, `rbac:tenant:{id}`, `rbac:org:{id}`, `rbac:all`. Customer: `customer_rbac:{userId}:{tenantId}:{orgId}` with `customer_rbac:user/tenant/all` tags.
- Rate limiter key prefixes: `login`, `login-ip`, `refresh`, `refresh-ip`, `reset`, `reset-ip`, `reset-confirm`; compound identifier format `"{ip}:{emailHash}"`.
- API-key auth cache: in-process only — MUST NOT appear in Redis.

### Environment variables

`JWT_SECRET` (required), `JWT_${AUD}_SECRET` (per-audience override), `JWT_LEGACY_GRACE_MINUTES`, `AUTH_TOKEN_SECRET`/`AUTH_SECRET`/`NEXTAUTH_SECRET` (token-hash pepper chain), `REMEMBER_ME_DAYS`, `LOOKUP_HASH_PEPPER`/`TENANT_DATA_ENCRYPTION_FALLBACK_KEY`/`TENANT_DATA_ENCRYPTION_KEY`, `RATE_LIMIT_*`, `OM_PASSWORD_*`, `OM_API_KEY_*`, `CUSTOMER_SESSION_TTL_DAYS`, `MAX_CUSTOMER_SESSIONS_PER_USER`, `OM_INIT_SUPERADMIN_EMAIL`/`OM_INIT_SUPERADMIN_PASSWORD`. Same names in every technology package.

## Concept mapping

| Upstream TS concept | Technology-agnostic concept a port implements |
|---|---|
| `packages/shared/src/lib/auth/jwt.ts` hand-rolled HS256 | Any JWT library configured for HS256 + unpadded base64url, with audience-derived key computation per R2 (the derivation itself must be hand-implemented) |
| `packages/create-app/template/src/app/api/[...slug]/route.ts` dispatcher | HTTP middleware/filter pipeline enforcing R20–R26 uniformly for all module routes |
| Route metadata `{requireAuth, requireFeatures, rateLimit}` exported per method | Declarative per-endpoint auth metadata (decorators, attributes, route options) |
| `resolveAuthFromRequestDetailed` + `sessionIntegrity.ts` | Request-authentication service returning `(context, status ∈ {authenticated, missing, invalid})` with DB revalidation per R17 |
| `AuthContext` object | Auth principal type: `{sub, sid?, tenantId, orgId, email?, roles?, isApiKey?, isSuperAdmin, actorTenantId?, actorOrgId?, keyId?, keyName?, userId?}` |
| `RbacService` (Awilix-registered) | Authorization service: `loadAcl(subject, scope)`, `userHasAllFeatures`, tag-based cache invalidation |
| `featureMatch.ts` | Pure wildcard-matching functions per R27 (unit-test verbatim) |
| Zod validators (`userLoginSchema`, `featureCheckRequestSchema`, `createApiKeySchema`) | Language-native validation producing identical accept/reject outcomes and error statuses |
| MikroORM entities in `auth/data/entities.ts` | Any ORM/query layer mapping to the exact table/column names above, with real migrations |
| `bcryptjs` cost 10 | Any bcrypt implementation, cost 10, `$2b$` compatible |
| `CacheStrategy` with tags | Redis-backed cache supporting tag-indexed deletion |
| `rate-limiter-flexible` (memory/redis) | Any fixed-window/points limiter honoring the key prefixes, env knobs, fail-open rule, and 429 headers |
| Module `setup.ts` `defaultRoleFeatures` + `ensureDefaultRoleAcls` | Idempotent merge-only role-ACL seeding invoked from setup/CLI |
| `mercato auth …` CLI | The port's CLI entrypoint with equivalent subcommands (R57–R58) |
| Custom-route after-interceptors on `auth/login` | Extension hook allowing response/token substitution on login (MAY be a no-op if the port has no interceptor system yet — document as ADR) |

## Allowed deviations

Idiomatic replacements are welcome when observable behavior is identical:

- **Validation**: replace Zod with Pydantic/FluentValidation/etc. — but status codes and error envelopes on rejection must match (login validation failure is 400 `{"ok":false,"error":"Invalid credentials"}`, not a 422 field-error dump).
- **JWT library**: use a native lib instead of hand-rolled code, provided R1–R4 hold exactly (unpadded base64url, strict aud/iss, legacy-grace fallback, derived keys).
- **ORM/migrations, DI, HTTP framework**: free choice; table/column names, indexes (including the partial unique index in R46), and route paths are fixed.
- **GET-refresh stringification bug**: upstream embeds `String(user.tenantId)` (so `"null"` for tenant-less users) in the GET-refresh JWT; ports SHOULD sign proper null claims — the observable redirect flow, not the bug, is the contract (analysis "Gotchas").
- **`signJwt` claim-override ordering** (payload can override `iat`/`exp`): ports MAY expose this only if they implement login interceptors; otherwise standard claim handling is fine.
- **Console warnings / log text**: free-form, but the R23 warn-once-and-ignore behavior for `requireRoles` must exist.

What must NOT change:

- Any status code, JSON body, cookie name/attribute, or header listed in Contracts — including the two distinct 401 envelope shapes and the uniform-401 anti-oracle branches (R8, R48).
- Hash formats at rest: staff session/reset tokens HMAC-SHA256 (R10), customer session tokens plain SHA-256 (R50), email `v2:` HMAC (R47), bcrypt cost 10 for passwords and API-key secrets. Do NOT "unify" staff vs customer session hashing.
- Key-derivation string `'open-mercato:jwt:v1:'` and the audience normalization (R2) — cross-technology token compatibility depends on it.
- Wildcard semantics (R27), ACL resolution order (R29), org-restriction-denies-first (R30), UserAcl exclusive override.
- Fail-open rate limiting (R54) and the timing-equalized dummy bcrypt compare (R8).
- Redis key patterns and tag names (R32) — a shared Redis must be readable by both the TS app and the port.
- `requireRoles` must never authorize anything (R23).

## Verification

`om-verify-parity` checks this spec against a running port (and, where possible, side-by-side with the TS reference on the same Postgres/Redis):

1. **Black-box HTTP parity** — for each Contracts row, replay canned requests and diff status, JSON body (exact match), and `Set-Cookie` names/attributes: login happy path (with/without `remember`), each 400/401/403/429 branch, refresh GET/POST success+failure, logout redirect, feature-check, password-reset round trip, API-key CRUD, customer login/refresh. Confirms R6–R15, R33–R34, R37–R38, R43, R48–R52.
2. **Cross-implementation token exchange** — a JWT minted by the TS reference with the same `JWT_SECRET` MUST verify in the port and vice versa (staff and customer audiences; audience cross-rejection; legacy-grace on/off). Confirms R1–R5. Likewise a `sessions.token` hash written by TS MUST be refreshable by the port (R10, R11) and an `omk_` secret created in TS MUST authenticate against the port (R40–R42).
3. **Revocation matrix** — issue token → logout / delete session / reset password / move user's tenant / soft-delete user → next request MUST be 401 with cookie clearing (R17, R18, R21, R38).
4. **RBAC table-driven tests** — a fixture set of role_acls/user_acls/org trees with expected `loadAcl` outputs and `userHasAllFeatures` verdicts, including wildcard edge cases (`mod.*` matches `mod`), UserAcl override, `__all__`, org-scope denial, superadmin shortcut, API-key subject branch (R27–R31, R41).
5. **Shared-Redis inspection** — after ACL loads, assert exact `rbac:*` key names, TTLs, payload shape; after a role edit, assert tag invalidation removed the tenant's keys; assert no API-key cache keys in Redis (R32, R44).
6. **DB schema diff** — compare `information_schema` (tables, columns, defaults, unique/partial indexes) of the port's migrations against upstream's for the tables in Contracts (R28, R45–R47, and table list).
7. **Guard conformance** — hit a protected route without auth (401), with a feature-missing user (403 + `requiredFeatures`), with polluted `?tenantId=` params (403 tenant guard), with `requireRoles`-only metadata (must pass) (R20–R26).
8. **Timing/anti-oracle spot check** — unknown-email vs wrong-password login latencies within noise; identical bodies across all 401 branches (R8, R48).
9. **Rate-limit behavior** — burst to 429, check `Retry-After` + `X-RateLimit-*`; kill Redis mid-test and confirm fail-open (R53–R55).
10. **Seeding idempotence** — run setup/sync-role-acls twice; second run changes nothing; manually added features are never removed (R35, R57).
