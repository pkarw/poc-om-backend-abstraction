using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenMercato.Modules.Dashboards.Migrations
{
    /// <summary>
    /// Byte-exact DDL for the dashboards tables <c>dashboard_layouts</c>,
    /// <c>dashboard_role_widgets</c>, <c>dashboard_user_widgets</c> (upstream MikroORM migration
    /// Migration20251030150038). uuid PK <c>gen_random_uuid()</c>; tenant_id/organization_id nullable
    /// (no FK); jsonb columns <c>not null default '[]'</c>; user_widgets.mode <c>text not null default
    /// 'inherit'</c>; three unique constraints, no secondary indexes. Raw SQL — the model snapshot is
    /// intentionally not updated. Applied at runtime by MigrateAsync().
    /// </summary>
    [DbContext(typeof(DashboardsMigrationsDbContext))]
    [Migration("20260707030000_AddDashboardsModule")]
    public partial class AddDashboardsModule : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE dashboard_layouts (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL,
    tenant_id uuid NULL,
    organization_id uuid NULL,
    layout_json jsonb NOT NULL DEFAULT '[]',
    created_at timestamptz NOT NULL,
    updated_at timestamptz NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT dashboard_layouts_pkey PRIMARY KEY (id),
    CONSTRAINT dashboard_layouts_user_id_tenant_id_organization_id_unique UNIQUE (user_id, tenant_id, organization_id)
);

CREATE TABLE dashboard_role_widgets (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    role_id uuid NOT NULL,
    tenant_id uuid NULL,
    organization_id uuid NULL,
    widget_ids_json jsonb NOT NULL DEFAULT '[]',
    created_at timestamptz NOT NULL,
    updated_at timestamptz NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT dashboard_role_widgets_pkey PRIMARY KEY (id),
    CONSTRAINT dashboard_role_widgets_role_id_tenant_id_organization_id_unique UNIQUE (role_id, tenant_id, organization_id)
);

CREATE TABLE dashboard_user_widgets (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL,
    tenant_id uuid NULL,
    organization_id uuid NULL,
    mode text NOT NULL DEFAULT 'inherit',
    widget_ids_json jsonb NOT NULL DEFAULT '[]',
    created_at timestamptz NOT NULL,
    updated_at timestamptz NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT dashboard_user_widgets_pkey PRIMARY KEY (id),
    CONSTRAINT dashboard_user_widgets_user_id_tenant_id_organization_id_unique UNIQUE (user_id, tenant_id, organization_id)
);
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS dashboard_user_widgets CASCADE;
DROP TABLE IF EXISTS dashboard_role_widgets CASCADE;
DROP TABLE IF EXISTS dashboard_layouts CASCADE;
");
        }
    }
}
