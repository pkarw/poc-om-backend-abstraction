# 0020 — Customers module: Phase 1 (records) deviations

Status: Accepted
Date: 2026-07-08
Scope: `.NET` port of `packages/core/src/modules/customers` — Phase 1 (people, companies, addresses,
tags, labels, entity-roles, person↔company links + custom fields). Upstream pinned `adc9da2`.
Contract: `upstream/analysis/modules/customers.md`.

## Context

Phase 1 lays down the core CRM records surface: people and companies (both rows in the polymorphic
`customer_entities` base + a 1:1 satellite profile), their addresses, tags, per-user labels, entity
roles, and person↔company links. It reuses the ported command bus (`OpenMercato.Core.Commands`), the
CRUD factory (`OpenMercato.Core.Crud.CrudRoute`), the entities custom-field codec, and the query_index
projection. This ADR records where the port intentionally diverges from a byte-for-byte reproduction and
which nuances are deferred to later phases.

## Decisions

1. **Polymorphic discriminator encoded in the index entity type.** Upstream points the people/companies
   list ORM at `CustomerEntity` and re-injects `kind:{$eq:'person'|'company'}` on every query. The .NET
   CRUD factory's index-backed list (`UseIndexList`) segregates by `entityType`, so people index under
   `customers:customer_person_profile` and companies under `customers:customer_company_profile` (record id
   = `customer_entities.id`). The base-table fallback path still injects `kind` via `ApplyFilters`. Net
   effect is identical (a person list never returns companies) with no runtime `kind` filter needed on the
   index path.

2. **Custom fields keyed by base entity id, not profile id.** Upstream stores/reads person/company custom
   fields on the profile row id under the CE entity id. The .NET codec (`ICrudCustomFields`) uses one
   `EntityType` for both indexer and CF merge, so Phase 1 keys custom fields under the CE profile entity id
   **using the base `customer_entities.id`** as the record id. Read and write are consistent (`cf_` on
   write, bare on read, plus `customValues`), custom-field filter/sort via the query index works, and the
   observable wire contract is preserved. The upstream "profile-id keying + entity/profile merge routing"
   is a documented internal detail, not part of the HTTP surface.

3. **Command owns custom-field + satellite writes (factory does not).** The .NET CRUD factory does not call
   `ICrudCustomFields.PersistAsync` in its mutation pipeline. The `customers.people.*` / `customers.companies.*`
   command handlers therefore (a) insert the base `customer_entities` row + the satellite profile
   atomically, and (b) call `PersistAsync` inside `ExecuteAsync` (same command transaction) so the values
   are committed before the factory's post-write index upsert reads them. This keeps base + satellite + EAV
   writes in one logical write, matching upstream's atomic-flush intent (SPEC-018).

4. **`CrudConfig.MapItemGet` opt-out (Core addition).** People and companies ship a hand-written enriched
   `GET /{id}` detail. To avoid an ASP.NET duplicate-route ambiguity with the factory's built-in single-get,
   `CrudConfig` gained a `MapItemGet` flag (default `true`); people/companies set it `false`. The `?id=`
   single-item shortcut on the list route is unaffected. Safe, additive, and does not change any existing
   module's behaviour (172 pre-existing tests still green).

5. **Custom base-row resolver for the index.** `CustomersIndexBaseRowResolver` teaches the query_index
   projection how to read the polymorphic people/companies base rows (`customer_entities` merged with the
   satellite profile). It registers last (customers loads after query_index in the catalog) so it wins over
   the generic `CustomEntitiesStorageBaseRowResolver`, to which it delegates every non-customers entity type.

6. **Preserved quirks (reproduced, not fixed).** People/companies create → **201** `{id, personId|companyId}`;
   update → `{ok, updatedAt}`; delete → **400** `{error:"Person/Company id is required"}` when id absent.
   Addresses/tags reuse `customers.activities.*` features. Tag **assign → 201 / unassign → 200** asymmetry;
   generic assign/unassign failure → **400** (not 500). Label **assign → 201 when created, else 200**;
   labels/assign|unassign declare only requireAuth and resolve the required feature at runtime from the
   target entity's kind. Entity-roles re-check the feature on every method. `profile:{}` update unwrap →
   **400** `profile must be an object` / `Unsupported profile field: X`. `assignable-staff` → **308** redirect.

## Deferred (PARITY-TODO, later phases)

- People/company **detail enrichment**: comments/activities/interactions/deals/todos collections and the
  `include`-token gating return empty arrays in Phase 1 (those tables/routes land in Phase 3).
- Company **`kpis`** (active-deal value, LTV, tenure, activity trend) and the enriched-companies
  `activeDeal`/`lastContactAt`/`clv` are structurally present but zero-valued until Phase 3.
- Entity-roles **`userName`/`userEmail`** hydration via the auth `User` join.
- Full list `sortFieldMap` on the index path, search-token search sources, and the advanced filter tree.
- `x-om-operation` undo/redo header is emitted; the undo engine itself is the audit_logs concern.

## Consequences

Phase 1 is green: build succeeds with 0 errors/warnings and the full suite is **180/180** (172 prior + 8
new customers tests covering the people round-trip with a query-index custom-field filter, company/address/
tag CRUD, `cf_`-write / bare-read, and the documented quirks). The declared surface (21 ACL features, 51
events, 5 CE sets, 2 notification types) from the foundation is unchanged. Phases 2–4 (dictionaries/settings,
pipeline/timeline, dashboard widgets) build on these commands, the index resolver, and the seeder.
