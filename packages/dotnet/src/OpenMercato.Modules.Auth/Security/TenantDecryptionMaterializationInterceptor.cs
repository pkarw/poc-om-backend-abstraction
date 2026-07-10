using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenMercato.Core.Modules;

namespace OpenMercato.Modules.Auth.Security;

/// <summary>
/// Global EF materialization interceptor that DECRYPTS mapped entity fields the moment an entity is
/// read back from the database — the .NET analog of upstream's read-side decrypt (MikroORM onLoad /
/// the explicit <c>decryptEntity</c> calls). Because it fires for EVERY materialized instance of a
/// registered CLR type, all read sites (tracked or <c>AsNoTracking</c>, LINQ or raw-through-EF) get
/// plaintext automatically — no per-query decrypt call is required.
///
/// CHANGE-TRACKING CORRECTNESS: the decrypt runs in <see cref="InitializedInstance"/>, i.e. AFTER EF
/// has set every scalar from the reader but BEFORE the entity is handed to the change tracker and its
/// original-values snapshot is taken. So EF snapshots the DECRYPTED state: a tracked round-trip that
/// re-saves an untouched entity sees it as <c>Unchanged</c> and never rewrites ciphertext.
///
/// FAIL-SOFT: decryption is delegated to <see cref="TenantDataEncryptionService"/>, whose map lookup
/// runs raw SQL and returns the payload unchanged when it can't run (e.g. the EF InMemory provider) or
/// when no map / no DEK applies. So on a non-relational provider, or a tenant without provisioned
/// encryption, this interceptor is a pure no-op. Decryption is also IDEMPOTENT: a value that is not a
/// valid <c>iv:ct:tag:v1</c> payload (already plaintext) is left exactly as-is, so this coexists
/// safely with Stage-1's explicit auth decrypt helpers (double-decrypt = no-op).
/// </summary>
public sealed class TenantDecryptionMaterializationInterceptor : IMaterializationInterceptor
{
    private readonly TenantDataEncryptionService _encryption;
    private readonly ModuleRegistry _registry;

    // Cache the writable string properties per CLR type (payload build/write-back set).
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> StringPropsCache = new();

    public TenantDecryptionMaterializationInterceptor(TenantDataEncryptionService encryption, ModuleRegistry registry)
    {
        _encryption = encryption;
        _registry = registry;
    }

    public object InitializedInstance(MaterializationInterceptionData materializationData, object instance)
    {
        var typeMap = _registry.EncryptedEntityTypeMap;
        if (typeMap.Count == 0) return instance;

        var clrType = instance.GetType();
        if (!typeMap.TryGetValue(clrType, out var entityId)) return instance;

        var context = materializationData.Context;
        if (context is null) return instance;

        var stringProps = StringPropsCache.GetOrAdd(clrType, GetStringProps);
        if (stringProps.Length == 0) return instance;

        var tenantId = ReadGuid(clrType, instance, "TenantId");
        var organizationId = ReadGuid(clrType, instance, "OrganizationId");

        // Build the candidate payload from the entity's string properties; DecryptEntityPayload only
        // touches the fields present in the map and leaves everything else (incl. plaintext) untouched.
        var payload = new Dictionary<string, object?>(stringProps.Length);
        foreach (var p in stringProps)
            payload[p.Name] = p.GetValue(instance);

        var decrypted = _encryption.DecryptEntityPayload(context, entityId, tenantId, organizationId, payload);

        foreach (var p in stringProps)
        {
            if (!decrypted.TryGetValue(p.Name, out var value)) continue;
            if (!Equals(p.GetValue(instance), value)) p.SetValue(instance, value);
        }

        return instance;
    }

    private static PropertyInfo[] GetStringProps(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0)
            .ToArray();

    private static Guid? ReadGuid(Type type, object instance, string propertyName)
    {
        var prop = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        return prop?.GetValue(instance) is Guid g ? g : null;
    }
}
