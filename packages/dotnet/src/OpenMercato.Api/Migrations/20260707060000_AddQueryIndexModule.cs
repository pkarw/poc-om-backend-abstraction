using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenMercato.Core.Data;

#nullable disable

namespace OpenMercato.Api.Migrations
{
    /// <summary>
    /// Byte-exact DDL for the query_index (hybrid read-model) tables — the .NET port of upstream
    /// query_index/data/entities.ts. Reproduces the <c>entity_indexes</c> projection with its generated
    /// <c>organization_id_coalesced</c> column + coalesced-org uniqueness (the upsert conflict target),
    /// the 6 <c>customers:*</c> partial covering indexes, and the jobs/coverage/error-log/status-log/
    /// search-token tables. jsonb for doc/embedding/payload/details; timestamptz for all timestamps; no
    /// cross-table FKs. Raw SQL — the model snapshot is intentionally not updated. Applied by MigrateAsync().
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260707060000_AddQueryIndexModule")]
    public partial class AddQueryIndexModule : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE entity_indexes (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    entity_type text NOT NULL,
    entity_id text NOT NULL,
    organization_id uuid NULL,
    organization_id_coalesced uuid GENERATED ALWAYS AS (coalesce(organization_id, '00000000-0000-0000-0000-000000000000'::uuid)) STORED,
    tenant_id uuid NULL,
    doc jsonb NOT NULL,
    embedding jsonb NULL,
    index_version int NOT NULL DEFAULT 1,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT entity_indexes_pkey PRIMARY KEY (id)
);
CREATE INDEX entity_indexes_type_idx ON entity_indexes (entity_type);
CREATE INDEX entity_indexes_entity_idx ON entity_indexes (entity_id);
CREATE INDEX entity_indexes_org_idx ON entity_indexes (organization_id);
CREATE INDEX entity_indexes_type_tenant_idx ON entity_indexes (entity_type, tenant_id);
CREATE UNIQUE INDEX entity_indexes_type_entity_org_coalesced_unique ON entity_indexes (entity_type, entity_id, organization_id_coalesced);
CREATE INDEX entity_indexes_customer_entity_doc_idx ON entity_indexes (entity_id, organization_id, tenant_id) INCLUDE (doc) WHERE deleted_at is null and entity_type = 'customers:customer_entity' and organization_id is not null and tenant_id is not null;
CREATE INDEX entity_indexes_customer_person_profile_doc_idx ON entity_indexes (entity_id, organization_id, tenant_id) INCLUDE (doc) WHERE deleted_at is null and entity_type = 'customers:customer_person_profile' and organization_id is not null and tenant_id is not null;
CREATE INDEX entity_indexes_customer_company_profile_doc_idx ON entity_indexes (entity_id, organization_id, tenant_id) INCLUDE (doc) WHERE deleted_at is null and entity_type = 'customers:customer_company_profile' and organization_id is not null and tenant_id is not null;
CREATE INDEX entity_indexes_customer_entity_tenant_doc_idx ON entity_indexes (tenant_id, entity_id) INCLUDE (doc) WHERE deleted_at is null and entity_type = 'customers:customer_entity' and organization_id is null and tenant_id is not null;
CREATE INDEX entity_indexes_customer_person_profile_tenant_doc_idx ON entity_indexes (tenant_id, entity_id) INCLUDE (doc) WHERE deleted_at is null and entity_type = 'customers:customer_person_profile' and organization_id is null and tenant_id is not null;
CREATE INDEX entity_indexes_customer_company_profile_tenant_doc_idx ON entity_indexes (tenant_id, entity_id) INCLUDE (doc) WHERE deleted_at is null and entity_type = 'customers:customer_company_profile' and organization_id is null and tenant_id is not null;

CREATE TABLE entity_index_jobs (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    entity_type text NOT NULL,
    organization_id uuid NULL,
    tenant_id uuid NULL,
    partition_index int NULL,
    partition_count int NULL,
    processed_count int NULL,
    total_count int NULL,
    heartbeat_at timestamptz NULL,
    status text NOT NULL,
    started_at timestamptz NOT NULL,
    finished_at timestamptz NULL,
    CONSTRAINT entity_index_jobs_pkey PRIMARY KEY (id)
);
CREATE INDEX entity_index_jobs_type_idx ON entity_index_jobs (entity_type);
CREATE INDEX entity_index_jobs_org_idx ON entity_index_jobs (organization_id);
CREATE UNIQUE INDEX entity_index_jobs_scope_unique ON entity_index_jobs (entity_type, coalesce(organization_id, '00000000-0000-0000-0000-000000000000'::uuid), coalesce(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid), coalesce(partition_index, -1), coalesce(partition_count, -1));

CREATE TABLE entity_index_coverage (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    entity_type text NOT NULL,
    tenant_id uuid NULL,
    organization_id uuid NULL,
    with_deleted boolean NOT NULL DEFAULT false,
    base_count int NOT NULL DEFAULT 0,
    indexed_count int NOT NULL DEFAULT 0,
    vector_indexed_count int NOT NULL DEFAULT 0,
    refreshed_at timestamptz NOT NULL,
    CONSTRAINT entity_index_coverage_pkey PRIMARY KEY (id)
);
CREATE UNIQUE INDEX entity_index_coverage_scope_idx ON entity_index_coverage (entity_type, tenant_id, organization_id, with_deleted);

CREATE TABLE indexer_error_logs (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    source text NOT NULL,
    handler text NOT NULL,
    entity_type text NULL,
    record_id text NULL,
    tenant_id uuid NULL,
    organization_id uuid NULL,
    payload jsonb NULL,
    message text NOT NULL,
    stack text NULL,
    occurred_at timestamptz NOT NULL,
    CONSTRAINT indexer_error_logs_pkey PRIMARY KEY (id)
);
CREATE INDEX indexer_error_logs_source_idx ON indexer_error_logs (source);
CREATE INDEX indexer_error_logs_occurred_idx ON indexer_error_logs (occurred_at);

CREATE TABLE indexer_status_logs (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    source text NOT NULL,
    handler text NOT NULL,
    level text NOT NULL DEFAULT 'info',
    entity_type text NULL,
    record_id text NULL,
    tenant_id uuid NULL,
    organization_id uuid NULL,
    message text NOT NULL,
    details jsonb NULL,
    occurred_at timestamptz NOT NULL,
    CONSTRAINT indexer_status_logs_pkey PRIMARY KEY (id)
);
CREATE INDEX indexer_status_logs_source_idx ON indexer_status_logs (source);
CREATE INDEX indexer_status_logs_occurred_idx ON indexer_status_logs (occurred_at);

CREATE TABLE search_tokens (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    entity_type text NOT NULL,
    entity_id text NOT NULL,
    organization_id uuid NULL,
    tenant_id uuid NULL,
    field text NOT NULL,
    token_hash text NOT NULL,
    token text NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT search_tokens_pkey PRIMARY KEY (id)
);
CREATE INDEX search_tokens_lookup_idx ON search_tokens (entity_type, field, token_hash, tenant_id, organization_id);
CREATE INDEX search_tokens_entity_idx ON search_tokens (entity_type, entity_id);
CREATE INDEX search_tokens_tenant_token_hash_idx ON search_tokens (tenant_id, token_hash);
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS search_tokens CASCADE;
DROP TABLE IF EXISTS indexer_status_logs CASCADE;
DROP TABLE IF EXISTS indexer_error_logs CASCADE;
DROP TABLE IF EXISTS entity_index_coverage CASCADE;
DROP TABLE IF EXISTS entity_index_jobs CASCADE;
DROP TABLE IF EXISTS entity_indexes CASCADE;
");
        }
    }
}
