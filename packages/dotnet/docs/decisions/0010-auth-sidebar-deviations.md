# 0010 — Auth sidebar slice deviations

Status: accepted
Date: 2026-07-07

Ports `api/sidebar/preferences/route.ts`, `api/sidebar/variants/route.ts`,
`api/sidebar/variants/[id]/route.ts` and `services/sidebarPreferencesService.ts`. Observable behavior
(paths, methods, status codes, JSON envelopes, cookie/header flags, the `duplicate_name` 409, the
optimistic-lock 409, POST-variants-returns-200) is 1:1. The following internal deviations are notable.

## 1. Validation `details` shape vs Zod `flatten()`
Upstream returns Zod's `error.flatten()` verbatim in `400 {"error":"Invalid payload","details":…}`.
`SidebarValidation` reproduces the flatten **shape** (`{formErrors:[], fieldErrors:{<field>:[…]}}`),
the field bucketing (variants nest under `settings`, preferences keep settings fields top-level), and
the load-bearing rules (types, min/max lengths, uuid checks, the `scope==='role'` +
`applyToRoles`/`clearRoleIds` superRefine — whose messages are copied verbatim). Individual generic
message strings ("Expected string", "String must contain at least 1 character(s)", …) are
approximations of Zod's copy, not byte-exact. Status code and envelope keys are exact.

## 2. uuid validation is canonical-hex, not Zod's version-checked regex
`applyToRoles`/`clearRoleIds`/`scope.roleId` accept any canonical 8-4-4-4-12 hex uuid rather than
Zod's stricter version/variant-checked pattern. Practically equivalent for real uuids.

## 3. Cache-tag invalidation is a no-op
Upstream invalidates `nav:sidebar:*` tags via the DI cache service. The .NET port has no read-through
nav cache yet, so `InvalidateCacheTags()` is a documented no-op (`// PARITY-TODO`). No response bytes
depend on it.

## 4. API-key auth branches are unreachable
Upstream distinguishes `auth.isApiKey`/`auth.userId` and can 403 with `"No user context"` /
`"Cannot save preferences: no user associated with this API key"`. The foundation `AuthContext`
resolves only JWT+session principals (`UserId` is always a valid Guid), so those branches never fire.
Kept structurally where cheap; marked `// PARITY-TODO`.

## 5. `updated_at` set in application code (MikroORM `onUpdate` parity)
Sidebar rows have no DB default for `updated_at`; the service sets it to `now` on updates and leaves it
null on insert, matching MikroORM's `onUpdate`-only semantics. Optimistic-lock comparisons and the
`updatedAt` response field key off this value formatted like JS `Date.toISOString()` (UTC, ms, `Z`).

## 6. Locale resolution
`SidebarLocale` mirrors `detectLocale()` precedence (`locale` cookie → `Accept-Language` → default
`en`) over the supported set `[en, pl, es, de]`. The resolved locale is echoed in responses and stored
in the row's `locale` column, which upstream excludes from every sidebar uniqueness scope.
