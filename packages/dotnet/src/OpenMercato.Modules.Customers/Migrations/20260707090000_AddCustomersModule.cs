using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenMercato.Modules.Customers.Migrations
{
    /// <summary>
    /// Byte-exact DDL for the customers (CRM) module — all 25 tables consolidated from the 21 upstream
    /// customers migrations into their final reconciled state. Reproduces the documented
    /// migration-vs-decorator discrepancies: <c>customer_entities</c> carries 4 PLAIN b-tree indexes
    /// (no WHERE / no kind col) plus the kind index; <c>customer_addresses.latitude/longitude</c> are
    /// <c>real</c> (float4); <c>customer_pipeline_stages</c> physical cols are <c>name</c>/<c>position</c>
    /// (props label/order); <c>customer_todo_links.todo_source</c> default is <c>'customers:interaction'</c>;
    /// partial-unique indexes on <c>customer_person_company_links</c> / <c>customer_entity_roles</c>
    /// (WHERE deleted_at IS NULL) and the interactions email dedupe. Join tables
    /// (<c>customer_deal_people</c>/<c>customer_deal_companies</c>) carry NO tenancy cols. FKs use
    /// ON UPDATE CASCADE; three are ON DELETE SET NULL (customer_people.company_entity_id,
    /// customer_activities.deal_id, customer_comments.deal_id). Raw SQL — the model snapshot is
    /// intentionally not updated. Applied by MigrateAsync().
    /// </summary>
    [DbContext(typeof(CustomersMigrationsDbContext))]
    [Migration("20260707090000_AddCustomersModule")]
    public partial class AddCustomersModule : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- 1. customer_entities (polymorphic base; kind discriminator; soft-delete) -----------------------
CREATE TABLE customer_entities (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    kind text NOT NULL,
    display_name text NOT NULL,
    description text NULL,
    owner_user_id uuid NULL,
    primary_email text NULL,
    primary_phone text NULL,
    status text NULL,
    lifecycle_stage text NULL,
    source text NULL,
    temperature text NULL,
    renewal_quarter text NULL,
    next_interaction_at timestamptz NULL,
    next_interaction_name text NULL,
    next_interaction_ref_id text NULL,
    next_interaction_icon text NULL,
    next_interaction_color text NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT customer_entities_pkey PRIMARY KEY (id)
);
CREATE INDEX idx_ce_tenant_person_id ON customer_entities (tenant_id, id);
CREATE INDEX idx_ce_tenant_company_id ON customer_entities (tenant_id, id);
CREATE INDEX idx_ce_tenant_org_company_id ON customer_entities (tenant_id, organization_id, id);
CREATE INDEX idx_ce_tenant_org_person_id ON customer_entities (tenant_id, organization_id, id);
CREATE INDEX customer_entities_org_tenant_kind_idx ON customer_entities (organization_id, tenant_id, kind);

-- 2. customer_people (1:1 person profile) -------------------------------------------------------
CREATE TABLE customer_people (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    first_name text NULL,
    last_name text NULL,
    preferred_name text NULL,
    job_title text NULL,
    department text NULL,
    seniority text NULL,
    timezone text NULL,
    linked_in_url text NULL,
    twitter_url text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    entity_id uuid NOT NULL,
    company_entity_id uuid NULL,
    CONSTRAINT customer_people_pkey PRIMARY KEY (id)
);
ALTER TABLE customer_people ADD CONSTRAINT customer_people_entity_id_unique UNIQUE (entity_id);
CREATE INDEX idx_customer_people_entity_id ON customer_people (entity_id);
CREATE INDEX customer_people_org_tenant_idx ON customer_people (organization_id, tenant_id);

-- 3. customer_companies (1:1 company profile) ---------------------------------------------------
CREATE TABLE customer_companies (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    legal_name text NULL,
    brand_name text NULL,
    domain text NULL,
    website_url text NULL,
    industry text NULL,
    size_bucket text NULL,
    annual_revenue numeric(16,2) NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    entity_id uuid NOT NULL,
    CONSTRAINT customer_companies_pkey PRIMARY KEY (id)
);
ALTER TABLE customer_companies ADD CONSTRAINT customer_companies_entity_id_unique UNIQUE (entity_id);
CREATE INDEX idx_customer_companies_entity_id ON customer_companies (entity_id);
CREATE INDEX customer_companies_org_tenant_idx ON customer_companies (organization_id, tenant_id);

-- 4. customer_person_company_links (soft-delete; partial unique) --------------------------------
CREATE TABLE customer_person_company_links (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    is_primary boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    person_entity_id uuid NOT NULL,
    company_entity_id uuid NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT customer_person_company_links_pkey PRIMARY KEY (id)
);
ALTER TABLE customer_person_company_links ADD CONSTRAINT customer_person_company_links_unique UNIQUE (person_entity_id, company_entity_id);
CREATE INDEX customer_person_company_links_company_idx ON customer_person_company_links (company_entity_id);
CREATE INDEX customer_person_company_links_person_idx ON customer_person_company_links (person_entity_id);
CREATE INDEX customer_person_company_links_scope_idx ON customer_person_company_links (organization_id, tenant_id);
CREATE UNIQUE INDEX customer_person_company_links_active_unique ON customer_person_company_links (person_entity_id, company_entity_id) WHERE deleted_at IS NULL;

-- 5. customer_person_company_roles (created_at only) --------------------------------------------
CREATE TABLE customer_person_company_roles (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    person_entity_id uuid NOT NULL,
    company_entity_id uuid NOT NULL,
    role_value text NOT NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT customer_person_company_roles_pkey PRIMARY KEY (id)
);
ALTER TABLE customer_person_company_roles ADD CONSTRAINT customer_pcr_unique UNIQUE (person_entity_id, company_entity_id, role_value);
CREATE INDEX customer_pcr_person_company_idx ON customer_person_company_roles (person_entity_id, company_entity_id);
CREATE INDEX customer_pcr_scope_idx ON customer_person_company_roles (organization_id, tenant_id);

-- 6. customer_company_billing (1 per company) ---------------------------------------------------
CREATE TABLE customer_company_billing (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    entity_id uuid NOT NULL,
    bank_name text NULL,
    bank_account_masked text NULL,
    payment_terms text NULL,
    preferred_currency text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT customer_company_billing_pkey PRIMARY KEY (id)
);
ALTER TABLE customer_company_billing ADD CONSTRAINT customer_company_billing_entity_unique UNIQUE (entity_id);
CREATE INDEX customer_company_billing_scope_idx ON customer_company_billing (organization_id, tenant_id);

-- 7. customer_entity_roles (soft-delete; partial unique; no FKs) --------------------------------
CREATE TABLE customer_entity_roles (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    entity_type text NOT NULL,
    entity_id uuid NOT NULL,
    user_id uuid NOT NULL,
    role_type text NOT NULL,
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT customer_entity_roles_pkey PRIMARY KEY (id)
);
ALTER TABLE customer_entity_roles ADD CONSTRAINT customer_entity_roles_unique UNIQUE (entity_type, entity_id, role_type);
CREATE INDEX customer_entity_roles_entity_idx ON customer_entity_roles (entity_type, entity_id);
CREATE INDEX customer_entity_roles_scope_idx ON customer_entity_roles (organization_id, tenant_id);
CREATE UNIQUE INDEX customer_entity_roles_active_unique ON customer_entity_roles (entity_type, entity_id, role_type) WHERE deleted_at IS NULL;

-- 8. customer_addresses (latitude/longitude real) -----------------------------------------------
CREATE TABLE customer_addresses (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    name text NULL,
    purpose text NULL,
    company_name text NULL,
    address_line1 text NOT NULL,
    address_line2 text NULL,
    city text NULL,
    region text NULL,
    postal_code text NULL,
    country text NULL,
    building_number text NULL,
    flat_number text NULL,
    latitude real NULL,
    longitude real NULL,
    is_primary boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    entity_id uuid NOT NULL,
    CONSTRAINT customer_addresses_pkey PRIMARY KEY (id)
);
CREATE INDEX customer_addresses_entity_idx ON customer_addresses (entity_id);

-- 9. customer_tags (UNIQUE org,tenant,slug) -----------------------------------------------------
CREATE TABLE customer_tags (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    slug text NOT NULL,
    label text NOT NULL,
    color text NULL,
    description text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT customer_tags_pkey PRIMARY KEY (id)
);
CREATE INDEX customer_tags_org_tenant_idx ON customer_tags (organization_id, tenant_id);
ALTER TABLE customer_tags ADD CONSTRAINT customer_tags_org_slug_unique UNIQUE (organization_id, tenant_id, slug);

-- 10. customer_tag_assignments (created_at only; UNIQUE tag,entity) -----------------------------
CREATE TABLE customer_tag_assignments (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    created_at timestamptz NOT NULL,
    tag_id uuid NOT NULL,
    entity_id uuid NOT NULL,
    CONSTRAINT customer_tag_assignments_pkey PRIMARY KEY (id)
);
CREATE INDEX customer_tag_assignments_entity_idx ON customer_tag_assignments (entity_id);
ALTER TABLE customer_tag_assignments ADD CONSTRAINT customer_tag_assignments_unique UNIQUE (tag_id, entity_id);

-- 11. customer_labels (per-user; UNIQUE user_id,tenant,org,slug) --------------------------------
CREATE TABLE customer_labels (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    user_id uuid NOT NULL,
    slug text NOT NULL,
    label text NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT customer_labels_pkey PRIMARY KEY (id)
);
ALTER TABLE customer_labels ADD CONSTRAINT customer_labels_unique UNIQUE (user_id, tenant_id, organization_id, slug);
CREATE INDEX customer_labels_scope_idx ON customer_labels (organization_id, tenant_id, user_id);

-- 12. customer_label_assignments (created_at only; UNIQUE label,entity) -------------------------
CREATE TABLE customer_label_assignments (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    user_id uuid NOT NULL,
    label_id uuid NOT NULL,
    entity_id uuid NOT NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT customer_label_assignments_pkey PRIMARY KEY (id)
);
ALTER TABLE customer_label_assignments ADD CONSTRAINT customer_label_assignments_unique UNIQUE (label_id, entity_id);
CREATE INDEX customer_label_assignments_entity_idx ON customer_label_assignments (entity_id);

-- 13. customer_deals (soft-delete) --------------------------------------------------------------
CREATE TABLE customer_deals (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    title text NOT NULL,
    description text NULL,
    status text NOT NULL DEFAULT 'open',
    pipeline_stage text NULL,
    pipeline_id uuid NULL,
    pipeline_stage_id uuid NULL,
    value_amount numeric(14,2) NULL,
    value_currency text NULL,
    probability int NULL,
    expected_close_at timestamptz NULL,
    owner_user_id uuid NULL,
    source text NULL,
    closure_outcome text NULL,
    loss_reason_id uuid NULL,
    loss_notes text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT customer_deals_pkey PRIMARY KEY (id)
);
CREATE INDEX customer_deals_org_tenant_idx ON customer_deals (organization_id, tenant_id);
CREATE INDEX customer_deals_closure_stats_idx ON customer_deals (organization_id, tenant_id, closure_outcome, updated_at);

-- 14. customer_deal_stage_transitions (soft-delete; UNIQUE deal,stage) --------------------------
CREATE TABLE customer_deal_stage_transitions (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    pipeline_id uuid NOT NULL,
    stage_id uuid NOT NULL,
    stage_label text NOT NULL,
    stage_order int NOT NULL,
    transitioned_at timestamptz NOT NULL,
    transitioned_by_user_id uuid NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    deal_id uuid NOT NULL,
    CONSTRAINT customer_deal_stage_transitions_pkey PRIMARY KEY (id)
);
ALTER TABLE customer_deal_stage_transitions ADD CONSTRAINT customer_deal_stage_transitions_deal_stage_uq UNIQUE (deal_id, stage_id);
CREATE INDEX customer_deal_stage_transitions_deal_idx ON customer_deal_stage_transitions (deal_id);
CREATE INDEX customer_deal_stage_transitions_org_tenant_idx ON customer_deal_stage_transitions (organization_id, tenant_id);

-- 15. customer_deal_people (NO TENANCY; created_at only; UNIQUE deal,person) --------------------
CREATE TABLE customer_deal_people (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    role text NULL,
    created_at timestamptz NOT NULL,
    deal_id uuid NOT NULL,
    person_entity_id uuid NOT NULL,
    CONSTRAINT customer_deal_people_pkey PRIMARY KEY (id)
);
CREATE INDEX customer_deal_people_person_idx ON customer_deal_people (person_entity_id);
CREATE INDEX customer_deal_people_deal_idx ON customer_deal_people (deal_id);
ALTER TABLE customer_deal_people ADD CONSTRAINT customer_deal_people_unique UNIQUE (deal_id, person_entity_id);

-- 16. customer_deal_companies (NO TENANCY; created_at only; UNIQUE deal,company) ----------------
CREATE TABLE customer_deal_companies (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    created_at timestamptz NOT NULL,
    deal_id uuid NOT NULL,
    company_entity_id uuid NOT NULL,
    CONSTRAINT customer_deal_companies_pkey PRIMARY KEY (id)
);
CREATE INDEX customer_deal_companies_company_idx ON customer_deal_companies (company_entity_id);
CREATE INDEX customer_deal_companies_deal_idx ON customer_deal_companies (deal_id);
ALTER TABLE customer_deal_companies ADD CONSTRAINT customer_deal_companies_unique UNIQUE (deal_id, company_entity_id);

-- 17. customer_activities (legacy timeline) -----------------------------------------------------
CREATE TABLE customer_activities (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    activity_type text NOT NULL,
    subject text NULL,
    body text NULL,
    occurred_at timestamptz NULL,
    author_user_id uuid NULL,
    appearance_icon text NULL,
    appearance_color text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    entity_id uuid NOT NULL,
    deal_id uuid NULL,
    CONSTRAINT customer_activities_pkey PRIMARY KEY (id)
);
CREATE INDEX customer_activities_entity_occurred_created_idx ON customer_activities (entity_id, occurred_at, created_at);
CREATE INDEX customer_activities_entity_idx ON customer_activities (entity_id);
CREATE INDEX customer_activities_org_tenant_idx ON customer_activities (organization_id, tenant_id);

-- 18. customer_interactions (soft-delete; unified timeline) --------------------------------------
CREATE TABLE customer_interactions (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    interaction_type text NOT NULL,
    title text NULL,
    body text NULL,
    status text NOT NULL DEFAULT 'planned',
    scheduled_at timestamptz NULL,
    occurred_at timestamptz NULL,
    priority int NULL,
    author_user_id uuid NULL,
    owner_user_id uuid NULL,
    appearance_icon text NULL,
    appearance_color text NULL,
    source text NULL,
    deal_id uuid NULL,
    duration_minutes int NULL,
    location text NULL,
    all_day boolean NULL,
    recurrence_rule text NULL,
    recurrence_end timestamptz NULL,
    participants jsonb NULL,
    reminder_minutes int NULL,
    visibility text NULL,
    linked_entities jsonb NULL,
    guest_permissions jsonb NULL,
    external_message_id uuid NULL,
    channel_provider_key text NULL,
    pinned boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    entity_id uuid NOT NULL,
    CONSTRAINT customer_interactions_pkey PRIMARY KEY (id)
);
CREATE INDEX customer_interactions_type_idx ON customer_interactions (tenant_id, organization_id, interaction_type);
CREATE INDEX customer_interactions_entity_status_scheduled_idx ON customer_interactions (entity_id, status, scheduled_at, created_at);
CREATE INDEX customer_interactions_org_tenant_status_idx ON customer_interactions (organization_id, tenant_id, status, scheduled_at);
CREATE INDEX customer_interactions_external_msg_idx ON customer_interactions (external_message_id) WHERE external_message_id IS NOT NULL;
CREATE INDEX customer_interactions_email_visibility_idx ON customer_interactions (entity_id, interaction_type, visibility, author_user_id) WHERE interaction_type = 'email' AND deleted_at IS NULL;
CREATE UNIQUE INDEX customer_interactions_email_dedupe_uq ON customer_interactions (entity_id, external_message_id) WHERE external_message_id IS NOT NULL AND deleted_at IS NULL;

-- 19. customer_comments (soft-delete) -----------------------------------------------------------
CREATE TABLE customer_comments (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    body text NOT NULL,
    author_user_id uuid NULL,
    appearance_icon text NULL,
    appearance_color text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    entity_id uuid NOT NULL,
    deal_id uuid NULL,
    CONSTRAINT customer_comments_pkey PRIMARY KEY (id)
);
CREATE INDEX customer_comments_entity_created_idx ON customer_comments (entity_id, created_at);
CREATE INDEX customer_comments_entity_idx ON customer_comments (entity_id);

-- 20. customer_todo_links (created_at only; UNIQUE entity,todo_id,todo_source) ------------------
CREATE TABLE customer_todo_links (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    todo_id uuid NOT NULL,
    todo_source text NOT NULL DEFAULT 'customers:interaction',
    created_at timestamptz NOT NULL,
    created_by_user_id uuid NULL,
    entity_id uuid NOT NULL,
    CONSTRAINT customer_todo_links_pkey PRIMARY KEY (id)
);
CREATE INDEX customer_todo_links_entity_created_idx ON customer_todo_links (entity_id, created_at);
CREATE INDEX customer_todo_links_entity_idx ON customer_todo_links (entity_id);
ALTER TABLE customer_todo_links ADD CONSTRAINT customer_todo_links_unique UNIQUE (entity_id, todo_id, todo_source);

-- 21. customer_pipelines ------------------------------------------------------------------------
CREATE TABLE customer_pipelines (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    name text NOT NULL,
    is_default boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT customer_pipelines_pkey PRIMARY KEY (id)
);
CREATE INDEX customer_pipelines_org_tenant_idx ON customer_pipelines (organization_id, tenant_id);

-- 22. customer_pipeline_stages (physical cols name/position -> props label/order) ---------------
CREATE TABLE customer_pipeline_stages (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    pipeline_id uuid NOT NULL,
    name text NOT NULL,
    position int NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT customer_pipeline_stages_pkey PRIMARY KEY (id)
);
CREATE INDEX customer_pipeline_stages_pipeline_position_idx ON customer_pipeline_stages (pipeline_id, position);
CREATE INDEX customer_pipeline_stages_org_tenant_idx ON customer_pipeline_stages (organization_id, tenant_id);

-- 23. customer_settings (UNIQUE org,tenant) -----------------------------------------------------
CREATE TABLE customer_settings (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    address_format text NOT NULL DEFAULT 'line_first',
    stuck_threshold_days int NOT NULL DEFAULT 14,
    dictionary_sort_modes jsonb NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT customer_settings_pkey PRIMARY KEY (id)
);
ALTER TABLE customer_settings ADD CONSTRAINT customer_settings_scope_unique UNIQUE (organization_id, tenant_id);

-- 24. customer_dictionary_entries (module-owned; UNIQUE org,tenant,kind,normalized_value) -------
CREATE TABLE customer_dictionary_entries (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    kind text NOT NULL,
    value text NOT NULL,
    normalized_value text NOT NULL,
    label text NOT NULL,
    color text NULL,
    icon text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT customer_dictionary_entries_pkey PRIMARY KEY (id)
);
CREATE INDEX customer_dictionary_entries_scope_idx ON customer_dictionary_entries (organization_id, tenant_id, kind);
ALTER TABLE customer_dictionary_entries ADD CONSTRAINT customer_dictionary_entries_unique UNIQUE (organization_id, tenant_id, kind, normalized_value);

-- 25. customer_dictionary_kind_settings (UNIQUE org,tenant,kind) ---------------------------------
CREATE TABLE customer_dictionary_kind_settings (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    kind text NOT NULL,
    selection_mode text NOT NULL DEFAULT 'single',
    visible_in_tags boolean NOT NULL DEFAULT true,
    sort_order int NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT customer_dictionary_kind_settings_pkey PRIMARY KEY (id)
);
ALTER TABLE customer_dictionary_kind_settings ADD CONSTRAINT customer_dictionary_kind_settings_unique UNIQUE (organization_id, tenant_id, kind);

-- Foreign keys (added after all tables exist) ---------------------------------------------------
ALTER TABLE customer_people ADD CONSTRAINT customer_people_entity_id_foreign FOREIGN KEY (entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
ALTER TABLE customer_people ADD CONSTRAINT customer_people_company_entity_id_foreign FOREIGN KEY (company_entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE ON DELETE SET NULL;
ALTER TABLE customer_companies ADD CONSTRAINT customer_companies_entity_id_foreign FOREIGN KEY (entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
ALTER TABLE customer_person_company_links ADD CONSTRAINT customer_person_company_links_person_entity_id_foreign FOREIGN KEY (person_entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
ALTER TABLE customer_person_company_links ADD CONSTRAINT customer_person_company_links_company_entity_id_foreign FOREIGN KEY (company_entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
ALTER TABLE customer_person_company_roles ADD CONSTRAINT customer_person_company_roles_person_entity_id_foreign FOREIGN KEY (person_entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
ALTER TABLE customer_person_company_roles ADD CONSTRAINT customer_person_company_roles_company_entity_id_foreign FOREIGN KEY (company_entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
ALTER TABLE customer_company_billing ADD CONSTRAINT customer_company_billing_entity_id_foreign FOREIGN KEY (entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
ALTER TABLE customer_addresses ADD CONSTRAINT customer_addresses_entity_id_foreign FOREIGN KEY (entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
ALTER TABLE customer_tag_assignments ADD CONSTRAINT customer_tag_assignments_tag_id_foreign FOREIGN KEY (tag_id) REFERENCES customer_tags (id) ON UPDATE CASCADE;
ALTER TABLE customer_tag_assignments ADD CONSTRAINT customer_tag_assignments_entity_id_foreign FOREIGN KEY (entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
ALTER TABLE customer_label_assignments ADD CONSTRAINT customer_label_assignments_label_id_foreign FOREIGN KEY (label_id) REFERENCES customer_labels (id) ON UPDATE CASCADE;
ALTER TABLE customer_label_assignments ADD CONSTRAINT customer_label_assignments_entity_id_foreign FOREIGN KEY (entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
ALTER TABLE customer_deal_stage_transitions ADD CONSTRAINT customer_deal_stage_transitions_deal_id_foreign FOREIGN KEY (deal_id) REFERENCES customer_deals (id) ON UPDATE CASCADE;
ALTER TABLE customer_deal_people ADD CONSTRAINT customer_deal_people_deal_id_foreign FOREIGN KEY (deal_id) REFERENCES customer_deals (id) ON UPDATE CASCADE;
ALTER TABLE customer_deal_people ADD CONSTRAINT customer_deal_people_person_entity_id_foreign FOREIGN KEY (person_entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
ALTER TABLE customer_deal_companies ADD CONSTRAINT customer_deal_companies_deal_id_foreign FOREIGN KEY (deal_id) REFERENCES customer_deals (id) ON UPDATE CASCADE;
ALTER TABLE customer_deal_companies ADD CONSTRAINT customer_deal_companies_company_entity_id_foreign FOREIGN KEY (company_entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
ALTER TABLE customer_activities ADD CONSTRAINT customer_activities_entity_id_foreign FOREIGN KEY (entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
ALTER TABLE customer_activities ADD CONSTRAINT customer_activities_deal_id_foreign FOREIGN KEY (deal_id) REFERENCES customer_deals (id) ON UPDATE CASCADE ON DELETE SET NULL;
ALTER TABLE customer_interactions ADD CONSTRAINT customer_interactions_entity_id_foreign FOREIGN KEY (entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
ALTER TABLE customer_comments ADD CONSTRAINT customer_comments_entity_id_foreign FOREIGN KEY (entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
ALTER TABLE customer_comments ADD CONSTRAINT customer_comments_deal_id_foreign FOREIGN KEY (deal_id) REFERENCES customer_deals (id) ON UPDATE CASCADE ON DELETE SET NULL;
ALTER TABLE customer_todo_links ADD CONSTRAINT customer_todo_links_entity_id_foreign FOREIGN KEY (entity_id) REFERENCES customer_entities (id) ON UPDATE CASCADE;
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS customer_dictionary_kind_settings CASCADE;
DROP TABLE IF EXISTS customer_dictionary_entries CASCADE;
DROP TABLE IF EXISTS customer_settings CASCADE;
DROP TABLE IF EXISTS customer_pipeline_stages CASCADE;
DROP TABLE IF EXISTS customer_pipelines CASCADE;
DROP TABLE IF EXISTS customer_todo_links CASCADE;
DROP TABLE IF EXISTS customer_comments CASCADE;
DROP TABLE IF EXISTS customer_interactions CASCADE;
DROP TABLE IF EXISTS customer_activities CASCADE;
DROP TABLE IF EXISTS customer_deal_companies CASCADE;
DROP TABLE IF EXISTS customer_deal_people CASCADE;
DROP TABLE IF EXISTS customer_deal_stage_transitions CASCADE;
DROP TABLE IF EXISTS customer_deals CASCADE;
DROP TABLE IF EXISTS customer_label_assignments CASCADE;
DROP TABLE IF EXISTS customer_labels CASCADE;
DROP TABLE IF EXISTS customer_tag_assignments CASCADE;
DROP TABLE IF EXISTS customer_tags CASCADE;
DROP TABLE IF EXISTS customer_addresses CASCADE;
DROP TABLE IF EXISTS customer_entity_roles CASCADE;
DROP TABLE IF EXISTS customer_company_billing CASCADE;
DROP TABLE IF EXISTS customer_person_company_roles CASCADE;
DROP TABLE IF EXISTS customer_person_company_links CASCADE;
DROP TABLE IF EXISTS customer_companies CASCADE;
DROP TABLE IF EXISTS customer_people CASCADE;
DROP TABLE IF EXISTS customer_entities CASCADE;
");
        }
    }
}
