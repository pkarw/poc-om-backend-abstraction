using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Commands;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Commands;

/// <summary>
/// Shared write helpers for the customers command handlers — base-table field application, custom-field
/// persistence (via the wired <see cref="ICrudCustomFields"/>), tag syncing, and snapshot/restore for
/// undo/redo. Centralized so people/companies/addresses/tags stay DRY and byte-consistent.
/// </summary>
internal static class CustomerWriteHelpers
{
    public const string PersonEntityType = "customers:customer_person_profile";
    public const string CompanyEntityType = "customers:customer_company_profile";

    // ---- base customer_entities field application (create + update) ---------------------------

    public static void ApplyBaseCreate(CustomerEntity e, JsonElement body)
    {
        e.Description = J.Str(body, "description");
        e.OwnerUserId = J.GuidOf(body, "ownerUserId");
        e.PrimaryEmail = ClearableEmail(body, "primaryEmail");
        e.PrimaryPhone = J.Str(body, "primaryPhone");
        e.Status = J.Str(body, "status");
        e.LifecycleStage = J.Str(body, "lifecycleStage");
        e.Source = J.Str(body, "source");
        e.Temperature = J.Str(body, "temperature");
        e.RenewalQuarter = J.Str(body, "renewalQuarter");
        e.IsActive = J.Bool(body, "isActive") ?? true;
    }

    public static void ApplyBaseUpdate(CustomerEntity e, IReadOnlyDictionary<string, JsonElement> fields)
    {
        if (fields.TryGetValue("displayName", out var dn) && dn.ValueKind == JsonValueKind.String) e.DisplayName = dn.GetString()!;
        if (fields.TryGetValue("description", out var d)) e.Description = AsString(d);
        if (fields.TryGetValue("ownerUserId", out var o)) e.OwnerUserId = AsGuid(o);
        if (fields.TryGetValue("primaryEmail", out var pe)) e.PrimaryEmail = AsString(pe);
        if (fields.TryGetValue("primaryPhone", out var pp)) e.PrimaryPhone = AsString(pp);
        if (fields.TryGetValue("status", out var s)) e.Status = AsString(s);
        if (fields.TryGetValue("lifecycleStage", out var ls)) e.LifecycleStage = AsString(ls);
        if (fields.TryGetValue("source", out var sr)) e.Source = AsString(sr);
        if (fields.TryGetValue("temperature", out var t)) e.Temperature = AsString(t);
        if (fields.TryGetValue("renewalQuarter", out var rq)) e.RenewalQuarter = AsString(rq);
        if (fields.TryGetValue("isActive", out var ia) && (ia.ValueKind == JsonValueKind.True || ia.ValueKind == JsonValueKind.False)) e.IsActive = ia.GetBoolean();
    }

    private static string? ClearableEmail(JsonElement body, string key)
    {
        var v = J.Str(body, key);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    // ---- custom fields ------------------------------------------------------------------------

    public static Task PersistCustomFieldsAsync(IServiceProvider services, string entityType, Guid recordId, JsonElement body, CommandContext ctx)
    {
        var codec = services.GetRequiredService<ICrudCustomFields>();
        return codec.PersistAsync(entityType, recordId.ToString(), body, ctx);
    }

    // ---- tags ---------------------------------------------------------------------------------

    /// <summary>Assign the given free-pool tag ids to a customer entity (idempotent; upstream create `tags`).</summary>
    public static async Task SyncTagsAsync(AppDbContext db, JsonElement body, Guid entityId, Guid orgId, Guid tenantId)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var el in tags.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String || !Guid.TryParse(el.GetString(), out var tagId)) continue;
            var exists = await db.Set<CustomerTagAssignment>().AnyAsync(a => a.TagId == tagId && a.EntityId == entityId);
            if (exists) continue;
            db.Set<CustomerTagAssignment>().Add(new CustomerTagAssignment
            {
                Id = Guid.NewGuid(), OrganizationId = orgId, TenantId = tenantId, TagId = tagId, EntityId = entityId, CreatedAt = now,
            });
        }
    }

    // ---- profile-object unwrap (update paths only; payload.ts normalizeProfilePayload) --------

    /// <summary>
    /// Flatten a nested <c>profile:{}</c> object onto top-level keys (only when not already present),
    /// producing the effective field map for an update. Non-object profile → 400; unknown key → 400.
    /// </summary>
    public static Dictionary<string, JsonElement> NormalizeProfile(JsonElement body, IReadOnlyCollection<string> profileKeys)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (body.ValueKind == JsonValueKind.Object)
            foreach (var prop in body.EnumerateObject())
                if (prop.Name != "profile") fields[prop.Name] = prop.Value;

        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("profile", out var profile))
        {
            if (profile.ValueKind != JsonValueKind.Object)
                throw CommandHttpException.BadRequest("profile must be an object");
            foreach (var prop in profile.EnumerateObject())
            {
                if (prop.Name is "id" or "updatedAt") continue;
                if (!profileKeys.Contains(prop.Name))
                    throw new CommandHttpException(400, new { error = $"Unsupported profile field: {prop.Name}" });
                if (!fields.ContainsKey(prop.Name)) fields[prop.Name] = prop.Value;
            }
        }
        return fields;
    }

    public static string? AsString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Null => null,
        _ => v.ToString(),
    };

    public static Guid? AsGuid(JsonElement v) =>
        v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : null;

    public static decimal? AsDecimal(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d
        : v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s
        : null;
}

/// <summary>Thin alias so command handlers can read body fields with the same surface as the routes.</summary>
internal static class J
{
    public static string? Str(JsonElement b, string n) => Api.CustomersHttp.Str(b, n);
    public static bool? Bool(JsonElement b, string n) => Api.CustomersHttp.Bool(b, n);
    public static int? Int(JsonElement b, string n) => Api.CustomersHttp.Int(b, n);
    public static decimal? Decimal(JsonElement b, string n) => Api.CustomersHttp.Decimal(b, n);
    public static float? Float(JsonElement b, string n) => Api.CustomersHttp.Float(b, n);
    public static Guid? GuidOf(JsonElement b, string n) => Api.CustomersHttp.GuidOf(b, n);
    public static bool Has(JsonElement b, string n) => Api.CustomersHttp.Has(b, n);
}
