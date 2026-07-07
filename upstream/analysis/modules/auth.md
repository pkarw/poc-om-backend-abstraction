# Port contract — auth
> Upstream commit adc9da27759e357febe9ed8d4b7182040d127349 (2026-07-01). Source: packages/core/src/modules/auth/. Regenerate via om-analyze-module.

## Overview

The `auth` module owns user accounts, sessions (JWT + opaque refresh/session tokens), roles, per-user/per-role ACLs (RBAC feature grants), password resets, user invites, user consents, and sidebar customization (per-user preferences, per-role preferences, named variants). It is a Tier‑1 platform-base module: it issues the JWT that the dispatcher validates (spec 05) and exposes the `rbacService`/`authService` that virtually every other module's route auth depends on. It is **mutually dependent with `directory`** (auth reads `Tenant`/`Organization` entities and calls `rebuildHierarchyForTenant`; directory relies on auth for RBAC) — the two must be ported together as one unit. Auth also owns the cross-module tenant/role/ACL seeding orchestration (`lib/setup-app.ts`) invoked by the `mercato auth setup` CLI. It declares module `name: 'auth'`, `title: 'Authentication & Accounts'`, `version: '0.1.0'`, and **no `requires` field** (`index.ts`) — all cross-module dependencies are implicit via imports.

## Dependencies

Derived from imports of `@open-mercato/core/modules/*`, DI resolutions, and FK/event evidence (Fragment D §13, Fragment C §6).

| module id | why needed | must be ported first |
|---|---|---|
| directory | `Tenant`/`Organization` entities read in `cli.ts`, `lib/setup-app.ts`, `commands/users.ts`, `api/users/route.ts`, `api/roles/route.ts`; `rebuildHierarchyForTenant` (org hierarchy rebuild); `resolveFeatureCheckContext` + org-scope utils (`api/admin/nav.ts`, `api/users/route.ts`, `getSelectedTenantFromRequest`, `resolveOrganizationScopeForRequest`); `buildOrgScopeUserCacheTag`/`buildOrgScopeTenantCacheTag` (rbacService cache invalidation, #2259). **Mutually dependent — port together.** | yes (co-port) |
| entities | `EncryptionMap` entity seeded per-tenant in `lib/setup-app.ts`; `CustomEntity` (UI chrome). Also custom-field values (`loadCustomFieldValues`, `setCustomFieldsIfAny`) on `auth:user` / `auth:role`. | yes |
| notifications | `buildNotificationFromType` + `resolveNotificationService` for role-change (`commands/users.ts`) and password-reset (`api/reset.ts`, `api/reset/confirm.ts`) notifications; auth registers 6 notification types. Calls are best-effort (try/caught). | no (soft — degrade gracefully) |
| api_keys | `ApiKey` entity resolved in `services/rbacService.ts` for `api_key:<id>` ACL subjects (grants from key's `rolesJson`/`organizationId`/`expiresAt`). | no (soft — API-key auth path only) |
| query_index | auth emits `query_index.coverage.warmup` (login), and the shared CRUD indexer emits `query_index.upsert_one`/`delete_one` for `auth:user`/`auth:role`. Consumed by query_index subscribers. | no (fire-and-forget; spec 03) |
| dashboards | `WidgetVisibilityEditor` — backend UI only (out of scope). | no (UI) |

**Consumers of auth (record as their evidence, not auth's dep):** other modules FK a bare `user_id uuid` (no DB FK) into auth's `users`, and subscribe to `auth.user.deleted` (`communication_channels:user-deleted-cascade`, `sso:user-deleted-cleanup`).

**Must-port-first (ordered):** `directory` (co-port with auth), then `entities`. `notifications`, `api_keys`, `query_index` are soft dependencies (auth degrades gracefully / fire-and-forget) and may follow.

## HTTP routes

Base path `/api/auth/<segments>` (module id `auth`, spec 02 APIHTTP-R1). All rate limits are **in-handler** via `checkAuthRateLimit` (fail-open); **no route uses dispatcher `metadata.rateLimit`**. Format below: `keyPrefix points/duration(+blockDuration)` with env prefix. Rate-limit 429 envelope: `{"error":"Too many requests. Please try again later."}` + headers `Retry-After`, `X-RateLimit-Limit`, `X-RateLimit-Remaining`, `X-RateLimit-Reset` (spec: `packages/shared/src/lib/ratelimit/helpers.ts`). Env override per endpoint: `RATE_LIMIT_<PREFIX>_POINTS/_DURATION/_BLOCK_DURATION`.

| METHOD | path | auth | requiredFeatures | rateLimit (in-handler) | source file |
|---|---|---|---|---|---|
| POST | /api/auth/login | public | — | IP `login-ip` 20/60+60 (LOGIN_IP); ip+email `login` 5/60+60 (LOGIN) | api/login.ts |
| POST | /api/auth/logout | ✓ | — | — | api/logout.ts |
| POST | /api/auth/reset | public | — | IP `reset-ip` 10/60+60 (RESET_IP); ip+email `reset` 3/60+60 (RESET) | api/reset.ts |
| POST | /api/auth/reset/confirm | public | — | IP-only `reset-confirm` 5/300 (RESET_CONFIRM) | api/reset/confirm.ts |
| GET | /api/auth/session/refresh | public | — | — (none on GET) | api/session/refresh.ts |
| POST | /api/auth/session/refresh | public | — | IP `refresh-ip` 60/60+60 (REFRESH_IP); ip+token `refresh` 15/60+60 (REFRESH) | api/session/refresh.ts |
| POST | /api/auth/feature-check | ✓ | — | — | api/feature-check.ts |
| GET | /api/auth/features | ✓ | `auth.acl.manage` | — | api/features.ts |
| GET | /api/auth/locale | public | — | — | api/locale/route.ts |
| POST | /api/auth/locale | public | — | — | api/locale/route.ts |
| GET | /api/auth/profile | ✓ | — | — | api/profile/route.ts |
| PUT | /api/auth/profile | ✓ | — | — | api/profile/route.ts |
| GET | /api/auth/admin/nav | ✓ | — | — | api/admin/nav.ts |
| GET | /api/auth/roles | ✓ | `auth.roles.list` | — | api/roles/route.ts |
| POST | /api/auth/roles | ✓ | `auth.roles.manage` | — | api/roles/route.ts |
| PUT | /api/auth/roles | ✓ | `auth.roles.manage` | — | api/roles/route.ts |
| DELETE | /api/auth/roles | ✓ | `auth.roles.manage` | — | api/roles/route.ts |
| GET | /api/auth/roles/acl | ✓ | `auth.acl.manage` | — | api/roles/acl/route.ts |
| PUT | /api/auth/roles/acl | ✓ | `auth.acl.manage` | — | api/roles/acl/route.ts |
| GET | /api/auth/users | ✓ | `auth.users.list` | — | api/users/route.ts |
| POST | /api/auth/users | ✓ | `auth.users.create` | — | api/users/route.ts |
| PUT | /api/auth/users | ✓ | `auth.users.edit` | — | api/users/route.ts |
| DELETE | /api/auth/users | ✓ | `auth.users.delete` | — | api/users/route.ts |
| GET | /api/auth/users/acl | ✓ | `auth.acl.manage` | — | api/users/acl/route.ts |
| PUT | /api/auth/users/acl | ✓ | `auth.acl.manage` | — | api/users/acl/route.ts |
| GET | /api/auth/users/consents | ✓ | `auth.users.edit` | — | api/users/consents/route.ts (explicit `metadata.path: '/auth/users/consents'`) |
| POST | /api/auth/users/resend-invite | ✓ | `auth.users.create` | IP-only `resend-invite` 3/300+300 (RESEND_INVITE) | api/users/resend-invite/route.ts |
| GET | /api/auth/sidebar/preferences | ✓ | — (in-handler `auth.sidebar.manage` for role scope) | — | api/sidebar/preferences/route.ts |
| PUT | /api/auth/sidebar/preferences | ✓ | `auth.sidebar.manage` | — | api/sidebar/preferences/route.ts |
| DELETE | /api/auth/sidebar/preferences | ✓ | `auth.sidebar.manage` | — | api/sidebar/preferences/route.ts |
| GET | /api/auth/sidebar/variants | ✓ | — | — | api/sidebar/variants/route.ts |
| POST | /api/auth/sidebar/variants | ✓ | `auth.sidebar.manage` | — | api/sidebar/variants/route.ts |
| GET | /api/auth/sidebar/variants/[id] | ✓ | — | — | api/sidebar/variants/[id]/route.ts |
| PUT | /api/auth/sidebar/variants/[id] | ✓ | `auth.sidebar.manage` | — | api/sidebar/variants/[id]/route.ts |
| DELETE | /api/auth/sidebar/variants/[id] | ✓ | `auth.sidebar.manage` | — | api/sidebar/variants/[id]/route.ts |

**35 method+path combinations across 19 route files.** Every route file exports its own `openApi: OpenApiRouteDoc` (consumed by `/api/docs/openapi`, spec 02). All are tagged `Authentication & Accounts` **except** `api/users/consents/route.ts` which is tagged **`Auth`**. There is **no** `api/interceptors.ts` and **no** aggregate `api/openapi.ts`. Password schema throughout (`buildPasswordSchema`, shared): `min(OM_PASSWORD_MIN_LENGTH default 6)` + digit/uppercase/special (each env-toggleable, default true), violation message `'Password does not meet the requirements.'`. Optimistic-lock 409s use spec 02 APIHTTP-R40 body via `enforceCommandOptimisticLock` (header `x-om-ext-optimistic-lock-expected-updated-at`, env `OM_OPTIMISTIC_LOCK`; no-op when header absent or row absent).

### POST /api/auth/login
Metadata: `{ requireAuth: false }` (public). Request `application/x-www-form-urlencoded` (parsed via `URLSearchParams` when content-type is exactly that) or any other content-type via `req.formData()`; parse failure → all fields empty → 400.

| field | type | required | default | constraints/coercions |
|---|---|---|---|---|
| email | string | yes | — | Zod `.email()` (`userLoginSchema`, validators.ts:7-12) |
| password | string | yes | — | `.min(6)` |
| remember | boolean token | no | false | `parseBooleanToken() === true` (`on`/`1`/`true`) |
| tenantId (alias `tenant`) | uuid string | no | — | trimmed; empty→undefined; `.uuid().optional()` |
| requireRole (alias `role`) | string→string[] | no | — | comma-split, trimmed, empties dropped; NOT Zod-validated |
| redirect | string | no | `/backend` | `sanitizeRedirectPath(redirectTo, getAppBaseUrl(req), '/backend')` |

Validation: `userLoginSchema.pick({email,password,tenantId}).safeParse`. Pipeline (load-bearing order): parse form → **rate limit before validation** → Zod → user lookup (`tenantId` present ⇒ `findUserByEmailAndTenant`; else `findUsersByEmail` and **exactly one match required** — ambiguous multi-tenant email treated as no user, #2242) → `verifyPassword` **always runs** (constant-time bcrypt vs dummy hash even for missing user) → optional role check (`requiredRoles.some(r => userRoleNames.includes(r))`) → `updateLastLoginAt` → `resetAuthRateLimit` (compound counter deleted, best-effort) → best-effort `eventBus.emitEvent('query_index.coverage.warmup', { tenantId })` → session row (`auth.createSession(user, expiresAt)`, expiry = remember ? now+`REMEMBER_ME_DAYS` (default 30) days : now+8h) → JWT `signJwt({sub, sid, tenantId, orgId, email, roles})` → `runCustomRouteAfterInterceptors({ routePath:'auth/login', method:'POST' })` (may replace body/status; sanitized request body passed = `{email, tenantId?, remember, requireRole?}` — **never the password**) → cookies.

Responses:
| status | body |
|---|---|
| 200 | `{"ok":true,"token":"<jwt>","redirect":"<sanitized path>","refreshToken":"<session token>"?}` — `refreshToken` only when `remember` |
| 400 | `{"ok":false,"error":"Invalid credentials"}` (Zod failure; i18n `auth.login.errors.invalidCredentials`) |
| 401 | `{"ok":false,"error":"Invalid email or password"}` — unknown user / wrong password / ambiguous email |
| 403 | `{"ok":false,"error":"Not authorized for this area"}` (i18n `auth.login.errors.permissionDenied`) — requireRole unmet |
| 429 | rate-limit envelope |
| any | interceptor `{ok:false}` result → its `statusCode` + body verbatim |

Cookies on 200: `auth_token=<jwt>` `{httpOnly, path:'/', sameSite:'lax', secure: prod, maxAge:28800}` (8h). `session_token`: with remember → same flags but `expires`=now+REMEMBER_ME_DAYS days, value=refresh token; without remember → set only if interceptor did not replace the token, `maxAge:28800`.

Events (fire-and-forget, `emitAuthEvent`): failure → `auth.login.failed` `{email, reason:'invalid_password'|'invalid_credentials'}` (`invalid_password` iff user had a hash); success → `auth.login.success` `{id, email, tenantId, organizationId}`.

### POST /api/auth/logout
Metadata: `{ POST: { requireAuth: true } }`. No body. Reads cookies `session_token`, `auth_token`; extracts `sid` from `auth_token` via `verifyJwt` (invalid→null, ignored); calls `deleteSessionById(sid)` and/or `deleteSessionByToken(sessToken)` (whole block try/caught). **Response: 307 redirect** to `<request origin>/login` (`NextResponse.redirect` default; openApi doc says 302, runtime is 307 per `__tests__/logout.test.ts:65`). Clears `auth_token=''` and `session_token=''` `{path:'/', maxAge:0}`. No JSON body. (Note: declared event `auth.logout` is **never emitted**.)

### POST /api/auth/reset
Metadata: `{ requireAuth: false }`. Form-data field `email`, `requestPasswordResetSchema = z.object({ email: z.string().email() })` (validators.ts:14-16). Rate limits before validation: IP `reset-ip` 10/60+60; compound `reset` 3/60+60. Builds reset URL via `toSecurityEmailUrl(req, '/reset/__token__')`. `authService.requestPasswordReset(email)`: unknown email → **200 `{"ok":true}`** (existence hiding); known → sends `ResetPasswordEmail` (subject i18n `auth.email.resetPassword.subject` fallback `'Reset your password'`; token TTL 60 min), and when user has tenantId creates notification `auth.password_reset.requested` (`sourceEntityType:'auth:user'`, best-effort).
| status | body |
|---|---|
| 200 | `{"ok":true}` (always) |
| 400 | `{"error":"Invalid request origin"}` |
| 422 | `{"error":"Validation failed","fieldErrors":{...zod flatten().fieldErrors}}` |
| 429 | rate-limit envelope |
| 500 | `{"error":"Password reset is not configured"}` |

### POST /api/auth/reset/confirm
Metadata: `{ requireAuth: false }`. Form-data `token`, `password`; `confirmPasswordResetSchema = { token: z.string().min(10), password: <passwordPolicy> }` (validators.ts:18-21). Rate limit **IP-only** `reset-confirm` 5/300 (no blockDuration), before validation.
| status | body |
|---|---|
| 200 | `{"ok":true,"redirect":"/login"}` |
| 400 | `{"ok":false,"error":"Invalid request"}` (Zod failure) |
| 400 | `{"ok":false,"error":"Invalid or expired token"}` (`confirmPasswordReset` returned null) |
| 429 | rate-limit envelope |

Side effect: notification `auth.password_reset.completed` (tenant-scoped, best-effort). `confirmPasswordReset` is atomic compare-and-set (replay-safe) and calls `deleteAllUserSessions`.

### GET /api/auth/session/refresh (browser flow)
Metadata: `{ GET: { requireAuth: false } }`. Query `redirect?` (`sanitizeRedirectPath(_, baseUrl, '/')`). **No rate limit.** Reads `session_token` cookie; missing or `refreshFromSessionToken` null ⇒ **307** redirect to `<origin>/login?redirect=<encoded>` clearing both cookies `{maxAge:0}`. Valid ⇒ new JWT `signJwt({sub, sid?, tenantId, orgId, email, roles})`, **307** redirect to `redirectTo`, sets `auth_token` `{httpOnly, path:'/', sameSite:'lax', secure:prod, maxAge:28800}`. (307 pinned in `__tests__/session.refresh.test.ts`; openApi doc says 302.)

### POST /api/auth/session/refresh (API/mobile flow)
Metadata: `{ POST: { requireAuth: false } }`. JSON `refreshSessionRequestSchema = { refreshToken: z.string().min(1) }` (validators.ts:23-25); malformed JSON tolerated (token null). Rate limits after parse, before token check: IP `refresh-ip` 60/60+60; compound (identifier = the refresh token, hashed) `refresh` 15/60+60.
| status | body | cookies |
|---|---|---|
| 200 | `{"ok":true,"accessToken":"<jwt>","expiresIn":28800}` | sets `auth_token` (maxAge 28800) |
| 400 | `{"ok":false,"error":"Missing or invalid refresh token"}` | clears both |
| 401 | `{"ok":false,"error":"Invalid or expired refresh token"}` | clears both |
| 429 | rate-limit envelope | — |

### POST /api/auth/feature-check
Metadata: `{ POST: { requireAuth: true } }`. JSON `featureCheckRequestSchema = { features: z.array(z.string().max(128)).max(50) }` (validators.ts:124-126); malformed→`{}`→400. Behavior: `rbacService.userHasAllFeatures(auth.sub, features, {tenantId, organizationId})`; on batch fail, re-checks each individually to build `granted`.
| status | body |
|---|---|
| 200 | `{"ok":true|false,"granted":[...],"userId":"<sub>"}` (ok=true when all granted / empty features) |
| 400 | `{"ok":false,"error":"Invalid request"}` |
| 401 | `{"ok":false,"error":"Unauthorized"}` |

### GET /api/auth/features
Metadata: `{ GET: { requireAuth: true, requireFeatures: ['auth.acl.manage'] } }`. No query. Aggregates `getModules()` static feature declarations: items `{id, title (fallback id), module (f.module||m.id), dependsOn?}` — `dependsOn` normalized (trim, drop empties, dedupe, omit when empty); deduped by `id` (first wins); sorted `module.localeCompare` then `id`. `modules: [{id, title: m.info.title||m.id}]`.
| status | body |
|---|---|
| 200 | `{"items":[{"id","title","module","dependsOn"?}],"modules":[{"id","title"}]}` |
| 401 | `{"error":"Unauthorized"}` |

### GET+POST /api/auth/locale
Metadata: `{ GET: { requireAuth: false }, POST: { requireAuth: false } }`. **POST** JSON `{locale}` (must be in configured `locales`): 200 `{"ok":true}` + cookie `locale=<locale>` `{path:'/', maxAge:31536000}` (365d, NOT httpOnly); invalid → 400 `{"error":"Invalid locale"}`; malformed JSON → 400 `{"error":"Bad request"}`. **GET** query `locale` (required, supported) + `redirect?` (sanitized, default `/`): invalid → 400 `{"error":"Invalid locale"}`; success → **307** redirect to `new URL(safePath, url.origin)` + same `locale` cookie.

### GET+PUT /api/auth/profile
Metadata: `{ GET: { requireAuth: true }, PUT: { requireAuth: true } }`. **GET** loads own `User` (`{id: auth.sub, deletedAt: null}`, scope `{tenantId, organizationId}`): 200 `{"email":"<user.email>","roles":[...auth.roles ?? []]}` (roles from JWT); 401 `{"error":"Unauthorized"}`; 404 `{"error":"User not found"}`; 400 `{"error":"Failed to load profile."}`.
**PUT** JSON (malformed→`{}`), inline schema (profile/route.ts:23-62): `email?` (`.email()`), `currentPassword?` (`.trim().min(1)`), `password?` (policy) + superRefine: at least one of email/password (`'Provide an email or password.'`); password⇒currentPassword required (`'Current password is required.'`); currentPassword⇒password required (`'New password is required.'`). Pipeline: validate → password change ⇒ load own user + `verifyPassword(user, currentPassword)` → command `auth.users.update` via commandBus (input `{id: auth.sub, email?, password?}`, ctx `{organizationScope:null, selectedOrganizationId: auth.orgId ?? null, organizationIds: auth.orgId ? [auth.orgId] : null}`) → re-derive roles → re-sign JWT (preserving `sid`) → set `auth_token` `{httpOnly, path:'/', sameSite:'lax', secure:prod, maxAge:28800}`.
| status | body |
|---|---|
| 200 | `{"ok":true,"email":"<new email>"}` + refreshed cookie |
| 400 | `{"error":"Invalid profile update.","issues":[...zod issues]}` |
| 400 | `{"error":"Current password is incorrect.","issues":[{"path":["currentPassword"],"message":"<same>"}]}` |
| 401 | `{"error":"Unauthorized"}` |
| 404 | `{"error":"User not found"}` |
| — | CrudHttpError from command → `err.status` + `err.body` verbatim |
| 400 | `{"error":"Failed to update profile."}` (catch-all) |

### GET /api/auth/admin/nav
Metadata: `{ GET: { requireAuth: true } }`. Query `orgId?`, `tenantId?` (empty→null; absent→undefined). Scope via `resolveFeatureCheckContext` (directory); fallback to token scope. Read-through cache key `nav:sidebar:v2:<locale>:<auth.sub>:<tenantId||'null'>:<orgId||'null'>`; write tags: `rbac:user:<sub>`, `rbac:tenant:<t>`?, `nav:entities:<t||'null'>`, `nav:locale:<locale>`, `nav:sidebar:user:<sub>`, `nav:sidebar:tenant:<t>`?, `nav:sidebar:organization:<o>`?, `nav:sidebar:scope:<sub>:<t||'null'>:<o||'null'>:<locale>`, plus `nav:sidebar:role:<role>` per JWT role (read/write failures swallowed). Payload by `resolveBackendChromePayload` (`lib/backendChrome.tsx` — UI-adjacent). 200 shape (`adminNavResponseSchema`, nav.ts:71-93): `{brand?, groups:[{id?,name,defaultName?,items:SidebarNavItem[]}], settingsSections:SectionGroup[], settingsPathPrefixes:string[], profileSections:SectionGroup[], profilePathPrefixes:string[], grantedFeatures:string[], roles:string[]}`; `SidebarNavItem={id?,href,title,defaultTitle?,enabled?,hidden?,pageContext?:'main'|'admin'|'settings'|'profile',iconName?,iconMarkup?,children?[]}` (recursive); `SectionGroup={id,label,labelKey?,order?,items:[{id,label,labelKey?,href,order?,iconName?,iconMarkup?,children?[]}]}`. 401 `{"error":"Unauthorized"}`.

### /api/auth/roles — GET (hand-written) + POST/PUT/DELETE (`makeCrudRoute`)
Metadata: GET `['auth.roles.list']`; POST/PUT/DELETE `['auth.roles.manage']` (all `requireAuth: true`).
**GET** query (`querySchema` roles/route.ts:24-30, `.passthrough()`): `id?` uuid; `page` `coerce.number().min(1)` default 1; `pageSize` coerce 1..100 default 50; `search?` ILIKE `%…%` on name (`escapeLikePattern`); `tenantId?` uuid (honored only for super admins). **Quirk (test-pinned):** unauthenticated handler call or Zod failure → **200 `{"items":[],"total":0,"totalPages":1}`** (not 401/400). Super admin via `rbacService.loadAcl(...).isSuperAdmin`; non-SA: filter `tenantId=actorTenant`, exclude `name='superadmin'` and role ids holding super-admin `RoleAcl` in the tenant; always `deletedAt: null`. Item (roles/route.ts:239-254): `{id, name, usersCount (non-deleted UserRole count), tenantId|null, tenantIds: exposeTenant?[tenantId]:[], tenantName: exposeTenant?name??id:null, updatedAt: ISO|null, ...customFieldValues}` (`exposeTenant = isSuperAdmin || roleTenant===auth.tenantId`; cf via `E.auth.role`=`'auth:role'`). Envelope: **200 `{"items":[...],"total":<count>,"totalPages":Math.max(1,ceil(count/pageSize)),"isSuperAdmin":<bool>}`** (no `page`/`pageSize` keys). Access log: `logCrudAccess` resourceKind `auth.role`, organizationId null, accessType `read:item` when `?id=`.
**Factory config** (roles/route.ts:76-131; spec 02 APIHTTP-R31…R39): `orm: { entity: Role, idField:'id', orgField:null, tenantField:null, softDeleteField:'deletedAt' }`; `events: roleCrudEvents`; `indexer: roleCrudIndexer`. Actions: create `commandId:'auth.roles.create'`, schema `z.object({}).passthrough()`, mapInput `enforceTenantSelection` on tenantId (explicit null preserved → command 400s; global roles unsupported), response `{id:String(result.id)}` status **201**; update `commandId:'auth.roles.update'`, mapInput `assertCanModifySuperAdminRole` + `assertCanAccessRoleTarget` on parsed.id, response `{ok:true}`; delete `commandId:'auth.roles.delete'`, mapInput same two guards on id (`parsed.id ?? raw.query.id ?? raw.body.id`), response `{ok:true}`. Exported `DELETE` pre-runs both guards against `?id=` (CrudHttpError→JSON) before delegating. Responses: POST **201 `{"id":"<uuid>"}`**, PUT/DELETE **200 `{"ok":true}`**. Guard errors: 401 `{"error":"Unauthorized"}`, 403 `{"error":"Only super administrators can modify super administrator roles."}`, 404 `{"error":"Role not found"}`; command validation → 400. Real validation in `commands/roles.ts` (name 2..100, tenantId uuid; reserved names `superadmin`/`admin` → 400 `'Role name is reserved'`). Derived events `auth.role.{creating,created,updating,updated,deleting,deleted}`.

### GET+PUT /api/auth/roles/acl
Metadata: both `requireAuth: true` + `['auth.acl.manage']`. **GET** query `{roleId: uuid (required), tenantId?: uuid}`. Role lookup for non-SA scoped `$or [{tenantId: authTenant},{tenantId: null}]`. Tenant resolution `parsed.tenantId ?? role.tenantId ?? auth.tenantId ?? null`; differing `?tenantId=` allowed only for SA or when equal to actor tenant, else 403. Non-SA passes `assertActorCanModifySuperAdminRoleTarget`.
| status | body |
|---|---|
| 200 | `{"isSuperAdmin":bool,"features":[...],"organizations":[...]|null,"updatedAt":ISO|null}` — all-default `{false,[],null,null}` when no `RoleAcl` |
| 400 | `{"error":"Invalid input"}` |
| 401 | `{"error":"Unauthorized"}` |
| 403 | `{"error":"Forbidden"}` or grant-check body |
| 404 | `{"error":"Not found"}` |

Access log resourceKind `auth.role_acl`, `read:item`. **PUT** JSON (malformed→`{}`→400) `{roleId: uuid, isSuperAdmin?: bool, features?: string[], organizations?: string[]|null, tenantId?: uuid}`. Same lookup/resolution; no resolvable tenant → 400 `{"error":"Tenant required"}`; non-SA writing another tenant → 403 `{"error":"Forbidden"}`. **Optimistic lock** on existing `RoleAcl` (resourceKind `auth.role_acl`) → 409 (skipped for first grant). Undefined fields keep existing (features/organizations via `normalizeGrantFeatureList`). `assertActorCanGrantAcl`. Persist inside `withAtomicFlush({transaction:true})`. Cache: `rbacService.invalidateTenantCache(tenantId)` + `cache.deleteByTags(['rbac:tenant:<t>'])`. 200 `{"ok":true,"sanitized":false}` (always `sanitized:false`); errors 400/401/403/404/409 as above.

### /api/auth/users — GET (hand-written) + POST/PUT/DELETE (`makeCrudRoute`)
Metadata: GET `['auth.users.list']`; POST `['auth.users.create']`; PUT `['auth.users.edit']`; DELETE `['auth.users.delete']` (all `requireAuth: true`).
**GET** query (`querySchema` users/route.ts:34-42, `.passthrough()`): `id?` uuid (adds `hasPassword` to item); `page`/`pageSize` coerced (1/50, pageSize 1..100); `search?` (matches encrypted email via `search_tokens`, org name ILIKE, role name ILIKE→UserRole; `$or`-combined; zero candidate sets → empty 200); `name?` (`$or` of `name ILIKE %…%` and search-token `field='name'`); `organizationId?` uuid (equality); `roleId` (repeated) uuid[]→`roleIds` (users must hold ≥1; intersected with `id`). **Quirk (test-pinned):** unauth/invalid query → **200 `{"items":[],"total":0,"totalPages":1}`**. Non-SA: no tenant→empty; scoped to actor tenant; super-admin users hidden (`listSuperAdminUserIds`→`id $nin`). SA: may select tenant via `om_selected_tenant` cookie (`getSelectedTenantFromRequest` → `resolveOrganizationScopeForRequest`; unresolvable / `filterIds:[]` → empty 200). Always `deletedAt: null`. Item (users/route.ts:430-447): `{id, email, name|null, organizationId|null, organizationName (name??id)|null, tenantId|null, tenantName (name??id)|null, roles: string[], roleIds: string[], hasPassword? (only when ?id=), updatedAt: ISO|null, ...customFieldValues}` (cf via `E.auth.user`=`'auth:user'`). Envelope **200 `{"items","total","totalPages":Math.max(1,ceil),"isSuperAdmin"}`**. Access log resourceKind `auth.user`, organizationId = effectiveSelectedOrganizationId, `read:item` when `?id=`.
**Factory config** (users/route.ts:111-166): `orm: { entity: User, idField:'id', orgField:null, tenantField:null, softDeleteField:'deletedAt' }`; `events: userCrudEvents`; `indexer: userCrudIndexer`. Actions: create `commandId:'auth.users.create'`, schema passthrough, mapInput `assertCanAssignRoles` (target tenant from payload.organizationId's tenant, else payload.id's user, else actor tenant), response `{id:String(result.user.id), ...(result.warning?{_warning:result.warning}:{})}` status **201**; update `commandId:'auth.users.update'`, mapInput `assertCanModifySuperAdminTarget` + `assertCanAccessUserTarget` (parsed.id) + `assertCanAssignRoles`, response `{ok:true}`; delete `commandId:'auth.users.delete'`, mapInput both target guards on id, response `{ok:true}`. Exported `DELETE` pre-checks `?id=` with both guards. Responses: POST **201 `{"id":"<uuid>","_warning"?:"<string>"}`** (`_warning` e.g. invite-email failure), PUT/DELETE **200 `{"ok":true}`**. Guard errors: 401 `{"error":"Unauthorized"}`; 403 grant bodies (`'Only super administrators can modify super administrator accounts.'`, `'Not authorized to access this user.'`, `'Cannot grant a role outside the target tenant.'`); `Role(s) not found: …` → 400; 404 `{"error":"User not found"}`. Command-enforced create schema (`commands/users.ts`): `{email: email, name: trim 1..120 nullable optional, password?: policy, sendInviteEmail?: bool, organizationId: uuid required, roles?: string[]}` + refine `'Either password or sendInviteEmail is required'` (path `['password']`); update `{id: uuid, email?, name?, password?, organizationId?, roles?}`. Derived events `auth.user.{creating,created,updating,updated,deleting,deleted}` (persistent). Duplicate-email is **per-tenant** (unique `(tenant_id, email_hash)`; #2934) → 400 `{"error":"Email already in use","fieldErrors":{email},"details":[{path:['email'],code:'duplicate',origin:'validation'}]}`.

### GET+PUT /api/auth/users/acl
Metadata: both `requireAuth: true` + `['auth.acl.manage']`. **GET** query `{userId: uuid}`. Non-SA: `assertActorCanModifySuperAdminUserTarget` + `assertActorCanAccessUserTarget` (403/404 passthrough). ACL row `{user: userId, tenantId: auth.tenantId}`.
| status | body |
|---|---|
| 200 | `{"hasCustomAcl":bool,"isSuperAdmin":bool,"features":[...],"organizations":[...]|null,"updatedAt":ISO|null}` — default `{false,false,[],null,null}` |
| 400 | `{"error":"Invalid input"}` |
| 401 | `{"error":"Unauthorized"}` |

Access log resourceKind `auth.user_acl`, `read:item`. **PUT** JSON (malformed→`{}`) `{userId: uuid, isSuperAdmin?: bool, features?: string[], organizations?: string[]|null}`. Non-SA guards as GET. **Optimistic lock** (resourceKind `auth.user_acl`) → 409. `assertActorCanGrantAcl`. Non-SA sanitization: strips tenant-restricted features (`*`, `directory.*`, anything starting `directory.tenants`); `requestedIsSuperAdmin` default **false** (unlike roles/acl which defaults to existing); non-SA granting SA when not already set → 403 `{"error":"Only super administrators can grant super admin access."}`; non-SA may revoke SA. Empty effective ACL (`!isSuperAdmin && features.length===0`) → row **deleted**; else upserted (both in `withAtomicFlush({transaction:true})`). Cache: `rbacService.invalidateUserCache(userId)` + `cache.deleteByTags(['rbac:user:<userId>'])`. 200 `{"ok":true,"sanitized":<bool>}` (`sanitized` true when a non-SA request was trimmed); errors 400/401/403/409.

### GET /api/auth/users/consents
Metadata: `{ path: '/auth/users/consents', GET: { requireAuth: true, requireFeatures: ['auth.users.edit'] } }` (explicit `metadata.path` = derived path; also `export default GET`). Query `{userId: uuid}`. Guard `assertActorCanAccessUserTarget` (403 `'Not authorized to access this user.'` / 404 `'User not found'`). Reads `UserConsent` `{userId, deletedAt: null, tenantId? (when actor has one), organizationId? (when actor has one)}` ordered `createdAt DESC`, decryption-aware.
| status | body |
|---|---|
| 200 | `{"ok":true,"items":[{"id","consentType","isGranted":bool,"grantedAt":ISO|null,"withdrawnAt":ISO|null,"source":string|null,"ipAddress":string|null,"integrityValid":bool,"createdAt":ISO,"updatedAt":ISO|null}]}` (`integrityValid` from `verifyConsentIntegrityHash`) |
| 400 | `{"ok":false,"error":"Invalid userId"}` |
| 401 | `{"ok":false,"error":"Unauthorized"}` |

openApi tag **`Auth`** (only route not tagged `Authentication & Accounts`).

### POST /api/auth/users/resend-invite
Metadata: `{ POST: { requireAuth: true, requireFeatures: ['auth.users.create'] } }`. Rate limit (after auth) IP-only `resend-invite` 3/300+300. JSON `readJsonSafe(req,{})` schema `{id: uuid}`. Pipeline: 422 on schema fail → `assertActorCanAccessUserTarget` (403/404) → user lookup `{id, deletedAt: null}` (+tenantId for non-SA; non-SA without tenant → 404) → user already has `passwordHash` → 409 → mutation guard `validateCrudMutationGuard` (resourceKind `auth.user`, operation `'custom'`; spec 02 APIHTTP-R39 — may block with own status/body); `runCrudMutationGuardAfterSuccess` after send → `getSecurityEmailBaseUrl` errors mapped (400/500) → invalidate all open `PasswordReset` rows (`usedAt=now`) → new token (`generateAuthToken()` raw, stored `hashAuthToken`), `expiresAt = now + 48h` (`INVITE_TOKEN_TTL_MS`) → invite URL `<base>/reset/<rawToken>` → `InviteUserEmail` (failure tolerated).
| status | body |
|---|---|
| 200 | `{"ok":true}` or `{"ok":true,"warning":"invite_email_failed"}` |
| 400 | `{"error":"Invalid request origin"}` |
| 401 | `{"error":"Unauthorized"}` |
| 404 | `{"error":"User not found"}` |
| 409 | `{"error":"User already has a password"}` |
| 422 | `{"error":"Validation failed","fieldErrors":{...}}` |
| 429 | rate-limit envelope |
| 500 | `{"error":"Invitation email is not configured"}` |
| — | guard-provided status/body |

### GET+PUT+DELETE /api/auth/sidebar/preferences
Metadata: GET `{requireAuth:true}`; PUT/DELETE `{requireAuth:true, requireFeatures:['auth.sidebar.manage']}`. GET has **no** dispatcher feature gate — role-scoped read enforces `auth.sidebar.manage` **in-handler**. Settings shape everywhere: `{version: int>0, groupOrder: string[], groupLabels: Record<string,string>, itemLabels: Record<string,string>, hiddenItems: string[], itemOrder: Record<string,string[]>}` (defaults from `SIDEBAR_PREFERENCES_VERSION` + empty). API-key auth uses `auth.userId`.
**GET** optional `?roleId=`. Role scope: missing manage feature → 403 `{"error":"Forbidden","requiredFeatures":["auth.sidebar.manage"]}`; role not in `$or [{tenantId: authTenant},{tenantId:null}]` → 404 `{"error":"Role not found"}`. 200: `{"locale","settings":{…},"canApplyToRoles":bool,"roles":[{"id","name","hasPreference":bool}],"scope":{"type":"role","roleId"}|{"type":"user"},"updatedAt":ISO|null}`. 401 `{"error":"Unauthorized"}`.
**PUT** body `sidebarPreferencesInputSchema` (validators.ts:73-101): `version?` int>0; `groupOrder?` string[] (items min 1, max 200); `groupLabels?`/`itemLabels?` Record<string,string> (keys min 1, values 1..120); `hiddenItems?` string[] max 500; `itemOrder?` Record<string,string[]> (lists max 500); `applyToRoles?`/`clearRoleIds?` uuid[] (**rejected with custom issues when `scope.type==='role'`**); `scope?` `{type:'user'}|{type:'role',roleId:uuid}` default user. Errors: invalid JSON → 400 `{"error":"Invalid JSON"}`; schema → 400 `{"error":"Invalid payload","details":<flatten()>}`; API key without user → 403 `{"error":"Cannot save preferences: no user associated with this API key"}`. Sanitization: ids/labels trimmed, empties dropped, deduped. Role scope: manage-feature 403, role 404, optimistic lock resourceKind `auth.role_sidebar_preference` → 409, then `saveRoleSidebarPreference`. User scope: `applyToRoles`/`clearRoleIds` require manage feature → 403; optimistic lock resourceKind `auth.sidebar_preference` → 409; unknown applyToRoles → 400 `{"error":"Invalid roles","missing":[...]}`; role writes + `RoleSidebarPreference` clears (`nativeDelete` by `{role $in, tenantId}`, locale-agnostic) in `withAtomicFlush({transaction:true})`. Cache tags invalidated: `nav:sidebar:user:<sub>`, `nav:sidebar:scope:<sub>:<t|'null'>:<o|'null'>:<locale>`, `nav:sidebar:role:<roleId>` per role. Success 200 = GET shape + `"appliedRoles":[...ids]` + `"clearedRoles":[...ids]`.
**DELETE** `?roleId=` required → 400 `{"error":"roleId query parameter is required"}`; manage feature in-handler → 403 (with `requiredFeatures`); role 404; `nativeDelete(RoleSidebarPreference, {role, tenantId})` (idempotent); invalidates `nav:sidebar:role:<id>`; 200 `{"ok":true,"scope":{"type":"role","roleId":"<id>"}}`.

### GET+POST /api/auth/sidebar/variants
Metadata: GET `{requireAuth:true}`; POST `{requireAuth:true, requireFeatures:['auth.sidebar.manage']}`. Serialized variant: `{"id":uuid,"name":string,"isActive":bool,"settings":{version,groupOrder,groupLabels,itemLabels,hiddenItems,itemOrder},"createdAt":ISO,"updatedAt":ISO|null}`. Scope: (effectiveUserId, auth.tenantId, auth.orgId, locale); API-key without `userId` → 403 `{"error":"No user context"}`.
**GET** 200 `{"locale":"<locale>","variants":[...]}` + header `cache-control: no-store, no-cache, must-revalidate`; 401 `{"error":"Unauthorized"}`.
**POST** body `createSidebarVariantInputSchema` (validators.ts:45-49): `{name?: trim 1..120, settings?: sidebarVariantSettingsSchema, isActive?: bool}`. Malformed JSON→`{}` (creates default variant auto-named "My preferences", "My preferences 2", …). Schema fail → 400 `{"error":"Invalid payload","details":<flatten()>}`. Unique violation on `(user_id, tenant_id, locale, name)` → **409** `{"error":"A variant with this name already exists. Choose a different name.","code":"duplicate_name"}`. Other errors → 500 `{"error":"<err.message>"}`. Success → **200** (not 201) `{"locale","variant":{...}}`.

### GET+PUT+DELETE /api/auth/sidebar/variants/[id]
Metadata: GET `{requireAuth:true}`; PUT/DELETE `{requireAuth:true, requireFeatures:['auth.sidebar.manage']}`. Handlers ignore dispatcher `params` and extract id as **last URL path segment** (`extractIdFromUrl`) — no UUID validation (bad id → 404). Common: 401 `{"error":"Unauthorized"}`; API key without user → 403 `{"error":"No user context"}`; lookup miss → 404 `{"error":"Variant not found"}`.
- **GET** → 200 `{"locale","variant":{...}}`.
- **PUT** body `updateSidebarVariantInputSchema` (validators.ts:51-55, same fields as create). Invalid JSON → 400 `{"error":"Invalid JSON"}`; schema → 400 `{"error":"Invalid payload","details":<flatten()>}`. `isActive:true` deactivates other variants in scope. 200 `{"locale","variant":{...}}`.
- **DELETE** soft delete (`deleted_at`, `isActive=false`). 200 `{"ok":true}`.

## Entities

11 entity classes (`data/entities.ts`), 11 migrations + 2 MikroORM snapshot JSONs (not DDL). All PKs `uuid NOT NULL DEFAULT gen_random_uuid()`; all timestamps `timestamptz`; all tables soft-delete via nullable `deleted_at`. **No cross-module FKs** — `tenant_id`/`organization_id` are bare `uuid` columns (tenancy by convention). ON DELETE NO ACTION everywhere; ON UPDATE CASCADE on all FKs **except** `sidebar_variants_user_id_foreign` (no clause). `created_at`/`updated_at` have no DB defaults (app `onCreate`/`onUpdate`). Migration-owned partial unique indexes intentionally omitted from decorators (entities.ts comments). Migration order: 20251030150038 (base 9 tables), 20251031083009 (roles `(tenant_id,name)` unique), 20251209080326 (`users.email_hash`), 20260324100000 (`user_consents`), 20260411203200 (drop global roles, `roles.tenant_id` NOT NULL, #687), 20260427081815 (`sidebar_variants`), 20260427124900 (partial variant unique), 20260427143311 (strip `locale` from all 3 sidebar uniqueness scopes), 20260601120000 (`updated_at` on users+roles, #2055), 20260610120000 (per-tenant partial unique on users email_hash, #2934), 20260611103000 (`user_roles` FK indexes, #2966).

### users (`User`)
| column | pg type | nullable | default | notes |
|---|---|---|---|---|
| id | uuid | no | gen_random_uuid() | PK `users_pkey` |
| tenant_id | uuid | yes | — | no FK |
| organization_id | uuid | yes | — | no FK |
| email | text | no | — | encrypted (non-deterministic); global unique DROPPED |
| email_hash | text | yes | — | deterministic lookup hash; partial unique below |
| name | text | yes | — | encrypted |
| password_hash | text | yes | — | bcrypt cost 10 |
| is_confirmed | boolean | no | true | |
| last_login_at | timestamptz | yes | — | |
| created_at | timestamptz | no | — | app onCreate |
| updated_at | timestamptz | yes | — | added 20260601120000 |
| deleted_at | timestamptz | yes | — | soft delete |

Indexes: `users_email_hash_idx` `(email_hash)`; `users_tenant_email_hash_uniq` UNIQUE `(tenant_id, email_hash) WHERE deleted_at IS NULL AND email_hash IS NOT NULL` (migration-owned).

### roles (`Role`)
| column | pg type | nullable | default | notes |
|---|---|---|---|---|
| id | uuid | no | gen_random_uuid() | PK `roles_pkey` |
| name | text | no | — | |
| tenant_id | uuid | **no** | — | created nullable, SET NOT NULL by 20260411203200 |
| created_at | timestamptz | no | — | |
| updated_at | timestamptz | yes | — | added 20260601120000 |
| deleted_at | timestamptz | yes | — | soft delete |

Constraint: `roles_tenant_id_name_unique` UNIQUE `(tenant_id, name)` — **full** (soft-deleted rows still occupy the name).

### user_roles (`UserRole`)
| column | pg type | nullable | default | notes |
|---|---|---|---|---|
| id | uuid | no | gen_random_uuid() | PK `user_roles_pkey` |
| user_id | uuid | no | — | FK `user_roles_user_id_foreign` → users.id, ON UPDATE CASCADE |
| role_id | uuid | no | — | FK `user_roles_role_id_foreign` → roles.id, ON UPDATE CASCADE |
| created_at | timestamptz | no | — | |
| deleted_at | timestamptz | yes | — | soft delete; **no updated_at** |

Indexes: `user_roles_user_id_idx`, `user_roles_role_id_idx`. No (user_id, role_id) uniqueness.

### sessions (`Session`)
| column | pg type | nullable | default | notes |
|---|---|---|---|---|
| id | uuid | no | gen_random_uuid() | PK `sessions_pkey` |
| user_id | uuid | no | — | FK `sessions_user_id_foreign` → users.id, ON UPDATE CASCADE |
| token | text | no | — | `sessions_token_unique`; stored value = HMAC-SHA256(rawToken) |
| expires_at | timestamptz | no | — | |
| created_at | timestamptz | no | — | |
| last_used_at | timestamptz | yes | — | |
| deleted_at | timestamptz | yes | — | soft delete; no tenant/org columns |

### password_resets (`PasswordReset`)
| column | pg type | nullable | default | notes |
|---|---|---|---|---|
| id | uuid | no | gen_random_uuid() | PK `password_resets_pkey` |
| user_id | uuid | no | — | FK `password_resets_user_id_foreign` → users.id, ON UPDATE CASCADE |
| token | text | no | — | `password_resets_token_unique`; stored = HMAC hash |
| expires_at | timestamptz | no | — | reset TTL 60 min; invite TTL 48h |
| used_at | timestamptz | yes | — | atomic compare-and-set on confirm |
| created_at | timestamptz | no | — | |
| deleted_at | timestamptz | yes | — | soft delete |

### role_acls (`RoleAcl`)
| column | pg type | nullable | default | notes |
|---|---|---|---|---|
| id | uuid | no | gen_random_uuid() | PK `role_acls_pkey` |
| role_id | uuid | no | — | FK `role_acls_role_id_foreign` → roles.id, ON UPDATE CASCADE (not indexed) |
| tenant_id | uuid | **no** | — | mandatory for ACL evaluation |
| features_json | jsonb | yes | — | string[] with wildcard (`example.*`) |
| is_super_admin | boolean | no | false | |
| organizations_json | jsonb | yes | — | string[]; null/empty = all orgs |
| created_at | timestamptz | no | — | |
| updated_at | timestamptz | yes | — | |
| deleted_at | timestamptz | yes | — | soft delete |

### user_acls (`UserAcl`)
Identical shape to `role_acls` but `user_id uuid NOT NULL`, FK `user_acls_user_id_foreign` → users.id ON UPDATE CASCADE (instead of role_id). PK `user_acls_pkey`. No secondary indexes.

### user_sidebar_preferences (`UserSidebarPreference`)
| column | pg type | nullable | default | notes |
|---|---|---|---|---|
| id | uuid | no | gen_random_uuid() | PK `user_sidebar_preferences_pkey` |
| user_id | uuid | no | — | FK → users.id, ON UPDATE CASCADE |
| tenant_id | uuid | yes | — | |
| organization_id | uuid | yes | — | |
| locale | text | no | — | NOT NULL but excluded from uniqueness since 20260427143311 |
| settings_json | jsonb | yes | — | |
| created_at | timestamptz | no | — | |
| updated_at | timestamptz | yes | — | onUpdate only, no onCreate |
| deleted_at | timestamptz | yes | — | soft delete |

Unique: `user_sidebar_preferences_active_unique_idx` UNIQUE `(user_id, tenant_id, organization_id) WHERE deleted_at IS NULL`.

### role_sidebar_preferences (`RoleSidebarPreference`)
| column | pg type | nullable | default | notes |
|---|---|---|---|---|
| id | uuid | no | gen_random_uuid() | PK `role_sidebar_preferences_pkey` |
| role_id | uuid | no | — | FK → roles.id, ON UPDATE CASCADE |
| tenant_id | uuid | yes | — | no organization_id column |
| locale | text | no | — | excluded from uniqueness since 20260427143311 |
| settings_json | jsonb | yes | — | |
| created_at | timestamptz | no | — | |
| updated_at | timestamptz | yes | — | |
| deleted_at | timestamptz | yes | — | soft delete |

Unique: `role_sidebar_preferences_active_unique_idx` UNIQUE `(role_id, tenant_id) WHERE deleted_at IS NULL`.

### sidebar_variants (`SidebarVariant`)
| column | pg type | nullable | default | notes |
|---|---|---|---|---|
| id | uuid | no | gen_random_uuid() | PK `sidebar_variants_pkey` |
| user_id | uuid | no | — | FK `sidebar_variants_user_id_foreign` → users.id — **no ON UPDATE/DELETE clause** (both NO ACTION) |
| tenant_id | uuid | yes | — | |
| organization_id | uuid | yes | — | |
| locale | text | no | — | excluded from uniqueness since 20260427143311 |
| name | text | no | — | |
| settings_json | jsonb | yes | — | |
| is_active | boolean | no | false | |
| created_at | timestamptz | no | — | |
| updated_at | timestamptz | yes | — | |
| deleted_at | timestamptz | yes | — | soft delete |

Unique: `sidebar_variants_active_name_unique_idx` UNIQUE `(user_id, tenant_id, name) WHERE deleted_at IS NULL`.

### user_consents (`UserConsent`)
| column | pg type | nullable | default | notes |
|---|---|---|---|---|
| id | uuid | no | gen_random_uuid() | PK `user_consents_pkey` |
| user_id | uuid | no | — | **NO FK** — plain `@Property` (only auth table referencing users without an FK) |
| tenant_id | uuid | yes | — | |
| organization_id | uuid | yes | — | |
| consent_type | text | no | — | |
| is_granted | boolean | no | false | |
| granted_at | timestamptz | yes | — | |
| withdrawn_at | timestamptz | yes | — | |
| source | text | yes | — | encrypted |
| ip_address | text | yes | — | encrypted |
| integrity_hash | text | yes | — | HMAC via `lib/consentIntegrity.ts` |
| created_at | timestamptz | no | — | |
| updated_at | timestamptz | yes | — | |
| deleted_at | timestamptz | yes | — | soft delete |

Constraint: `user_consents_user_id_tenant_id_consent_type_unique` UNIQUE `(user_id, tenant_id, consent_type)` — full constraint.

### Encryption (`encryption.ts`) — `defaultEncryptionMaps: ModuleEncryptionMap[]` (shared mechanism, spec 07)
| entityId | field | hashField |
|---|---|---|
| `auth:user` | `email` | `email_hash` |
| `auth:user` | `name` | — |
| `auth:user_consent` | `ip_address` | — |
| `auth:user_consent` | `source` | — |

AES-256-GCM per-row IV (ciphertext non-deterministic → uniqueness keyed on `email_hash`). `hashField` = keyed HMAC-SHA-256 with lookup pepper, hex prefixed `v2:` (legacy fallback unkeyed `sha256(lower(trim(value)))`); `lookupHashCandidates` returns both. Module wrappers: `lib/emailHash.ts` (`computeEmailHash`, `emailHashLookupValues`).

## Custom entities & field sets

**None.** No `ce.ts`, `data/extensions.ts`, `data/fields.ts`, or `constants.ts` — auth declares no custom-field sets, custom entities, or entity extensions. (It does read/write custom-field *values* on `auth:user`/`auth:role` via the shared data engine, but declares no field definitions.)

## Events

Declared via `createModuleEvents({ moduleId: 'auth', events })` (`events.ts`; not `strict` → undeclared emits log `console.error` but still fire). 12 declared ids.

### Emitted
| name | payload | emitted from | persistent |
|---|---|---|---|
| `auth.login.failed` | `{ email, reason: 'invalid_password'|'invalid_credentials' }` | api/login.ts:125 (fire-and-forget) | no |
| `auth.login.success` | `{ id, email, tenantId, organizationId }` (tenantId/orgId nullable) | api/login.ts:165 (fire-and-forget) | no |
| `auth.user.created`/`updated`/`deleted` | `{ id, organizationId, tenantId }` (`userCrudEvents.buildPayload`) | commands/users.ts via `emitCrudSideEffects` (spec 03); also `makeCrudRoute` | **yes** |
| `auth.role.created`/`updated`/`deleted` | `{ id, tenantId }` (`roleCrudEvents.buildPayload` — **no organizationId**) | commands/roles.ts; also `makeCrudRoute` | **yes** |
| `query_index.coverage.warmup` | `{ tenantId }` | api/login.ts:145 (`eventBus.emitEvent`, try-caught) — belongs to query_index module | no |
| `query_index.upsert_one` / `query_index.delete_one` | `{ entityType, recordId, organizationId?, tenantId }` + `crudAction`/`coverageBaseDelta` | shared indexer path (spec 03) via `userCrudIndexer` (`E.auth.user`) / `roleCrudIndexer` (`E.auth.role`) | per spec 03 |

Per-command CRUD emission map: `auth.users.create` execute→created / undo→deleted (hard-delete + cascade) / redo→created; `auth.users.update` execute→updated / undo→updated; `auth.users.delete` execute→deleted / undo→**no event** (silent restore); `auth.roles.create` execute→created / undo→**no event** / redo→created; `auth.roles.update` execute→updated / undo→updated; `auth.roles.delete` execute→deleted / undo→updated (restore emitted as update). Custom-field writes use `notify: false` (no extra `auth.*.updated`).

**Declared but NEVER emitted anywhere in the repo:** `auth.logout`, `auth.password.changed`, `auth.password.reset.requested`, `auth.password.reset.completed` (password-reset feedback goes via notifications instead).

### Consumed
**None.** No `subscribers/` directory; auth consumes no events. (Other modules consume `auth.user.deleted` — see ambiguity below.)

### Notification types (`notifications.ts`, module `'auth'`, 6 defs — via notifications module, NOT eventBus)
| type | severity | expiresAfterHours | created from |
|---|---|---|---|
| `auth.password_reset.requested` | info | 24 | api/reset.ts (user has tenantId) |
| `auth.password_reset.completed` | success | 72 | api/reset/confirm.ts (user has tenantId) |
| `auth.account.locked` | warning | — | **never created** |
| `auth.login.new_device` | info | 168 | **never created** |
| `auth.role.assigned` | success | 168 | commands/users.ts `notifyRoleChanges` (skipped if no tenantId) |
| `auth.role.revoked` | warning | 168 | commands/users.ts `notifyRoleChanges` |

Created via `resolveNotificationService(...).create(buildNotificationFromType(typeDef, {recipientUserId, sourceEntityType:'auth:user', sourceEntityId}), {tenantId, organizationId})`, always try-caught.

## Workers & queues

**None.** No `workers/` directory; no queue enqueue calls anywhere in the module. Emails (`ResetPasswordEmail`, `InviteUserEmail`) are sent **synchronously** via `sendEmail` (`@open-mercato/shared/lib/email/send`, direct Resend call) at `api/reset.ts`, `api/users/resend-invite/route.ts`, and `commands/users.ts` (`sendInviteToUser`; failure → `warning: 'invite_email_failed'`, no rollback).

## ACL features

All `module: 'auth'` (`acl.ts`; re-exported from `index.ts`).
| id | title | used by |
|---|---|---|
| `auth.users.list` | List users | GET /api/auth/users |
| `auth.users.create` | Create users | POST /api/auth/users, POST /api/auth/users/resend-invite |
| `auth.users.edit` | Edit users | PUT /api/auth/users, GET /api/auth/users/consents |
| `auth.users.delete` | Delete users | DELETE /api/auth/users |
| `auth.roles.list` | List roles | GET /api/auth/roles |
| `auth.roles.manage` | Manage roles | POST/PUT/DELETE /api/auth/roles |
| `auth.acl.manage` | Manage ACLs | GET /api/auth/features, GET+PUT /api/auth/roles/acl, GET+PUT /api/auth/users/acl |
| `auth.sidebar.manage` | Manage sidebar presets | PUT/DELETE /api/auth/sidebar/preferences (+ in-handler on GET role scope), POST /api/auth/sidebar/variants, PUT/DELETE /api/auth/sidebar/variants/[id] |

## Setup & seeding

`setup.ts`: `export const setup: ModuleSetupConfig = { defaultRoleFeatures: { admin: ['auth.*'] } }`. **No** `onTenantCreated`, `seedDefaults`, `seedExamples`, or `defaultCustomerRoleFeatures`. (superadmin gets `isSuperAdmin: true` + merged features via `ensureDefaultRoleAcls`.)

Auth **owns the cross-module tenant/role/ACL seeding orchestration** (`lib/setup-app.ts`), invoked by `mercato auth setup`:
- Constants: `DEFAULT_ROLE_NAMES = ['employee','admin','superadmin']`; `DEMO_SUPERADMIN_EMAIL='superadmin@acme.com'`; `DEFAULT_DERIVED_EMAIL_DOMAIN='acme.com'`.
- `ensureRoles(em, {roleNames?, tenantId})` — **requires tenantId** (`throw 'ensureRoles requires a tenantId — global roles are not supported'`); transactionally creates missing `Role {name, tenantId}`.
- `setupInitialTenant(em, options)` — primary roles default `['superadmin']`; existing-user reuse path (`reusedExistingUser: true`) unless `failIfUserExists`; `orgSlug` global uniqueness pre-flight (`OrgSlugExistsError`); creates `Tenant` `` `${orgName} Tenant` `` + `Organization {slug, isActive, depth:0, ancestor/child/descendantIds:[]}`; optional tenant DEK + `EncryptionMap` seeding; base users primary + (default) `admin@acme.com` role `['admin']` + `employee@acme.com` role `['employee']` password `'secret'` (bcrypt 10), `isConfirmed` = `primaryUser.confirm ?? true`, `emailHash = computeEmailHash(email)`; `rebuildHierarchyForTenant`; `ensureDefaultRoleAcls` (merges every module's `defaultRoleFeatures`); `deactivateDemoSuperAdminIfSelfOnboardingEnabled`; calls each module's `setup.onTenantCreated`. Returns `{tenantId, organizationId, users:[{user,roles,created}], reusedExistingUser}`.
- `ensureDefaultRoleAcls` / `ensureCustomRoleAcls` / `ensureRoleAclFor` — idempotent RoleAcl create/merge (upgrades `isSuperAdmin`, never downgrades). superadmin → `isSuperAdmin: true`.

## DI services

`di.ts` `register(container)`:
| name | registration | role | consumed by |
|---|---|---|---|
| `authService` | `asClass(AuthService).scoped()` | login/password/session/reset business logic | login, logout, reset, profile, session/refresh routes |
| `rbacService` | `asClass(RbacService).scoped()`; when `isRbacDefaultCacheEnabled()` (`OM_RBAC_DEFAULT_CACHE` = on/1/true/yes): `asFunction((c) => new RbacService(c.em, c.cache ?? createRbacFallbackCache())).scoped()` | ACL resolution + caching (5-min TTL) | features/feature-check/roles/users/*/acl/sidebar routes, all cross-module auth |

Also re-exports `resetRbacFallbackCache` (test helper). Container-registered `cache` (via `@open-mercato/cache`) preempts the fallback. Key business services (routes depend on observable behavior): `AuthService` (bcrypt cost 10; constant-time password verify against fixed dummy hash `$2b$10$OcZrhmZpIzJOjkfwUrk7d.Nl0eHNzOvalBcBlt5Ran.4lj8R3HZg6`, #2242; HMAC-SHA256 token hashing; reset TTL 60 min; atomic compare-and-set confirm; `deleteAllUserSessions` on password change), `RbacService` (cache key `rbac:<userId>:<tenantId||'null'>:<organizationId||'null'>`, tags `rbac:user:<id>`/`rbac:all`/`rbac:tenant:<id>`/`rbac:org:<id>`; per-user UserAcl wins exclusively over role aggregation; wildcard `*`/`prefix.*`; API-key `api_key:<id>` subject path via `api_keys.ApiKey`), `sidebarPreferencesService`, `RbacFallbackCache` (LRU MAX_ENTRIES 5000). Session binding (`lib/sessionIntegrity.ts`): JWT `sid` must reference a live non-expired `Session {id: sid, user: sub, deletedAt: null}` — this is what makes logout/password-reset revoke issued JWTs; tokens without `sid` allowed only with `_legacyToken === true` (grace window `JWT_LEGACY_GRACE_MINUTES`).

## CLI commands

`cli.ts` default export order `[addUser, seedRoles, syncRoleAcls, rotateEncryptionKey, addOrganization, setupApp, listOrganizations, listTenants, listUsers, setPassword]`. All password-taking commands enforce shared password policy.
| command | args | behavior |
|---|---|---|
| `add-user` | `--email --password --organizationId\|orgId\|org [--roles csv]` | creates `User` (bcrypt 10, `isConfirmed:true`, emailHash) + tenant-scoped `Role` finds-or-creates + `UserRole`. Prints `'User created with id <id>'` |
| `seed-roles` | `[--tenantId\|tenant\|tenant_id]` | `ensureRoles` for given tenant or all tenants |
| `sync-role-acls` | `[--tenantId] [--no-superadmin]` | requires generated CLI modules; `ensureDefaultRoleAcls` + `ensureCustomRoleAcls` per tenant |
| `rotate-encryption-key` | `[--tenantId] [--organizationId] [--old-key <secret>] [--dry-run] [--debug]` | aborts unless tenant-data encryption enabled; re-encrypts `users.email` + recomputes `emailHash` |
| `add-org` | `--name` | creates `Tenant` `` `${name} Tenant` `` + `Organization`, `rebuildHierarchyForTenant` |
| `setup` | `--orgName --email --password [--orgSlug] [--roles superadmin,admin,employee] [--skip-password-policy] [--with-examples] [--json]` | `--orgSlug` must match `/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/` (else exit 2); calls `setupInitialTenant` (`includeDerivedUsers:true`, `failIfUserExists: orgSlug!==undefined`); `--json` emits one line `{tenantId, organizationId, adminUserId, adminEmail, reusedExistingUser}`. Errors: `OrgSlugExistsError`/`USER_EXISTS` → exit 1; missing args/policy fail → exit 2 |
| `list-orgs` | — | table ID\|Name\|Tenant ID\|Created |
| `list-tenants` | — | table ID\|Name\|Created |
| `list-users` | `[--organizationId\|orgId\|org] [--tenantId\|tenant]` | table with roles |
| `set-password` | `--email --password` | finds user cross-tenant by `$or:[{email},{emailHash:{$in: emailHashLookupValues(email)}}]`; sets bcrypt-10 hash |

## Configuration

i18n: `de/en/es/pl.json`, 250 leaf keys each. Env vars (module scope):
| var | default / behavior |
|---|---|
| `REMEMBER_ME_DAYS` | `'30'` (remember-me session/cookie days) |
| `NODE_ENV` | `secure` cookie flag when `'production'`; secrets required in prod |
| `AUTH_TOKEN_SECRET` → `AUTH_SECRET` → `NEXTAUTH_SECRET` → `JWT_SECRET` | token-hash secret chain; prod-throw, dev default `'om-auth-token-dev-only-secret'` |
| `CONSENT_INTEGRITY_SECRET` → `AUTH_SECRET` → `NEXTAUTH_SECRET` → `JWT_SECRET` | consent-integrity secret chain; dev default `'om-consent-integrity-dev-only-secret'` |
| `OM_RBAC_DEFAULT_CACHE` | default OFF; `on\|1\|true\|yes` enables in-process LRU fallback cache |
| `OM_TEST_MODE` + `OM_TEST_AUTH_RATE_LIMIT_MODE` | `'1'` + `'opt-in'` → rate limit only when header `x-om-test-rate-limit: on` |
| `OM_INTEGRATION_TEST` | disables the limiter globally |
| `OM_PASSWORD_MIN_LENGTH` / `OM_PASSWORD_REQUIRE_{DIGIT,UPPERCASE,SPECIAL}` (+`NEXT_PUBLIC_` variants) | minLength 6, all requires true |
| `OM_OPTIMISTIC_LOCK` | enables optimistic-lock enforcement (spec 02 R40) |
| `RATE_LIMIT_<PREFIX>_POINTS/_DURATION/_BLOCK_DURATION` | per-endpoint override |
| `JWT_LEGACY_GRACE_MINUTES` | legacy (no-`sid`) token grace window |
| `TENANT_DATA_ENCRYPTION_DEBUG`, `TENANT_DATA_ENCRYPTION_FALLBACK_KEY` | cli.ts rotate-encryption-key |
| `OM_CLI_QUIET` | defaulted `'1'` in setup `--json` mode |
| `DEMO_MODE`, `OM_INIT_FLOW`, `SELF_SERVICE_ONBOARDING_ENABLED` | demo superadmin gating in setup-app |
| `OM_INIT_SUPERADMIN_EMAIL`, `OM_INIT_ADMIN_EMAIL`, `OM_INIT_EMPLOYEE_EMAIL` | derived-user email overrides |
| `OM_INIT_ADMIN_PASSWORD`, `OM_INIT_EMPLOYEE_PASSWORD` | default `'secret'` |
| `INVITE_TOKEN_TTL_MS` (constant, `lib/inviteToken.ts`) | 48h |

## Not ported

UI artifacts (out of scope): `backend/**` pages (users, roles, profile, change-password, settings, sidebar-customization) + `backend/users/roleOptions.ts`; `frontend/**` (login.tsx, login-injection.ts, reset.tsx, reset/[token]/page.tsx); `components/**` (AclEditor.tsx, AclDependencyDiagnosticsPanel.tsx, UserConsentsPanel.tsx); `emails/ResetPasswordEmail.tsx`, `emails/InviteUserEmail.tsx` (JSX rendering — token/URL/subject/TTL semantics ARE captured above); `lib/backendChrome.tsx`, `lib/profile-sections.tsx`; `agentic/standalone-guide.md`, README/AGENTS/CLAUDE docs. `data/validators.ts` is in-scope (Zod → field tables above). `.snapshot-*.json` MikroORM snapshots not runtime DDL. `__tests__`/`__integration__` read only to pin behavior (logout 307, session.refresh 307/401, roles/users empty-envelope quirk, optimistic-lock/tenant-scoping).

## Porting checklist

1. [ ] **Migrations** — 11 tables in migration order (base 9 → user_consents → sidebar_variants), with exact index/constraint names, partial unique indexes (`users_tenant_email_hash_uniq`, three sidebar `*_active_*_idx`), `roles.tenant_id NOT NULL`, `updated_at` on users+roles, `user_roles` FK indexes. Preserve full vs partial uniqueness semantics.
2. [ ] **Entities** — 11 entity classes; soft delete on all; bare `tenant_id`/`organization_id` (no FK); intra-module FKs (ON UPDATE CASCADE except `sidebar_variants`); `user_consents.user_id` has NO FK.
3. [ ] **Encryption maps** — `auth:user`(email+email_hash, name), `auth:user_consent`(ip_address, source); AES-256-GCM + HMAC lookup hash (spec 07).
4. [ ] **Services** — `AuthService` (bcrypt 10, constant-time verify vs fixed dummy hash, HMAC token hashing, session/reset lifecycle, atomic confirm), `RbacService` (cache keys/tags, per-user-ACL-wins, wildcard matching, api_key subject path), `sidebarPreferencesService`, RBAC fallback LRU, `sessionIntegrity` JWT↔Session binding, grant-check helpers, `enforceTenantSelection`, `safeRedirect`.
5. [ ] **Commands** — `auth.users.{create,update,delete}` + `auth.roles.{create,update,delete}` with undo/redo, per-tenant duplicate-email, reserved role names, rename/move-blocked-while-assigned, invite flow, CRUD side-effect + indexer configs.
6. [ ] **Routes** — 35 method+path combos; per-method requireAuth/requireFeatures; in-handler rate limits (fail-open); hand-written GET + `makeCrudRoute` for roles/users; empty-envelope quirk; optimistic-lock 409s; explicit `metadata.path` on consents; openApi docs (note logout/refresh 302-doc vs 307-runtime).
7. [ ] **Subscribers** — none.
8. [ ] **Workers/queues** — none (emails sent synchronously).
9. [ ] **ACL** — 8 features (exact ids/titles); `defaultRoleFeatures: { admin: ['auth.*'] }`.
10. [ ] **Setup/seed** — `lib/setup-app.ts` orchestration (`ensureRoles`, `setupInitialTenant`, `ensureDefaultRoleAcls`/`ensureCustomRoleAcls`), DEFAULT_ROLE_NAMES, derived users, demo-superadmin gating; calls directory `rebuildHierarchyForTenant` + entities `EncryptionMap`.
11. [ ] **DI** — `authService`, `rbacService` (with `OM_RBAC_DEFAULT_CACHE` fallback wiring).
12. [ ] **CLI** — 10 commands (add-user, seed-roles, sync-role-acls, rotate-encryption-key, add-org, setup, list-orgs, list-tenants, list-users, set-password).
13. [ ] **Notifications** — register 6 types (`notifications.ts`); wire role-change + password-reset creations (best-effort).
14. [ ] **Events** — declare 12 ids; emit `auth.login.{success,failed}` + CRUD events with exact payloads; fire `query_index.coverage.warmup` on login.
15. [ ] **Tests / parity** — run `om-verify-parity auth <tech>`; assert byte-exact routes, envelopes, status codes, event payloads, table/column/index names.

## Upstream ambiguities (unresolved / as-is)

1. **`auth.user.deleted` payload contract mismatch (real upstream bug — port verbatim, do NOT silently "fix").** Auth's only emit path is `userCrudEvents.buildPayload` → `{ id, organizationId, tenantId }` (verified `commands/users.ts:128-132`; no `userId` field, no other emit site). Both consumers read `payload.userId`: `communication_channels:user-deleted-cascade` fail-closes (`typeof payload.userId !== 'string'` → early return, verified `subscribers/user-deleted-cascade.ts:54-55`) and `sso:user-deleted-cleanup` queries `{ userId: payload.userId }`. **Net effect at this pin: the deletion cascades never fire.** A faithful 1:1 port reproduces this (emit `{id,...}`, consumers keyed on `userId`); flag for upstream but do not diverge.
2. **logout / session.refresh openApi doc vs runtime status.** Both routes' `openApi` docs declare **302**; runtime returns **307** (`NextResponse.redirect` default), pinned by `__tests__/logout.test.ts:65` and `session.refresh.test.ts`. Ported runtime must be 307; openApi doc mismatch is upstream-as-is.
3. **roles/users GET empty-envelope quirk on unauth/invalid.** The hand-written GET handlers return **200 `{items:[],total:0,totalPages:1}`** rather than 401/400 on unauthenticated call or Zod failure (test-pinned). In production the dispatcher's `requireAuth` fires first, so this is only observable when the handler is invoked directly — port the handler behavior as-is.
4. **`roles.tenant_id` full uniqueness vs soft-delete.** `roles_tenant_id_name_unique` is a full (non-partial) constraint — soft-deleted roles still occupy their `(tenant_id, name)`, unlike users' partial unique. Intentional per migration history; reproduce exactly.
