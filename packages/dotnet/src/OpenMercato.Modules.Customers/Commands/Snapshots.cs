using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers.Commands;

/// <summary>Snapshot/restore helpers for undo/redo of the customers records (base + satellite rows).</summary>
internal static class Snapshots
{
    public static CustomerEntitySnapshot Of(CustomerEntity e) => new(
        e.Id, e.OrganizationId, e.TenantId, e.Kind, e.DisplayName, e.Description, e.OwnerUserId,
        e.PrimaryEmail, e.PrimaryPhone, e.Status, e.LifecycleStage, e.Source, e.Temperature,
        e.RenewalQuarter, e.IsActive, e.CreatedAt, e.UpdatedAt, e.DeletedAt);

    public static void Apply(CustomerEntity e, CustomerEntitySnapshot s)
    {
        e.OrganizationId = s.OrganizationId; e.TenantId = s.TenantId; e.Kind = s.Kind;
        e.DisplayName = s.DisplayName; e.Description = s.Description; e.OwnerUserId = s.OwnerUserId;
        e.PrimaryEmail = s.PrimaryEmail; e.PrimaryPhone = s.PrimaryPhone; e.Status = s.Status;
        e.LifecycleStage = s.LifecycleStage; e.Source = s.Source; e.Temperature = s.Temperature;
        e.RenewalQuarter = s.RenewalQuarter; e.IsActive = s.IsActive; e.CreatedAt = s.CreatedAt;
        e.UpdatedAt = s.UpdatedAt; e.DeletedAt = s.DeletedAt;
    }

    public static PersonProfileSnapshot Of(CustomerPersonProfile p) => new(
        p.Id, p.OrganizationId, p.TenantId, p.FirstName, p.LastName, p.PreferredName, p.JobTitle,
        p.Department, p.Seniority, p.Timezone, p.LinkedInUrl, p.TwitterUrl, p.EntityId, p.CompanyEntityId,
        p.CreatedAt, p.UpdatedAt);

    public static CompanyProfileSnapshot Of(CustomerCompanyProfile c) => new(
        c.Id, c.OrganizationId, c.TenantId, c.LegalName, c.BrandName, c.Domain, c.WebsiteUrl, c.Industry,
        c.SizeBucket, c.AnnualRevenue, c.EntityId, c.CreatedAt, c.UpdatedAt);
}
