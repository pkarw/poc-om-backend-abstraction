# Auth & RBAC (staff auth, API keys, features/ACLs, customer/portal auth)

> Analyzed at upstream commit adc9da27759e357febe9ed8d4b7182040d127349 (2026-07-01). Regenerate via the om-sync-upstream skill.

## Purpose

Open Mercato has **three independent authentication planes**, all multi-tenant:

1. **Staff (backend) auth** — `auth` module. Password login → HMAC-SHA256 JWT (`auth_token` cookie or `Authorization: Bearer`) bound to a DB `sessions` row (`sid` claim), plus an opaque refresh token (`session_token` cookie). RBAC via string **features** with wildcards, resolved from role-level and user-level ACLs.
2. **Machine auth** — `api_keys` module. Bcrypt-hashed opaque secrets (`omk_…`) sent via `X-Api-Key` or `Authorization: ApiKey …`; keys carry a role list and tenant/org scope and are evaluated through the same `RbacService`.
3. **Customer/portal auth** — `customer_accounts` module (the `portal` module is frontend-only pages; it reuses customer auth). Separate `customer_users` table, separate cookies (`customer_auth_token`, `customer_session_token`), separate JWT *audience* (`customer`) derived from the same base `JWT_SECRET`, separate portal RBAC (`CustomerRbacService`, `isPortalAdmin` instead of `isSuperAdmin`).

Tenancy comes from the `directory` module: `tenants` → `organizations` (tree). Staff users hang off `(tenant_id, organization_id)` FK-less UUID columns; roles are strictly tenant-scoped.

## Key source locations

| Path (repo-relative) | Contents |
|---|---|
| `packages/core/src/modules/auth/api/login.ts` | `POST /api/auth/login` — form login, rate limits, JWT + cookies |
| `packages/core/src/modules/auth/api/logout.ts` | `POST /api/auth/logout` — session delete + cookie clear + 302 |
| `packages/core/src/modules/auth/api/session/refresh.ts` | `GET`/`POST /api/auth/session/refresh` — refresh-token → new JWT |
| `packages/core/src/modules/auth/api/reset.ts`, `api/reset/confirm.ts` | Password reset request/confirm |
| `packages/core/src/modules/auth/api/feature-check.ts` | `POST /api/auth/feature-check` — batch feature evaluation |
| `packages/core/src/modules/auth/api/features.ts` | `GET /api/auth/features` — catalog of declared features |
| `packages/core/src/modules/auth/api/users/route.ts`, `api/roles/route.ts`, `api/roles/acl/route.ts`, `api/users/acl/route.ts` | Staff user/role/ACL admin CRUD |
| `packages/core/src/modules/auth/services/authService.ts` | Password verify (timing-equalized), sessions, password resets, role names |
| `packages/core/src/modules/auth/services/rbacService.ts` | ACL resolution + caching + `userHasAllFeatures` |
| `packages/core/src/modules/auth/lib/sessionIntegrity.ts` | `resolveCanonicalStaffAuthContext` — per-request JWT↔DB revalidation |
| `packages/core/src/modules/auth/lib/tokenHash.ts` | Opaque token generation + HMAC hashing (sessions, resets) |
| `packages/core/src/modules/auth/lib/emailHash.ts` → `packages/shared/src/lib/encryption/aes.ts` | Deterministic email lookup hash (`hashForLookup`) |
| `packages/core/src/modules/auth/lib/tenantAccess.ts` | `normalizeTenantId`, `resolveIsSuperAdmin`, `enforceTenantSelection` |
| `packages/core/src/modules/auth/lib/rateLimitCheck.ts` | Two-layer (IP + IP:emailhash) fail-open auth rate limiting |
| `packages/core/src/modules/auth/lib/setup-app.ts` | `setupInitialTenant`, `ensureRoles`, `ensureDefaultRoleAcls`, `ensureCustomRoleAcls` |
| `packages/core/src/modules/auth/cli.ts` | CLI: `setup`, `add-user`, `seed-roles`, `sync-role-acls`, `set-password`, `list-*`, `rotate-encryption-key` |
| `packages/core/src/modules/auth/data/entities.ts` | `users`, `roles`, `user_roles`, `sessions`, `password_resets`, `role_acls`, `user_acls`, `user_consents`, sidebar prefs |
| `packages/core/src/modules/auth/data/validators.ts` | Zod: `userLoginSchema`, reset schemas, `featureCheckRequestSchema`, `userCreateSchema` |
| `packages/core/src/modules/auth/acl.ts` | Auth module feature IDs |
| `packages/core/src/modules/auth/setup.ts` | `defaultRoleFeatures: { admin: ['auth.*'] }` |
| `packages/core/src/modules/auth/events.ts` | `auth.login.success/failed`, `auth.logout`, `auth.password.*`, user/role CRUD events |
| `packages/shared/src/lib/auth/jwt.ts` | JWT sign/verify (HS256, audience-derived secrets, legacy grace) |
| `packages/shared/src/lib/auth/server.ts` | `AuthContext`, request auth resolution (JWT → API key fallback), superadmin scope cookies |
| `packages/shared/src/lib/auth/featureMatch.ts` | `matchFeature`, `hasAllFeatures` wildcard semantics |
| `packages/shared/src/lib/auth/apiKeyAuthCache.ts` | In-process API-key auth cache (fingerprinted secrets, TTLs) |
| `packages/shared/src/lib/auth/passwordPolicy.ts` | Env-driven password policy |
| `packages/create-app/template/src/app/api/[...slug]/route.ts` | **The API dispatcher**: auth resolution, `requireAuth`/`requireFeatures` enforcement, 401/403 envelopes, tenant-candidate guard |
| `packages/core/src/modules/api_keys/services/apiKeyService.ts` | Key generation/hash/verify, session keys, one-time keys |
| `packages/core/src/modules/api_keys/data/entities.ts` | `api_keys` table |
| `packages/core/src/modules/api_keys/api/keys/route.ts` | `GET/POST/DELETE /api/api_keys/keys` |
| `packages/core/src/modules/api_keys/acl.ts` + `setup.ts` | `api_keys.view/create/delete`; admin default `api_keys.*` |
| `packages/core/src/modules/directory/data/entities.ts` | `tenants`, `organizations` (tree columns) |
| `packages/core/src/modules/directory/constants.ts` | `ALL_ORGANIZATIONS_COOKIE_VALUE = '__all__'` |
| `packages/core/src/modules/directory/utils/organizationScope.ts` | `resolveFeatureCheckContext` (org used for feature checks) |
| `packages/core/src/modules/customer_accounts/api/login.ts`, `api/portal/sessions-refresh.ts`, `api/portal/logout.ts` | Customer login/refresh/logout |
| `packages/core/src/modules/customer_accounts/services/customerSessionService.ts` | Customer sessions + audience JWT |
| `packages/core/src/modules/customer_accounts/services/customerRbacService.ts` | Portal ACLs (`isPortalAdmin`) |
| `packages/core/src/modules/customer_accounts/lib/customerAuth.ts` | `getCustomerAuthFromRequest`, `requireCustomerAuth`, `requireCustomerFeature` |
| `packages/shared/src/lib/ratelimit/{config.ts,helpers.ts}` | Rate limit envs, 429 envelope + headers |

## How it works

### JWT format and secrets (`packages/shared/src/lib/auth/jwt.ts`)

- Hand-rolled **HS256** JWT: header `{"alg":"HS256","typ":"JWT"}`, base64url (no padding, `+`→`-`, `/`→`_`), signature = HMAC-SHA256 over `header.payload` with the signing key; verify uses `crypto.timingSafeEqual` after length check.
- Body always gets `iat` and `exp` (default TTL **8h = 28800 s**), then payload spread, then `iss`/`aud` if not already present. Default `iss = 'open-mercato'`, default audience `'staff'`.
- **Audience-derived secrets**: the signing key is NOT the raw `JWT_SECRET`. For audience `A`: normalize `A` (lowercase, non-alnum → `_`), check env override `JWT_${A_UPPER}_SECRET`; otherwise `HMAC-SHA256(key = JWT_SECRET, message = 'open-mercato:jwt:v1:' + normalizedAudience)` hex digest. Memoized per `(audience, baseSecret)`.
- `verifyJwt(token)` (no explicit secret) verifies against the staff-derived secret and enforces `aud === 'staff'` and `iss === 'open-mercato'`; expired (`exp < now`) → null. **Legacy grace fallback**: if that fails and `JWT_LEGACY_GRACE_MINUTES` isn't `0|false|off` (default enabled), retry with the *raw* `JWT_SECRET` and no aud/iss enforcement; success sets `payload._legacyToken = true`.
- `signAudienceJwt(audience, payload, ttl)` / `verifyAudienceJwt(audience, token)` wrap the same functions; used by customer auth with audience `'customer'`. Staff JWTs cannot verify as customer and vice versa despite the shared base secret.

Staff JWT payload as issued by login (`auth/api/login.ts`):

```json
{
  "iat": 1767225600, "exp": 1767254400, "iss": "open-mercato", "aud": "staff",
  "sub": "<user uuid>", "sid": "<session uuid>",
  "tenantId": "<uuid|null>", "orgId": "<uuid|null>",
  "email": "user@example.com", "roles": ["admin", "employee"]
}
```

### Staff login flow (`auth/api/login.ts`)

1. Parse body: `application/x-www-form-urlencoded` (parsed via `URLSearchParams`) or any other content-type via `formData()`. Fields: `email`, `password`, `remember` (`on|1|true` etc. via `parseBooleanToken`), `tenantId` (alias `tenant`), `requireRole` (alias `role`, comma-separated list), `redirect`. Parse failure → all-empty fields (falls to 400).
2. **Rate limits (before validation/DB)** via `checkAuthRateLimit`: IP layer (`login-ip`, default 20/60s, block 60s) then compound layer keyed `"{ip}:{computeEmailHash(email)}"` (`login`, default 5/60s, block 60s). Env overrides `RATE_LIMIT_LOGIN[_IP]_{POINTS,DURATION,BLOCK_DURATION}`. Fail-open (limiter errors ignored). 429 body `{"error":"Too many requests. Please try again later."}` with `Retry-After`, `X-RateLimit-Limit/Remaining/Reset` headers.
3. Validate `{email, password, tenantId?}` with `userLoginSchema` (`email` email, `password` min 6, `tenantId` uuid optional). Failure → **400** `{"ok":false,"error":"Invalid credentials"}`.
4. User lookup: with `tenantId` → `findUserByEmailAndTenant`; without → `findUsersByEmail` and **only accept a unique match** (ambiguous multi-tenant email is treated as no user; deliberate anti-oracle, issue #2242). Lookup matches `email` OR `email_hash IN (lookupHashCandidates(email))` with `deletedAt: null` (email may be encrypted at rest).
5. `verifyPassword(user, password)` **always** runs bcrypt `compare` — against the fixed dummy hash `$2b$10$OcZrhmZpIzJOjkfwUrk7d.Nl0eHNzOvalBcBlt5Ran.4lj8R3HZg6` if user/hash missing — so unknown-email, wrong-password and multi-tenant all return an identical **401** `{"ok":false,"error":"Invalid email or password"}` with identical latency. Emits `auth.login.failed` (fire-and-forget).
6. Optional role gate: if `requireRole` given, user's role names (tenant-scoped) must intersect; else **403** `{"ok":false,"error":"Not authorized for this area"}`.
7. On success: `updateLastLoginAt` (native update), compound rate-limit key reset, best-effort `query_index.coverage.warmup` event, roles re-resolved for the effective tenant.
8. Session: `expiresAt = now + (remember ? REMEMBER_ME_DAYS(default 30) days : 8h)`. `createSession` generates raw token = `randomBytes(32).hex` (64 chars), stores `HMAC-SHA256(secret, raw).hex` in `sessions.token` (secret = first of `AUTH_TOKEN_SECRET | AUTH_SECRET | NEXTAUTH_SECRET | JWT_SECRET`; prod refuses to boot without one, dev falls back to `'om-auth-token-dev-only-secret'` with a warning).
9. JWT signed with claims above; `auth.login.success` emitted. Response body passes through **custom-route after-interceptors** (`routePath: 'auth/login'`) which may replace token/refreshToken/status.
10. **200 body**: `{"ok":true,"token":"<jwt>","redirect":"<sanitized path, default /backend>"}` plus `"refreshToken":"<raw session token>"` only when `remember` was set. Cookies (all `HttpOnly; Path=/; SameSite=Lax; Secure` in production):
    - `auth_token=<jwt>`, `Max-Age=28800` (always).
    - `session_token=<raw refresh token>`: with `remember` → `Expires = now + REMEMBER_ME_DAYS days`; without `remember` (and untouched token) → `Max-Age=28800`.

### Refresh (`auth/api/session/refresh.ts`, `requireAuth: false` both methods)

- **GET** (browser): reads `session_token` cookie. Missing/invalid → clear both cookies + **302** to `/login?redirect=<encoded>`. Valid → `refreshFromSessionToken` (hash lookup, expiry check, live user, roles), sign a fresh JWT `{sub, sid, tenantId: String(user.tenantId), orgId: String(user.organizationId), email, roles}` and 302 to sanitized `redirect` (default `/`), setting `auth_token` (Max-Age 28800). Note: GET stringifies tenant/org so a null becomes `"null"` — the canonical-context revalidation on the next request normalizes this.
- **POST** (API/mobile): JSON `{"refreshToken": string}` (`refreshSessionRequestSchema`, min 1). Rate limits `refresh`/`refresh-ip` (15/60s and 60/60s, block 60s). Missing token → **400** `{"ok":false,"error":"Missing or invalid refresh token"}` + cookie clear. Invalid/expired → **401** `{"ok":false,"error":"Invalid or expired refresh token"}` + cookie clear. Success → **200** `{"ok":true,"accessToken":"<jwt>","expiresIn":28800}` and sets `auth_token` cookie.
- The refresh token is opaque and **not rotated** on use; it stays valid until session expiry/deletion.

### Logout (`auth/api/logout.ts`, `requireAuth: true` for POST)

Reads `session_token` and `auth_token` cookies; deletes the session by id (`sid` from a still-verifiable `auth_token`) and/or by token hash; always responds **302 redirect to `/login`** (request-origin absolute URL) and clears both cookies (`Max-Age=0`).

### Request authentication (`packages/shared/src/lib/auth/server.ts` + dispatcher)

`resolveAuthFromRequestDetailed(req)` returns `{auth, status: 'authenticated'|'missing'|'invalid'}`:

1. Trusted-context short-circuit: a symbol-keyed envelope (`Symbol.for('open-mercato.auth.trustedContext')`) attached to synthetic Requests bypasses parsing.
2. Token extraction order: `Authorization: Bearer <jwt>` (case-insensitive prefix), else `auth_token` cookie (URL-decoded).
3. `verifyJwt(token)`; a payload with `type === 'customer'` is **rejected** (`invalid`) — customer JWTs never authenticate staff routes.
4. **Canonical revalidation** (`resolveCanonicalStaffAuthContext`, `auth/lib/sessionIntegrity.ts`) on every request:
   - `sub`, actor tenant/org must be `null` or UUID-shaped (else reject).
   - If JWT has `sid`: session row must exist with `id = sid AND user = sub AND deleted_at IS NULL` and not be expired → this is what makes logout/password-reset revoke live JWTs. No `sid` is only allowed when `_legacyToken === true` (legacy-grace verified).
   - User must exist, not deleted, and the user's **current** `tenant_id`/`organization_id` must equal the JWT claims (moving a user invalidates old tokens).
   - Recomputes fresh `roles` (live tenant-scoped, soft-deleted role links dropped) and `isSuperAdmin` (user ACL `is_super_admin` OR any linked role's RoleAcl `is_super_admin`).
   - Returns `{...auth, sub, tenantId, orgId, roles, isSuperAdmin}` or `null`.
5. If JWT missing/invalid, **API key fallback**: `extractApiKey` = `X-Api-Key` header, else `Authorization: ApiKey <secret>`.
6. **Superadmin scope override**: when the resolved context has `isSuperAdmin === true`, cookies `om_selected_tenant` and `om_selected_org` overwrite `tenantId`/`orgId` (originals kept as `actorTenantId`/`actorOrgId`); org value `__all__` (or empty) → `orgId = null`; role list gets `'superadmin'` appended if absent.

`AuthContext` shape (nullable):

```ts
type AuthContext = {
  sub: string; sid?: string | null
  tenantId: string | null; orgId: string | null
  email?: string; roles?: string[]
  isApiKey?: boolean; userId?: string; keyId?: string; keyName?: string
  // plus isSuperAdmin, actorTenantId, actorOrgId, _legacyToken at runtime
} | null
```

### The dispatcher & guards (`packages/create-app/template/src/app/api/[...slug]/route.ts`)

All module API routes are served through one Next.js catch-all `/api/[...slug]`. Module file `packages/core/src/modules/<mod>/api/<path>.ts` (or `<path>/route.ts`) maps to **`/api/<mod>/<path>`** (e.g. `auth/api/session/refresh.ts` → `/api/auth/session/refresh`, `api_keys/api/keys/route.ts` → `/api/api_keys/keys`). Per method:

- Route metadata: per-method `{ requireAuth?: boolean, requireFeatures?: string[], rateLimit?: {...} }` (legacy single-object metadata is normalized to per-method). **`requireRoles` is deprecated and NOT enforced** — only warned (role names are tenant-mutable/spoofable).
- **Default is authenticated**: `requireAuth !== false` requires a non-null auth → else **401** `{"error":"Unauthorized"}`.
- **Tenant parameter-pollution guard** (issue #2665): every distinct `tenantId` candidate — every repeated `?tenantId=` query param plus a body-level `tenantId` (JSON object field or form field, on non-GET/HEAD/OPTIONS) — is validated via `enforceTenantSelection`; a non-superadmin targeting a foreign tenant → **403** `{"error":"Not authorized to target this tenant."}`. Literal strings `"null"`/`"undefined"` are normalized.
- **Feature guard**: if `requireFeatures` non-empty → must be authed (**401** otherwise), then `rbacService.userHasAllFeatures(auth.sub, requiredFeatures, {tenantId, organizationId})` where the org comes from `resolveFeatureCheckContext` (selected-org cookie → auth.orgId if allowed → first allowed org). Failure → **403** `{"error":"Forbidden","requiredFeatures":[...]}` (and a console warning with granted features).
- Optional per-route IP rate limit, then handler runs inside `runWithCacheTenant(auth?.tenantId)` and receives `ctx = { params, auth }`.
- If auth resolution status was `invalid` and the response is 401, the response **clears `auth_token` and `session_token` cookies** (`Max-Age=0`).
- No route match / no handler → **404** `{"error":"Not Found"}`.

### RBAC model

**Tables** (`auth/data/entities.ts`):

- `roles(id uuid pk, name text, tenant_id uuid NOT NULL, created_at, updated_at, deleted_at)` — unique `(tenant_id, name)`. Roles are always tenant-scoped; "global roles are not supported" (`ensureRoles` throws without tenant).
- `user_roles(id, user_id, role_id, created_at, deleted_at)`.
- `role_acls(id, role_id, tenant_id uuid NOT NULL, features_json json[], is_super_admin bool default false, organizations_json json[]|null, timestamps)` — `organizations_json` null/empty/contains-`'__all__'` ⇒ all orgs.
- `user_acls` — same shape keyed by user; **a UserAcl row for `(user, tenant)` replaces role aggregation entirely** (exclusive override).

**Feature naming**: `<module>.<area>.<action>` or `<module>.<action>` strings declared in each module's `acl.ts` as `{ id, title, module }` (optionally `dependsOn`). Examples: `auth.users.list/create/edit/delete`, `auth.roles.list/manage`, `auth.acl.manage`, `auth.sidebar.manage`, `api_keys.view/create/delete`.

**Wildcard matching** (`packages/shared/src/lib/auth/featureMatch.ts`) — exact semantics a port must copy:

```ts
matchFeature(required, granted):
  granted === '*'            → true
  granted endsWith '.*'      → required === prefix || required.startsWith(prefix + '.')
  else                       → granted === required
hasAllFeatures(required, granted):
  required empty → true; granted empty → false
  every required has some granted matching
```

**`RbacService.loadAcl(userId, {tenantId, organizationId})`** (`auth/services/rbacService.ts`) returns `{isSuperAdmin, features[], organizations: string[]|null}`:

1. Cache lookup: key `rbac:{userId}:{tenantId||'null'}:{organizationId||'null'}`, TTL 5 min, tags `rbac:user:{id}`, `rbac:all`, `rbac:tenant:{t}`, `rbac:org:{o}` (backed by the shared `CacheStrategy`, i.e. Redis-capable).
2. Non-API-key subjects: **global super-admin check first** (any `user_acls.is_super_admin=true` row, else any RoleAcl with `is_super_admin` on any linked role, tenant-agnostic; memoized in-process) → `{isSuperAdmin:true, features:['*'], organizations:null}`.
3. Subjects `api_key:<id>`: load key, expired/deleted ⇒ empty ACL; tenant = scope.tenantId || key.tenantId; aggregate RoleAcls of `key.roles_json`; `organizations` starts as `[key.organizationId]` if set, and RoleAcls with null/`'__all__'` orgs widen it to `null`.
4. Users: resolve tenant (scope → user default; none ⇒ empty ACL). If a `user_acls` row exists for `(user, tenant)` → use it verbatim. Else aggregate all `role_acls` of the user's tenant roles: OR `is_super_admin`, union `features` (order-preserving dedupe), org lists union — any null/`'__all__'` list ⇒ `organizations = null` (all).

**`userHasAllFeatures(userId, required, scope)`**: empty `required` ⇒ true; super-admin ⇒ true (modulo enabled-modules registry — feature's owning module must be enabled when a module allowlist is configured); org restriction: if ACL orgs is a list and `scope.organizationId` not in it (and no `'__all__'`) ⇒ false; else wildcard `hasAllFeatures` over grants filtered by enabled modules.

Cache invalidation: `invalidateUserCache(userId)` (also drops org-scope cache tag `org-scope:user:{id}`), `invalidateTenantCache`, `invalidateOrganizationCache`, `invalidateAllCache`. Role ACL edits must invalidate the tenant.

**Default role seeding** (`auth/lib/setup-app.ts`): built-in role names `employee`, `admin`, `superadmin`. Each module's `setup.ts` may export `defaultRoleFeatures: { superadmin?: string[], admin?: string[], employee?: string[], <customRole>?: string[] }` (auth: `admin: ['auth.*']`; api_keys: `admin: ['api_keys.*']`). `ensureDefaultRoleAcls(em, tenantId, modules, {includeSuperadminRole})` merges these per role and upserts one RoleAcl per role per tenant — features are **merged idempotently** (set union, never removed); superadmin role's ACL gets `is_super_admin=true`. `ensureCustomRoleAcls` re-runs the custom-role subset after app seeders. CLI `mercato auth sync-role-acls [--tenant <id>] [--no-superadmin]` runs both for one or all tenants.

**Bootstrap**: CLI `mercato auth setup --orgName <n> --email <e> --password <p> [--orgSlug s] [--roles csv] [--skip-password-policy] [--with-examples] [--json]` → `setupInitialTenant`: creates Tenant `"<orgName> Tenant"` + Organization, roles, primary user (superadmin) + derived `admin@…`/`employee@…` users (env `OM_INIT_ADMIN_EMAIL/PASSWORD`, `OM_INIT_EMPLOYEE_EMAIL/PASSWORD`, default password `secret`), role ACLs, tenant DEK when encryption is on, then module `onTenantCreated` hooks. The `mercato init` flow defaults credentials from `OM_INIT_SUPERADMIN_EMAIL`/`OM_INIT_SUPERADMIN_PASSWORD` (fallback `superadmin@acme.com`/`secret`). Passwords hashed with **bcrypt cost 10**. Password policy (`passwordPolicy.ts`): default min 6 + require digit + uppercase + special; envs `OM_PASSWORD_MIN_LENGTH`, `OM_PASSWORD_REQUIRE_DIGIT/UPPERCASE/SPECIAL` (and `NEXT_PUBLIC_` variants).

### API keys (`api_keys` module)

- **Secret format**: `omk_{4 bytes hex}.{24 bytes hex}` (e.g. `omk_a1b2c3d4.…`, 61 chars). `keyPrefix = secret.slice(0, 12)` stored in plaintext with a **unique index**; full secret stored only as **bcrypt(cost 10)** in `key_hash`.
- **Lookup** (`findApiKeyBySecret`): find candidates by `key_prefix` + `deleted_at IS NULL`, skip expired, bcrypt-compare each.
- **Wire format**: `X-Api-Key: <secret>` header, or `Authorization: ApiKey <secret>` (case-insensitive scheme). Only consulted when no valid staff JWT was presented.
- **Auth context building** (`resolveApiKeyAuth` in `shared/lib/auth/server.ts`): resolve role names from `roles_json` (live roles), compute `isSuperAdmin` from any RoleAcl `is_super_admin` on those roles; keys tied to a user (`session_user_id ?? created_by`) require that user to exist, be live, and match the key's tenant AND org exactly (else auth fails); user-less keys require the key's tenant/org to exist, be active, and be mutually consistent. Result: `{sub: "api_key:<id>", tenantId, orgId, roles, isApiKey: true, isSuperAdmin, keyId, keyName, userId?}`. RBAC then runs with subject `api_key:<id>` (see `loadAcl` branch above — the global-superadmin shortcut is skipped for API keys).
- **In-process cache** (`apiKeyAuthCache.ts`): keyed by HMAC fingerprint of the secret (random per-process key), success TTL 30 s (`OM_API_KEY_AUTH_TTL_MS`), negative TTL 5 s (`OM_API_KEY_AUTH_NEGATIVE_TTL_MS`), LRU max 1000 entries; success expiry clamped to key `expires_at`. `last_used_at` written at most every 60 s per key (`OM_API_KEY_LAST_USED_WRITE_INTERVAL_MS`).
- **Session keys** (AI chat): name `__session_{sess_<32hex>}__`, TTL default 30 min, secret AES-GCM-encrypted with the tenant DEK for later recovery (`session_secret_encrypted`); `withOnetimeApiKey` creates a ≤5-min key, runs a callback, soft-deletes.
- **Management API** `/api/api_keys/keys` (guards `api_keys.view|create|delete`): GET paginated list (`{items,total,page,pageSize,totalPages}` — item: `id,name,description,keyPrefix,organizationId,organizationName,createdAt,lastUsedAt,expiresAt,roles:[{id,name}]`); POST (`createApiKeySchema`: `name` 1–120, `description ≤1000`, `tenantId?`, `organizationId?`, `roles: string[]` of role UUIDs or names, `expiresAt?` future date) returns `{id,name,keyPrefix,secret,tenantId,organizationId,roles}` — **secret shown exactly once**; role grants are checked against the actor's own ACL (`assertActorCanGrantRoles`), cross-tenant roles → 400, missing tenant context → 400 `{"error":"Tenant context required"}`; DELETE `?id=<uuid>` soft-deletes and invalidates caches.

### Multi-tenant directory model

`tenants(id, name, is_active, timestamps, deleted_at)`; `organizations(id, tenant_id FK, name, slug (unique per tenant), logo_url, is_active, parent_id, root_id, tree_path, depth, ancestor_ids jsonb, child_ids jsonb, descendant_ids jsonb, timestamps)`. A staff user belongs to exactly one `(tenant_id, organization_id)` pair (nullable columns, no join table); **visibility across organizations** is granted through ACL `organizations_json` lists plus the org tree (org-scope resolution in `directory/utils/organizationScope.ts` expands to descendants). Superadmins switch tenant/org via `om_selected_tenant` / `om_selected_org` cookies; ordinary users can select among their allowed orgs via `om_selected_org` (validated against the resolved scope). Email uniqueness for users is **per-tenant** via partial unique index `users_tenant_email_hash_uniq (tenant_id, email_hash) WHERE deleted_at IS NULL AND email_hash IS NOT NULL` (raw SQL in `Migration20260610120000`).

`email_hash` = `hashForLookup(value)`: normalized (trim/lowercase per `normalizeLookupValue`), then `'v2:' + HMAC-SHA256(pepper, value).hex` where pepper = `LOOKUP_HASH_PEPPER || TENANT_DATA_ENCRYPTION_FALLBACK_KEY || TENANT_DATA_ENCRYPTION_KEY`; without any pepper, legacy plain `SHA-256(value).hex`. Reads use `lookupHashCandidates` = `[v2, legacy]` in `$in` filters.

### Customer / portal auth (`customer_accounts`; `portal` module is frontend pages only)

- `POST /api/customer_accounts/login` — JSON `{email, password, tenantId?, organizationId?}`; tenant resolved from body or request host (`resolveTenantContext`). Failure branches (unknown, no-hash, inactive, locked, wrong password, email-not-verified) all bcrypt-equalized and return the **same 401** `{"ok":false,"error":"Invalid email or password"}`; wrong password increments a lockout counter. Success: portal ACL loaded (`CustomerRbacService`: per-user `customer_user_acls` override else aggregated `customer_role_acls`; `isPortalAdmin` ⇒ features `['*']`), session row (`customer_user_sessions`, token = `randomBytes(32).base64url`, stored as plain `SHA-256` hex — **no HMAC pepper here**, TTL `CUSTOMER_SESSION_TTL_DAYS` default 30, per-user cap `MAX_CUSTOMER_SESSIONS_PER_USER` default 5 with oldest-first revocation). Response **200** `{"ok":true,"user":{id,email,displayName,emailVerified},"resolvedFeatures":[…]}` + cookies `customer_auth_token` (JWT, Max-Age 28800) and `customer_session_token` (raw, Max-Age 30 d), both HttpOnly/Lax/Secure-in-prod.
- Customer JWT: audience `'customer'`, TTL 8 h, payload `{sub, sid, type:'customer', tenantId, orgId, email, displayName, customerEntityId, personEntityId, resolvedFeatures}`.
- `getCustomerAuthFromRequest`: Bearer or `customer_auth_token` cookie; `verifyAudienceJwt('customer', …)` with raw-secret legacy fallback; requires `type === 'customer'`; `sid` session must still be active (fail-closed on infra errors); user re-validated (live, active, `sessions_revoked_at` vs `iat`); features re-resolved fresh. `requireCustomerAuth` throws 401 `{"ok":false,"error":"Authentication required"}`; `requireCustomerFeature` throws 403 `{"ok":false,"error":"Insufficient permissions"}`.
- `POST /api/customer_accounts/portal/sessions-refresh`: cookie `customer_session_token` → 401 variants `{"ok":false,"error":"No session token"|"Invalid or expired session"|"Account not active"|"Session refresh failed"}`; success 200 `{"ok":true,"resolvedFeatures":[…]}` + fresh `customer_auth_token`.

## Public contracts

### Staff endpoints (all under `/api`)

| Method & path | Auth | Request | Success | Errors |
|---|---|---|---|---|
| `POST /api/auth/login` | none | form-urlencoded: `email`, `password`, `remember?`, `tenantId?`/`tenant?`, `requireRole?`/`role?` (CSV), `redirect?` | 200 `{"ok":true,"token":"<jwt>","redirect":"/backend","refreshToken?":"<64 hex>"}` + cookies `auth_token`, `session_token` | 400 `{"ok":false,"error":"Invalid credentials"}`; 401 `{"ok":false,"error":"Invalid email or password"}`; 403 `{"ok":false,"error":"Not authorized for this area"}`; 429 `{"error":"Too many requests. Please try again later."}` |
| `POST /api/auth/logout` | required | cookies only | 302 → `/login`, clears both cookies | (dispatcher 401 if unauthenticated) |
| `GET /api/auth/session/refresh?redirect=` | none | `session_token` cookie | 302 → redirect target, sets `auth_token` | 302 → `/login?redirect=…` + cookie clear |
| `POST /api/auth/session/refresh` | none | JSON `{"refreshToken":"…"}` | 200 `{"ok":true,"accessToken":"<jwt>","expiresIn":28800}` + `auth_token` cookie | 400 `{"ok":false,"error":"Missing or invalid refresh token"}`; 401 `{"ok":false,"error":"Invalid or expired refresh token"}` (both clear cookies); 429 |
| `POST /api/auth/reset` | none | form: `email` | always 200 `{"ok":true}` (no account-existence leak) | 422 `{"error":"Validation failed","fieldErrors":{…}}`; 429; 400/500 for origin misconfig |
| `POST /api/auth/reset/confirm` | none | form: `token` (min 10), `password` (policy) | 200 `{"ok":true,"redirect":"/login"}` — reset token single-use (atomic CAS on `used_at`), deletes **all** user sessions | 400 `{"ok":false,"error":"Invalid request"|"Invalid or expired token"}`; 429 |
| `POST /api/auth/feature-check` | required | JSON `{"features":[string ≤128] ≤50}` | 200 `{"ok":bool,"granted":[…],"userId":"…"}` (per-feature evaluation when overall check fails) | 400 `{"ok":false,"error":"Invalid request"}`; 401 `{"ok":false,"error":"Unauthorized"}` |
| `GET /api/auth/features` | `auth.acl.manage` | — | 200 `{"items":[{"id","title","module","dependsOn?"}],"modules":[{"id","title"}]}` sorted by module then id | 401 |
| `GET/POST/PUT/DELETE /api/auth/users` | `auth.users.list/create/edit/delete` | staff user CRUD | | |
| `GET/POST/PUT/DELETE /api/auth/roles` | `auth.roles.list` (GET) / `auth.roles.manage` | role CRUD | | |
| `GET/PUT /api/auth/roles/acl`, `/api/auth/users/acl` | `auth.acl.manage` | role/user ACL editing | | |
| `GET/POST/DELETE /api/api_keys/keys` | `api_keys.view/create/delete` | see above | 200 list / 201 create (`secret` once) / 200 `{"success":true}` | 400 `{"error":"Tenant context required"}`, 400 role errors, 403 `{"error":"Organization out of scope"}`, 404 |

### Dispatcher-level envelopes (uniform for every route)

- 401: `{"error":"Unauthorized"}` (+ cookie clearing when the presented token was invalid rather than absent)
- 403 feature: `{"error":"Forbidden","requiredFeatures":["…"]}`
- 403 tenant guard: `{"error":"Not authorized to target this tenant."}`
- 404: `{"error":"Not Found"}`
- 429: `{"error":"<msg>"}` + headers `Retry-After`, `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset`
- CRUD helpers throw `CrudHttpError(status, {error})` → `badRequest 400 / forbidden 403 / notFound 404 / conflict 409`, all `{"error": string}`.

### Cookies & headers

| Name | Plane | Content | Attributes |
|---|---|---|---|
| `auth_token` | staff | JWT (aud `staff`) | HttpOnly, Path=/, SameSite=Lax, Secure(prod), Max-Age 28800 |
| `session_token` | staff | raw refresh token (64 hex) | same; Expires now+REMEMBER_ME_DAYS d (remember) or Max-Age 28800 |
| `customer_auth_token` | customer | JWT (aud `customer`) | same; Max-Age 28800 |
| `customer_session_token` | customer | raw token (base64url 43) | same; Max-Age 2592000 |
| `om_selected_tenant`, `om_selected_org` | staff UI | tenant/org UUID; org may be `__all__` | read by auth resolution (superadmin scope) & org-scope filter |
| `Authorization: Bearer <jwt>` | staff & customer APIs | takes precedence over cookies | |
| `X-Api-Key: <secret>` / `Authorization: ApiKey <secret>` | machine | checked only after JWT path fails | |

### Database tables (Postgres, all soft-deletable via `deleted_at`)

- `users(id uuid pk gen_random_uuid(), tenant_id uuid?, organization_id uuid?, email text, email_hash text (idx users_email_hash_idx), name text?, password_hash text?, is_confirmed bool def true, last_login_at, created_at, updated_at, deleted_at)` + partial unique `(tenant_id, email_hash) WHERE deleted_at IS NULL AND email_hash IS NOT NULL`
- `roles(id, name, tenant_id NOT NULL, …)` unique `(tenant_id, name)`; `user_roles(id, user_id, role_id, created_at, deleted_at)` + idx on both FKs
- `sessions(id, user_id, token text UNIQUE ← HMAC-SHA256 hex, expires_at, created_at, last_used_at, deleted_at)`
- `password_resets(id, user_id, token text UNIQUE ← HMAC-SHA256 hex, expires_at (now+1h), used_at, created_at, deleted_at)`
- `role_acls(id, role_id, tenant_id NOT NULL, features_json json, is_super_admin bool def false, organizations_json json?, …)`; `user_acls` same keyed by `user_id`
- `api_keys(id, name, description?, tenant_id?, organization_id?, key_hash text (bcrypt), key_prefix text UNIQUE, roles_json json (role UUIDs), created_by uuid?, session_token text?, session_user_id uuid?, session_secret_encrypted text?, last_used_at, expires_at?, …)`
- `tenants` / `organizations` — see directory model above
- `user_consents(id, user_id, tenant_id?, organization_id?, consent_type, is_granted, granted_at, withdrawn_at, source, ip_address, integrity_hash, …)` unique `(user_id, tenant_id, consent_type)`

### Redis / cache structures

- RBAC ACL cache (via shared `CacheStrategy`, Redis when configured): key `rbac:{userId}:{tenantId|'null'}:{orgId|'null'}` → `{isSuperAdmin:boolean, features:string[], organizations:string[]|null}`; TTL 300 000 ms; tags `rbac:user:{id}`, `rbac:tenant:{id}`, `rbac:org:{id}`, `rbac:all` (tag-based deletion API required). Customer analogue `customer_rbac:{userId}:{tenantId}:{orgId}` with tags `customer_rbac:user/tenant/all`.
- Rate limiter: strategy `RATE_LIMIT_STRATEGY=memory|redis` (`REDIS_URL`), key prefixes per endpoint (`login`, `login-ip`, `refresh`, `refresh-ip`, `reset`, `reset-ip`, `reset-confirm`), knobs `RATE_LIMIT_{PREFIX}_{POINTS,DURATION,BLOCK_DURATION}`, `RATE_LIMIT_ENABLED`, `RATE_LIMIT_TRUST_PROXY_DEPTH` (default 1). Client IP: X-Forwarded-For honored only when trust depth > 0 (Nth-from-last entry), else `X-Real-IP`.
- API-key auth cache is in-process only (not Redis).

## Helpers to mirror

| Helper (source) | Signature / behavior |
|---|---|
| `signJwt(payload, secretOrOptions?, expiresInSec?)` (`shared/lib/auth/jwt.ts`) | HS256; default staff audience + derived secret; adds `iat/exp/iss/aud`; TTL default 28800 |
| `verifyJwt(token, secretOrOptions?)` | timing-safe verify; aud/iss enforcement on default path; legacy raw-secret fallback flagging `_legacyToken` |
| `deriveJwtAudienceSecret(audience, base?)` | env `JWT_${AUD}_SECRET` override else `HMAC-SHA256(JWT_SECRET, 'open-mercato:jwt:v1:'+aud)` hex |
| `signAudienceJwt(aud, payload, ttl=28800)` / `verifyAudienceJwt(aud, token)` | audience-pinned wrappers |
| `matchFeature(required, granted): boolean` / `hasAllFeatures(required[], granted[]): boolean` (`featureMatch.ts`) | wildcard semantics (see above) |
| `generateAuthToken(): string` / `hashAuthToken(raw): string` (`auth/lib/tokenHash.ts`) | 32-byte hex token; HMAC-SHA256 with `AUTH_TOKEN_SECRET|AUTH_SECRET|NEXTAUTH_SECRET|JWT_SECRET` |
| `computeEmailHash(email)` / `emailHashLookupValues(email)` (`auth/lib/emailHash.ts`) | keyed `v2:` HMAC lookup hash + legacy candidate list |
| `AuthService.verifyPassword(user\|null, password)` | constant-time bcrypt vs dummy hash; returns false unless stored hash exists AND matches |
| `AuthService.createSession(user, expiresAt)` / `refreshFromSessionToken(token)` / `deleteSessionById/ByToken` / `deleteAllUserSessions(userId)` | session lifecycle (store hash only) |
| `AuthService.requestPasswordReset(email)` / `confirmPasswordReset(token, newPassword)` | 1-hour token; single-use via `nativeUpdate(…, {usedAt: null} → {usedAt: now})` CAS; confirm nukes all sessions |
| `RbacService.loadAcl(userId, scope)` / `userHasAllFeatures(userId, required, scope)` / `getGrantedFeatures` / `tenantHasFeature(tenantId, feature, {organizationId?})` / `invalidate{User,Tenant,Organization,All}Cache` | core authorization engine |
| `resolveCanonicalStaffAuthContext(em, auth)` / `isAuthContextValid` (`sessionIntegrity.ts`) | per-request JWT↔DB revalidation & role/superadmin recomputation |
| `resolveAuthFromRequestDetailed(req)` / `getAuthFromRequest(req)` / `getAuthFromCookies()` (`shared/lib/auth/server.ts`) | request → `AuthContext` with status |
| `extractApiKey(req)` / `resolveApiKeyAuth(secret)` | API key wire formats & context building |
| `normalizeTenantId(value)` / `resolveIsSuperAdmin(ctx)` / `enforceTenantSelection(ctx, requested)` (`tenantAccess.ts`) | tenant guard; throws `forbidden('Not authorized to target this tenant.')` |
| `checkAuthRateLimit({req, ipConfig, compoundConfig?, compoundIdentifier?})` / `resetAuthRateLimit(key, config)` (`rateLimitCheck.ts`) | fail-open two-layer limiter; compound key `"{ip}:{emailHash}"` |
| `checkRateLimit(service, config, key, msg)` / `getClientIp(req, trustProxyDepth)` (`ratelimit/helpers.ts`) | 429 response + headers |
| `generateApiKeySecret()` / `hashApiKey(secret)` / `verifyApiKey(secret, hash)` / `findApiKeyBySecret(em, secret)` (`apiKeyService.ts`) | `omk_` format, bcrypt-10, prefix lookup |
| `createApiKeyAuthCache(options)` (`apiKeyAuthCache.ts`) | fingerprinted LRU with success/negative TTLs and lastUsed throttling |
| `setupInitialTenant(em, options)` / `ensureRoles` / `ensureDefaultRoleAcls` / `ensureCustomRoleAcls` (`setup-app.ts`) | provisioning + role-ACL sync |
| `getPasswordPolicy()` / `validatePassword(pw, policy)` / `buildPasswordSchema()` (`passwordPolicy.ts`) | env-driven policy |
| `resolveFeatureCheckContext({container, auth, request})` (`directory/utils/organizationScope.ts`) | org id used in `requireFeatures` checks: selected org → auth org (if allowed) → first allowed |
| Customer side: `CustomerSessionService.createSession/refreshSession/revokeAllUserSessions`, `CustomerRbacService.loadAcl/userHasAllFeatures`, `getCustomerAuthFromRequest/requireCustomerAuth/requireCustomerFeature`, `generateSecureToken`/`hashToken` | portal plane equivalents |

## Behavioral details a port MUST replicate

1. **Route auth defaults**: absent metadata ⇒ authentication required; `requireAuth: false` must be explicit per method. `requireRoles` must be ignored (warn only) — only `requireFeatures` authorizes.
2. **Exact status codes on login**: 400 malformed/validation, 401 uniform invalid credentials (constant-time; identical body for unknown email / wrong password / multi-tenant ambiguity), 403 `requireRole` mismatch, 429 rate limited. Compound rate-limit counter is deleted on successful login.
3. **JWT specifics**: HS256; base64url unpadded; `exp` checked only when present; `aud`/`iss` strictly equal on default verification; audience-derived key exactly `HMAC-SHA256(JWT_SECRET, 'open-mercato:jwt:v1:'+normalizedAudience)` hex string used as HMAC key; `JWT_${AUD}_SECRET` env override; legacy raw-secret fallback gated by `JWT_LEGACY_GRACE_MINUTES` (default on).
4. **Session binding**: JWTs with `sid` are only valid while the session row `(id=sid, user=sub, deleted_at IS NULL, expires_at > now)` exists — logout, `deleteAllUserSessions`, and password-reset-confirm revoke live JWTs immediately. `sid`-less tokens are valid only through the legacy fallback path.
5. **Canonical context**: JWT `tenantId`/`orgId` must equal the user's *current* DB values or auth fails (`invalid`). Roles in the JWT are advisory; effective roles and `isSuperAdmin` are recomputed per request. Non-UUID scope claims (other than null/empty) ⇒ reject.
6. **Cookie clearing on invalid tokens**: when the presented staff token failed verification/revalidation and the final response is 401, clear `auth_token` + `session_token`.
7. **Customer/staff isolation**: a payload with `type:'customer'` never authenticates staff routes (returns `invalid`, not fallback-to-API-key… actually it returns immediately as invalid); staff JWTs fail customer verification by audience.
8. **ACL resolution order**: global superadmin shortcut (users only) → per-user ACL exclusive override → role aggregation. Org lists: null/empty/`'__all__'` ⇒ unrestricted; restricted list + out-of-scope `scope.organizationId` ⇒ deny regardless of features. Feature dedupe preserves insertion order.
9. **Wildcards**: `*` matches everything; `mod.*` matches `mod` itself and `mod.anything…`; everything else exact. Empty `required` ⇒ allow; empty grants with non-empty required ⇒ deny.
10. **Tenant guard**: every `tenantId` occurrence in query string (all repeats) and body must pass `enforceTenantSelection`; non-superadmins may only target their own tenant (or none); superadmins may target any (or `null` = global). `"null"`/`"undefined"` string literals are treated as null/absent.
11. **API keys**: prefix = first 12 chars incl. `omk_`; unique prefix; bcrypt verify over candidates; expired keys skipped; user-bound keys die with the user or on tenant/org mismatch; keyless-user keys require active tenant/org; secret returned exactly once at creation; RBAC subject id is literally `api_key:<uuid>`.
12. **Refresh semantics**: refresh token is opaque, not rotated, hashed at rest with the HMAC pepper; POST refresh returns `expiresIn: 28800`; both failure modes clear cookies.
13. **Password reset**: request endpoint always 200 `{"ok":true}` for unknown emails; token TTL 60 min; single-use enforced by atomic compare-and-set; confirm deletes all sessions; new hash bcrypt-10.
14. **Rate limiting is fail-open** (limiter/Redis failure never blocks auth) and disabled wholesale under `OM_INTEGRATION_TEST`; 429 must carry `Retry-After` and `X-RateLimit-*` headers.
15. **Seeding is merge-only**: `ensureDefaultRoleAcls`/`sync-role-acls` union features into existing RoleAcls and can set (never unset) `is_super_admin`; roles unique per `(tenant, name)`; roles without tenant are unsupported.
16. **Email lookups** must check both `email` equality and `email_hash IN (v2, legacy)`; new writes use the keyed `v2:` hash when a pepper is configured.
17. **Superadmin scope cookies** only apply when the canonical context says `isSuperAdmin`; they swap `tenantId`/`orgId` and preserve `actorTenantId`/`actorOrgId`; `__all__` org ⇒ `orgId=null`.
18. **Feature-check endpoint** falls back to per-feature evaluation to report the granted subset; `granted` echoes the requested strings, not expanded wildcards.
19. Customer login: every failure branch (incl. unverified email and lockout) is a 401 with the same body; lockout counter increments only on wrong password; session cap revokes oldest sessions; `sessions_revoked_at` invalidates all JWTs issued before it (compared against `iat`).

## Gotchas

- **The dispatcher lives in the app template**, not in a package: `packages/create-app/template/src/app/api/[...slug]/route.ts`. Any port of "middleware" behavior must come from there, not from `packages/shared`.
- `signJwt` spreads `payload` **after** `iat/exp`, so a payload could override them; `iss`/`aud` are only set when absent. Don't "fix" this ordering — interceptors rely on being able to pass through custom tokens.
- Two distinct 401 error bodies exist: dispatcher-level `{"error":"Unauthorized"}` vs endpoint-level `{"ok":false,"error":…}` (login/refresh/feature-check) — clients depend on both shapes.
- The GET refresh route signs `tenantId: String(user.tenantId)` — for a tenant-less user this literally embeds `"null"`/`"undefined"` strings; `normalizeScopeId` in sessionIntegrity rejects non-UUID strings, which can force re-login for such users. Replicate observable behavior (redirect flow) rather than the stringification bug if possible, but note the canonical check treats non-UUID claims as invalid.
- Session/reset tokens are HMAC-hashed with a secret; **changing `JWT_SECRET` (when it's the only secret set) silently invalidates all sessions and pending resets** because stored hashes no longer match.
- `sessions.token` HMAC vs `customer_user_sessions.token_hash` plain SHA-256 — different hashing on the two planes; don't unify.
- bcrypt is `bcryptjs` cost 10 everywhere (user passwords, API-key secrets). API-key verification is O(candidates × bcrypt); the unique `key_prefix` keeps candidates ≈1.
- RBAC cache requires **tag-based invalidation**; the tenant-scoped cache context wrapper (`runWithCacheTenant`) means invalidation deletes across both current and null tenant contexts plus hints — a Redis-tag implementation must reproduce cross-context deletes.
- API-key auth cache is per-process with a random fingerprint key — deliberately not shared; a revoked key can stay valid up to 30 s on other nodes (`invalidateByKeyId` is only local).
- `loadAcl` for API keys skips the global-superadmin shortcut but computes `isSuper` from RoleAcls of the key's roles; `resolveApiKeyAuth` *also* computes `isSuperAdmin` tenant-agnostically (`RoleAcl.isSuperAdmin` with no tenant filter) — subtle difference in tenant filtering between the two paths.
- The deprecated `metadata.requireRoles` still appears in some modules; ports must accept-and-ignore it identically (log once per route+method).
- `features.json` on ACLs may contain wildcards; `GET /api/auth/features` lists only *declared* features — grants are free-form strings validated nowhere at write time (except the enabled-modules filter at check time).
- `enforceTenantSelection` returns the actor's tenant when the candidate is `undefined`, but `null` from a superadmin means "global" — the tri-state (`undefined` vs `null` vs string) is load-bearing.
- Login accepts multipart/other content types via `formData()`; JSON bodies are **not** accepted by staff login (customer login is JSON-only) — keep the asymmetry.
- `OM_TEST_MODE=1` + `OM_TEST_AUTH_RATE_LIMIT_MODE=opt-in` makes auth rate limits opt-in per request via header `x-om-test-rate-limit: on` (integration-test hook).
