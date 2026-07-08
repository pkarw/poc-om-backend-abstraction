namespace OpenMercato.Modules.Customers.Data;

// Port of upstream packages/core/src/modules/customers/data/entities.ts (25 @Entity classes).
// PascalCase props map to the exact snake_case columns created byte-exact by the raw-SQL migration
// 20260707090000_AddCustomersModule. EF only maps the runtime model (CustomersModule.ConfigureModel).
// Tenancy (organization_id + tenant_id) is present on every table EXCEPT the two deal join tables
// (customer_deal_people / customer_deal_companies). Soft-delete (deleted_at) only where noted.

// -----------------------------------------------------------------------------------------------
// 1. CustomerEntity — customer_entities (polymorphic base; kind discriminator 'person'|'company')
// -----------------------------------------------------------------------------------------------
/// <summary>
/// The polymorphic base record for both people and companies (upstream <c>CustomerEntity</c>).
/// The physical discriminator column is <c>kind</c> (migration-created; the decorator property was
/// <c>type</c> but queries alias it as <c>kind</c> — see contract ambiguity #1). Kind-specific
/// attributes live in the 1:1 satellite tables <see cref="CustomerPersonProfile"/> /
/// <see cref="CustomerCompanyProfile"/>. Soft-delete.
/// </summary>
public sealed class CustomerEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string? PrimaryEmail { get; set; }
    public string? PrimaryPhone { get; set; }
    public string? Status { get; set; }
    public string? LifecycleStage { get; set; }
    public string? Source { get; set; }
    public string? Temperature { get; set; }
    public string? RenewalQuarter { get; set; }
    public DateTimeOffset? NextInteractionAt { get; set; }
    public string? NextInteractionName { get; set; }
    public string? NextInteractionRefId { get; set; }
    public string? NextInteractionIcon { get; set; }
    public string? NextInteractionColor { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 2. CustomerPersonProfile — customer_people (1:1 satellite for kind='person')
// -----------------------------------------------------------------------------------------------
/// <summary>Person-specific 1:1 profile (upstream <c>CustomerPersonProfile</c>). UNIQUE entity_id.</summary>
public sealed class CustomerPersonProfile
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? PreferredName { get; set; }
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? Seniority { get; set; }
    public string? Timezone { get; set; }
    public string? LinkedInUrl { get; set; }
    public string? TwitterUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid EntityId { get; set; }
    /// <summary>Legacy single-company link; FK ON DELETE SET NULL.</summary>
    public Guid? CompanyEntityId { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 3. CustomerCompanyProfile — customer_companies (1:1 satellite for kind='company')
// -----------------------------------------------------------------------------------------------
/// <summary>Company-specific 1:1 profile (upstream <c>CustomerCompanyProfile</c>). UNIQUE entity_id.</summary>
public sealed class CustomerCompanyProfile
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string? LegalName { get; set; }
    public string? BrandName { get; set; }
    public string? Domain { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? Industry { get; set; }
    public string? SizeBucket { get; set; }
    public decimal? AnnualRevenue { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid EntityId { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 4. CustomerPersonCompanyLink — customer_person_company_links (soft-del)
// -----------------------------------------------------------------------------------------------
/// <summary>Many-to-many person↔company employment link (upstream <c>CustomerPersonCompanyLink</c>).
/// Soft-delete; partial UNIQUE (person,company) WHERE deleted_at IS NULL.</summary>
public sealed class CustomerPersonCompanyLink
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public bool IsPrimary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid PersonEntityId { get; set; }
    public Guid CompanyEntityId { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 5. CustomerPersonCompanyRole — customer_person_company_roles (created_at only)
// -----------------------------------------------------------------------------------------------
/// <summary>Role of a person within a company (upstream <c>CustomerPersonCompanyRole</c>).
/// UNIQUE (person,company,role_value). created_at only (no updated_at, no soft-delete).</summary>
public sealed class CustomerPersonCompanyRole
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid PersonEntityId { get; set; }
    public Guid CompanyEntityId { get; set; }
    public string RoleValue { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 6. CustomerCompanyBilling — customer_company_billing (UNIQUE entity_id)
// -----------------------------------------------------------------------------------------------
/// <summary>1-per-company billing details (upstream <c>CustomerCompanyBilling</c>). UNIQUE entity_id.</summary>
public sealed class CustomerCompanyBilling
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid EntityId { get; set; }
    public string? BankName { get; set; }
    public string? BankAccountMasked { get; set; }
    public string? PaymentTerms { get; set; }
    public string? PreferredCurrency { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 7. CustomerEntityRole — customer_entity_roles (soft-del; no FKs)
// -----------------------------------------------------------------------------------------------
/// <summary>Per-user role assignment on a customer entity (upstream <c>CustomerEntityRole</c>).
/// Soft-delete; partial UNIQUE (entity_type,entity_id,role_type) WHERE deleted_at IS NULL. No FKs.</summary>
public sealed class CustomerEntityRole
{
    public Guid Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public Guid UserId { get; set; }
    public string RoleType { get; set; } = string.Empty;
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 8. CustomerAddress — customer_addresses (latitude/longitude are real/float4)
// -----------------------------------------------------------------------------------------------
/// <summary>Postal address for a customer entity (upstream <c>CustomerAddress</c>).
/// latitude/longitude are <c>real</c> (float4) per the migration. No soft-delete.</summary>
public sealed class CustomerAddress
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string? Name { get; set; }
    public string? Purpose { get; set; }
    public string? CompanyName { get; set; }
    public string AddressLine1 { get; set; } = string.Empty;
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? PostalCode { get; set; }
    public string? Country { get; set; }
    public string? BuildingNumber { get; set; }
    public string? FlatNumber { get; set; }
    public float? Latitude { get; set; }
    public float? Longitude { get; set; }
    public bool IsPrimary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid EntityId { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 9. CustomerTag — customer_tags (UNIQUE org,tenant,slug)
// -----------------------------------------------------------------------------------------------
/// <summary>Free-pool tag (upstream <c>CustomerTag</c>). UNIQUE (org,tenant,slug). No soft-delete.</summary>
public sealed class CustomerTag
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Color { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 10. CustomerTagAssignment — customer_tag_assignments (created_at only; UNIQUE tag,entity)
// -----------------------------------------------------------------------------------------------
/// <summary>Tag→entity assignment (upstream <c>CustomerTagAssignment</c>). UNIQUE (tag,entity).</summary>
public sealed class CustomerTagAssignment
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid TagId { get; set; }
    public Guid EntityId { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 11. CustomerLabel — customer_labels (per-user; UNIQUE user_id,tenant,org,slug)
// -----------------------------------------------------------------------------------------------
/// <summary>Per-user private label (upstream <c>CustomerLabel</c>). UNIQUE (user_id,tenant,org,slug).</summary>
public sealed class CustomerLabel
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 12. CustomerLabelAssignment — customer_label_assignments (created_at only; UNIQUE label,entity)
// -----------------------------------------------------------------------------------------------
/// <summary>Label→entity assignment (upstream <c>CustomerLabelAssignment</c>). UNIQUE (label,entity).</summary>
public sealed class CustomerLabelAssignment
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid LabelId { get; set; }
    public Guid EntityId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 13. CustomerDeal — customer_deals (soft-del)
// -----------------------------------------------------------------------------------------------
/// <summary>Sales opportunity (upstream <c>CustomerDeal</c>). Soft-delete. pipeline_id /
/// pipeline_stage_id are plain uuids (no FK).</summary>
public sealed class CustomerDeal
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = "open";
    public string? PipelineStage { get; set; }
    public Guid? PipelineId { get; set; }
    public Guid? PipelineStageId { get; set; }
    public decimal? ValueAmount { get; set; }
    public string? ValueCurrency { get; set; }
    public int? Probability { get; set; }
    public DateTimeOffset? ExpectedCloseAt { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string? Source { get; set; }
    /// <summary><c>won | lost | null</c>.</summary>
    public string? ClosureOutcome { get; set; }
    public Guid? LossReasonId { get; set; }
    public string? LossNotes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 14. CustomerDealStageTransition — customer_deal_stage_transitions (soft-del; UNIQUE deal,stage)
// -----------------------------------------------------------------------------------------------
/// <summary>Pipeline stage transition history for a deal (upstream <c>CustomerDealStageTransition</c>).
/// Soft-delete; UNIQUE (deal_id,stage_id). FK deal_id→customer_deals.</summary>
public sealed class CustomerDealStageTransition
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid PipelineId { get; set; }
    public Guid StageId { get; set; }
    public string StageLabel { get; set; } = string.Empty;
    public int StageOrder { get; set; }
    public DateTimeOffset TransitionedAt { get; set; }
    public Guid? TransitionedByUserId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid DealId { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 15. CustomerDealPersonLink — customer_deal_people (NO TENANCY; created_at only)
// -----------------------------------------------------------------------------------------------
/// <summary>Deal↔person link (upstream <c>CustomerDealPersonLink</c>). Join table: NO tenancy cols.
/// UNIQUE (deal,person). FKs to customer_deals/customer_entities.</summary>
public sealed class CustomerDealPersonLink
{
    public Guid Id { get; set; }
    public string? Role { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid DealId { get; set; }
    public Guid PersonEntityId { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 16. CustomerDealCompanyLink — customer_deal_companies (NO TENANCY; created_at only)
// -----------------------------------------------------------------------------------------------
/// <summary>Deal↔company link (upstream <c>CustomerDealCompanyLink</c>). Join table: NO tenancy cols.
/// UNIQUE (deal,company).</summary>
public sealed class CustomerDealCompanyLink
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid DealId { get; set; }
    public Guid CompanyEntityId { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 17. CustomerActivity — customer_activities (legacy timeline; FK deal_id ON DELETE SET NULL)
// -----------------------------------------------------------------------------------------------
/// <summary>Legacy activity timeline row (upstream <c>CustomerActivity</c>). No soft-delete.
/// FK deal_id→customer_deals ON DELETE SET NULL.</summary>
public sealed class CustomerActivity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string ActivityType { get; set; } = string.Empty;
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public Guid? AuthorUserId { get; set; }
    public string? AppearanceIcon { get; set; }
    public string? AppearanceColor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid EntityId { get; set; }
    public Guid? DealId { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 18. CustomerInteraction — customer_interactions (soft-del; unified timeline)
// -----------------------------------------------------------------------------------------------
/// <summary>Unified interaction/task/email record (upstream <c>CustomerInteraction</c>). Soft-delete.
/// deal_id is a plain uuid (no FK). jsonb: participants/linked_entities/guest_permissions.
/// external_message_id links to communication_channels (cross-module, no raw FK).</summary>
public sealed class CustomerInteraction
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string InteractionType { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string Status { get; set; } = "planned";
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public int? Priority { get; set; }
    public Guid? AuthorUserId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public string? AppearanceIcon { get; set; }
    public string? AppearanceColor { get; set; }
    public string? Source { get; set; }
    public Guid? DealId { get; set; }
    public int? DurationMinutes { get; set; }
    public string? Location { get; set; }
    public bool? AllDay { get; set; }
    public string? RecurrenceRule { get; set; }
    public DateTimeOffset? RecurrenceEnd { get; set; }
    public string? Participants { get; set; }
    public int? ReminderMinutes { get; set; }
    public string? Visibility { get; set; }
    public string? LinkedEntities { get; set; }
    public string? GuestPermissions { get; set; }
    public Guid? ExternalMessageId { get; set; }
    public string? ChannelProviderKey { get; set; }
    public bool Pinned { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid EntityId { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 19. CustomerComment — customer_comments (soft-del; FK deal_id ON DELETE SET NULL)
// -----------------------------------------------------------------------------------------------
/// <summary>Comment/note on an entity or deal (upstream <c>CustomerComment</c>). Soft-delete.
/// FK deal_id→customer_deals ON DELETE SET NULL.</summary>
public sealed class CustomerComment
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string Body { get; set; } = string.Empty;
    public Guid? AuthorUserId { get; set; }
    public string? AppearanceIcon { get; set; }
    public string? AppearanceColor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public Guid EntityId { get; set; }
    public Guid? DealId { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 20. CustomerTodoLink — customer_todo_links (created_at only; UNIQUE entity,todo_id,todo_source)
// -----------------------------------------------------------------------------------------------
/// <summary>Legacy todo bridge link (upstream <c>CustomerTodoLink</c>). todo_source default
/// finalized to <c>'customers:interaction'</c>. UNIQUE (entity,todo_id,todo_source).</summary>
public sealed class CustomerTodoLink
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid TodoId { get; set; }
    public string TodoSource { get; set; } = "customers:interaction";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public Guid EntityId { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 21. CustomerPipeline — customer_pipelines
// -----------------------------------------------------------------------------------------------
/// <summary>Sales pipeline (upstream <c>CustomerPipeline</c>). No soft-delete.</summary>
public sealed class CustomerPipeline
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 22. CustomerPipelineStage — customer_pipeline_stages (col name→prop Label, col position→prop Order)
// -----------------------------------------------------------------------------------------------
/// <summary>A stage within a pipeline (upstream <c>CustomerPipelineStage</c>). NOTE: physical column
/// <c>name</c> maps to prop <see cref="Label"/> and physical column <c>position</c> maps to prop
/// <see cref="Order"/> (migration renamed label→name, stage_order→position).</summary>
public sealed class CustomerPipelineStage
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public Guid PipelineId { get; set; }
    /// <summary>Physical column <c>name</c>.</summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>Physical column <c>position</c>.</summary>
    public int Order { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 23. CustomerSettings — customer_settings (UNIQUE org,tenant)
// -----------------------------------------------------------------------------------------------
/// <summary>Per-org module settings (upstream <c>CustomerSettings</c>). UNIQUE (org,tenant).
/// dictionary_sort_modes is jsonb.</summary>
public sealed class CustomerSettings
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string AddressFormat { get; set; } = "line_first";
    public int StuckThresholdDays { get; set; } = 14;
    public string? DictionarySortModes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 24. CustomerDictionaryEntry — customer_dictionary_entries (UNIQUE org,tenant,kind,normalized_value)
// -----------------------------------------------------------------------------------------------
/// <summary>Module-owned dictionary entry (upstream <c>CustomerDictionaryEntry</c> — a SEPARATE
/// first-class table, not the generic dictionaries module). created_at/updated_at, no soft-delete.
/// UNIQUE (org,tenant,kind,normalized_value).</summary>
public sealed class CustomerDictionaryEntry
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string NormalizedValue { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Color { get; set; }
    public string? Icon { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// -----------------------------------------------------------------------------------------------
// 25. CustomerDictionaryKindSetting — customer_dictionary_kind_settings (UNIQUE org,tenant,kind)
// -----------------------------------------------------------------------------------------------
/// <summary>Per-kind dictionary UI settings (upstream <c>CustomerDictionaryKindSetting</c>).
/// UNIQUE (org,tenant,kind).</summary>
public sealed class CustomerDictionaryKindSetting
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid TenantId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string SelectionMode { get; set; } = "single";
    public bool VisibleInTags { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
