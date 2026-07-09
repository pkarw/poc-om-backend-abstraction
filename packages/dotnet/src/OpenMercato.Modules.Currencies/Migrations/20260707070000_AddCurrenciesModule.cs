using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenMercato.Modules.Currencies.Migrations
{
    /// <summary>
    /// Byte-exact DDL for the currencies module tables — the .NET port of upstream
    /// currencies/data/entities.ts. Creates <c>currencies</c>, <c>exchange_rates</c> and
    /// <c>currency_fetch_configs</c> with their scope/pair indexes and unique constraints (uuid PKs via
    /// gen_random_uuid, numeric(18,8) rate, timestamptz timestamps, jsonb fetch-config). No cross-table
    /// FKs (pairs reference codes, not currency ids — matching upstream). Raw SQL — the model snapshot is
    /// intentionally not updated. Applied by MigrateAsync().
    /// </summary>
    [DbContext(typeof(CurrenciesMigrationsDbContext))]
    [Migration("20260707070000_AddCurrenciesModule")]
    public partial class AddCurrenciesModule : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE currencies (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    code text NOT NULL,
    name text NOT NULL,
    symbol text NULL,
    decimal_places int NOT NULL DEFAULT 2,
    thousands_separator text NULL,
    decimal_separator text NULL,
    is_base boolean NOT NULL DEFAULT false,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT currencies_pkey PRIMARY KEY (id)
);
CREATE INDEX currencies_scope_idx ON currencies (organization_id, tenant_id);
CREATE UNIQUE INDEX currencies_code_scope_unique ON currencies (organization_id, tenant_id, code);

CREATE TABLE exchange_rates (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    from_currency_code text NOT NULL,
    to_currency_code text NOT NULL,
    rate numeric(18,8) NOT NULL,
    date timestamptz NOT NULL,
    source text NOT NULL,
    type text NULL,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz NULL,
    CONSTRAINT exchange_rates_pkey PRIMARY KEY (id)
);
CREATE INDEX exchange_rates_scope_idx ON exchange_rates (organization_id, tenant_id);
CREATE INDEX exchange_rates_pair_idx ON exchange_rates (from_currency_code, to_currency_code, date);
CREATE UNIQUE INDEX exchange_rates_pair_datetime_source_unique ON exchange_rates (organization_id, tenant_id, from_currency_code, to_currency_code, date, source);

CREATE TABLE currency_fetch_configs (
    id uuid NOT NULL DEFAULT gen_random_uuid(),
    organization_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    provider text NOT NULL,
    is_enabled boolean NOT NULL DEFAULT false,
    sync_time text NULL,
    last_sync_at timestamptz NULL,
    last_sync_status text NULL,
    last_sync_message text NULL,
    last_sync_count int NULL,
    config jsonb NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT currency_fetch_configs_pkey PRIMARY KEY (id)
);
CREATE INDEX currency_fetch_configs_scope_idx ON currency_fetch_configs (organization_id, tenant_id);
CREATE INDEX currency_fetch_configs_enabled_idx ON currency_fetch_configs (is_enabled, sync_time);
CREATE UNIQUE INDEX currency_fetch_configs_provider_scope_unique ON currency_fetch_configs (organization_id, tenant_id, provider);
");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS currency_fetch_configs CASCADE;
DROP TABLE IF EXISTS exchange_rates CASCADE;
DROP TABLE IF EXISTS currencies CASCADE;
");
        }
    }
}
