using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenMercato.Core.Data;

#nullable disable

namespace OpenMercato.Api.Migrations
{
    /// <summary>
    /// Byte-exact DDL for the directory tables <c>tenants</c> and <c>organizations</c> (upstream
    /// migrations 20251030150038 [both tables + FK] → 20260314143323 [slug + unique] →
    /// 20260607222259 [logo_url], collapsed into one). Timestamps are <c>timestamptz</c> (no
    /// precision). parent_id/root_id carry NO DB FK (hierarchy managed in lib/hierarchy.ts). Raw SQL —
    /// the model snapshot is intentionally not updated. Applied at runtime by MigrateAsync().
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260707020000_AddDirectoryModule")]
    public partial class AddDirectoryModule : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE tenants (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    name text NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT tenants_pkey PRIMARY KEY (id)
);

CREATE TABLE organizations (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL,
    name text NOT NULL,
    slug text NULL,
    logo_url text NULL,
    is_active boolean NOT NULL DEFAULT true,
    parent_id uuid NULL,
    root_id uuid NULL,
    tree_path text NULL,
    depth int NOT NULL DEFAULT 0,
    ancestor_ids jsonb NOT NULL DEFAULT '[]',
    child_ids jsonb NOT NULL DEFAULT '[]',
    descendant_ids jsonb NOT NULL DEFAULT '[]',
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT organizations_pkey PRIMARY KEY (id),
    CONSTRAINT organizations_tenant_id_foreign FOREIGN KEY (tenant_id) REFERENCES tenants (id) ON UPDATE CASCADE
);
CREATE UNIQUE INDEX organizations_tenant_slug_uniq ON organizations (tenant_id, slug);
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS organizations;
DROP TABLE IF EXISTS tenants;
");
        }
    }
}
