using Microsoft.EntityFrameworkCore;
using OpenMercato.Core.Crud;
using OpenMercato.Core.Data;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Customers.Commands;
using OpenMercato.Modules.Customers.Data;
using OpenMercato.Modules.Entities.Lib;

namespace OpenMercato.Modules.Customers.Seeding;

/// <summary>
/// Phase-1 seeder — the port of the records subset of upstream <c>setup.ts</c>/<c>cli.ts</c>
/// (seedDefaults + seedExamples). Installs the 5 CE field sets (install-from-CE), seeds the 14-slug
/// <c>customer_tags</c> free pool and a small dictionary subset, then the example companies/people
/// (Brightside Solar / Harborview Analytics / Copperleaf Design Co. with two contacts each). Every
/// seeded person/company is projected into the query index so index-backed lists return them.
/// Idempotent (guards by tag slug / example display name).
/// </summary>
public static class CustomersSeeder
{
    /// <summary>The free-pool tag slugs (CUSTOM_TAG_SEED_DEFAULTS).</summary>
    public static readonly string[] TagSlugs =
    {
        "architecture", "hospitality", "retail", "healthcare", "tech", "manufacturing", "decision-maker",
        "influencer", "end-user", "blocker", "vip", "strategic-account", "reference-customer", "case-study-candidate",
    };

    /// <summary>A seeded dictionary entry: <c>value</c> plus optional label/color/icon
    /// (upstream <c>ensureDictionaryEntry</c>). When Label is null the value is used as the label.</summary>
    private readonly record struct DictSeed(string Value, string? Label = null, string? Color = null, string? Icon = null);

    /// <summary>Full customers dictionary defaults — the port of <c>seedCustomerDictionaries</c> (cli.ts)
    /// plus the <c>customer_role_type</c> set seeded by setup.ts. <c>renewal_quarter</c> is generated
    /// (current year + 2 future years × Q1–Q4).</summary>
    private static IReadOnlyList<(string Kind, DictSeed[] Entries)> DictionaryDefaults()
    {
        var year = DateTimeOffset.UtcNow.Year;
        var renewalQuarters = Enumerable.Range(0, 3)
            .SelectMany(offset => Enumerable.Range(1, 4).Select(q => new DictSeed($"{year + offset}_q{q}")))
            .ToArray();

        return new (string, DictSeed[])[]
        {
            ("status", Seeds("active", "inactive", "pending", "archived")),
            ("lifecycle_stage", Seeds("lead", "prospect", "customer", "subscriber", "churned", "other")),
            ("source", Seeds("linkedin", "email", "web_form", "referral", "customer_referral", "partner_referral",
                "event", "cold_outreach", "facebook", "typeform", "other")),
            ("address_type", Seeds("office", "work", "billing", "shipping", "home")),
            ("activity_type", Seeds("call", "email", "meeting", "note", "task")),
            ("job_title", Seeds("Director of Operations", "VP of Partnerships", "Founder & Principal",
                "Senior Project Manager", "Chief Revenue Officer", "Director of Retail Partnerships")),
            ("deal_status", Seeds("open", "closed", "win", "loose", "in_progress")),
            ("pipeline_stage", Seeds("opportunity", "marketing_qualified_lead", "sales_qualified_lead", "offering",
                "negotiations", "win", "loose", "stalled")),
            ("industry", Seeds("Renewable Energy", "Software", "Interior Design", "SaaS", "E-commerce", "Healthcare",
                "Manufacturing", "Logistics", "Financial Services", "Retail", "Hospitality", "Energy", "Media")),
            ("temperature", Seeds("hot", "high", "medium", "low", "cold")),
            ("renewal_quarter", renewalQuarters),
            ("person_company_role", Seeds("decision_maker", "influencer", "budget_holder", "technical_evaluator",
                "primary_contact", "end_user")),
            ("customer_role_type", new[]
            {
                new DictSeed("sales_owner", "Sales Owner", "#2563eb", "lucide:briefcase"),
                new DictSeed("service_owner", "Service Owner", "#16a34a", "lucide:headphones"),
                new DictSeed("account_manager", "Account Manager", "#f59e0b", "lucide:user-check"),
            }),
        };
    }

    private static DictSeed[] Seeds(params string[] values) => values.Select(v => new DictSeed(v)).ToArray();

    private static readonly (string Company, string Industry, string Lifecycle, (string First, string Last)[] People)[] Examples =
    {
        ("Brightside Solar", "Renewable Energy", "customer", new[] { ("Mia", "Johnson"), ("Daniel", "Cho") }),
        ("Harborview Analytics", "Software", "prospect", new[] { ("Arjun", "Patel"), ("Lena", "Ortiz") }),
        ("Copperleaf Design Co.", "Interior Design", "customer", new[] { ("Taylor", "Brooks"), ("Naomi", "Harris") }),
    };

    public static async Task<int> SeedAsync(
        AppDbContext db, ModuleRegistry registry, ICrudIndexer indexer, Guid tenantId, Guid organizationId, CancellationToken ct = default)
    {
        // 1) Install the CE field sets so records carry their custom-field definitions.
        await InstallFromCe.InstallAsync(db, registry, tenantId, organizationId: null, ct: ct);

        var now = DateTimeOffset.UtcNow;
        var seeded = 0;

        // 2) Dictionary defaults (full set + role types).
        foreach (var (kind, entries) in DictionaryDefaults())
            foreach (var seed in entries)
            {
                var norm = seed.Value.Trim().ToLowerInvariant();
                var exists = await db.Set<CustomerDictionaryEntry>().AnyAsync(e =>
                    e.OrganizationId == organizationId && e.TenantId == tenantId && e.Kind == kind && e.NormalizedValue == norm, ct);
                if (exists) continue;
                db.Set<CustomerDictionaryEntry>().Add(new CustomerDictionaryEntry
                {
                    Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId, Kind = kind,
                    Value = seed.Value, NormalizedValue = norm, Label = seed.Label ?? seed.Value,
                    Color = seed.Color, Icon = seed.Icon, CreatedAt = now, UpdatedAt = now,
                });
            }

        // 3) Tag free pool.
        foreach (var slug in TagSlugs)
        {
            var exists = await db.Set<CustomerTag>().AnyAsync(t => t.OrganizationId == organizationId && t.TenantId == tenantId && t.Slug == slug, ct);
            if (exists) continue;
            db.Set<CustomerTag>().Add(new CustomerTag
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId, Slug = slug,
                Label = ToLabel(slug), CreatedAt = now, UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync(ct);

        // 4) Example companies + people (guarded by display name).
        foreach (var ex in Examples)
        {
            var companyExists = await db.Set<CustomerEntity>().AnyAsync(e =>
                e.Kind == "company" && e.DisplayName == ex.Company && e.TenantId == tenantId && e.DeletedAt == null, ct);
            if (companyExists) continue;

            var company = new CustomerEntity
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId, Kind = "company",
                DisplayName = ex.Company, LifecycleStage = ex.Lifecycle, Status = "active", IsActive = true,
                CreatedAt = now, UpdatedAt = now,
            };
            db.Set<CustomerEntity>().Add(company);
            // Save the base customer_entities row before its satellite profile: ConfigureModel maps
            // columns only (no FK relationships), so EF cannot order the inserts to satisfy the DB FK.
            await db.SaveChangesAsync(ct);
            db.Set<CustomerCompanyProfile>().Add(new CustomerCompanyProfile
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId, EntityId = company.Id,
                Industry = ex.Industry, BrandName = ex.Company, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            await indexer.UpsertOneAsync(CustomerWriteHelpers.CompanyEntityType, company.Id.ToString(), organizationId, tenantId, "create", ct);
            seeded++;

            foreach (var (first, last) in ex.People)
            {
                var person = new CustomerEntity
                {
                    Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId, Kind = "person",
                    DisplayName = $"{first} {last}", Status = "active", LifecycleStage = ex.Lifecycle, IsActive = true,
                    CreatedAt = now, UpdatedAt = now,
                };
                db.Set<CustomerEntity>().Add(person);
                await db.SaveChangesAsync(ct); // base row before satellite profile + link (DB FK ordering)
                db.Set<CustomerPersonProfile>().Add(new CustomerPersonProfile
                {
                    Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId, EntityId = person.Id,
                    FirstName = first, LastName = last, CompanyEntityId = company.Id, CreatedAt = now, UpdatedAt = now,
                });
                db.Set<CustomerPersonCompanyLink>().Add(new CustomerPersonCompanyLink
                {
                    Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId,
                    PersonEntityId = person.Id, CompanyEntityId = company.Id, IsPrimary = true, CreatedAt = now, UpdatedAt = now,
                });
                await db.SaveChangesAsync(ct);
                await indexer.UpsertOneAsync(CustomerWriteHelpers.PersonEntityType, person.Id.ToString(), organizationId, tenantId, "create", ct);
                seeded++;
            }
        }

        return seeded;
    }

    private static string ToLabel(string slug) =>
        string.Join(' ', slug.Split('-').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}
