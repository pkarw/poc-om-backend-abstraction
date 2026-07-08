using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenMercato.Core.Data;

#nullable disable

namespace OpenMercato.Api.Migrations
{
    /// <summary>
    /// Byte-exact DDL for the dictionaries module tables — the .NET port of the three upstream
    /// dictionaries migrations folded into one: base <c>dictionaries</c> + <c>dictionary_entries</c>
    /// (Migration20251030150038), then <c>position</c>/<c>is_default</c> + the position backfill + the
    /// partial <c>one default per dict</c> unique index + the guarded <c>customers.status</c> active-default
    /// seed (Migration20260410171544), then <c>entry_sort_mode</c> (Migration20260602202147). The FK is
    /// <c>on update cascade</c>. Raw SQL — the model snapshot is intentionally not updated. Applied by
    /// MigrateAsync().
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260707080000_AddDictionariesModule")]
    public partial class AddDictionariesModule : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE dictionaries (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    key text NOT NULL,
    name text NOT NULL,
    description text NULL,
    is_system boolean NOT NULL DEFAULT false,
    is_active boolean NOT NULL DEFAULT true,
    manager_visibility text NOT NULL DEFAULT 'default',
    entry_sort_mode text NOT NULL DEFAULT 'label_asc',
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT dictionaries_pkey PRIMARY KEY (id)
);
ALTER TABLE dictionaries ADD CONSTRAINT dictionaries_scope_key_unique UNIQUE (organization_id, tenant_id, key);

CREATE TABLE dictionary_entries (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    dictionary_id uuid NOT NULL,
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    value text NOT NULL,
    normalized_value text NOT NULL,
    label text NOT NULL,
    color text NULL,
    icon text NULL,
    position int NOT NULL DEFAULT 0,
    is_default boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT dictionary_entries_pkey PRIMARY KEY (id)
);
CREATE INDEX dictionary_entries_scope_idx ON dictionary_entries (dictionary_id, organization_id, tenant_id);
ALTER TABLE dictionary_entries ADD CONSTRAINT dictionary_entries_unique UNIQUE (dictionary_id, organization_id, tenant_id, normalized_value);
ALTER TABLE dictionary_entries ADD CONSTRAINT dictionary_entries_dictionary_id_foreign FOREIGN KEY (dictionary_id) REFERENCES dictionaries (id) ON UPDATE CASCADE;
CREATE UNIQUE INDEX dictionary_entries_one_default_per_dict ON dictionary_entries (dictionary_id, organization_id, tenant_id) WHERE is_default = true;

-- Seed the initial default for the out-of-the-box `customers.status` dictionary only (guarded so
-- customized tenants are not overridden). No-op unless a `customers.status` dictionary exists.
UPDATE dictionary_entries AS entries
SET is_default = true
FROM dictionaries AS dictionaries
WHERE entries.dictionary_id = dictionaries.id
  AND dictionaries.key = 'customers.status'
  AND entries.normalized_value = 'active'
  AND NOT EXISTS (
    SELECT 1 FROM dictionary_entries AS existing
    WHERE existing.dictionary_id = entries.dictionary_id
      AND existing.organization_id = entries.organization_id
      AND existing.tenant_id = entries.tenant_id
      AND existing.is_default = true
  );
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS dictionary_entries CASCADE;
DROP TABLE IF EXISTS dictionaries CASCADE;
");
        }
    }
}
