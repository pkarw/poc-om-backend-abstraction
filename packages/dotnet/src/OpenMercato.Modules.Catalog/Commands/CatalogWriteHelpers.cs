using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Catalog.Api;
using OpenMercato.Modules.Catalog.Data;

namespace OpenMercato.Modules.Catalog.Commands;

/// <summary>
/// Shared write helpers for the catalog command handlers: apply the product base fields from the raw
/// body (patch semantics on update), and sync the simple nested associations (categoryIds, tags) that
/// the product list decoration reads. Mirrors the customers module's <c>CustomerWriteHelpers</c>.
/// </summary>
internal static class CatalogWriteHelpers
{
    /// <summary>Apply base product columns from the request body. On update (<paramref name="isCreate"/>
    /// false) only fields present in the body are touched (patch); on create, absent optionals keep the
    /// entity defaults. <c>title</c> is set by the caller on create (it is validated + required).</summary>
    public static void ApplyProductBase(CatalogProduct p, JsonElement body, bool isCreate)
    {
        if (CatalogHttp.Has(body, "title"))
        {
            var t = CatalogHttp.Str(body, "title")?.Trim();
            if (!string.IsNullOrEmpty(t)) p.Title = t;
        }
        if (CatalogHttp.Has(body, "subtitle")) p.Subtitle = CatalogHttp.Str(body, "subtitle");
        if (CatalogHttp.Has(body, "description")) p.Description = CatalogHttp.Str(body, "description");
        if (CatalogHttp.Has(body, "sku")) p.Sku = CatalogHttp.Str(body, "sku");
        if (CatalogHttp.Has(body, "handle")) p.Handle = CatalogHttp.Str(body, "handle");
        if (CatalogHttp.Has(body, "taxRateId")) p.TaxRateId = CatalogHttp.GuidOf(body, "taxRateId");
        if (CatalogHttp.Has(body, "taxRate")) p.TaxRate = CatalogHttp.Decimal(body, "taxRate");
        if (CatalogHttp.Has(body, "productType"))
        {
            var pt = CatalogHttp.Str(body, "productType")?.Trim();
            if (!string.IsNullOrEmpty(pt)) p.ProductType = pt;
        }
        if (CatalogHttp.Has(body, "statusEntryId")) p.StatusEntryId = CatalogHttp.GuidOf(body, "statusEntryId");
        if (CatalogHttp.Has(body, "primaryCurrencyCode")) p.PrimaryCurrencyCode = CatalogHttp.Str(body, "primaryCurrencyCode");
        if (CatalogHttp.Has(body, "defaultUnit")) p.DefaultUnit = CatalogHttp.Str(body, "defaultUnit");
        if (CatalogHttp.Has(body, "defaultSalesUnit")) p.DefaultSalesUnit = CatalogHttp.Str(body, "defaultSalesUnit");
        if (CatalogHttp.Has(body, "defaultSalesUnitQuantity"))
        {
            var q = CatalogHttp.Decimal(body, "defaultSalesUnitQuantity");
            if (q is { } qv && qv > 0) p.DefaultSalesUnitQuantity = qv;
        }
        if (CatalogHttp.Has(body, "uomRoundingScale"))
        {
            var s = CatalogHttp.Int(body, "uomRoundingScale");
            if (s is { } sv && sv is >= 0 and <= 6) p.UomRoundingScale = (short)sv;
        }
        if (CatalogHttp.Has(body, "uomRoundingMode"))
        {
            var m = CatalogHttp.Str(body, "uomRoundingMode")?.Trim();
            if (m is "half_up" or "down" or "up") p.UomRoundingMode = m;
        }
        if (CatalogHttp.Has(body, "unitPriceEnabled")) p.UnitPriceEnabled = CatalogHttp.Bool(body, "unitPriceEnabled") ?? p.UnitPriceEnabled;
        if (CatalogHttp.Has(body, "unitPriceReferenceUnit")) p.UnitPriceReferenceUnit = CatalogHttp.Str(body, "unitPriceReferenceUnit");
        if (CatalogHttp.Has(body, "unitPriceBaseQuantity")) p.UnitPriceBaseQuantity = CatalogHttp.Decimal(body, "unitPriceBaseQuantity");
        if (CatalogHttp.Has(body, "defaultMediaId")) p.DefaultMediaId = CatalogHttp.GuidOf(body, "defaultMediaId");
        if (CatalogHttp.Has(body, "defaultMediaUrl")) p.DefaultMediaUrl = CatalogHttp.Str(body, "defaultMediaUrl");
        if (CatalogHttp.Has(body, "weightValue")) p.WeightValue = CatalogHttp.Decimal(body, "weightValue");
        if (CatalogHttp.Has(body, "weightUnit")) p.WeightUnit = CatalogHttp.Str(body, "weightUnit");
        if (CatalogHttp.Has(body, "dimensions")) p.Dimensions = CatalogHttp.RawJson(body, "dimensions");
        if (CatalogHttp.Has(body, "optionSchemaId")) p.OptionSchemaId = CatalogHttp.GuidOf(body, "optionSchemaId");
        if (CatalogHttp.Has(body, "customFieldsetCode")) p.CustomFieldsetCode = CatalogHttp.Str(body, "customFieldsetCode");
        if (CatalogHttp.Has(body, "isConfigurable")) p.IsConfigurable = CatalogHttp.Bool(body, "isConfigurable") ?? p.IsConfigurable;
        if (CatalogHttp.Has(body, "isActive")) p.IsActive = CatalogHttp.Bool(body, "isActive") ?? p.IsActive;
        if (CatalogHttp.Has(body, "metadata")) p.Metadata = CatalogHttp.RawJson(body, "metadata");
    }

    public static ProductSnapshot Snapshot(CatalogProduct p) => new(
        p.Title, p.Subtitle, p.Description, p.Sku, p.Handle, p.ProductType, p.StatusEntryId?.ToString(),
        p.PrimaryCurrencyCode, p.DefaultUnit, p.DefaultSalesUnit, p.DefaultSalesUnitQuantity, p.UnitPriceEnabled,
        p.UnitPriceReferenceUnit, p.CustomFieldsetCode, p.OptionSchemaId?.ToString(), p.IsConfigurable, p.IsActive);

    /// <summary>Replace the product's category assignments with the body's <c>categoryIds</c> (only when
    /// the key is present). Assignments have no soft-delete, so removal is a hard delete.</summary>
    public static async Task SyncCategoriesAsync(AppDbContext db, JsonElement body, Guid productId, Guid organizationId, Guid tenantId)
    {
        if (!CatalogHttp.Has(body, "categoryIds")) return;
        var ids = CatalogHttp.StringArray(body, "categoryIds")
            .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
            .Where(g => g is not null).Select(g => g!.Value).Distinct().ToList();

        var existing = await db.Set<CatalogProductCategoryAssignment>().Where(a => a.ProductId == productId).ToListAsync();
        db.Set<CatalogProductCategoryAssignment>().RemoveRange(existing);
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < ids.Count; i++)
        {
            db.Set<CatalogProductCategoryAssignment>().Add(new CatalogProductCategoryAssignment
            {
                Id = Guid.NewGuid(),
                ProductId = productId,
                CategoryId = ids[i],
                OrganizationId = organizationId,
                TenantId = tenantId,
                Position = i,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    /// <summary>Replace the product's tag assignments with the body's <c>tags</c> labels (only when the key
    /// is present), find-or-creating each tag in the free pool by its slug.</summary>
    public static async Task SyncTagsAsync(AppDbContext db, JsonElement body, Guid productId, Guid organizationId, Guid tenantId)
    {
        if (!CatalogHttp.Has(body, "tags")) return;
        var labels = CatalogHttp.StringArray(body, "tags");

        var existing = await db.Set<CatalogProductTagAssignment>().Where(a => a.ProductId == productId).ToListAsync();
        db.Set<CatalogProductTagAssignment>().RemoveRange(existing);

        var now = DateTimeOffset.UtcNow;
        var seenSlugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            var slug = Slugify(label);
            if (slug.Length == 0 || !seenSlugs.Add(slug)) continue;
            var tag = await db.Set<CatalogProductTag>()
                .FirstOrDefaultAsync(t => t.OrganizationId == organizationId && t.TenantId == tenantId && t.Slug == slug);
            if (tag is null)
            {
                tag = new CatalogProductTag
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    TenantId = tenantId,
                    Label = label,
                    Slug = slug,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Set<CatalogProductTag>().Add(tag);
            }
            db.Set<CatalogProductTagAssignment>().Add(new CatalogProductTagAssignment
            {
                Id = Guid.NewGuid(),
                ProductId = productId,
                TagId = tag.Id,
                OrganizationId = organizationId,
                TenantId = tenantId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    /// <summary>Lowercase slug: keep [a-z0-9], collapse every other run to a single '-', trim leading/trailing.</summary>
    public static string Slugify(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastDash = false;
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }
        var s = sb.ToString();
        return s.TrimEnd('-');
    }
}
