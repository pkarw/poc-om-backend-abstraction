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

        // Labels/colors/icons mirror upstream cli.ts DEFAULTS 1:1 — the UI renders status badges + dictionary
        // combobox options from these (a lowercase "open" would render "open" instead of the "Open" badge
        // the specs assert). (OM integration tests TC-CRM-006/007/083 …)
        return new (string, DictSeed[])[]
        {
            ("status", new[] {
                new DictSeed("active", "Active", "#22c55e", "lucide:user-check"),
                new DictSeed("inactive", "Inactive", "#94a3b8", "lucide:pause-circle"),
                new DictSeed("pending", "Pending", "#f59e0b", "lucide:clock"),
                new DictSeed("archived", "Archived", "#64748b", "lucide:archive") }),
            ("lifecycle_stage", new[] {
                new DictSeed("lead", "Lead", "#3b82f6", "lucide:sparkles"),
                new DictSeed("prospect", "Prospect", "#8b5cf6", "lucide:eye"),
                new DictSeed("customer", "Customer", "#22c55e", "lucide:handshake"),
                new DictSeed("subscriber", "Subscriber", "#10b981", "lucide:bell"),
                new DictSeed("churned", "Churned", "#ef4444", "lucide:user-x"),
                new DictSeed("other", "Other", "#94a3b8", "lucide:circle") }),
            ("source", new[] {
                new DictSeed("linkedin", "LinkedIn", "#0a66c2", "lucide:linkedin"),
                new DictSeed("email", "Email", "#3b82f6", "lucide:mail"),
                new DictSeed("web_form", "Web form", "#22c55e", "lucide:globe"),
                new DictSeed("referral", "Referral", "#8b5cf6", "lucide:users"),
                new DictSeed("customer_referral", "Customer referral", "#22c55e", "lucide:thumbs-up"),
                new DictSeed("partner_referral", "Partner referral", "#3b82f6", "lucide:handshake"),
                new DictSeed("event", "Conference / Event", "#f59e0b", "lucide:calendar"),
                new DictSeed("cold_outreach", "Cold outreach", "#94a3b8", "lucide:phone"),
                new DictSeed("facebook", "Facebook", "#1877f2", "lucide:facebook"),
                new DictSeed("typeform", "Typeform", "#262627", "lucide:file-text"),
                new DictSeed("other", "Other", "#64748b", "lucide:circle") }),
            ("address_type", new[] {
                new DictSeed("office", "Office", "#3b82f6", "lucide:building"),
                new DictSeed("work", "Work", "#6366f1", "lucide:briefcase"),
                new DictSeed("billing", "Billing", "#f97316", "lucide:wallet"),
                new DictSeed("shipping", "Shipping", "#22c55e", "lucide:truck"),
                new DictSeed("home", "Home", "#10b981", "lucide:map-pin") }),
            ("activity_type", new[] {
                new DictSeed("call", "Call", "#2563eb", "lucide:phone-call"),
                new DictSeed("email", "Email", "#16a34a", "lucide:mail"),
                new DictSeed("meeting", "Meeting", "#f59e0b", "lucide:users"),
                new DictSeed("note", "Note", "#a855f7", "lucide:notebook"),
                new DictSeed("task", "Task", "#ef4444", "lucide:check-square") }),
            ("job_title", Seeds("Director of Operations", "VP of Partnerships", "Founder & Principal",
                "Senior Project Manager", "Chief Revenue Officer", "Director of Retail Partnerships")),
            ("deal_status", new[] {
                new DictSeed("open", "Open", "#2563eb", "lucide:circle"),
                new DictSeed("closed", "Closed", "#6b7280", "lucide:check-circle"),
                new DictSeed("win", "Win", "#22c55e", "lucide:trophy"),
                new DictSeed("loose", "Loose", "#ef4444", "lucide:flag"),
                new DictSeed("in_progress", "In progress", "#f59e0b", "lucide:activity") }),
            ("pipeline_stage", new[] {
                new DictSeed("opportunity", "Opportunity", "#38bdf8", "lucide:target"),
                new DictSeed("marketing_qualified_lead", "Marketing Qualified Lead", "#a855f7", "lucide:sparkles"),
                new DictSeed("sales_qualified_lead", "Sales Qualified Lead", "#f97316", "lucide:users"),
                new DictSeed("offering", "Offering", "#22c55e", "lucide:package"),
                new DictSeed("negotiations", "Negotiations", "#facc15", "lucide:handshake"),
                new DictSeed("win", "Win", "#16a34a", "lucide:award"),
                new DictSeed("loose", "Loose", "#ef4444", "lucide:flag"),
                new DictSeed("stalled", "Stalled", "#6b7280", "lucide:alert-circle") }),
            ("industry", Seeds("Renewable Energy", "Software", "Interior Design", "SaaS", "E-commerce", "Healthcare",
                "Manufacturing", "Logistics", "Financial Services", "Retail", "Hospitality", "Energy", "Media")),
            ("temperature", Seeds("hot", "high", "medium", "low", "cold")),
            ("renewal_quarter", renewalQuarters),
            ("person_company_role", new[] {
                new DictSeed("decision_maker", "Decision maker", "#f59e0b", "lucide:crown"),
                new DictSeed("influencer", "Influencer", "#8b5cf6", "lucide:sparkles"),
                new DictSeed("budget_holder", "Budget holder", "#3b82f6", "lucide:wallet"),
                new DictSeed("technical_evaluator", "Technical evaluator", "#22c55e", "lucide:wrench"),
                new DictSeed("primary_contact", "Primary contact", "#0ea5e9", "lucide:star"),
                new DictSeed("end_user", "End user", "#64748b", "lucide:user") }),
            ("customer_role_type", new[]
            {
                new DictSeed("sales_owner", "Sales Owner", "#2563eb", "lucide:briefcase"),
                new DictSeed("service_owner", "Service Owner", "#16a34a", "lucide:headphones"),
                new DictSeed("account_manager", "Account Manager", "#f59e0b", "lucide:user-check"),
            }),
        };
    }

    private static DictSeed[] Seeds(params string[] values) => values.Select(v => new DictSeed(v)).ToArray();

    // Currency codes seeded into the generic 'currency' dictionary (upstream seeds all Intl currencies;
    // this is the priority set + common ISO 4217 codes — enough for the currency selector + deal values).
    private static readonly (string Code, string Label)[] CurrencyDictionarySeeds =
    {
        ("EUR", "EUR – Euro"), ("USD", "USD – US Dollar"), ("GBP", "GBP – British Pound"), ("PLN", "PLN – Polish Zloty"),
        ("AUD", "AUD – Australian Dollar"), ("BRL", "BRL – Brazilian Real"), ("CAD", "CAD – Canadian Dollar"),
        ("CHF", "CHF – Swiss Franc"), ("CNY", "CNY – Chinese Yuan"), ("CZK", "CZK – Czech Koruna"),
        ("DKK", "DKK – Danish Krone"), ("HKD", "HKD – Hong Kong Dollar"), ("HUF", "HUF – Hungarian Forint"),
        ("ILS", "ILS – Israeli New Shekel"), ("INR", "INR – Indian Rupee"), ("JPY", "JPY – Japanese Yen"),
        ("KRW", "KRW – South Korean Won"), ("MXN", "MXN – Mexican Peso"), ("NOK", "NOK – Norwegian Krone"),
        ("NZD", "NZD – New Zealand Dollar"), ("RON", "RON – Romanian Leu"), ("SEK", "SEK – Swedish Krona"),
        ("SGD", "SGD – Singapore Dollar"), ("TRY", "TRY – Turkish Lira"), ("UAH", "UAH – Ukrainian Hryvnia"),
        ("ZAR", "ZAR – South African Rand"),
    };

    /// <summary>Port of <c>seedCurrencyDictionary</c>: ensure a generic <c>Dictionary</c> (key=<c>currency</c>,
    /// isSystem) + a <c>DictionaryEntry</c> per currency code for the scope. Idempotent.</summary>
    private static async Task SeedCurrencyDictionaryAsync(
        AppDbContext db, Guid tenantId, Guid organizationId, DateTimeOffset now, CancellationToken ct)
    {
        // Skip when the generic dictionaries entities aren't mapped in this context (e.g. a minimal
        // test DbContext that only loads the customers module).
        if (db.Model.FindEntityType(typeof(OpenMercato.Modules.Dictionaries.Data.Dictionary)) is null) return;

        var dict = await db.Set<OpenMercato.Modules.Dictionaries.Data.Dictionary>().FirstOrDefaultAsync(d =>
            d.TenantId == tenantId && d.OrganizationId == organizationId && d.Key == "currency" && d.DeletedAt == null, ct);
        if (dict is null)
        {
            dict = new OpenMercato.Modules.Dictionaries.Data.Dictionary
            {
                Id = Guid.NewGuid(), OrganizationId = organizationId, TenantId = tenantId,
                Key = "currency", Name = "Currencies", Description = "ISO 4217 currencies",
                IsSystem = true, IsActive = true, ManagerVisibility = "default",
                CreatedAt = now, UpdatedAt = now,
            };
            db.Set<OpenMercato.Modules.Dictionaries.Data.Dictionary>().Add(dict);
            await db.SaveChangesAsync(ct);
        }

        var existing = await db.Set<OpenMercato.Modules.Dictionaries.Data.DictionaryEntry>()
            .Where(e => e.DictionaryId == dict.Id && e.TenantId == tenantId && e.OrganizationId == organizationId)
            .Select(e => e.NormalizedValue).ToListAsync(ct);
        var have = new HashSet<string>(existing, StringComparer.Ordinal);
        var pos = existing.Count;
        foreach (var (code, label) in CurrencyDictionarySeeds)
        {
            var norm = code.ToLowerInvariant();
            if (!have.Add(norm)) continue;
            db.Set<OpenMercato.Modules.Dictionaries.Data.DictionaryEntry>().Add(new OpenMercato.Modules.Dictionaries.Data.DictionaryEntry
            {
                Id = Guid.NewGuid(), DictionaryId = dict.Id, OrganizationId = organizationId, TenantId = tenantId,
                Value = code, NormalizedValue = norm, Label = label, Position = pos++, CreatedAt = now, UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Build a bare-key custom-field value map (the <c>custom</c> block of an upstream example
    /// record). Values are native CLR types (string/bool/int) — <see cref="RecordCustomFields.SetAsync"/>
    /// routes each to its storage column from the installed def's kind.</summary>
    private static Dictionary<string, object?> Cf(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    /// <summary>An example person (upstream <c>CUSTOMER_EXAMPLES[].people[]</c>). Contact fields are the exact
    /// OM values: <c>Email</c>/<c>Phone</c> land on the person's <see cref="CustomerEntity"/> (primary_email/
    /// primary_phone — what the people LIST reads); job/department/seniority/timezone/linkedIn/preferredName land
    /// on the <see cref="CustomerPersonProfile"/>; <c>Source</c> on the entity.</summary>
    private readonly record struct PersonSeed(
        string First, string Last, string? PreferredName, string Email, string Phone,
        string JobTitle, string Department, string Seniority, string Timezone, string LinkedInUrl, string Source,
        IReadOnlyDictionary<string, object?> Custom);

    /// <summary>An example company (upstream <c>CUSTOMER_EXAMPLES[]</c>). Contact/identity fields are the exact
    /// OM values: <c>PrimaryEmail</c>/<c>PrimaryPhone</c>/<c>Source</c>/<c>Description</c> land on the
    /// <see cref="CustomerEntity"/>; <c>LegalName</c>/<c>Domain</c>/<c>WebsiteUrl</c>/<c>SizeBucket</c>/<c>Industry</c>
    /// on the <see cref="CustomerCompanyProfile"/>.</summary>
    private readonly record struct CompanySeed(
        string Company, string Industry, string Lifecycle, string PrimaryEmail, string PrimaryPhone,
        string Source, string Description, string LegalName, string Domain, string WebsiteUrl, string SizeBucket,
        IReadOnlyDictionary<string, object?> Custom, PersonSeed[] People);

    private static readonly CompanySeed[] Examples =
    {
        new("Brightside Solar", "Renewable Energy", "customer",
            "hello@brightsidesolar.com", "+1 415-555-0148", "partner_referral",
            "Community solar developer helping multifamily buildings reduce energy costs across California.",
            "Brightside Solar LLC", "brightsidesolar.com", "https://brightsidesolar.com", "51-200",
            Cf(("relationship_health", "healthy"), ("renewal_quarter", "Q3"),
                ("executive_notes", "High NPS across HOA portfolio; exploring bundled battery upsell for 2025 budgets."),
                ("customer_marketing_case", true)),
            new[]
            {
                new PersonSeed("Mia", "Johnson", "Mia", "mia.johnson@brightsidesolar.com", "+1 415-555-0162",
                    "Director of Operations", "Operations", "director", "America/Los_Angeles",
                    "https://www.linkedin.com/in/miajohnson-operations/", "partner_referral",
                    Cf(("buying_role", "champion"), ("preferred_pronouns", "she/her"), ("newsletter_opt_in", true))),
                new PersonSeed("Daniel", "Cho", null, "daniel.cho@brightsidesolar.com", "+1 628-555-0199",
                    "VP of Partnerships", "Business Development", "vp", "America/Los_Angeles",
                    "https://www.linkedin.com/in/danielcho-energy/", "outbound_campaign",
                    Cf(("buying_role", "economic_buyer"), ("preferred_pronouns", "he/him"), ("newsletter_opt_in", false))),
            }),
        new("Harborview Analytics", "Software", "prospect",
            "info@harborviewanalytics.com", "+1 617-555-0024", "industry_event",
            "Boston-based analytics platform helping consumer brands optimize merchandising decisions.",
            "Harborview Analytics Inc.", "harborviewanalytics.com", "https://harborviewanalytics.com", "201-500",
            Cf(("relationship_health", "monitor"), ("renewal_quarter", "Q4"),
                ("executive_notes", "Pilot success metrics trending positive; CFO wants ROI modeling before expansion."),
                ("customer_marketing_case", false)),
            new[]
            {
                new PersonSeed("Arjun", "Patel", null, "arjun.patel@harborviewanalytics.com", "+1 617-555-0168",
                    "Chief Revenue Officer", "Revenue", "c-level", "America/New_York",
                    "https://www.linkedin.com/in/arjunpatel-sales/", "industry_event",
                    Cf(("buying_role", "economic_buyer"), ("preferred_pronouns", "he/him"), ("newsletter_opt_in", true))),
                new PersonSeed("Lena", "Ortiz", null, "lena.ortiz@harborviewanalytics.com", "+1 617-555-0179",
                    "Director of Retail Partnerships", "Partnerships", "director", "America/New_York",
                    "https://www.linkedin.com/in/lenaortiz-retail/", "industry_event",
                    Cf(("buying_role", "champion"), ("preferred_pronouns", "she/her"), ("newsletter_opt_in", true))),
            }),
        new("Copperleaf Design Co.", "Interior Design", "customer",
            "studio@copperleaf.design", "+1 512-555-0456", "customer_referral",
            "Boutique interior design studio specializing in hospitality and boutique retail projects across Texas.",
            "Copperleaf Design Company", "copperleaf.design", "https://copperleaf.design", "11-50",
            Cf(("relationship_health", "healthy"), ("renewal_quarter", "Q1"),
                ("executive_notes", "Boutique studio with strong referrals; share sustainability case studies with ownership group."),
                ("customer_marketing_case", true)),
            new[]
            {
                new PersonSeed("Taylor", "Brooks", null, "taylor.brooks@copperleaf.design", "+1 512-555-0489",
                    "Founder & Principal", "Leadership", "c-level", "America/Chicago",
                    "https://www.linkedin.com/in/taylorbrooks-design/", "customer_referral",
                    Cf(("buying_role", "economic_buyer"), ("preferred_pronouns", "they/them"), ("newsletter_opt_in", false))),
                new PersonSeed("Naomi", "Harris", null, "naomi.harris@copperleaf.design", "+1 512-555-0521",
                    "Senior Project Manager", "Projects", "manager", "America/Chicago",
                    "https://www.linkedin.com/in/naomiharris-pm/", "customer_referral",
                    Cf(("buying_role", "influencer"), ("preferred_pronouns", "she/her"), ("newsletter_opt_in", true))),
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
        string ValueCurrency, int Probability, int ExpectedCloseDays, string Source,
        IReadOnlyDictionary<string, object?> Custom, DealParticipant[] People);

    /// <summary>The 2 deals per company (6 total) from <c>CUSTOMER_EXAMPLES</c>.</summary>
    private static readonly (string Company, DealSeed[] Deals)[] ExampleDeals =
    {
        ("Brightside Solar", new[]
        {
            new DealSeed("Redwood Residences Solar Rollout", "40-home solar installation with ongoing maintenance plan.",
                "in_progress", "negotiations", 185000m, "USD", 55, 45, "partner_referral",
                Cf(("competitive_risk", "medium"), ("implementation_complexity", "standard"), ("estimated_seats", 40), ("requires_legal_review", true)),
                new[] { new DealParticipant("Mia Johnson", "Project Sponsor"), new DealParticipant("Daniel Cho", "Executive Sponsor") }),
            new DealSeed("Sunset Lofts Battery Upgrade", "Battery upgrade for existing solar customers to extend overnight coverage.",
                "open", "offering", 82000m, "USD", 40, 65, "inbound_web",
                Cf(("competitive_risk", "high"), ("implementation_complexity", "complex"), ("estimated_seats", 28), ("requires_legal_review", false)),
                new[] { new DealParticipant("Mia Johnson", "Point of Contact") }),
        }),
        ("Harborview Analytics", new[]
        {
            new DealSeed("Blue Harbor Grocers Pilot Program", "Six-month pilot of merchandising analytics across 28 locations.",
                "win", "win", 96000m, "USD", 100, -25, "industry_event",
                Cf(("competitive_risk", "low"), ("implementation_complexity", "standard"), ("estimated_seats", 28), ("requires_legal_review", false)),
                new[] { new DealParticipant("Arjun Patel", "Executive Sponsor"), new DealParticipant("Lena Ortiz", "Account Lead") }),
            new DealSeed("Midwest Outfitters Expansion", "Expansion opportunity covering 120 stores in the Midwest region.",
                "open", "opportunity", 210000m, "USD", 35, 120, "outbound_campaign",
                Cf(("competitive_risk", "medium"), ("implementation_complexity", "complex"), ("estimated_seats", 120), ("requires_legal_review", true)),
                new[] { new DealParticipant("Lena Ortiz", "Account Lead") }),
        }),
        ("Copperleaf Design Co.", new[]
        {
            new DealSeed("Wanderstay Boutique Renovation", "Full lobby and guest suite redesign for the Wanderstay hospitality group.",
                "in_progress", "sales_qualified_lead", 145000m, "USD", 65, 35, "customer_referral",
                Cf(("competitive_risk", "medium"), ("implementation_complexity", "complex"), ("estimated_seats", 12), ("requires_legal_review", true)),
                new[] { new DealParticipant("Taylor Brooks", "Principal Designer"), new DealParticipant("Naomi Harris", "Project Lead") }),
            new DealSeed("Cedar Creek Retreat Expansion", "New wellness center build-out including retail area and treatment rooms.",
                "loose", "loose", 98000m, "USD", 0, -70, "customer_referral",
                Cf(("competitive_risk", "high"), ("implementation_complexity", "standard"), ("estimated_seats", 8), ("requires_legal_review", false)),
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

        // 3b) Generic 'currency' dictionary (upstream seedCurrencyDictionary). The customers detail/deal
        // pages fetch /api/customers/dictionaries/currency; a 404 there throws an uncaught
        // "Currency dictionary is not configured yet." on the page and breaks its interactions.
        await SeedCurrencyDictionaryAsync(db, tenantId, organizationId, now, ct);

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

        // display_name / title are ENCRYPTED at rest, so a SQL `WHERE display_name = 'X'` compares
        // ciphertext and never matches. Instead load the candidate rows filtered ONLY by non-encrypted
        // columns (kind/tenant/org/deleted) — the materialization interceptor decrypts them on load — and
        // match names IN MEMORY. Deal↔company/person are resolved through these name→id maps (built as we
        // go), never by re-querying an encrypted column. This also keeps the seed idempotent across boots.
        var companyIdByName = (await db.Set<CustomerEntity>()
                .Where(e => e.Kind == "company" && e.TenantId == tenantId && e.OrganizationId == organizationId && e.DeletedAt == null)
                .ToListAsync(ct))
            .GroupBy(e => e.DisplayName ?? "").ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var personIdByName = (await db.Set<CustomerEntity>()
                .Where(e => e.Kind == "person" && e.TenantId == tenantId && e.OrganizationId == organizationId && e.DeletedAt == null)
                .ToListAsync(ct))
            .GroupBy(e => e.DisplayName ?? "").ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        // 1) Example companies + people (idempotent via the in-memory, post-decrypt name maps).
        foreach (var ex in Examples)
        {
            if (companyIdByName.ContainsKey(ex.Company)) continue;

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
            // Custom-field VALUES (upstream company.custom) — written to custom_field_values (EAV, not
            // encrypted) BEFORE indexing so the projected query-index doc carries the cf_* keys the list reads.
            await RecordCustomFields.SetAsync(db, CustomerWriteHelpers.CompanyEntityType, company.Id.ToString(), tenantId, organizationId, ex.Custom, ct);
            await indexer.UpsertOneAsync(CustomerWriteHelpers.CompanyEntityType, company.Id.ToString(), organizationId, tenantId, "create", ct);
            companyIdByName[ex.Company] = company.Id;
            seeded++;

            foreach (var p in ex.People)
            {
                var personName = $"{p.First} {p.Last}";
                if (personIdByName.ContainsKey(personName)) continue;
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
                // Custom-field VALUES (upstream person.custom: buying_role/preferred_pronouns/newsletter_opt_in) —
                // written before indexing so the projected doc carries the cf_* keys the people list reads.
                await RecordCustomFields.SetAsync(db, CustomerWriteHelpers.PersonEntityType, person.Id.ToString(), tenantId, organizationId, p.Custom, ct);
                await indexer.UpsertOneAsync(CustomerWriteHelpers.PersonEntityType, person.Id.ToString(), organizationId, tenantId, "create", ct);
                personIdByName[personName] = person.Id;
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

        // Existing deal titles (decrypted on load) for in-memory idempotency — Title is encrypted at rest.
        var existingDealTitles = new HashSet<string>(
            (await db.Set<CustomerDeal>()
                .Where(x => x.TenantId == tenantId && x.OrganizationId == organizationId && x.DeletedAt == null)
                .ToListAsync(ct)).Select(x => x.Title ?? ""), StringComparer.Ordinal);

        foreach (var (companyName, deals) in ExampleDeals)
        {
            if (!companyIdByName.TryGetValue(companyName, out var companyId)) continue;

            foreach (var d in deals)
            {
                if (!existingDealTitles.Add(d.Title)) continue; // already seeded (in-memory, post-decrypt)

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
                    Id = Guid.NewGuid(), DealId = deal.Id, CompanyEntityId = companyId, CreatedAt = now,
                });
                foreach (var participant in d.People)
                {
                    if (!personIdByName.TryGetValue(participant.PersonName, out var personId)) continue;
                    db.Set<CustomerDealPersonLink>().Add(new CustomerDealPersonLink
                    {
                        Id = Guid.NewGuid(), DealId = deal.Id, PersonEntityId = personId, Role = participant.Role, CreatedAt = now,
                    });
                }
                await db.SaveChangesAsync(ct);
                // Custom-field VALUES (upstream deal.custom: competitive_risk/implementation_complexity/
                // estimated_seats/requires_legal_review) — written before indexing so the projected doc carries
                // the cf_* keys the deals list reads.
                await RecordCustomFields.SetAsync(db, DealWriteHelpers.DealEntityType, deal.Id.ToString(), tenantId, organizationId, d.Custom, ct);
                await indexer.UpsertOneAsync(DealWriteHelpers.DealEntityType, deal.Id.ToString(), organizationId, tenantId, "create", ct);
                seeded++;

                // PARITY-TODO: upstream also seeds per-deal activities (CustomerActivity) and activity.custom
                // custom-field VALUES via the entities data engine, plus company/person interactions, notes
                // (CustomerComment) and addresses (CustomerAddress). These are lower priority than
                // deals+pipeline+links; port them in a follow-up.
            }
        }

        return seeded;
    }

    private static string ToLabel(string slug) =>
        string.Join(' ', slug.Split('-').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}
