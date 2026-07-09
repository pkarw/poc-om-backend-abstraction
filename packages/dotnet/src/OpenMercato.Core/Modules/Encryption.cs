namespace OpenMercato.Core.Modules;

/// <summary>
/// One field-encryption rule (upstream <c>EncryptedFieldRule</c> in
/// packages/shared/src/lib/encryption/tenantDataEncryptionService.ts). <see cref="Field"/> is the
/// entity field/column to encrypt; when <see cref="HashField"/> is set, a deterministic
/// lookup hash of the plaintext is written into that field (e.g. email → email_hash).
/// </summary>
public sealed record EncryptedFieldRule(string Field, string? HashField = null);

/// <summary>
/// A module's default encryption map for one entity (upstream per-module <c>encryption.ts</c>
/// <c>ModuleEncryptionMap</c> / <c>defaultEncryptionMaps</c>). Seeded into <c>encryption_maps</c>
/// per tenant/org by the initial-tenant seeder.
/// </summary>
public sealed record ModuleEncryptionMap(string EntityId, IReadOnlyList<EncryptedFieldRule> Fields);
