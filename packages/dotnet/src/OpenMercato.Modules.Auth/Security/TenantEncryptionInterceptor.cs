using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpenMercato.Core.Modules;

namespace OpenMercato.Modules.Auth.Security;

/// <summary>
/// EF SaveChanges interceptor that encrypts mapped entity fields with the per-tenant DEK just before
/// they are persisted — the .NET analog of upstream's MikroORM onFlush encryption subscriber
/// (packages/shared/src/lib/encryption/subscriber.ts).
///
/// For each Added/Modified entry whose CLR type is registered in
/// <see cref="ModuleRegistry.EncryptedEntityTypeMap"/> it reads the row's tenant/organization ids,
/// runs <see cref="TenantDataEncryptionService.EncryptEntityPayload"/>, and writes the resulting
/// ciphertext (+ any hashField) back onto the entry's <c>CurrentValues</c>.
///
/// Fail-soft: when no encryption map applies (e.g. the EF InMemory provider used in tests, or a
/// tenant without a provisioned map) the service returns the payload unchanged and this is a no-op.
/// </summary>
public sealed class TenantEncryptionInterceptor : SaveChangesInterceptor
{
    private readonly TenantDataEncryptionService _encryption;
    private readonly ModuleRegistry _registry;

    public TenantEncryptionInterceptor(TenantDataEncryptionService encryption, ModuleRegistry registry)
    {
        _encryption = encryption;
        _registry = registry;
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Process(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Process(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Process(DbContext? context)
    {
        if (context is null) return;
        var typeMap = _registry.EncryptedEntityTypeMap;
        if (typeMap.Count == 0) return;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            if (!typeMap.TryGetValue(entry.Entity.GetType(), out var entityId)) continue;

            var tenantId = ReadGuid(entry, "TenantId");
            var organizationId = ReadGuid(entry, "OrganizationId");

            // Build the payload from all non-key scalar properties so map field/hashField names resolve
            // against real CLR property names.
            var payload = new Dictionary<string, object?>();
            foreach (var prop in entry.Properties)
            {
                if (prop.Metadata.IsPrimaryKey()) continue;
                payload[prop.Metadata.Name] = prop.CurrentValue;
            }

            var encrypted = _encryption.EncryptEntityPayload(context, entityId, tenantId, organizationId, payload);
            foreach (var (name, value) in encrypted)
            {
                var prop = entry.Properties.FirstOrDefault(p => p.Metadata.Name == name);
                if (prop is null) continue;
                if (!Equals(prop.CurrentValue, value)) prop.CurrentValue = value;
            }
        }
    }

    private static Guid? ReadGuid(EntityEntry entry, string propertyName)
    {
        var prop = entry.Properties.FirstOrDefault(p => p.Metadata.Name == propertyName);
        return prop?.CurrentValue switch
        {
            Guid g => g,
            _ => null,
        };
    }
}
