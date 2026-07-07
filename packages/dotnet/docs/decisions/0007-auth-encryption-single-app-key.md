# 0007 — Auth field encryption uses a single app-level key (not per-tenant DEK)

Status: accepted
Date: 2026-07-07
Context: porting the `auth` module encryption maps (`auth:user` email/name,
`auth:user_consent` source/ip_address) — spec 07, contract "Encryption" section.

## Decision

`EncryptionService` reproduces the observable at-rest format of upstream
`packages/shared/src/lib/encryption/aes.ts`:

- **AES-256-GCM**, random per-row 12-byte IV, serialized as
  `ivB64:ciphertextB64:tagB64:v1` — byte-compatible with `encryptWithAesGcm` /
  `decryptWithAesGcm` (returns null on any format/auth failure).
- **Email lookup hash** exactly per spec 05 R47 / contract: `v2:` + lowercase-hex
  HMAC-SHA256(pepper, `lower(trim(email))`) when a pepper is configured
  (`LOOKUP_HASH_PEPPER` → `TENANT_DATA_ENCRYPTION_FALLBACK_KEY` →
  `TENANT_DATA_ENCRYPTION_KEY`), else the legacy unkeyed `SHA-256(lower(trim(email)))` hex.
  `EmailHashLookupValues` returns both candidates for read-side matching.

## Deviation — key source

Upstream derives a **per-tenant DEK** via a KMS (`tenantDataEncryptionService.ts` + Vault).
That machinery (tenant DEK table, KMS transit, rotation CLI) is out of scope for this port
wave, so ciphertext columns are encrypted/decrypted with a **single app-level key** =
`SHA-256(JWT_SECRET)` (32 bytes). Consequences and why they are acceptable:

- **Login by email is unaffected.** Lookups key on the deterministic `email_hash` (HMAC,
  correct per R47), never on the non-deterministic ciphertext. This ADR keeps the hash exact.
- Ciphertext is not portable to/from the TS reference (different key), but the wire format is
  identical and round-trips within this port.
- No tenant isolation of the encryption key. Documented risk; revisit when the `entities`
  module (which owns `EncryptionMap`/DEK seeding) and a KMS adapter are ported.

The email/name/consent fields remain encrypted at rest and the lookup-hash contract holds,
which is what login, per-tenant email uniqueness (R46), and consent decryption depend on.
