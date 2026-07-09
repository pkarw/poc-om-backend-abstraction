using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    /// <summary>An example person (upstream <c>CUSTOMER_EXAMPLES[].people[]</c>). Contact fields are the exact
    /// OM values: <c>Email</c>/<c>Phone</c> land on the person's <see cref="CustomerEntity"/> (primary_email/
    /// primary_phone — what the people LIST reads); job/department/seniority/timezone/linkedIn/preferredName land
    /// on the <see cref="CustomerPersonProfile"/>; <c>Source</c> on the entity.</summary>
    private readonly record struct PersonSeed(
        string First, string Last, string? PreferredName, string Email, string Phone,
        string JobTitle, string Department, string Seniority, string Timezone, string LinkedInUrl, string Source);

    /// <summary>An example company (upstream <c>CUSTOMER_EXAMPLES[]</c>). Contact/identity fields are the exact
    /// OM values: <c>PrimaryEmail</c>/<c>PrimaryPhone</c>/<c>Source</c>/<c>Description</c> land on the
    /// <see cref="CustomerEntity"/>; <c>LegalName</c>/<c>Domain</c>/<c>WebsiteUrl</c>/<c>SizeBucket</c>/<c>Industry</c>
    /// on the <see cref="CustomerCompanyProfile"/>.</summary>
    private readonly record struct CompanySeed(
        string Company, string Industry, string Lifecycle, string PrimaryEmail, string PrimaryPhone,
        string Source, string Description, string LegalName, string Domain, string WebsiteUrl, string SizeBucket,
        PersonSeed[] People);

    private static readonly CompanySeed[] Examples =
    {
        new("Brightside Solar", "Renewable Energy", "customer",
            "hello@brightsidesolar.com", "+1 415-555-0148", "partner_referral",
            "Community solar developer helping multifamily buildings reduce energy costs across California.",
            "Brightside Solar LLC", "brightsidesolar.com", "https://brightsidesolar.com", "51-200",
            new[]
            {
                new PersonSeed("Mia", "Johnson", "Mia", "mia.johnson@brightsidesolar.com", "+1 415-555-0162",
                    "Director of Operations", "Operations", "director", "America/Los_Angeles",
                    "https://www.linkedin.com/in/miajohnson-operations/", "partner_referral"),
                new PersonSeed("Daniel", "Cho", null, "daniel.cho@brightsidesolar.com", "+1 628-555-0199",
                    "VP of Partnerships", "Business Development", "vp", "America/Los_Angeles",
                    "https://www.linkedin.com/in/danielcho-energy/", "outbound_campaign"),
            }),
        new("Harborview Analytics", "Software", "prospect",
            "info@harborviewanalytics.com", "+1 617-555-0024", "industry_event",
            "Boston-based analytics platform helping consumer brands optimize merchandising decisions.",
            "Harborview Analytics Inc.", "harborviewanalytics.com", "https://harborviewanalytics.com", "201-500",
            new[]
            {
                new PersonSeed("Arjun", "Patel", null, "arjun.patel@harborviewanalytics.com", "+1 617-555-0168",
                    "Chief Revenue Officer", "Revenue", "c-level", "America/New_York",
                    "https://www.linkedin.com/in/arjunpatel-sales/", "industry_event"),
                new PersonSeed("Lena", "Ortiz", null, "lena.ortiz@harborviewanalytics.com", "+1 617-555-0179",
                    "Director of Retail Partnerships", "Partnerships", "director", "America/New_York",
                    "https://www.linkedin.com/in/lenaortiz-retail/", "industry_event"),
            }),
        new("Copperleaf Design Co.", "Interior Design", "customer",
            "studio@copperleaf.design", "+1 512-555-0456", "customer_referral",
            "Boutique interior design studio specializing in hospitality and boutique retail projects across Texas.",
            "Copperleaf Design Company", "copperleaf.design", "https://copperleaf.design", "11-50",
            new[]
            {
                new PersonSeed("Taylor", "Brooks", null, "taylor.brooks@copperleaf.design", "+1 512-555-0489",
                    "Founder & Principal", "Leadership", "c-level", "America/Chicago",
                    "https://www.linkedin.com/in/taylorbrooks-design/", "customer_referral"),
                new PersonSeed("Naomi", "Harris", null, "naomi.harris@copperleaf.design", "+1 512-555-0521",
                    "Senior Project Manager", "Projects", "manager", "America/Chicago",
                    "https://www.linkedin.com/in/naomiharris-pm/", "customer_referral"),
            }),
    };

    /// <summary>The 8 default pipeline stages (upstream <c>PIPELINE_STAGE_DEFAULTS</c>), ordered. Only the
    /// stage <c>value</c> (canonical key, used to map a deal's <c>pipelineStage</c> → seeded stage id) and
    /// <c>label</c> (the physical <c>customer_pipeline_stages.name</c> column) are persisted; upstream color/
    /// icon live on the <c>pipeline_stage</c> dictionary, not on the stage rows.</summary>
    private static readonly (string Value, string Label)[] PipelineStageDefaults =
    {
        ("opportunity", "Opportunity"),
        ("marketing_qualified_lead", "Marketing Qualified Lead"),
        ("sales_qualified_lead", "Sales Qualified Lead"),
        ("offering", "Offering"),
        ("negotiations", "Negotiations"),
        ("win", "Win"),
        ("loose", "Loose"),
        ("stalled", "Stalled"),
    };

    /// <summary>A deal participant: a person (by display name) plus their role on the deal.</summary>
    private readonly record struct DealParticipant(string PersonName, string Role);

    /// <summary>An example deal (upstream <c>CUSTOMER_EXAMPLES[].deals[]</c>). <c>StageValue</c> is the
    /// canonical <c>pipelineStage</c> key; <c>ExpectedCloseDays</c> is upstream <c>isoDaysFromNow(n)</c> — we
    /// compute <c>DateTimeOffset.UtcNow.AddDays(n)</c> rather than hardcode a date.</summary>
    private readonly record struct DealSeed(
        string Title, string Description, string Status, string StageValue, decimal ValueAmount,
        string ValueCurrency, int Probability, int ExpectedCloseDays, string Source, DealParticipant[] People);

    /// <summary>The 2 deals per company (6 total) from <c>CUSTOMER_EXAMPLES</c>.</summary>
    private static readonly (string Company, DealSeed[] Deals)[] ExampleDeals =
    {
        ("Brightside Solar", new[]
        {
            new DealSeed("Redwood Residences Solar Rollout", "40-home solar installation with ongoing maintenance plan.",
                "in_progress", "negotiations", 185000m, "USD", 55, 45, "partner_referral",
                new[] { new DealParticipant("Mia Johnson", "Project Sponsor"), new DealParticipant("Daniel Cho", "Executive Sponsor") }),
            new DealSeed("Sunset Lofts Battery Upgrade", "Battery upgrade for existing solar customers to extend overnight coverage.",
                "open", "offering", 82000m, "USD", 40, 65, "inbound_web",
                new[] { new DealParticipant("Mia Johnson", "Point of Contact") }),
        }),
        ("Harborview Analytics", new[]
        {
            new DealSeed("Blue Harbor Grocers Pilot Program", "Six-month pilot of merchandising analytics across 28 locations.",
                "win", "win", 96000m, "USD", 100, -25, "industry_event",
                new[] { new DealParticipant("Arjun Patel", "Executive Sponsor"), new DealParticipant("Lena Ortiz", "Account Lead") }),
            new DealSeed("Midwest Outfitters Expansion", "Expansion opportunity covering 120 stores in the Midwest region.",
                "open", "opportunity", 210000m, "USD", 35, 120, "outbound_campaign",
                new[] { new DealParticipant("Lena Ortiz", "Account Lead") }),
        }),
        ("Copperleaf Design Co.", new[]
        {
            new DealSeed("Wanderstay Boutique Renovation", "Full lobby and guest suite redesign for the Wanderstay hospitality group.",
                "in_progress", "sales_qualified_lead", 145000m, "USD", 65, 35, "customer_referral",
                new[] { new DealParticipant("Taylor Brooks", "Principal Designer"), new DealParticipant("Naomi Harris", "Project Lead") }),
            new DealSeed("Cedar Creek Retreat Expansion", "New wellness center build-out including retail area and treatment rooms.",
                "loose", "loose", 98000m, "USD", 0, -70, "customer_referral",
                new[] { new DealParticipant("Taylor Brooks", "Principal Designer") }),
        }),
    };

    /// <summary>Full seed (defaults + examples). Thin wrapper: <see cref="SeedDefaultsAsync"/> then
    /// <see cref="SeedExamplesAsync"/>. Same signature/behaviour the <c>customers-seed</c> CLI + tests rely on.</summary>
    public static async Task<int> SeedAsync(
        AppDbContext db, ModuleRegistry registry, ICrudIndexer indexer, Guid tenantId, Guid organizationId, CancellationToken ct = default)
    {
        await SeedDefaultsAsync(db, registry, tenantId, organizationId, ct);
        return await SeedExamplesAsync(db, registry, indexer, tenantId, organizationId, ct);
    }

    /// <summary>The <c>seedDefaults</c> half (upstream <c>setup.ts</c>): CE field-set install + tag free pool +
    /// customer dictionary defaults + the default pipeline (<c>seedDefaultPipeline</c>). Idempotent.</summary>
    public static async Task SeedDefaultsAsync(
        AppDbContext db, ModuleRegistry registry, Guid tenantId, Guid organizationId, CancellationToken ct = default)
    {
        // 1) Install the CE field sets so records carry their custom-field definitions.
        await InstallFromCe.InstallAsync(db, registry, tenantId, organizationId: null, ct: ct);

        var now = DateTimeOffset.UtcNow;

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

        // 4) Default pipeline (seedDefaultPipeline port). Guarded by an existing default pipeline for the scope.
        var hasDefaultPipeline = await db.Set<CustomerPipeline>().AnyAsync(p =>
            p.TenantId == tenantId && p.OrganizationId == organizationId && p.IsDefault, ct);
        if (!hasDefaultPipeline)
        {
            var pipeline = new CustomerPipeline
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId,
                Name = "Default Pipeline", IsDefault = true, CreatedAt = now, UpdatedAt = now,
            };
            db.Set<CustomerPipeline>().Add(pipeline);
            // Save the base pipeline row before its stages (DB FK pipeline_id → customer_pipelines; EF maps
            // columns only, no FK relationships, so it cannot order the inserts itself).
            await db.SaveChangesAsync(ct);
            for (var i = 0; i < PipelineStageDefaults.Length; i++)
                db.Set<CustomerPipelineStage>().Add(new CustomerPipelineStage
                {
                    Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId, PipelineId = pipeline.Id,
                    Label = PipelineStageDefaults[i].Label, Order = i, CreatedAt = now, UpdatedAt = now,
                });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>The <c>seedExamples</c> half (upstream <c>seedCustomerExamples</c>): the 3 example companies +
    /// their people, then the 2 deals per company (6 total) with deal↔company / deal↔person links, projecting
    /// every seeded record into the query index. Returns the count of records seeded (companies+people+deals).
    /// Idempotent (companies/people guarded by display name; deals guarded by title).</summary>
    public static async Task<int> SeedExamplesAsync(
        AppDbContext db, ModuleRegistry registry, ICrudIndexer indexer, Guid tenantId, Guid organizationId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var seeded = 0;

        // 1) Example companies + people (guarded by display name).
        foreach (var ex in Examples)
        {
            var companyExists = await db.Set<CustomerEntity>().AnyAsync(e =>
                e.Kind == "company" && e.DisplayName == ex.Company && e.TenantId == tenantId && e.DeletedAt == null, ct);
            if (companyExists) continue;

            var company = new CustomerEntity
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId, Kind = "company",
                DisplayName = ex.Company, LifecycleStage = ex.Lifecycle, Status = "active", IsActive = true,
                PrimaryEmail = ex.PrimaryEmail, PrimaryPhone = ex.PrimaryPhone, Source = ex.Source,
                Description = ex.Description, CreatedAt = now, UpdatedAt = now,
            };
            db.Set<CustomerEntity>().Add(company);
            // Save the base customer_entities row before its satellite profile: ConfigureModel maps
            // columns only (no FK relationships), so EF cannot order the inserts to satisfy the DB FK.
            await db.SaveChangesAsync(ct);
            db.Set<CustomerCompanyProfile>().Add(new CustomerCompanyProfile
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId, EntityId = company.Id,
                Industry = ex.Industry, BrandName = ex.Company, LegalName = ex.LegalName, Domain = ex.Domain,
                WebsiteUrl = ex.WebsiteUrl, SizeBucket = ex.SizeBucket, CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            await indexer.UpsertOneAsync(CustomerWriteHelpers.CompanyEntityType, company.Id.ToString(), organizationId, tenantId, "create", ct);
            seeded++;

            foreach (var p in ex.People)
            {
                // Person contact lands on the entity (primary_email/primary_phone) — that is what the people
                // LIST mapApiItem reads; the person profile carries job/department/seniority/timezone/linkedIn.
                var person = new CustomerEntity
                {
                    Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId, Kind = "person",
                    DisplayName = $"{p.First} {p.Last}", Status = "active", LifecycleStage = ex.Lifecycle, IsActive = true,
                    PrimaryEmail = p.Email, PrimaryPhone = p.Phone, Source = p.Source,
                    CreatedAt = now, UpdatedAt = now,
                };
                db.Set<CustomerEntity>().Add(person);
                await db.SaveChangesAsync(ct); // base row before satellite profile + link (DB FK ordering)
                db.Set<CustomerPersonProfile>().Add(new CustomerPersonProfile
                {
                    Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId, EntityId = person.Id,
                    FirstName = p.First, LastName = p.Last, PreferredName = p.PreferredName, JobTitle = p.JobTitle,
                    Department = p.Department, Seniority = p.Seniority, Timezone = p.Timezone, LinkedInUrl = p.LinkedInUrl,
                    CompanyEntityId = company.Id, CreatedAt = now, UpdatedAt = now,
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

        // 2) Deals + links (upstream seedCustomerExamples deal loop). Runs after companies/people so it can
        //    resolve their entities from the DB (independent of whether they were just seeded above).
        //    Build the stage value→id lookup from the default pipeline so deals land in the pipeline view.
        var defaultPipeline = await db.Set<CustomerPipeline>().FirstOrDefaultAsync(p =>
            p.TenantId == tenantId && p.OrganizationId == organizationId && p.IsDefault, ct);
        var stageValueToId = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (defaultPipeline is not null)
        {
            var stages = await db.Set<CustomerPipelineStage>()
                .Where(s => s.PipelineId == defaultPipeline.Id)
                .OrderBy(s => s.Order).ToListAsync(ct);
            for (var i = 0; i < stages.Count && i < PipelineStageDefaults.Length; i++)
                stageValueToId[PipelineStageDefaults[i].Value] = stages[i].Id;
        }

        foreach (var (companyName, deals) in ExampleDeals)
        {
            var company = await db.Set<CustomerEntity>().FirstOrDefaultAsync(e =>
                e.Kind == "company" && e.DisplayName == companyName && e.TenantId == tenantId && e.DeletedAt == null, ct);
            if (company is null) continue;

            foreach (var d in deals)
            {
                var dealExists = await db.Set<CustomerDeal>().AnyAsync(x =>
                    x.TenantId == tenantId && x.OrganizationId == organizationId && x.Title == d.Title && x.DeletedAt == null, ct);
                if (dealExists) continue;

                var resolvedStageId = stageValueToId.TryGetValue(d.StageValue, out var sid) ? (Guid?)sid : null;
                var deal = new CustomerDeal
                {
                    Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId,
                    Title = d.Title, Description = d.Description, Status = d.Status,
                    PipelineStage = d.StageValue,
                    PipelineId = resolvedStageId is not null ? defaultPipeline!.Id : null,
                    PipelineStageId = resolvedStageId,
                    ValueAmount = d.ValueAmount, ValueCurrency = d.ValueCurrency, Probability = d.Probability,
                    // upstream isoDaysFromNow(n) → relative to now (do not hardcode a date).
                    ExpectedCloseAt = DateTimeOffset.UtcNow.AddDays(d.ExpectedCloseDays),
                    Source = d.Source, CreatedAt = now, UpdatedAt = now,
                };
                db.Set<CustomerDeal>().Add(deal);
                // Save the base customer_deals row before its join links (DB FK deal_id → customer_deals).
                await db.SaveChangesAsync(ct);

                db.Set<CustomerDealCompanyLink>().Add(new CustomerDealCompanyLink
                {
                    Id = Guid.NewGuid(), DealId = deal.Id, CompanyEntityId = company.Id, CreatedAt = now,
                });
                foreach (var participant in d.People)
                {
                    var person = await db.Set<CustomerEntity>().FirstOrDefaultAsync(e =>
                        e.Kind == "person" && e.DisplayName == participant.PersonName && e.TenantId == tenantId && e.DeletedAt == null, ct);
                    if (person is null) continue;
                    db.Set<CustomerDealPersonLink>().Add(new CustomerDealPersonLink
                    {
                        Id = Guid.NewGuid(), DealId = deal.Id, PersonEntityId = person.Id, Role = participant.Role, CreatedAt = now,
                    });
                }
                await db.SaveChangesAsync(ct);
                await indexer.UpsertOneAsync(DealWriteHelpers.DealEntityType, deal.Id.ToString(), organizationId, tenantId, "create", ct);
                seeded++;

                // PARITY-TODO: upstream also seeds per-deal activities (CustomerActivity) and per-deal/entity
                // custom-field VALUES (dealInfo.custom / activity.custom) via the entities data engine, plus
                // company/person interactions, notes (CustomerComment) and addresses (CustomerAddress). These
                // are lower priority than deals+pipeline+links and the custom-field values need the entities
                // codec; port them in a follow-up.
            }
        }

        return seeded;
    }

    private static string ToLabel(string slug) =>
        string.Join(' ', slug.Split('-').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}
