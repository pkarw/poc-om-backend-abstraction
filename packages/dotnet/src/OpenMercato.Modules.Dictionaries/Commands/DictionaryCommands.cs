using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Dictionaries.Data;
using OpenMercato.Modules.Dictionaries.Lib;

namespace OpenMercato.Modules.Dictionaries.Commands;

internal static class DictionaryProtection
{
    /// <summary>Currency dictionaries (key <c>currency</c>/<c>currencies</c>) are immutable/undeletable.</summary>
    public static bool IsProtectedCurrency(string key)
    {
        var k = key.Trim().ToLowerInvariant();
        return k is "currency" or "currencies";
    }
}

/// <summary>
/// <c>dictionaries.dictionary.create</c> — the port of the inline create in upstream
/// <c>api/route.ts</c> POST, moved onto the command bus per the .NET write convention. Enforces the
/// scope key uniqueness (409) and lowercases the key.
/// </summary>
public sealed class CreateDictionaryCommand
    : ICommand<DictionaryCreateInput, DictionaryResult>, ICommandLogMetadataBuilder<DictionaryCreateInput, DictionaryResult>
{
    public string CommandId => "dictionaries.dictionary.create";

    public async Task<DictionaryResult> ExecuteAsync(DictionaryCreateInput input, CommandContext ctx, IServiceProvider services)
    {
        if (ctx.OrganizationId is not { } orgId || ctx.TenantId is not { } tenantId)
            throw CommandHttpException.BadRequest("Organization context is required");

        var db = services.GetRequiredService<AppDbContext>();
        var key = input.Key.Trim().ToLowerInvariant();

        var existing = await db.Set<Dictionary>().FirstOrDefaultAsync(d =>
            d.OrganizationId == orgId && d.TenantId == tenantId && d.Key == key && d.DeletedAt == null);
        if (existing is not null)
            throw CommandHttpException.Conflict("A dictionary with this key already exists");

        var now = DateTimeOffset.UtcNow;
        var dictionary = new Dictionary
        {
            Id = Guid.NewGuid(),
            Key = key,
            Name = input.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description!.Trim(),
            OrganizationId = orgId,
            TenantId = tenantId,
            IsSystem = input.IsSystem ?? false,
            IsActive = input.IsActive ?? true,
            ManagerVisibility = "default",
            EntrySortMode = DictionaryEntrySortModes.Resolve(input.EntrySortMode),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Set<Dictionary>().Add(dictionary);
        return new DictionaryResult(dictionary.Id.ToString());
    }

    public CommandLogMetadata BuildLog(DictionaryCreateInput input, DictionaryResult result, CommandContext ctx) =>
        new() { ActionLabel = "Create dictionary", ResourceKind = "dictionaries.dictionary", ResourceId = result.Id };
}

/// <summary>
/// <c>dictionaries.dictionary.update</c> — the port of upstream <c>api/[dictionaryId]/route.ts</c> PATCH.
/// Currency protection, key-change strict-regex + duplicate check, and the <c>is_active=false ⇒ deleted_at</c>
/// (soft delete) / <c>is_active=true ⇒ deleted_at=null</c> (restore) toggle.
/// </summary>
public sealed class UpdateDictionaryCommand : ICommand<DictionaryUpdateInput, DictionaryResult>
{
    private static readonly Regex StrictKey = new("^[a-z0-9][a-z0-9_-]*$", RegexOptions.Compiled);

    public string CommandId => "dictionaries.dictionary.update";

    public async Task<DictionaryResult> ExecuteAsync(DictionaryUpdateInput input, CommandContext ctx, IServiceProvider services)
    {
        if (ctx.OrganizationId is not { } orgId || ctx.TenantId is not { } tenantId)
            throw CommandHttpException.BadRequest("Organization context is required");

        var db = services.GetRequiredService<AppDbContext>();
        var dictionary = await db.Set<Dictionary>().FirstOrDefaultAsync(d =>
            d.Id == input.Id && d.OrganizationId == orgId && d.TenantId == tenantId && d.DeletedAt == null);
        if (dictionary is null) throw CommandHttpException.NotFound("Dictionary not found");

        OptimisticLock.Enforce("dictionaries.dictionary", dictionary.Id.ToString(), dictionary.UpdatedAt.UtcDateTime, ctx);

        var protectedCurrency = DictionaryProtection.IsProtectedCurrency(dictionary.Key);
        if (protectedCurrency)
        {
            if (input.Provided.Contains("key") && input.Key is not null && input.Key.Trim().ToLowerInvariant() != dictionary.Key)
                throw CommandHttpException.BadRequest("The currency dictionary cannot be modified or deleted.");
            if (input.Provided.Contains("isActive") && input.IsActive == false)
                throw CommandHttpException.BadRequest("The currency dictionary cannot be modified or deleted.");
        }

        if (input.Provided.Contains("key") && input.Key is not null)
        {
            var key = input.Key.Trim().ToLowerInvariant();
            if (key != dictionary.Key)
            {
                if (!StrictKey.IsMatch(key))
                    throw CommandHttpException.BadRequest("Use lowercase letters, numbers, hyphen, or underscore.");
                var dup = await db.Set<Dictionary>().FirstOrDefaultAsync(d =>
                    d.Key == key && d.OrganizationId == orgId && d.TenantId == tenantId && d.DeletedAt == null);
                if (dup is not null) throw CommandHttpException.Conflict("A dictionary with this key already exists");
                dictionary.Key = key;
            }
        }

        if (input.Provided.Contains("name") && input.Name is not null)
            dictionary.Name = input.Name.Trim();
        if (input.Provided.Contains("description"))
            dictionary.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description!.Trim();
        if (input.Provided.Contains("isActive") && input.IsActive is { } active)
        {
            dictionary.IsActive = active;
            dictionary.DeletedAt = active ? null : (dictionary.DeletedAt ?? DateTimeOffset.UtcNow);
        }
        if (input.Provided.Contains("entrySortMode") && input.EntrySortMode is not null)
            dictionary.EntrySortMode = DictionaryEntrySortModes.Resolve(input.EntrySortMode);

        dictionary.UpdatedAt = DateTimeOffset.UtcNow;
        return new DictionaryResult(dictionary.Id.ToString());
    }
}

/// <summary>
/// <c>dictionaries.dictionary.delete</c> — soft delete (upstream DELETE). Currency dictionaries are
/// protected (400). Sets <c>is_active=false</c> + <c>deleted_at</c>.
/// </summary>
public sealed class DeleteDictionaryCommand : ICommand<DictionaryDeleteInput, DictionaryResult>
{
    public string CommandId => "dictionaries.dictionary.delete";

    public async Task<DictionaryResult> ExecuteAsync(DictionaryDeleteInput input, CommandContext ctx, IServiceProvider services)
    {
        if (ctx.OrganizationId is not { } orgId || ctx.TenantId is not { } tenantId)
            throw CommandHttpException.BadRequest("Organization context is required");

        var db = services.GetRequiredService<AppDbContext>();
        var dictionary = await db.Set<Dictionary>().FirstOrDefaultAsync(d =>
            d.Id == input.Id && d.OrganizationId == orgId && d.TenantId == tenantId && d.DeletedAt == null);
        if (dictionary is null) throw CommandHttpException.NotFound("Dictionary not found");

        if (DictionaryProtection.IsProtectedCurrency(dictionary.Key))
            throw CommandHttpException.BadRequest("The currency dictionary cannot be modified or deleted.");

        dictionary.IsActive = false;
        dictionary.DeletedAt = dictionary.DeletedAt ?? DateTimeOffset.UtcNow;
        dictionary.UpdatedAt = DateTimeOffset.UtcNow;
        return new DictionaryResult(dictionary.Id.ToString());
    }
}
