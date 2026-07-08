using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenMercato.Core.Data;

#nullable disable

namespace OpenMercato.Api.Migrations
{
    /// <summary>
    /// Byte-exact DDL for the entities (custom-field engine) tables — the .NET collapse of upstream
    /// MikroORM migrations 20251030150038 [custom_entities / custom_entities_storage / custom_field_defs
    /// / custom_field_values] → 20251116183728 [custom_field_entity_configs] → 20251209080326
    /// [encryption_maps]. Column types/indexes reproduce the upstream <c>addSql</c> statements verbatim
    /// (uuid PK <c>gen_random_uuid()</c>; entity_id/record_id/field_key <c>text</c>; config_json/doc/
    /// fields_json <c>jsonb</c>; value_int <c>int</c>; value_float <c>real</c>; timestamps
    /// <c>timestamptz</c>; no cross-table FKs). Raw SQL — the model snapshot is intentionally not
    /// updated. Applied at runtime by MigrateAsync().
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260707050000_AddEntitiesModule")]
    public partial class AddEntitiesModule : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE custom_entities (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    entity_id text NOT NULL,
    label text NOT NULL,
    description text NULL,
    label_field text NULL,
    default_editor text NULL,
    show_in_sidebar boolean NOT NULL DEFAULT false,
    organization_id uuid NULL,
    tenant_id uuid NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT custom_entities_pkey PRIMARY KEY (id)
);
CREATE INDEX custom_entities_unique_idx ON custom_entities (entity_id, organization_id, tenant_id);

CREATE TABLE custom_entities_storage (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    entity_type text NOT NULL,
    entity_id text NOT NULL,
    organization_id uuid NULL,
    tenant_id uuid NULL,
    doc jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT custom_entities_storage_pkey PRIMARY KEY (id)
);
CREATE INDEX custom_entities_storage_unique_idx ON custom_entities_storage (entity_type, entity_id, organization_id);

CREATE TABLE custom_field_defs (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    entity_id text NOT NULL,
    organization_id uuid NULL,
    tenant_id uuid NULL,
    key text NOT NULL,
    kind text NOT NULL,
    config_json jsonb NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT custom_field_defs_pkey PRIMARY KEY (id)
);
CREATE INDEX cf_defs_entity_key_idx ON custom_field_defs (key);
CREATE INDEX cf_defs_active_entity_key_scope_idx ON custom_field_defs (entity_id, key, tenant_id, organization_id);
CREATE INDEX cf_defs_active_entity_global_idx ON custom_field_defs (entity_id);
CREATE INDEX cf_defs_active_entity_org_idx ON custom_field_defs (entity_id, organization_id);
CREATE INDEX cf_defs_active_entity_tenant_idx ON custom_field_defs (entity_id, tenant_id);
CREATE INDEX cf_defs_active_entity_tenant_org_idx ON custom_field_defs (entity_id, tenant_id, organization_id);

CREATE TABLE custom_field_values (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    entity_id text NOT NULL,
    record_id text NOT NULL,
    organization_id uuid NULL,
    tenant_id uuid NULL,
    field_key text NOT NULL,
    value_text text NULL,
    value_multiline text NULL,
    value_int int NULL,
    value_float real NULL,
    value_bool boolean NULL,
    created_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT custom_field_values_pkey PRIMARY KEY (id)
);
CREATE INDEX cf_values_entity_record_field_idx ON custom_field_values (field_key);
CREATE INDEX cf_values_entity_record_tenant_idx ON custom_field_values (entity_id, record_id, tenant_id);

CREATE TABLE custom_field_entity_configs (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    entity_id text NOT NULL,
    organization_id uuid NULL,
    tenant_id uuid NULL,
    config_json jsonb NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT custom_field_entity_configs_pkey PRIMARY KEY (id)
);
CREATE INDEX cf_entity_cfgs_entity_org_idx ON custom_field_entity_configs (entity_id, organization_id);
CREATE INDEX cf_entity_cfgs_entity_tenant_idx ON custom_field_entity_configs (entity_id, tenant_id);
CREATE INDEX cf_entity_cfgs_entity_scope_idx ON custom_field_entity_configs (entity_id, tenant_id, organization_id);

CREATE TABLE encryption_maps (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    entity_id text NOT NULL,
    tenant_id uuid NULL,
    organization_id uuid NULL,
    fields_json jsonb NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT encryption_maps_pkey PRIMARY KEY (id)
);
CREATE INDEX encryption_maps_entity_scope_idx ON encryption_maps (entity_id, tenant_id, organization_id);
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS encryption_maps CASCADE;
DROP TABLE IF EXISTS custom_field_entity_configs CASCADE;
DROP TABLE IF EXISTS custom_field_values CASCADE;
DROP TABLE IF EXISTS custom_field_defs CASCADE;
DROP TABLE IF EXISTS custom_entities_storage CASCADE;
DROP TABLE IF EXISTS custom_entities CASCADE;
");
        }
    }
}
