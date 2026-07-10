-- Enterprise record_locks schema (open-mercato/app image bundles the enterprise `record_locks`
-- module and serves /api/record_locks/*, but OM's `db:migrate` only scans core modules, so the
-- table is never created). Without it, every guarded mutation the OM UI performs — logging an
-- activity on a deal, and other form saves that call POST /api/record_locks/acquire — fails with
-- "relation record_locks does not exist" (500) and the UI shows "Failed to save activity."
--
-- DDL lifted 1:1 from packages/enterprise/src/modules/record_locks/migrations (final state after
-- Migration20260218002838 + churn migrations). Runs once on a fresh volume, before OM migrates.
-- Idempotent so a re-run is harmless.

create table if not exists "record_locks" (
  "id" uuid not null default gen_random_uuid(),
  "resource_kind" text not null,
  "resource_id" text not null,
  "token" text not null,
  "strategy" text not null default 'optimistic',
  "status" text not null default 'active',
  "locked_by_user_id" uuid not null,
  "locked_by_ip" text null,
  "base_action_log_id" uuid null,
  "locked_at" timestamptz not null,
  "last_heartbeat_at" timestamptz not null,
  "expires_at" timestamptz not null,
  "released_at" timestamptz null,
  "released_by_user_id" uuid null,
  "release_reason" text null,
  "tenant_id" uuid not null,
  "organization_id" uuid null,
  "created_at" timestamptz not null,
  "updated_at" timestamptz not null,
  "deleted_at" timestamptz null,
  constraint "record_locks_pkey" primary key ("id")
);

create index if not exists "record_locks_expiry_status_idx" on "record_locks" ("tenant_id", "expires_at", "status");
create index if not exists "record_locks_owner_status_idx" on "record_locks" ("tenant_id", "locked_by_user_id", "status");
create index if not exists "record_locks_resource_status_idx" on "record_locks" ("tenant_id", "resource_kind", "resource_id", "status");
create unique index if not exists "record_locks_active_scope_org_unique" on "record_locks" ("tenant_id", "organization_id", "resource_kind", "resource_id") where deleted_at is null and status = 'active' and organization_id is not null;
create unique index if not exists "record_locks_active_scope_tenant_unique" on "record_locks" ("tenant_id", "resource_kind", "resource_id") where deleted_at is null and status = 'active' and organization_id is null;
create unique index if not exists "record_locks_token_unique" on "record_locks" ("token");

create table if not exists "record_lock_conflicts" (
  "id" uuid not null default gen_random_uuid(),
  "resource_kind" text not null,
  "resource_id" text not null,
  "status" text not null default 'pending',
  "resolution" text null,
  "base_action_log_id" uuid null,
  "incoming_action_log_id" uuid null,
  "conflict_actor_user_id" uuid not null,
  "incoming_actor_user_id" uuid null,
  "resolved_by_user_id" uuid null,
  "resolved_at" timestamptz null,
  "tenant_id" uuid not null,
  "organization_id" uuid null,
  "created_at" timestamptz not null,
  "updated_at" timestamptz not null,
  "deleted_at" timestamptz null,
  constraint "record_lock_conflicts_pkey" primary key ("id")
);

create index if not exists "record_lock_conflicts_users_idx" on "record_lock_conflicts" ("tenant_id", "conflict_actor_user_id", "incoming_actor_user_id", "created_at");
create index if not exists "record_lock_conflicts_resource_idx" on "record_lock_conflicts" ("tenant_id", "resource_kind", "resource_id", "status", "created_at");
