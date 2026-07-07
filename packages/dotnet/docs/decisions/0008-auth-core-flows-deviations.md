# 0008 — Auth core-flow slice deviations (login/logout/reset/session/profile/features/locale)

Status: accepted
Date: 2026-07-07

## Context

Domain slice 1 ports the core auth HTTP flows and `AuthService`
(upstream `packages/core/src/modules/auth/api/{login,logout,reset,session,profile,feature-check,features,locale}`
and `services/authService.ts`). A few upstream dependencies are not yet ported; this ADR records the
resulting deviations. Observable behavior (paths, status codes, JSON envelopes, cookie names/flags,
307 redirects) matches the contract.

## Decisions

1. **Rate limiter — Redis fixed window, fail-open.** Upstream uses `rate-limiter-flexible` with a
   memory insurance limiter (`packages/shared/src/lib/ratelimit`). This port implements a small
   in-handler fixed-window limiter over the shared `IConnectionMultiplexer`
   (`RateLimit/AuthRateLimiter.cs`): `INCR` + `EXPIRE(duration)`, with a `blockDuration` window on
   first rejection. It is **fail-open** (any error → request proceeds) and honors the same disable
   switches (`OM_INTEGRATION_TEST`, `RATE_LIMIT_ENABLED=false`, and the
   `OM_TEST_MODE` + `OM_TEST_AUTH_RATE_LIMIT_MODE=opt-in` header escape hatch). The 429 envelope
   (`{"error":"Too many requests. Please try again later."}`) and headers
   (`Retry-After`, `X-RateLimit-Limit/Remaining/Reset`) and the per-endpoint points/duration/block
   defaults + `RATE_LIMIT_<PREFIX>_*` overrides match the contract. Sliding-window/atomicity nuances
   of rate-limiter-flexible are not reproduced (acceptable for a fail-open guard).

2. **`AuthService` is not DI-registered; handlers construct it.** `AuthModule.ConfigureServices`
   (foundation, not editable by this slice) leaves domain-service registration to the integrator.
   To compose without editing it, route handlers build `new AuthService(db, passwords, tokens, enc)`
   from DI singletons + the scoped `AppDbContext`. AuthService itself is stateless w.r.t. DI lifetime.

3. **Profile PUT applies the update directly (no commandBus).** Upstream routes the mutation through
   `commandBus.execute('auth.users.update')`, which lives in the users/roles slice. This slice applies
   the email/password change directly to the row (encrypt email + recompute `email_hash`, bcrypt the
   password, bump `updated_at`) and re-signs the JWT preserving `sid`. Consequences: per-tenant
   duplicate-email enforcement, `auth.user.updated` CRUD events, custom-field writes and the exact
   `Email already in use` 400 body are **not** produced here — a DB-level failure maps to the
   catch-all `400 {"error":"Failed to update profile."}`. Marked `// PARITY-TODO`.

4. **Emails and notifications are no-ops.** The password-reset email (`ResetPasswordEmail`) and the
   tenant-scoped `auth.password_reset.{requested,completed}` notifications are skipped (email +
   notifications modules not ported). The reset-token row is still created so `/reset/confirm`
   works; existence-hiding (`200 {ok:true}`) is preserved. Marked `// PARITY-TODO`.

5. **`query_index.coverage.warmup` on login is emitted best-effort** via `IEventBus`; with no
   query_index subscriber present it is a no-op. `auth.login.success`/`auth.login.failed` are emitted
   with the exact upstream payloads (best-effort).

## Consequences

Login, logout, password reset/confirm, session refresh (browser 307 + API JSON), profile get/update,
feature-check, features aggregation and locale selection are 1:1 on the wire. Items 3–4 become full
parity once the users/roles commands, notifications and email modules are ported.
