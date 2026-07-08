using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenMercato.Core.Data;

#nullable disable

namespace OpenMercato.Api.Migrations
{
    /// <summary>
    /// Byte-exact DDL for the audit_logs <c>action_logs</c> table (upstream MikroORM migrations
    /// 20251030150038 [create] → 20260207101938 [parent_resource_*] → 20260412160533
    /// [action_type/changed_fields/primary_changed_field/source_key] → 20260423202109
    /// [related_resource_*], collapsed into one). uuid PK <c>gen_random_uuid()</c>; tenant_id/
    /// organization_id/actor_user_id nullable uuid (no FK); execution_state <c>text not null default
    /// 'done'</c>; command_payload/snapshot_before/snapshot_after/changes_json/context_json jsonb;
    /// changed_fields <c>text[]</c>; timestamps <c>timestamptz</c>. Raw SQL — the model snapshot is
    /// intentionally not updated. Applied at runtime by MigrateAsync().
    /// (The audit_logs <c>access_logs</c> table is out of scope for the command-bus port.)
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260707040000_AddActionLogs")]
    public partial class AddActionLogs : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE action_logs (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    tenant_id uuid NULL,
    organization_id uuid NULL,
    actor_user_id uuid NULL,
    command_id text NOT NULL,
    action_label text NULL,
    action_type text NULL,
    resource_kind text NULL,
    resource_id text NULL,
    parent_resource_kind text NULL,
    parent_resource_id text NULL,
    execution_state text NOT NULL DEFAULT 'done',
    undo_token text NULL,
    command_payload jsonb NULL,
    snapshot_before jsonb NULL,
    snapshot_after jsonb NULL,
    changes_json jsonb NULL,
    changed_fields text[] NULL,
    primary_changed_field text NULL,
    context_json jsonb NULL,
    source_key text NULL,
    related_resource_kind text NULL,
    related_resource_id text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT action_logs_pkey PRIMARY KEY (id)
);

CREATE INDEX action_logs_tenant_idx ON action_logs (tenant_id, created_at);
CREATE INDEX action_logs_actor_idx ON action_logs (actor_user_id, created_at);
CREATE INDEX action_logs_resource_idx ON action_logs (tenant_id, resource_kind, resource_id, created_at);
CREATE INDEX action_logs_parent_resource_idx ON action_logs (tenant_id, parent_resource_kind, parent_resource_id, created_at);
CREATE INDEX action_logs_action_type_idx ON action_logs (tenant_id, organization_id, action_type, created_at);
CREATE INDEX action_logs_source_key_idx ON action_logs (tenant_id, organization_id, source_key, created_at);
CREATE INDEX action_logs_primary_changed_field_idx ON action_logs (tenant_id, organization_id, primary_changed_field, created_at);
CREATE INDEX action_logs_changed_fields_idx ON action_logs USING gin (changed_fields);
CREATE INDEX action_logs_related_resource_idx ON action_logs (tenant_id, related_resource_kind, related_resource_id, created_at);
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS action_logs CASCADE;
");
        }
    }
}
