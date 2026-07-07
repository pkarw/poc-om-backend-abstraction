using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenMercato.Core.Data;

#nullable disable

namespace OpenMercato.Api.Migrations
{
    /// <summary>
    /// Byte-exact DDL for the 11 auth tables (upstream migration order 20251030150038 ..
    /// 20260611103000, collapsed into one). Table names, column names/types, PK/FK/constraint
    /// names, and the partial unique indexes (WHERE deleted_at IS NULL) reproduce the port
    /// contract's Entities section exactly. Raw SQL — the model snapshot is intentionally not
    /// updated for these tables (see SKILL note). Discovered and applied at runtime by
    /// AppDbContext.Database.MigrateAsync().
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20260707010000_AddAuthModule")]
    public partial class AddAuthModule : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE users (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    tenant_id uuid NULL,
    organization_id uuid NULL,
    email text NOT NULL,
    email_hash text NULL,
    name text NULL,
    password_hash text NULL,
    is_confirmed boolean NOT NULL DEFAULT true,
    last_login_at timestamptz NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT users_pkey PRIMARY KEY (id)
);
CREATE INDEX users_email_hash_idx ON users (email_hash);
CREATE UNIQUE INDEX users_tenant_email_hash_uniq ON users (tenant_id, email_hash)
    WHERE deleted_at IS NULL AND email_hash IS NOT NULL;

CREATE TABLE roles (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    name text NOT NULL,
    tenant_id uuid NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT roles_pkey PRIMARY KEY (id),
    CONSTRAINT roles_tenant_id_name_unique UNIQUE (tenant_id, name)
);

CREATE TABLE user_roles (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL,
    role_id uuid NOT NULL,
    created_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT user_roles_pkey PRIMARY KEY (id),
    CONSTRAINT user_roles_user_id_foreign FOREIGN KEY (user_id) REFERENCES users (id) ON UPDATE CASCADE ON DELETE NO ACTION,
    CONSTRAINT user_roles_role_id_foreign FOREIGN KEY (role_id) REFERENCES roles (id) ON UPDATE CASCADE ON DELETE NO ACTION
);
CREATE INDEX user_roles_user_id_idx ON user_roles (user_id);
CREATE INDEX user_roles_role_id_idx ON user_roles (role_id);

CREATE TABLE sessions (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL,
    token text NOT NULL,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    last_used_at timestamptz NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT sessions_pkey PRIMARY KEY (id),
    CONSTRAINT sessions_token_unique UNIQUE (token),
    CONSTRAINT sessions_user_id_foreign FOREIGN KEY (user_id) REFERENCES users (id) ON UPDATE CASCADE ON DELETE NO ACTION
);

CREATE TABLE password_resets (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL,
    token text NOT NULL,
    expires_at timestamptz NOT NULL,
    used_at timestamptz NULL,
    created_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT password_resets_pkey PRIMARY KEY (id),
    CONSTRAINT password_resets_token_unique UNIQUE (token),
    CONSTRAINT password_resets_user_id_foreign FOREIGN KEY (user_id) REFERENCES users (id) ON UPDATE CASCADE ON DELETE NO ACTION
);

CREATE TABLE role_acls (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    role_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    features_json jsonb NULL,
    is_super_admin boolean NOT NULL DEFAULT false,
    organizations_json jsonb NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT role_acls_pkey PRIMARY KEY (id),
    CONSTRAINT role_acls_role_id_foreign FOREIGN KEY (role_id) REFERENCES roles (id) ON UPDATE CASCADE ON DELETE NO ACTION
);

CREATE TABLE user_acls (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    features_json jsonb NULL,
    is_super_admin boolean NOT NULL DEFAULT false,
    organizations_json jsonb NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT user_acls_pkey PRIMARY KEY (id),
    CONSTRAINT user_acls_user_id_foreign FOREIGN KEY (user_id) REFERENCES users (id) ON UPDATE CASCADE ON DELETE NO ACTION
);

CREATE TABLE user_sidebar_preferences (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL,
    tenant_id uuid NULL,
    organization_id uuid NULL,
    locale text NOT NULL,
    settings_json jsonb NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT user_sidebar_preferences_pkey PRIMARY KEY (id),
    CONSTRAINT user_sidebar_preferences_user_id_foreign FOREIGN KEY (user_id) REFERENCES users (id) ON UPDATE CASCADE ON DELETE NO ACTION
);
CREATE UNIQUE INDEX user_sidebar_preferences_active_unique_idx
    ON user_sidebar_preferences (user_id, tenant_id, organization_id)
    WHERE deleted_at IS NULL;

CREATE TABLE role_sidebar_preferences (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    role_id uuid NOT NULL,
    tenant_id uuid NULL,
    locale text NOT NULL,
    settings_json jsonb NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT role_sidebar_preferences_pkey PRIMARY KEY (id),
    CONSTRAINT role_sidebar_preferences_role_id_foreign FOREIGN KEY (role_id) REFERENCES roles (id) ON UPDATE CASCADE ON DELETE NO ACTION
);
CREATE UNIQUE INDEX role_sidebar_preferences_active_unique_idx
    ON role_sidebar_preferences (role_id, tenant_id)
    WHERE deleted_at IS NULL;

CREATE TABLE sidebar_variants (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL,
    tenant_id uuid NULL,
    organization_id uuid NULL,
    locale text NOT NULL,
    name text NOT NULL,
    settings_json jsonb NULL,
    is_active boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT sidebar_variants_pkey PRIMARY KEY (id),
    CONSTRAINT sidebar_variants_user_id_foreign FOREIGN KEY (user_id) REFERENCES users (id)
);
CREATE UNIQUE INDEX sidebar_variants_active_name_unique_idx
    ON sidebar_variants (user_id, tenant_id, name)
    WHERE deleted_at IS NULL;

CREATE TABLE user_consents (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    user_id uuid NOT NULL,
    tenant_id uuid NULL,
    organization_id uuid NULL,
    consent_type text NOT NULL,
    is_granted boolean NOT NULL DEFAULT false,
    granted_at timestamptz NULL,
    withdrawn_at timestamptz NULL,
    source text NULL,
    ip_address text NULL,
    integrity_hash text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT user_consents_pkey PRIMARY KEY (id),
    CONSTRAINT user_consents_user_id_tenant_id_consent_type_unique UNIQUE (user_id, tenant_id, consent_type)
);
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS user_consents;
DROP TABLE IF EXISTS sidebar_variants;
DROP TABLE IF EXISTS role_sidebar_preferences;
DROP TABLE IF EXISTS user_sidebar_preferences;
DROP TABLE IF EXISTS user_acls;
DROP TABLE IF EXISTS role_acls;
DROP TABLE IF EXISTS password_resets;
DROP TABLE IF EXISTS sessions;
DROP TABLE IF EXISTS user_roles;
DROP TABLE IF EXISTS roles;
DROP TABLE IF EXISTS users;
");
        }
    }
}
