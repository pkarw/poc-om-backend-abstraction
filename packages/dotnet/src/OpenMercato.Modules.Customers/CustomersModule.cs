using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenMercato.Core.Modules;
using OpenMercato.Modules.Customers.Api;
using OpenMercato.Modules.Customers.Data;

namespace OpenMercato.Modules.Customers;

/// <summary>
/// The customers (CRM) module — port of upstream packages/core/src/modules/customers. Owns 25 tables
/// (byte-exact DDL in the raw-SQL migration <c>20260707090000_AddCustomersModule</c>); ConfigureModel
/// wires only the runtime EF model. This foundation lays down the full declaration surface (21 ACL
/// features, 51 events, 5 CE field sets / 4 distinct sets, 2 notification types, default role
/// features) plus the polymorphic people/companies model; phase agents add routes (via
/// <see cref="ICustomersRouteGroup"/>) and command handlers (via <see cref="ConfigureServices"/>).
/// </summary>
public sealed class CustomersModule : IModule
{
    public string Id => "customers";

    // -------------------------------------------------------------------------------------------
    // ACL — 21 features (acl.ts). Bare ids kept for back-compat; titles below are byte-exact.
    // -------------------------------------------------------------------------------------------
    public IReadOnlyList<string> AclFeatures { get; } = new[]
    {
        "customers.people.view",
        "customers.people.manage",
        "customers.companies.view",
        "customers.companies.manage",
        "customers.deals.view",
        "customers.deals.manage",
        "customers.activities.view",
        "customers.activities.manage",
        "customers.settings.manage",
        "customers.pipelines.view",
        "customers.pipelines.manage",
        "customers.widgets.todos",
        "customers.widgets.next-interactions",
        "customers.widgets.new-customers",
        "customers.widgets.new-deals",
        "customers.interactions.view",
        "customers.interactions.manage",
        "customers.roles.view",
        "customers.roles.manage",
        "customers.email.compose",
        "customers.email.view_private",
    };

    /// <summary>The 21 ACL features with byte-exact titles (acl.ts, module 'customers').
    /// <c>customers.email.view_private</c> is declared but INERT in v1 (strict owner-only email; no
    /// admin bypass) — register with no behavioral effect. See contract ambiguity #6.</summary>
    public IReadOnlyList<AclFeatureDefinition> AclFeatureDefinitions { get; } = new[]
    {
        new AclFeatureDefinition("customers.people.view", "View people"),
        new AclFeatureDefinition("customers.people.manage", "Manage people"),
        new AclFeatureDefinition("customers.companies.view", "View companies"),
        new AclFeatureDefinition("customers.companies.manage", "Manage companies"),
        new AclFeatureDefinition("customers.deals.view", "View deals"),
        new AclFeatureDefinition("customers.deals.manage", "Manage deals"),
        new AclFeatureDefinition("customers.activities.view", "View activities"),
        new AclFeatureDefinition("customers.activities.manage", "Manage activities"),
        new AclFeatureDefinition("customers.settings.manage", "Manage customer settings"),
        new AclFeatureDefinition("customers.pipelines.view", "View pipelines"),
        new AclFeatureDefinition("customers.pipelines.manage", "Manage pipelines"),
        new AclFeatureDefinition("customers.widgets.todos", "Use customer todos widget"),
        new AclFeatureDefinition("customers.widgets.next-interactions", "Use customer next interactions widget"),
        new AclFeatureDefinition("customers.widgets.new-customers", "Use customer new customers widget"),
        new AclFeatureDefinition("customers.widgets.new-deals", "Use customer new deals widget"),
        new AclFeatureDefinition("customers.interactions.view", "View interactions"),
        new AclFeatureDefinition("customers.interactions.manage", "Manage interactions"),
        new AclFeatureDefinition("customers.roles.view", "View entity roles"),
        new AclFeatureDefinition("customers.roles.manage", "Manage entity roles"),
        new AclFeatureDefinition("customers.email.compose", "Compose / send emails from CRM"),
        new AclFeatureDefinition("customers.email.view_private", "View other users' private emails (reserved — INERT in v1)"),
    };

    /// <summary>Default role features (setup.ts): admin gets the <c>customers.*</c> wildcard; employee
    /// gets 17 explicit grants (NOT settings.manage / interactions.manage / pipelines.manage /
    /// email.view_private).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultRoleFeatures { get; } =
        new Dictionary<string, IReadOnlyList<string>>
        {
            ["admin"] = new[] { "customers.*" },
            ["employee"] = new[]
            {
                "customers.people.view",
                "customers.people.manage",
                "customers.companies.view",
                "customers.companies.manage",
                "customers.deals.view",
                "customers.deals.manage",
                "customers.activities.view",
                "customers.activities.manage",
                "customers.pipelines.view",
                "customers.interactions.view",
                "customers.widgets.todos",
                "customers.widgets.next-interactions",
                "customers.widgets.new-customers",
                "customers.widgets.new-deals",
                "customers.roles.view",
                "customers.roles.manage",
                "customers.email.compose",
            },
        };

    // -------------------------------------------------------------------------------------------
    // Notifications — 2 types (notifications.ts), both expiresAfterHours 168 (7 days).
    // -------------------------------------------------------------------------------------------
    public IReadOnlyList<NotificationTypeDefinition> NotificationTypes { get; } = new[]
    {
        new NotificationTypeDefinition("customers.deal.won", "success", 168),
        new NotificationTypeDefinition("customers.deal.lost", "warning", 168),
    };

    // -------------------------------------------------------------------------------------------
    // Events — 51 (events.ts). All declared persistent (emitCrudSideEffects persistent:true +
    // documented-persistent lifecycle events). Payload shape noted where non-standard.
    // -------------------------------------------------------------------------------------------
    public IReadOnlyList<EventDeclaration> DeclaredEvents { get; } = new[]
    {
        // People
        new EventDeclaration("customers.person.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.person.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.person.deleted", "{ id, organizationId, tenantId }", true),
        // Companies
        new EventDeclaration("customers.company.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.company.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.company.deleted", "{ id, organizationId, tenantId }", true),
        // Deals
        new EventDeclaration("customers.deal.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.deal.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.deal.deleted", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.deal.won", "{ id, tenantId, organizationId, ownerUserId, title, valueAmount, valueCurrency }", true),
        new EventDeclaration("customers.deal.lost", "{ id, tenantId, organizationId, ownerUserId, title, valueAmount, valueCurrency }", true),
        // Comments
        new EventDeclaration("customers.comment.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.comment.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.comment.deleted", "{ id, organizationId, tenantId }", true),
        // Addresses
        new EventDeclaration("customers.address.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.address.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.address.deleted", "{ id, organizationId, tenantId }", true),
        // Activities
        new EventDeclaration("customers.activity.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.activity.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.activity.deleted", "{ id, organizationId, tenantId }", true),
        // Tags (+ assign/remove)
        new EventDeclaration("customers.tag.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.tag.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.tag.deleted", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.tag.assigned", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.tag.removed", "{ id, organizationId, tenantId }", true),
        // Todos
        new EventDeclaration("customers.todo.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.todo.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.todo.deleted", "{ id, organizationId, tenantId }", true),
        // Interactions (canonical + lifecycle)
        new EventDeclaration("customers.interaction.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.interaction.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.interaction.completed", "{ id, entityId?, status, occurredAt }", true),
        new EventDeclaration("customers.interaction.canceled", "{ id, entityId?, status }", true),
        new EventDeclaration("customers.interaction.reverted", "{ id, entityId? }", true),
        new EventDeclaration("customers.interaction.deleted", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.next_interaction.updated", "{ id, organizationId, tenantId }", true),
        // Entity Roles
        new EventDeclaration("customers.entity_role.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.entity_role.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.entity_role.deleted", "{ id, organizationId, tenantId }", true),
        // Labels
        new EventDeclaration("customers.label.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.label.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.label.deleted", "{ id, organizationId, tenantId }", true),
        // Label Assignments
        new EventDeclaration("customers.label_assignment.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.label_assignment.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.label_assignment.deleted", "{ id, organizationId, tenantId }", true),
        // Person-Company Links (clientBroadcast)
        new EventDeclaration("customers.person_company_link.created", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.person_company_link.updated", "{ id, organizationId, tenantId }", true),
        new EventDeclaration("customers.person_company_link.deleted", "{ id, organizationId, tenantId }", true),
        // Email integration (clientBroadcast)
        new EventDeclaration("customers.email.linked", "{ interactionId, ... }", true),
        new EventDeclaration("customers.email.visibility_changed", "{ interactionId, previousVisibility, nextVisibility, authorUserId, actorUserId, adminBypass, tenantId, organizationId }", true),
    };

    // -------------------------------------------------------------------------------------------
    // Custom fields — 5 CE registrations using 4 distinct field sets (ce.ts + customFieldDefaults.ts).
    // customer_interaction reuses the activity field set. Installed at organizationId:null (tenant-global).
    // No field is required (cf.* default false).
    // -------------------------------------------------------------------------------------------
    public IReadOnlyList<CustomFieldSet> CustomFieldSets { get; } = new[]
    {
        new CustomFieldSet("customers:customer_person_profile", PersonFields),
        new CustomFieldSet("customers:customer_company_profile", CompanyFields),
        new CustomFieldSet("customers:customer_deal", DealFields),
        new CustomFieldSet("customers:customer_activity", ActivityFields),
        // Reuses CUSTOMER_ACTIVITY_CUSTOM_FIELDS (same 3 fields as activity).
        new CustomFieldSet("customers:customer_interaction", ActivityFields),
    };

    private static readonly IReadOnlyList<CustomFieldDefinition> PersonFields = new[]
    {
        new CustomFieldDefinition("buying_role", "select", "Buying role", Options: new[] { "economic_buyer", "champion", "technical_evaluator", "influencer" }),
        new CustomFieldDefinition("preferred_pronouns", "text", "Preferred pronouns"),
        new CustomFieldDefinition("newsletter_opt_in", "boolean", "Newsletter opt-in"),
    };

    private static readonly IReadOnlyList<CustomFieldDefinition> CompanyFields = new[]
    {
        new CustomFieldDefinition("relationship_health", "select", "Relationship health", Options: new[] { "healthy", "monitor", "at_risk" }),
        new CustomFieldDefinition("renewal_quarter", "select", "Renewal quarter", Options: new[] { "Q1", "Q2", "Q3", "Q4" }),
        new CustomFieldDefinition("executive_notes", "multiline", "Executive notes"),
        new CustomFieldDefinition("customer_marketing_case", "boolean", "Marketing case study ready"),
    };

    private static readonly IReadOnlyList<CustomFieldDefinition> DealFields = new[]
    {
        new CustomFieldDefinition("competitive_risk", "select", "Competitive risk", Options: new[] { "low", "medium", "high" }),
        new CustomFieldDefinition("implementation_complexity", "select", "Implementation complexity", Options: new[] { "light", "standard", "complex" }),
        new CustomFieldDefinition("estimated_seats", "integer", "Estimated seats/licenses"),
        new CustomFieldDefinition("requires_legal_review", "boolean", "Requires legal review"),
    };

    private static readonly IReadOnlyList<CustomFieldDefinition> ActivityFields = new[]
    {
        new CustomFieldDefinition("engagement_sentiment", "select", "Engagement sentiment", Options: new[] { "positive", "neutral", "negative" }),
        new CustomFieldDefinition("shared_with_leadership", "boolean", "Shared with leadership"),
        new CustomFieldDefinition("follow_up_owner", "text", "Follow-up owner"),
    };

    // -------------------------------------------------------------------------------------------
    // Services — phase agents register command handlers / workers / subscribers here.
    // -------------------------------------------------------------------------------------------
    public void ConfigureServices(IServiceCollection services)
    {
        // Command handlers — reflection-discovered. Every non-abstract class in the Customers assembly
        // that implements OpenMercato.Core.Commands.ICommand is registered as a scoped ICommand, so later
        // phases add command classes as NEW files without ever editing this module. The CommandBus resolves
        // each handler by its CommandId. Phase 1 records + Phase 2 dictionaries/settings + all future phases
        // are picked up automatically.
        var commandType = typeof(OpenMercato.Core.Commands.ICommand);
        var commandImpls = typeof(CustomersModule).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && commandType.IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
        foreach (var impl in commandImpls)
            services.AddScoped(commandType, impl);

        // Index base-row resolver: teaches the query_index projection how to read the polymorphic
        // people/companies base rows (customer_entities + satellite). Registered here (customers loads
        // after query_index) so it wins over the generic storage resolver and delegates the rest to it.
        services.AddScoped<OpenMercato.Modules.QueryIndex.Lib.IIndexBaseRowResolver, Lib.CustomersIndexBaseRowResolver>();
    }

    /// <summary>CLI commands (cli.ts subset): <c>customers-seed</c> installs CE defs + Phase-1 seed data.</summary>
    public IReadOnlyList<ICliCommand> CliCommands { get; } = new ICliCommand[]
    {
        new Cli.CustomersSeedCommand(),
    };

    // -------------------------------------------------------------------------------------------
    // Routes — reflection over ICustomersRouteGroup (parallel to auth's IAuthRouteGroup).
    // -------------------------------------------------------------------------------------------
    public void MapRoutes(IEndpointRouteBuilder routes)
    {
        var groupType = typeof(ICustomersRouteGroup);
        var implementations = typeof(CustomersModule).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && groupType.IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in implementations)
        {
            if (Activator.CreateInstance(type) is ICustomersRouteGroup group)
                group.Map(routes);
        }
    }

    // -------------------------------------------------------------------------------------------
    // Model — all 25 entities mapped byte-exact onto the shared AppDbContext.
    // -------------------------------------------------------------------------------------------
    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CustomerEntity>(e =>
        {
            e.ToTable("customer_entities");
            e.HasKey(x => x.Id).HasName("customer_entities_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Kind).HasColumnName("kind").IsRequired();
            e.Property(x => x.DisplayName).HasColumnName("display_name").IsRequired();
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.PrimaryEmail).HasColumnName("primary_email");
            e.Property(x => x.PrimaryPhone).HasColumnName("primary_phone");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.LifecycleStage).HasColumnName("lifecycle_stage");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.Temperature).HasColumnName("temperature");
            e.Property(x => x.RenewalQuarter).HasColumnName("renewal_quarter");
            e.Property(x => x.NextInteractionAt).HasColumnName("next_interaction_at").HasColumnType("timestamptz");
            e.Property(x => x.NextInteractionName).HasColumnName("next_interaction_name");
            e.Property(x => x.NextInteractionRefId).HasColumnName("next_interaction_ref_id");
            e.Property(x => x.NextInteractionIcon).HasColumnName("next_interaction_icon");
            e.Property(x => x.NextInteractionColor).HasColumnName("next_interaction_color");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomerPersonProfile>(e =>
        {
            e.ToTable("customer_people");
            e.HasKey(x => x.Id).HasName("customer_people_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.FirstName).HasColumnName("first_name");
            e.Property(x => x.LastName).HasColumnName("last_name");
            e.Property(x => x.PreferredName).HasColumnName("preferred_name");
            e.Property(x => x.JobTitle).HasColumnName("job_title");
            e.Property(x => x.Department).HasColumnName("department");
            e.Property(x => x.Seniority).HasColumnName("seniority");
            e.Property(x => x.Timezone).HasColumnName("timezone");
            e.Property(x => x.LinkedInUrl).HasColumnName("linked_in_url");
            e.Property(x => x.TwitterUrl).HasColumnName("twitter_url");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.CompanyEntityId).HasColumnName("company_entity_id");
        });

        modelBuilder.Entity<CustomerCompanyProfile>(e =>
        {
            e.ToTable("customer_companies");
            e.HasKey(x => x.Id).HasName("customer_companies_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.LegalName).HasColumnName("legal_name");
            e.Property(x => x.BrandName).HasColumnName("brand_name");
            e.Property(x => x.Domain).HasColumnName("domain");
            e.Property(x => x.WebsiteUrl).HasColumnName("website_url");
            e.Property(x => x.Industry).HasColumnName("industry");
            e.Property(x => x.SizeBucket).HasColumnName("size_bucket");
            e.Property(x => x.AnnualRevenue).HasColumnName("annual_revenue").HasColumnType("numeric(16,2)");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
        });

        modelBuilder.Entity<CustomerPersonCompanyLink>(e =>
        {
            e.ToTable("customer_person_company_links");
            e.HasKey(x => x.Id).HasName("customer_person_company_links_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.IsPrimary).HasColumnName("is_primary");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.PersonEntityId).HasColumnName("person_entity_id");
            e.Property(x => x.CompanyEntityId).HasColumnName("company_entity_id");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomerPersonCompanyRole>(e =>
        {
            e.ToTable("customer_person_company_roles");
            e.HasKey(x => x.Id).HasName("customer_person_company_roles_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PersonEntityId).HasColumnName("person_entity_id");
            e.Property(x => x.CompanyEntityId).HasColumnName("company_entity_id");
            e.Property(x => x.RoleValue).HasColumnName("role_value").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomerCompanyBilling>(e =>
        {
            e.ToTable("customer_company_billing");
            e.HasKey(x => x.Id).HasName("customer_company_billing_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.BankName).HasColumnName("bank_name");
            e.Property(x => x.BankAccountMasked).HasColumnName("bank_account_masked");
            e.Property(x => x.PaymentTerms).HasColumnName("payment_terms");
            e.Property(x => x.PreferredCurrency).HasColumnName("preferred_currency");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomerEntityRole>(e =>
        {
            e.ToTable("customer_entity_roles");
            e.HasKey(x => x.Id).HasName("customer_entity_roles_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.EntityType).HasColumnName("entity_type").IsRequired();
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.RoleType).HasColumnName("role_type").IsRequired();
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomerAddress>(e =>
        {
            e.ToTable("customer_addresses");
            e.HasKey(x => x.Id).HasName("customer_addresses_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Purpose).HasColumnName("purpose");
            e.Property(x => x.CompanyName).HasColumnName("company_name");
            e.Property(x => x.AddressLine1).HasColumnName("address_line1").IsRequired();
            e.Property(x => x.AddressLine2).HasColumnName("address_line2");
            e.Property(x => x.City).HasColumnName("city");
            e.Property(x => x.Region).HasColumnName("region");
            e.Property(x => x.PostalCode).HasColumnName("postal_code");
            e.Property(x => x.Country).HasColumnName("country");
            e.Property(x => x.BuildingNumber).HasColumnName("building_number");
            e.Property(x => x.FlatNumber).HasColumnName("flat_number");
            e.Property(x => x.Latitude).HasColumnName("latitude").HasColumnType("real");
            e.Property(x => x.Longitude).HasColumnName("longitude").HasColumnType("real");
            e.Property(x => x.IsPrimary).HasColumnName("is_primary");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
        });

        modelBuilder.Entity<CustomerTag>(e =>
        {
            e.ToTable("customer_tags");
            e.HasKey(x => x.Id).HasName("customer_tags_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Slug).HasColumnName("slug").IsRequired();
            e.Property(x => x.Label).HasColumnName("label").IsRequired();
            e.Property(x => x.Color).HasColumnName("color");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomerTagAssignment>(e =>
        {
            e.ToTable("customer_tag_assignments");
            e.HasKey(x => x.Id).HasName("customer_tag_assignments_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.TagId).HasColumnName("tag_id");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
        });

        modelBuilder.Entity<CustomerLabel>(e =>
        {
            e.ToTable("customer_labels");
            e.HasKey(x => x.Id).HasName("customer_labels_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Slug).HasColumnName("slug").IsRequired();
            e.Property(x => x.Label).HasColumnName("label").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomerLabelAssignment>(e =>
        {
            e.ToTable("customer_label_assignments");
            e.HasKey(x => x.Id).HasName("customer_label_assignments_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.LabelId).HasColumnName("label_id");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomerDeal>(e =>
        {
            e.ToTable("customer_deals");
            e.HasKey(x => x.Id).HasName("customer_deals_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Title).HasColumnName("title").IsRequired();
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.Status).HasColumnName("status").IsRequired();
            e.Property(x => x.PipelineStage).HasColumnName("pipeline_stage");
            e.Property(x => x.PipelineId).HasColumnName("pipeline_id");
            e.Property(x => x.PipelineStageId).HasColumnName("pipeline_stage_id");
            e.Property(x => x.ValueAmount).HasColumnName("value_amount").HasColumnType("numeric(14,2)");
            e.Property(x => x.ValueCurrency).HasColumnName("value_currency");
            e.Property(x => x.Probability).HasColumnName("probability");
            e.Property(x => x.ExpectedCloseAt).HasColumnName("expected_close_at").HasColumnType("timestamptz");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.ClosureOutcome).HasColumnName("closure_outcome");
            e.Property(x => x.LossReasonId).HasColumnName("loss_reason_id");
            e.Property(x => x.LossNotes).HasColumnName("loss_notes");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomerDealStageTransition>(e =>
        {
            e.ToTable("customer_deal_stage_transitions");
            e.HasKey(x => x.Id).HasName("customer_deal_stage_transitions_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PipelineId).HasColumnName("pipeline_id");
            e.Property(x => x.StageId).HasColumnName("stage_id");
            e.Property(x => x.StageLabel).HasColumnName("stage_label").IsRequired();
            e.Property(x => x.StageOrder).HasColumnName("stage_order");
            e.Property(x => x.TransitionedAt).HasColumnName("transitioned_at").HasColumnType("timestamptz");
            e.Property(x => x.TransitionedByUserId).HasColumnName("transitioned_by_user_id");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
            e.Property(x => x.DealId).HasColumnName("deal_id");
        });

        modelBuilder.Entity<CustomerDealPersonLink>(e =>
        {
            e.ToTable("customer_deal_people");
            e.HasKey(x => x.Id).HasName("customer_deal_people_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Role).HasColumnName("role");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.DealId).HasColumnName("deal_id");
            e.Property(x => x.PersonEntityId).HasColumnName("person_entity_id");
        });

        modelBuilder.Entity<CustomerDealCompanyLink>(e =>
        {
            e.ToTable("customer_deal_companies");
            e.HasKey(x => x.Id).HasName("customer_deal_companies_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.DealId).HasColumnName("deal_id");
            e.Property(x => x.CompanyEntityId).HasColumnName("company_entity_id");
        });

        modelBuilder.Entity<CustomerActivity>(e =>
        {
            e.ToTable("customer_activities");
            e.HasKey(x => x.Id).HasName("customer_activities_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ActivityType).HasColumnName("activity_type").IsRequired();
            e.Property(x => x.Subject).HasColumnName("subject");
            e.Property(x => x.Body).HasColumnName("body");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");
            e.Property(x => x.AuthorUserId).HasColumnName("author_user_id");
            e.Property(x => x.AppearanceIcon).HasColumnName("appearance_icon");
            e.Property(x => x.AppearanceColor).HasColumnName("appearance_color");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.DealId).HasColumnName("deal_id");
        });

        modelBuilder.Entity<CustomerInteraction>(e =>
        {
            e.ToTable("customer_interactions");
            e.HasKey(x => x.Id).HasName("customer_interactions_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.InteractionType).HasColumnName("interaction_type").IsRequired();
            e.Property(x => x.Title).HasColumnName("title");
            e.Property(x => x.Body).HasColumnName("body");
            e.Property(x => x.Status).HasColumnName("status").IsRequired();
            e.Property(x => x.ScheduledAt).HasColumnName("scheduled_at").HasColumnType("timestamptz");
            e.Property(x => x.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz");
            e.Property(x => x.Priority).HasColumnName("priority");
            e.Property(x => x.AuthorUserId).HasColumnName("author_user_id");
            e.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            e.Property(x => x.AppearanceIcon).HasColumnName("appearance_icon");
            e.Property(x => x.AppearanceColor).HasColumnName("appearance_color");
            e.Property(x => x.Source).HasColumnName("source");
            e.Property(x => x.DealId).HasColumnName("deal_id");
            e.Property(x => x.DurationMinutes).HasColumnName("duration_minutes");
            e.Property(x => x.Location).HasColumnName("location");
            e.Property(x => x.AllDay).HasColumnName("all_day");
            e.Property(x => x.RecurrenceRule).HasColumnName("recurrence_rule");
            e.Property(x => x.RecurrenceEnd).HasColumnName("recurrence_end").HasColumnType("timestamptz");
            e.Property(x => x.Participants).HasColumnName("participants").HasColumnType("jsonb");
            e.Property(x => x.ReminderMinutes).HasColumnName("reminder_minutes");
            e.Property(x => x.Visibility).HasColumnName("visibility");
            e.Property(x => x.LinkedEntities).HasColumnName("linked_entities").HasColumnType("jsonb");
            e.Property(x => x.GuestPermissions).HasColumnName("guest_permissions").HasColumnType("jsonb");
            e.Property(x => x.ExternalMessageId).HasColumnName("external_message_id");
            e.Property(x => x.ChannelProviderKey).HasColumnName("channel_provider_key");
            e.Property(x => x.Pinned).HasColumnName("pinned");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
        });

        modelBuilder.Entity<CustomerComment>(e =>
        {
            e.ToTable("customer_comments");
            e.HasKey(x => x.Id).HasName("customer_comments_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Body).HasColumnName("body").IsRequired();
            e.Property(x => x.AuthorUserId).HasColumnName("author_user_id");
            e.Property(x => x.AppearanceIcon).HasColumnName("appearance_icon");
            e.Property(x => x.AppearanceColor).HasColumnName("appearance_color");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            e.Property(x => x.DeletedAt).HasColumnName("deleted_at").HasColumnType("timestamptz");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
            e.Property(x => x.DealId).HasColumnName("deal_id");
        });

        modelBuilder.Entity<CustomerTodoLink>(e =>
        {
            e.ToTable("customer_todo_links");
            e.HasKey(x => x.Id).HasName("customer_todo_links_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.TodoId).HasColumnName("todo_id");
            e.Property(x => x.TodoSource).HasColumnName("todo_source").IsRequired();
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
            e.Property(x => x.EntityId).HasColumnName("entity_id");
        });

        modelBuilder.Entity<CustomerPipeline>(e =>
        {
            e.ToTable("customer_pipelines");
            e.HasKey(x => x.Id).HasName("customer_pipelines_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name").IsRequired();
            e.Property(x => x.IsDefault).HasColumnName("is_default");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomerPipelineStage>(e =>
        {
            e.ToTable("customer_pipeline_stages");
            e.HasKey(x => x.Id).HasName("customer_pipeline_stages_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PipelineId).HasColumnName("pipeline_id");
            // Physical columns: label→name, order→position (migration renamed).
            e.Property(x => x.Label).HasColumnName("name").IsRequired();
            e.Property(x => x.Order).HasColumnName("position");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomerSettings>(e =>
        {
            e.ToTable("customer_settings");
            e.HasKey(x => x.Id).HasName("customer_settings_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.AddressFormat).HasColumnName("address_format").IsRequired();
            e.Property(x => x.StuckThresholdDays).HasColumnName("stuck_threshold_days");
            e.Property(x => x.DictionarySortModes).HasColumnName("dictionary_sort_modes").HasColumnType("jsonb");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomerDictionaryEntry>(e =>
        {
            e.ToTable("customer_dictionary_entries");
            e.HasKey(x => x.Id).HasName("customer_dictionary_entries_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Kind).HasColumnName("kind").IsRequired();
            e.Property(x => x.Value).HasColumnName("value").IsRequired();
            e.Property(x => x.NormalizedValue).HasColumnName("normalized_value").IsRequired();
            e.Property(x => x.Label).HasColumnName("label").IsRequired();
            e.Property(x => x.Color).HasColumnName("color");
            e.Property(x => x.Icon).HasColumnName("icon");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });

        modelBuilder.Entity<CustomerDictionaryKindSetting>(e =>
        {
            e.ToTable("customer_dictionary_kind_settings");
            e.HasKey(x => x.Id).HasName("customer_dictionary_kind_settings_pkey");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.OrganizationId).HasColumnName("organization_id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Kind).HasColumnName("kind").IsRequired();
            e.Property(x => x.SelectionMode).HasColumnName("selection_mode").IsRequired();
            e.Property(x => x.VisibleInTags).HasColumnName("visible_in_tags");
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamptz");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
        });
    }
}
