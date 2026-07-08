using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Data;
using OpenMercato.Modules.Customers.Data;
using OpenMercato.Modules.QueryIndex.Lib;

namespace OpenMercato.Modules.Customers.Lib;

/// <summary>
/// Customers-owned <see cref="IIndexBaseRowResolver"/> — resolves the BASE row for the index doc of the
/// polymorphic people/companies read models (<c>customers:customer_person_profile</c> /
/// <c>customers:customer_company_profile</c>, keyed by the <c>customer_entities.id</c>). The base row is
/// the <c>customer_entities</c> record MERGED with its 1:1 satellite profile (so index docs carry both
/// base and profile columns for filter/sort). Registered last (customers loads after query_index in the
/// catalog) so it wins over the generic <see cref="CustomEntitiesStorageBaseRowResolver"/>, to which it
/// delegates every non-customers entity type.
/// </summary>
public sealed class CustomersIndexBaseRowResolver : IIndexBaseRowResolver
{
    private readonly AppDbContext _db;
    private readonly CustomEntitiesStorageBaseRowResolver _fallback;

    public CustomersIndexBaseRowResolver(AppDbContext db)
    {
        _db = db;
        _fallback = new CustomEntitiesStorageBaseRowResolver(db);
    }

    public async Task<IReadOnlyDictionary<string, object?>?> LoadAsync(
        string entityType, string recordId, Guid? organizationId, Guid? tenantId, CancellationToken ct = default)
    {
        // Deal read model (customers:customer_deal) is a first-class physical table (customer_deals),
        // not the polymorphic entity/satellite pattern — project it directly so the query index carries
        // the deal base columns for search/filter/sort (title/status/value_*/pipeline_*), Phase 3.
        if (entityType == "customers:customer_deal")
            return await LoadDealAsync(recordId, ct);

        var kind = entityType switch
        {
            "customers:customer_person_profile" => "person",
            "customers:customer_company_profile" => "company",
            _ => null,
        };
        if (kind is null)
            return await _fallback.LoadAsync(entityType, recordId, organizationId, tenantId, ct);

        if (!Guid.TryParse(recordId, out var id)) return null;
        var entity = await _db.Set<CustomerEntity>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id && e.Kind == kind && e.DeletedAt == null, ct);
        if (entity is null) return null;

        var doc = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = entity.Id.ToString(),
            ["kind"] = entity.Kind,
            ["display_name"] = entity.DisplayName,
            ["description"] = entity.Description,
            ["owner_user_id"] = entity.OwnerUserId?.ToString(),
            ["primary_email"] = entity.PrimaryEmail,
            ["primary_phone"] = entity.PrimaryPhone,
            ["status"] = entity.Status,
            ["lifecycle_stage"] = entity.LifecycleStage,
            ["source"] = entity.Source,
            ["temperature"] = entity.Temperature,
            ["renewal_quarter"] = entity.RenewalQuarter,
            ["organization_id"] = entity.OrganizationId.ToString(),
            ["tenant_id"] = entity.TenantId.ToString(),
            ["created_at"] = entity.CreatedAt.ToUniversalTime().ToString("o"),
            ["updated_at"] = entity.UpdatedAt.ToUniversalTime().ToString("o"),
        };

        if (kind == "person")
        {
            var p = await _db.Set<CustomerPersonProfile>().AsNoTracking().FirstOrDefaultAsync(x => x.EntityId == id, ct);
            if (p is not null)
            {
                doc["first_name"] = p.FirstName; doc["last_name"] = p.LastName; doc["preferred_name"] = p.PreferredName;
                doc["job_title"] = p.JobTitle; doc["department"] = p.Department; doc["seniority"] = p.Seniority;
            }
        }
        else
        {
            var c = await _db.Set<CustomerCompanyProfile>().AsNoTracking().FirstOrDefaultAsync(x => x.EntityId == id, ct);
            if (c is not null)
            {
                doc["legal_name"] = c.LegalName; doc["brand_name"] = c.BrandName; doc["domain"] = c.Domain;
                doc["website_url"] = c.WebsiteUrl; doc["industry"] = c.Industry; doc["size_bucket"] = c.SizeBucket;
            }
        }

        return doc;
    }

    /// <summary>Project a <c>customer_deals</c> row into the index base doc (snake-case keys matching the
    /// deal list <c>fields</c>), so <c>customers:customer_deal</c> lists filter/sort by base + cf fields.</summary>
    private async Task<IReadOnlyDictionary<string, object?>?> LoadDealAsync(string recordId, CancellationToken ct)
    {
        if (!Guid.TryParse(recordId, out var id)) return null;
        var d = await _db.Set<CustomerDeal>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (d is null) return null;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = d.Id.ToString(),
            ["title"] = d.Title,
            ["description"] = d.Description,
            ["status"] = d.Status,
            ["pipeline_stage"] = d.PipelineStage,
            ["pipeline_id"] = d.PipelineId?.ToString(),
            ["pipeline_stage_id"] = d.PipelineStageId?.ToString(),
            ["value_amount"] = d.ValueAmount,
            ["value_currency"] = d.ValueCurrency,
            ["probability"] = d.Probability,
            ["expected_close_at"] = d.ExpectedCloseAt?.ToUniversalTime().ToString("o"),
            ["owner_user_id"] = d.OwnerUserId?.ToString(),
            ["source"] = d.Source,
            ["closure_outcome"] = d.ClosureOutcome,
            ["loss_reason_id"] = d.LossReasonId?.ToString(),
            ["loss_notes"] = d.LossNotes,
            ["organization_id"] = d.OrganizationId.ToString(),
            ["tenant_id"] = d.TenantId.ToString(),
            ["created_at"] = d.CreatedAt.ToUniversalTime().ToString("o"),
            ["updated_at"] = d.UpdatedAt.ToUniversalTime().ToString("o"),
        };
    }
}
