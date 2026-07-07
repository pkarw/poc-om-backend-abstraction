# 0006 — Auth JWT (hand-rolled HS256) and session-bound revalidation

Status: accepted
Date: 2026-07-07
Context: porting the `auth` module (spec 05, contract upstream/analysis/modules/auth.md).

## Decision

**Hand-rolled HS256 JWT.** `JwtService` reproduces upstream
`packages/shared/src/lib/auth/jwt.ts` byte-for-byte rather than using a JWT library, so a
token minted by the TypeScript reference verifies in this port and vice-versa (spec 05
verification step 2, cross-implementation token exchange). Specifics preserved:

- Header `{"alg":"HS256","typ":"JWT"}`, unpadded base64url segments, signature =
  HMAC-SHA256 over `header.payload`, constant-time comparison (`CryptographicOperations.FixedTimeEquals`).
- Audience-derived signing key (spec 05 R2): key = env `JWT_STAFF_SECRET` if set, else the
  lowercase hex of `HMAC-SHA256(key=JWT_SECRET, msg="open-mercato:jwt:v1:staff")` used as a
  UTF-8 key string. Audience normalization (lowercase, non-alphanumeric collapsed to `_`)
  matches upstream so staff/customer keys never cross-verify.
- Issuer `open-mercato`, audience `staff`, default TTL 28800 s. Verification enforces
  `aud`/`iss` strict equality and rejects past `exp`; missing `exp` accepted.
- Legacy grace (R4): when default verification fails and `JWT_LEGACY_GRACE_MINUTES` is not
  `0`/`false`/`off`, retry with the raw `JWT_SECRET` (no aud/iss) and mark `LegacyToken = true`.

Claims carried: `sub, sid, tenantId, orgId, email, roles` plus standard `iat/exp/iss/aud`.

## Deviation — session binding rejects tokens without `sid`

Upstream `sessionIntegrity.ts` allows a `sid`-less token during a legacy grace window
(`JWT_LEGACY_GRACE_MINUTES`). The foundation `HttpContextAuth.ResolveAsync` **requires** a
`sid` that references a live, non-expired, non-deleted `sessions` row, and rejects tokens
without one. Rationale: the grace window exists only for rolling deploys of already-issued
`sid`-less tokens, which this fresh port never mints. This keeps logout / password-reset /
session-delete immediately revoking issued JWTs (spec 05 R17b/R18) with no grace hole. The
JWT-level legacy grace (verifying an old signature) is still honored by `JwtService`; only
the session-binding step is stricter. Revisit if importing pre-existing `sid`-less tokens.
