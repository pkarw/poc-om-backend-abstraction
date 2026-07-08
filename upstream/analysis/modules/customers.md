# Port contract — customers

> Upstream commit `adc9da27759e357febe9ed8d4b7182040d127349` (2026-07-01). Source: `packages/core/src/modules/customers/`. Regenerate via `om-analyze-module customers`.
> **LARGE module** — 25 ORM entities/tables, ~54 HTTP route files (~90 method+path endpoints), 51 declared events, 21 features, 5 custom-field CE registrations (4 distinct sets), 2 queues, 4 subscribers, 19 command files (~45 handlers), 21 migrations.
> Shared machinery is referenced by spec id, not re-derived: `makeCrudRoute` envelope + `x-om-operation` metadata → `specs/02-api-compatibility.md`; data layer / optimistic-lock guard → `specs/03-data-layer.md`; events/queues → `specs/04-events-and-queues.md`; auth/RBAC dispatcher → `specs/05-auth-and-rbac.md`; declare-now surface parity → `specs/10-module-contract-parity.md`.

## Overview

`customers` is Open Mercato's CRM domain module (Tier 3). It manages **people and companies** (both rows in a polymorphic `customer_entities` base table discriminated by `kind`, with 1:1 satellite profile tables), their **addresses, tags, per-user labels, entity roles, and person↔company links**; a full **sales pipeline** (deals, pipeline stages, stage transitions, deal↔person/company links); a **unified interaction/activity/task/email timeline** (with legacy activity/todo bridge adapters); **comments**; module-scoped **dictionaries** and **settings**; and four **dashboard-widget data endpoints**. Routes are a mix of `makeCrudRoute`-generated CRUD (people, companies, addresses, tags, deals, comments, interactions-writes) and hand-written command-bus routes (detail views, links, roles, dictionaries, settings, pipelines, aggregations). It sits downstream of the platform base (auth, directory, entities/custom-fields, dictionaries, currencies, feature_toggles, query_index, progress) and integrates with communication_channels for email linking.

## Dependencies

| module | why needed | must be ported first |
|---|---|---|
| entities (custom fields) | CE registration (`ce.ts`), custom-field defs (`ensureCustomFieldDefinitions`), EAV values (`loadCustomFieldValues`, `splitCustomFieldPayload`, `extractAllCustomFieldEntries`), CF filters + routing (`custom_field_defs`), `#generated/entities.ids.generated` (`E`/`CoreEntities`), install-from-CE. Pervasive in lists/details/commands/seed. | **yes** (no port) |
| dictionaries | `currency` route reads generic `Dictionary`/`DictionaryEntry` (keys `currency`/`currencies`); entry sort machinery (`dictionaries/lib/entrySort`); `DictionaryEntrySortMode` type on `customer_settings`; deal-stats loss-reason lookup (key `sales.deal_loss_reason`); org-inheritance `Organization`. **Note:** customer dictionaries are a *separate* first-class table (`customer_dictionary_entries`), NOT the generic dictionaries module. | **yes** (no port) |
| currencies | aggregate + summary resolve tenant base currency (`currencies.is_base`) and convert via `exchangeRateService.getRates({maxDaysBack:60, autoFetch:false})`; `RateResult` type in `lib/dealsMetrics`. | **yes** (no port) |
| directory | `resolveOrganizationScopeForRequest`, `isOrganizationReadAccessAllowed`, `resolveCustomerDetailTenantScope`, org-scope guards, `Organization` ancestor inheritance. | yes (✅ ported) |
| auth | `getAuthFromRequest`, `rbacService.userHasAllFeatures`/`getGrantedFeatures`, `User` entity join (comment/role/owner hydration). | yes (✅ ported) |
| feature_toggles | `setup.ts` seeds 3 `FeatureToggle` rows (interaction flags). | yes (no port) |
| query_index | read-model indexing (`indexer.entityType`), `query_index.coverage.refresh` events, CLI stress-test doc builder. | yes (no port) |
| progress | bulk-deal routes/workers create/fail progress jobs (`progressService`). | yes (no port) |
| dashboards | 4 widget routes reuse `resolveWidgetScope`; gated by `dashboards.view`. | yes (🔍 analyzed) |
| communication_channels | 2 subscribers consume `communication_channels.message.received|sent` → create `CustomerInteraction`; interaction `external_message_id` extension link. | for email linking |
| staff | `assignable-staff` is a 308 redirect shim to `/api/staff/team-members/assignable`. | for that shim only |
| inbox_ops, messages, search, notifications, analytics, audit_logs (undo), cache | inbox actions, message-object types, search index, notification types, analytics entities, undo/redo, dictionary cache. | shared surfaces |

---

## Porting tiers

### A. Dependency prerequisites (port these first, minimal surface customers actually needs)

Ordered by port-priority. "Minimal surface" = only what customers touches.

1. **entities / custom-fields** — REQUIRED, no port yet. Needs: (a) CE registry so `customers:customer_person_profile|customer_company_profile|customer_deal|customer_activity|customer_interaction` resolve; (b) custom-field definition install (`ensureCustomFieldDefinitions` at `organizationId: null`, tenant-global); (c) EAV value read/write (`loadCustomFieldValues`, `setCustomFields`), CF query filters (`buildCustomFieldFiltersFromQuery`), payload split/extract (`splitCustomFieldPayload`, `extractAllCustomFieldEntries`), and `custom_field_defs` routing (per-key canonical home entity vs profile). Nothing else from entities is required.
2. **dictionaries** — REQUIRED, no port yet. Needs: generic `Dictionary` + `DictionaryEntry` tables (for the `currency` route + deal-stats loss-reason), and the entry-sort helpers (`resolveDictionaryEntrySortMode`, `sortDictionaryEntries`, `dictionaryEntrySortModeSchema`). Customer-owned dictionaries live in customers' own tables, so most of the generic module is NOT needed — only storage + sort + a currency dictionary + `DictionaryManagerVisibility`/`DictionaryEntryOption` types.
3. **currencies** — REQUIRED, no port yet. Needs ONLY: `currencies` table with `is_base` flag + `exchangeRateService.getRates(...)`. Used solely by `deals/aggregate` and `deals/summary` for base-currency conversion. All other routes echo currency as free-text.
4. **directory** (✅), **auth** (✅) — already ported; customers uses org-scope resolution + rbac feature checks + `User` join.
5. **feature_toggles, query_index, progress, dashboards, communication_channels** — needed for setup seed (toggles), indexing, bulk workers, widget scope, and email linking respectively. Customers can boot Phase 1–3 with these stubbed except progress (Phase 3 bulk) and communication_channels (email subscribers).

Dependencies still lacking a port: **entities, dictionaries, currencies** (all Tier 1/2). These are the blocking prerequisites.

### B. Phased customers plan

| Phase | Scope | Entities (tables) | Route groups |
|---|---|---|---|
| **1 — core records** | people/companies/addresses/tags/labels/entity-roles/person-company links + custom fields | customer_entities, customer_people, customer_companies, customer_person_company_links, customer_person_company_roles, customer_company_billing, customer_addresses, customer_tags, customer_tag_assignments, customer_labels, customer_label_assignments, customer_entity_roles | people (+[id], +companies(+[linkId],+enriched), +roles, +check-phone), companies (+[id], +people, +roles), addresses, tags (+assign/unassign), labels (+assign/unassign), assignable-staff (redirect) |
| **2 — dictionaries & settings** | module dictionaries, kind settings, settings | customer_dictionary_entries, customer_dictionary_kind_settings, customer_settings | dictionaries/[kind] (+[id], currency, kind-settings), settings/address-format, settings/dictionary-sort-modes, settings/stuck-threshold |
| **3 — pipeline & timeline** | deals, interactions, activities, comments, todos, pipelines, stages, bulk workers | customer_deals, customer_deal_stage_transitions, customer_deal_people, customer_deal_companies, customer_activities, customer_interactions, customer_comments, customer_todo_links, customer_pipelines, customer_pipeline_stages | deals (+[id](+people,+companies,+stats), aggregate, summary, bulk-update-owner, bulk-update-stage), interactions (+[id]/visibility, complete, cancel, conflicts, counts, tasks), activities (deprecated bridge), comments, todos (deprecated bridge), pipelines, pipeline-stages (+reorder) |
| **4 — dashboard widgets** | read-only aggregation endpoints for the dashboards module | (none — reads customer_entities / customer_deals / interactions) | dashboard/widgets/{customer-todos, new-customers, new-deals, next-interactions} |

Phase 1 requires entities/custom-fields. Phase 2 requires dictionaries + Phase 1. Phase 3 requires currencies (aggregate/summary), progress (bulk), communication_channels (email subscribers). Phase 4 requires dashboards.

---

## HTTP routes — summary

Auth column: ✓ = dispatcher `requireAuth`. Feature column = `requireFeatures` metadata. Envelope conventions (paged list `{items,total,page,pageSize,totalPages}`, `x-om-operation` undo header) per specs/02 unless noted. Hand-written routes use bespoke JSON shapes (documented per-group).

### Records (Phase 1)

| METHOD | path | auth | requiredFeatures | source |
|---|---|---|---|---|
| GET | /api/customers/people | ✓ | customers.people.view | people/route.ts (makeCrudRoute) |
| POST/PUT/DELETE | /api/customers/people | ✓ | customers.people.manage | people/route.ts |
| GET | /api/customers/people/[id] | ✓ | customers.people.view | people/[id]/route.ts (hand-written) |
| GET/POST | /api/customers/people/[id]/companies | ✓ | .view / .manage | people/[id]/companies/route.ts |
| PATCH/DELETE | /api/customers/people/[id]/companies/[linkId] | ✓ | customers.people.manage | .../[linkId]/route.ts |
| GET | /api/customers/people/[id]/companies/enriched | ✓ | customers.people.view | .../enriched/route.ts |
| GET/POST/PUT/DELETE | /api/customers/people/[id]/roles | ✓ | customers.roles.view / .manage | people/[id]/roles/route.ts → entity-roles-factory('person') |
| GET | /api/customers/people/check-phone | ✓ | customers.people.view | people/check-phone/route.ts |
| GET | /api/customers/companies | ✓ | customers.companies.view | companies/route.ts (makeCrudRoute) |
| POST/PUT/DELETE | /api/customers/companies | ✓ | customers.companies.manage | companies/route.ts |
| GET | /api/customers/companies/[id] | ✓ | customers.companies.view | companies/[id]/route.ts (hand-written) |
| GET | /api/customers/companies/[id]/people | ✓ | customers.companies.view | companies/[id]/people/route.ts |
| GET/POST/PUT/DELETE | /api/customers/companies/[id]/roles | ✓ | customers.roles.view / .manage | companies/[id]/roles/route.ts → entity-roles-factory('company') |
| GET/POST/PUT/DELETE | /api/customers/addresses | ✓ | customers.activities.view / .manage | addresses/route.ts (makeCrudRoute) |
| GET/POST/PUT/DELETE | /api/customers/tags | ✓ | customers.activities.view / .manage | tags/route.ts (makeCrudRoute) |
| POST | /api/customers/tags/assign | ✓ | customers.activities.manage | tags/assign/route.ts |
| POST | /api/customers/tags/unassign | ✓ | customers.activities.manage | tags/unassign/route.ts |
| GET/POST | /api/customers/labels | ✓ | customers.people.view / .manage | labels/route.ts |
| POST | /api/customers/labels/assign | ✓ | — (requireAuth only; feature resolved at runtime) | labels/assign/route.ts |
| POST | /api/customers/labels/unassign | ✓ | — (requireAuth only; feature resolved at runtime) | labels/unassign/route.ts |
| GET | /api/customers/assignable-staff | ✓ | customers.roles.view | assignable-staff/route.ts (**308 redirect, DEPRECATED**) |

**Feature-mapping quirks (preserve verbatim):** `addresses` + `tags` CRUD reuse `customers.activities.*` (no dedicated feature). `labels` GET/POST use `customers.people.*`. `labels/assign|unassign` declare only `requireAuth` and resolve the required feature at runtime from the target entity's kind (`customers.companies.manage` for a company, else `customers.people.manage`) via `rbac.userHasAllFeatures`.

### Dictionaries & settings (Phase 2)

| METHOD | path | auth | requiredFeatures | source |
|---|---|---|---|---|
| GET | /api/customers/dictionaries/{kind} | ✓ | customers.people.view | dictionaries/[kind]/route.ts |
| POST | /api/customers/dictionaries/{kind} | ✓ | customers.settings.manage | dictionaries/[kind]/route.ts |
| PATCH/DELETE | /api/customers/dictionaries/{kind}/{id} | ✓ | customers.settings.manage | dictionaries/[kind]/[id]/route.ts |
| GET | /api/customers/dictionaries/currency | ✓ | customers.people.view | dictionaries/currency/route.ts |
| GET | /api/customers/dictionaries/kind-settings | ✓ | customers.people.view | dictionaries/kind-settings/route.ts |
| PATCH | /api/customers/dictionaries/kind-settings | ✓ | customers.settings.manage | dictionaries/kind-settings/route.ts |
| GET/PUT | /api/customers/settings/address-format | ✓ | customers.settings.manage | settings/address-format/route.ts |
| GET/PATCH | /api/customers/settings/dictionary-sort-modes | ✓ | customers.settings.manage | settings/dictionary-sort-modes/route.ts |
| GET/PUT | /api/customers/settings/stuck-threshold | ✓ | customers.deals.manage | settings/stuck-threshold/route.ts |

### Pipeline & timeline (Phase 3)

| METHOD | path | auth | requiredFeatures | source |
|---|---|---|---|---|
| GET | /api/customers/deals | ✓ | customers.deals.view | deals/route.ts (makeCrudRoute) |
| POST/PUT/DELETE | /api/customers/deals | ✓ | customers.deals.manage | deals/route.ts |
| GET | /api/customers/deals/[id] | ✓ | customers.deals.view (+in-handler recheck) | deals/[id]/route.ts |
| GET | /api/customers/deals/[id]/people | ✓ | customers.deals.view | deals/[id]/people/route.ts |
| GET | /api/customers/deals/[id]/companies | ✓ | customers.deals.view | deals/[id]/companies/route.ts |
| GET | /api/customers/deals/[id]/stats | ✓ | customers.deals.view (+recheck) | deals/[id]/stats/route.ts |
| GET | /api/customers/deals/aggregate | ✓ | customers.deals.view | deals/aggregate/route.ts |
| GET | /api/customers/deals/summary | ✓ | customers.deals.view | deals/summary/route.ts |
| POST | /api/customers/deals/bulk-update-owner | ✓ | customers.deals.manage | deals/bulk-update-owner/route.ts |
| POST | /api/customers/deals/bulk-update-stage | ✓ | customers.deals.manage | deals/bulk-update-stage/route.ts |
| GET | /api/customers/interactions | ✓ | customers.interactions.view | interactions/route.ts (GET hand-written cursor) |
| POST/PUT/DELETE | /api/customers/interactions | ✓ | customers.interactions.manage | interactions/route.ts (makeCrudRoute writes) |
| PATCH | /api/customers/interactions/[id]/visibility | ✓ | customers.email.compose | interactions/[id]/visibility/route.ts (metadata.path override) |
| POST | /api/customers/interactions/complete | ✓ | customers.interactions.manage | interactions/complete/route.ts |
| POST | /api/customers/interactions/cancel | ✓ | customers.interactions.manage | interactions/cancel/route.ts |
| GET | /api/customers/interactions/conflicts | ✓ | customers.interactions.view | interactions/conflicts/route.ts |
| GET | /api/customers/interactions/counts | ✓ | customers.interactions.view | interactions/counts/route.ts |
| GET | /api/customers/interactions/tasks | ✓ | customers.interactions.view | interactions/tasks/route.ts |
| GET/POST/PUT/DELETE | /api/customers/activities | ✓ | customers.activities.view / .manage | activities/route.ts (**DEPRECATED bridge**, sunset 2026-06-30) |
| GET/POST/PUT/DELETE | /api/customers/comments | ✓ | customers.activities.view / .manage | comments/route.ts (makeCrudRoute) |
| GET/POST/PUT/DELETE | /api/customers/todos | ✓ | customers.view (GET) / customers.interactions.manage (writes) | todos/route.ts (**DEPRECATED bridge**) |
| GET/POST/PUT/DELETE | /api/customers/pipelines | ✓ | customers.pipelines.view / .manage | pipelines/route.ts |
| GET/POST/PUT/DELETE | /api/customers/pipeline-stages | ✓ | customers.pipelines.view / .manage | pipeline-stages/route.ts |
| POST | /api/customers/pipeline-stages/reorder | ✓ | customers.pipelines.manage | pipeline-stages/reorder/route.ts |

### Dashboard widgets (Phase 4) — all GET

| path | auth | requiredFeatures | source |
|---|---|---|---|
| /api/customers/dashboard/widgets/customer-todos | ✓ | dashboards.view + customers.widgets.todos | .../customer-todos/route.ts |
| /api/customers/dashboard/widgets/new-customers | ✓ | dashboards.view + customers.widgets.new-customers | .../new-customers/route.ts |
| /api/customers/dashboard/widgets/new-deals | ✓ | dashboards.view + customers.widgets.new-deals | .../new-deals/route.ts |
| /api/customers/dashboard/widgets/next-interactions | ✓ | dashboards.view + customers.widgets.next-interactions | .../next-interactions/route.ts |

No `rateLimit` on any customers route. Every route exports an `openApi` doc (tag `Customers`).

---

## Route detail

### `customer_entities` base-table pattern (load-bearing)

People and companies are **both rows in `customer_entities`**, discriminated by `kind` (`'person'`|`'company'`). Shared columns (display_name, description, owner_user_id, primary_email, primary_phone, status, lifecycle_stage, source, temperature, renewal_quarter, next_interaction_*, is_active) live on `customer_entities`. Kind-specific attributes live in 1:1 satellite tables joined by `entity_id`: `customer_people` (`CustomerPersonProfile`) and `customer_companies` (`CustomerCompanyProfile`). Every list/detail/create/update path operates on TWO tables; list CRUD points its ORM entity at `CustomerEntity` and re-merges profile columns in an `afterList` hook (the list projection comes from the query-index, so encrypted source-of-truth columns must be re-loaded and decrypted). Indexer/list entityId for both = `customers:customer_entity`; CF sources = `customers:customer_person_profile` / `customers:customer_company_profile`.

### People / Companies CRUD (makeCrudRoute)

Both use `makeCrudRoute` (spec/02). List envelope `{items,total,page,pageSize,totalPages}` with `paginationMetaOptional:true`. OpenAPI via `createCustomersCrudOpenApi` (tag `Customers`).

**people/route.ts** `orm`: entity `CustomerEntity`, idField `id`, orgField `organizationId`, tenantField `tenantId`, softDeleteField `deletedAt`. `enrichers.entityId` `customers.person`; `indexer.entityType` `customers:customer_entity`. `customFieldSources`: `customers:customer_person_profile` (table `customer_people`, alias `person_profile`, id→entity_id). `joins`: left `tag_assignments` → `customer_tag_assignments` (id→entity_id). `buildFilters` always injects `kind:{$eq:'person'}`. `list.fields` (snake): id, display_name, description, owner_user_id, primary_email, primary_phone, status, lifecycle_stage, source, next_interaction_at/name/ref_id/icon/color, organization_id, tenant_id, kind, created_at, updated_at. `sortFieldMap`: name→display_name, email/primaryEmail→primary_email, status, lifecycleStage→lifecycle_stage, source, nextInteractionAt→next_interaction_at, createdAt, updatedAt. `transformItem` strips `kind`, extracts `cf:*`. `afterList` re-loads `CustomerEntity`+`CustomerPersonProfile` via `findWithDecryption` and overlays base+profile fields.

**companies/route.ts** identical shape; features `customers.companies.*`; `kind:{$eq:'company'}`; CF source alias `company_profile` (table `customer_companies`); no `enrichers`; `afterList` overlays company profile fields (legal_name, brand_name, domain, website_url, industry, size_bucket, annual_revenue).

**people list query** (`.passthrough()`): page(≥1,def1), pageSize(1–100,def50), search, email, emailStartsWith, emailContains, status, lifecycleStage, source, hasEmail, hasPhone, hasNextInteraction, createdFrom, createdTo, sortField, sortDir(asc|desc), id(uuid), tagIds, tagIdsEmpty, excludeIds, excludeLinkedCompanyId(uuid), excludeLinkedDealId(uuid). Filter semantics: `search` via `findMatchingEntityIdsBySearchTokensAcrossSources` (customer_entity + person_profile search_tokens) with ILIKE fallback over display_name/primary_email/primary_phone/description/next_interaction_name; email exact/startsWith/contains (lowercased, mutually exclusive); status/lifecycleStage/source eq; `tagIds` (comma)→tag_assignments.tag_id $in; `tagIdsEmpty`→forces zero-UUID no-match; excludeIds + excludeLinkedCompanyId (resolves active person-company links) + excludeLinkedDealId (via `CustomerDealPersonLink`)→`applyEntityIdExclusion`; hasEmail/hasPhone/hasNextInteraction→`$exists`; createdFrom/To→created_at $gte/$lte; CF filters over entity+person_profile; advanced filter tree via query engine.
**companies list query** adds `excludeLinkedPersonId`(uuid, resolves active links); `excludeLinkedCompanyId` there is a **plain self-exclude** (not a link lookup). Search sources customer_entity + company_profile.

**People actions:** create→command `customers.people.create`, body `withScopedPayload`→split CFs→`personCreateSchema`, **201** `{ id: entityId??id??null, personId: personId??null }`. update→`customers.people.update`, body scoped→`normalizeProfilePayload`→split CFs→`personUpdateSchema`, `{ ok:true, updatedAt:<ISO|null> }`. delete→`customers.people.delete`, id from body/parsed/query/`?id=`, **400** `{error:'Person id is required'}` if absent, `{ok:true}`; delete OpenAPI documents **422** `{error, code:'PERSON_HAS_DEPENDENTS'}`.
**Companies actions:** `customers.companies.create` (`companyCreateSchema`, 201 `{id, companyId}`); `customers.companies.update` (`normalizeCompanyProfilePayload`→`companyUpdateSchema`, `{ok,updatedAt}`); `customers.companies.delete` (400 `'Company id is required'`, `{ok:true}`, 422 `COMPANY_HAS_DEPENDENTS`).

### Addresses / Tags CRUD (makeCrudRoute)

**addresses**: entity `CustomerAddress` (no soft delete), entityId `customers:customer_address`. `list.fields`: id, entity_id, name, purpose, company_name, address_line1/2, building_number, flat_number, city, region, postal_code, country, latitude, longitude, is_primary, organization_id, tenant_id. `buildFilters`: entityId→entity_id eq, id→id eq. list query: page/pageSize(def50), entityId(uuid), id(uuid), sortField, sortDir. create `customers.addresses.create` (201 `{id: addressId??id??null}`), update `customers.addresses.update` (`{ok:true}`), delete `customers.addresses.delete` (400 `'Address id is required'`, `{ok:true}`).

**tags**: entity `CustomerTag` (no soft delete), entityId `customers:customer_tag`. `list.fields`: id, slug, label, color, description, organization_id, tenant_id. `buildFilters`: search→`label $ilike %term%`. pageSize **default 100**. create `customers.tags.create` (201 `{id: tagId??id??null}`), update `customers.tags.update` (`{ok:true}`), delete `customers.tags.delete` (400 `'Tag id is required'`, `{ok:true}`).

### Request schemas (data/validators.ts)

Shared helpers: `scopedSchema` = `{organizationId:uuid, tenantId:uuid}` (injected by `withScopedPayload`). `phoneSchema`: empty→null, trim, max 50, `isValidPhoneNumber` refine, nullable. `clearableEmailSchema` empty→null/email/max320. `clearableUrlSchema` empty→null/url/max300. `clearableDomainSchema` empty→null/max200. `nextInteractionSchema` (strict): at(coerce date), name(1–200), refId(≤191), icon(≤100), color(`/^#([0-9a-fA-F]{6})$/`).
`baseEntitySchema`: displayName(1–200), description(≤4000), ownerUserId(uuid), primaryEmail(clearable), primaryPhone(phone), status(≤100), lifecycleStage(≤100), source(≤150), temperature(≤100), renewalQuarter(≤100), isActive(bool), nextInteraction(nullable), tags(uuid[]).

| schema | fields beyond scoped+base | required |
|---|---|---|
| personCreateSchema | firstName(1–120,req), lastName(1–120,req), displayName **optional**, preferredName≤120, jobTitle≤150, department≤150, seniority≤100, timezone≤120, linkedInUrl(clearable url), twitterUrl, companyEntityId(uuid nullable) | firstName,lastName,org,tenant |
| personUpdateSchema | id(uuid) + base/person `.partial()` | id |
| companyCreateSchema | displayName(req), legalName≤200, brandName≤200, domain(clearable), websiteUrl, industry≤150, sizeBucket≤100, annualRevenue(coerce ≥0) | displayName,org,tenant |
| companyUpdateSchema | id + companyCreateSchema.partial() | id |
| addressCreateSchema | entityId(uuid,req), name≤150, purpose≤150, companyName≤200, addressLine1(1–300,req), addressLine2≤300, buildingNumber≤50, flatNumber≤50, city≤150, region≤150, postalCode≤30, country≤150, latitude/longitude(coerce), isPrimary(bool) | entityId,addressLine1,org,tenant |
| addressUpdateSchema | id + addressCreateSchema.partial() | id |
| tagCreateSchema | slug(1–80, `/^[a-z0-9_-]+$/`), label(1–120), color≤30, description≤400 | slug,label,org,tenant |
| tagUpdateSchema | id + tagCreateSchema.partial() | id |
| tagAssignmentSchema | tagId(uuid), entityId(uuid) + scoped | all (assign AND unassign) |
| labelCreateSchema | label(1–120), slug(1–80,regex) optional | label |
| labelAssignmentSchema | labelId(uuid), entityId(uuid) | both |
| personCompanyLinkCreateSchema | scoped + personEntityId, companyEntityId, isPrimary? | |
| personCompanyLinkUpdateSchema | scoped + linkId(uuid), isPrimary(bool,req) | |
| personCompanyLinkDeleteSchema | scoped + linkId | |
| entityRoleCreateSchema | scoped + entityType(company\|person), entityId, roleType(1–100), userId | |
| entityRoleUpdateSchema | scoped + id, userId | |
| entityRoleDeleteSchema | scoped + id | |

**`profile:{}` unwrap normalizers** (payload.ts, update paths only): `normalizeProfilePayload`/`normalizeCompanyProfilePayload` flatten a nested `profile` object onto top-level keys (only when not already present). Person keys: firstName,lastName,preferredName,jobTitle,department,seniority,timezone,linkedInUrl,twitterUrl,companyEntityId. Company keys: legalName,brandName,domain,websiteUrl,industry,sizeBucket,annualRevenue. `id`/`updatedAt` ignored. Non-object profile→**400** `{error:'profile must be an object'}`; unknown key→**400** `{error:'Unsupported profile field: {{field}}'}`. Create does NOT normalize.

### People/Companies detail (hand-written, NOT makeCrudRoute)

**GET /people/[id]** — `getAuthFromRequest` (401), params `{id:uuid}` (400 `'Invalid person id'`), `resolveCustomerDetailTenantScope(id,'person',auth)` (404 `'Person not found'` on mismatch), load `CustomerEntity` (findOneWithDecryption, 404), `isOrganizationReadAccessAllowed` (403 `'Access denied'`), load `CustomerPersonProfile`. Query: repeatable/comma `include` tokens (case-insens.): `addresses`, `comments|notes`, `activities`, `deals`, `interactions`, `todos|tasks`; absent→empty arrays but `counts` always computed. **200** (`personDetailResponseSchema`): `interactionMode`(canonical|legacy), `person`{…all base fields…}, `profile`{…person profile…}|null, `customFields`(merged, see below), `tags`(tag+label assignments→{id,label,color}), `addresses[]`, `comments[]`(author name/email via `User`), `activities[]`, `interactions[]`, `deals[]`, `todos[]`, `isPrimary`, `companies[]`{id,displayName,isPrimary}, `company`{id,displayName}|null, `plannedActivitiesPreview`(≤5), `counts`{tags,comments,activities,interactions,todos,addresses,deals,companies}, `viewer`{userId,name,email}. Errors 400/401/403/404 `{error}`. Interactions legacy-vs-canonical via `resolveCustomerInteractionFeatureFlags`, email-visibility filtered (strict owner-only, no admin bypass).

**GET /companies/[id]** — same skeleton; feature `customers.companies.view`; 400 `'Invalid company id'`, 404 `'Company not found'`; extra include token `people`. Response adds company `temperature`/`renewalQuarter`, company `profile`{id,legalName,brandName,domain,websiteUrl,industry,sizeBucket,annualRevenue}, `people[]` (linked summary), and **`kpis`** (`CompanyDetailKpiSummary`){activeDealsCount, activeDealsValue, dealCurrency, activityCount, activityTrend{value,direction up|down|unchanged}|null, ltvValue, completedDealsCount, clientTenureYears}. `counts` includes `people`.

**CF merge on detail** (`lib/customFieldRouting.ts`): loads EAV for entity (`customers:customer_entity`, keyed by entity id) AND profile (`…_person_profile`/`…_company_profile`, keyed by profile id), merges via `custom_field_defs` routing — entity values first; per profile value, if routing says key belongs to entity fill-only-when-absent, else profile wins; then `normalizeCustomFieldResponse`→`{}` when null.

### Person↔company links & enriched

- **GET/POST /people/[id]/companies** — `loadPersonContext` (401/404/403/404 profile). GET→`{items:[{id: linkId??companyId, companyId, displayName, isPrimary}]}` via `summarizePersonCompanies`. POST body `{companyId:uuid, isPrimary?}`, requires `selectedOrganizationId` (400 `'Organization context is required'`), mutation-guard, command `customers.personCompanyLinks.create`, `{ok:true, result:{id,companyId,displayName,isPrimary}}`, sets `x-om-operation`.
- **PATCH/DELETE /people/[id]/companies/[linkId]** — `resolveLinkId` accepts a real link id OR a company entity id (404 `'Person-company link not found'`). PATCH `{isPrimary?}`: undefined→no-op `{ok:true, result:null}`, else command `customers.personCompanyLinks.update`. DELETE→command `customers.personCompanyLinks.delete`, `{ok:true}`. Both mutation-guard + `x-om-operation`.
- **GET /people/[id]/companies/enriched** — page(1)/pageSize(1–100,def20)/search/sort(name-asc|name-desc|recent). Batch-loads per company profile/address/billing/tags/roles/deals/interactions; **in-memory filter/sort/paginate** → paged envelope; item incl `activeDeal`, `lastContactAt`, `clv`, `status`, `lifecycleStage`, `temperature`, `renewalQuarter`. **Deal-status literals here are `'win'`/`'loose'`** (differs from KPI `'won'`/`'lost'`).
- **GET /companies/[id]/people** — page(1)/pageSize(1–100,def20)/search/sort; active links→`CompanyPersonItem`, in-memory paged envelope.
- `summarizePersonCompanies` fallback: if active links, list them; else if profile has legacy `company_entity_id`, emit ONE synthetic row `{linkId: companyId, companyId, displayName, isPrimary:true, synthetic:true}`.

### Entity roles (entity-roles-factory.ts)

Shared factory `createEntityRolesHandlers('person'|'company')`; `people/[id]/roles` + `companies/[id]/roles` are thin wrappers. Metadata GET `customers.roles.view`, POST/PUT/DELETE `customers.roles.manage`. `resourceKind` `customers.person`|`customers.company`. Every method re-checks the feature on the target org via `rbac.userHasAllFeatures` (403 `'Access denied'`, 401 no actor, 500 rbac unresolved). GET→loads `CustomerEntity` (404 `'Customer not found'`) then `CustomerEntityRole` rows (orderBy roleType asc), joins `User`; `{items:[{userName?,userEmail?,userPhone?(always null),id,entityType,entityId,userId,roleType,createdAt,updatedAt}]}`. POST body `{roleType(1–100),userId}`→command `customers.entityRoles.create`, **201** `{id: roleId??null}` (OpenAPI 409 'Role already assigned'). PUT `?roleId=uuid` body `{userId}` (404 `'Role not found'`)→`customers.entityRoles.update` `{ok:true}`. DELETE `?roleId=uuid`→`customers.entityRoles.delete` `{ok:true}`.

### Tags/labels assign

- **tags/assign, tags/unassign** (`customers.activities.manage`): `withScopedPayload`→`tagAssignmentSchema`→command `customers.tags.assign`/`.unassign`. assign→**201** `{id: assignmentId??null}`; unassign→**200** `{id: assignmentId??null}` (null if nothing assigned). Generic failure→**400** `{error:'Failed to assign/unassign tag'}` (not 500). `x-om-operation`.
- **GET/POST /labels** — labels are per-user+per-org (unique userId+tenant+org+slug). Actor via `resolveLabelActorUserId` (prefers auth.userId uuid; API-key callers without user id→401). Missing-table (`42P01`) tolerance: GET→`{items:[],assignedIds:[]}`; writes→**503** `{error:'Customer label tables are missing. Run yarn db:migrate.'}`. GET query: entityId, organizationId, ids(comma), page(1), pageSize(1–100,def50), search → `{items:[{id,slug,label}], assignedIds:uuid[], total,page,pageSize,totalPages}` (400 no org). POST `labelCreateSchema`+optional organizationId, slug from `slugifyLabel`, command `customers.labels.create`, **201** `{id,slug,label}` (OpenAPI 409 duplicate slug).
- **labels/assign, labels/unassign** (requireAuth only): actor via `resolveLabelActorUserId` (401), body `labelAssignmentSchema`+optional organizationId (400 no org), load `CustomerEntity` (404 `'Entity not found'`), derive kind→resourceKind + required feature (`customers.companies.manage` if company else `customers.people.manage`), `rbac.userHasAllFeatures` (403, 500 no rbac). assign→command `customers.labels.assign`, **201 if result.created else 200**, `{id: assignmentId}`; unassign→`customers.labels.unassign`, **200** `{id: assignmentId|null}`. Generic failure→500 `{error:'Failed to assign/unassign label'}`.

### Special / deprecated

- **GET /people/check-phone** (`customers.people.view`): query `{digits: /^\d{4,}$/}` (else 200 `{match:null}`). QueryBuilder `CustomerEntity` kind='person', not deleted, `regexp_replace(primary_phone,'\D','','g') = :digits`, scoped, limit 1. **200** `{match:{id,displayName}|null}`.
- **GET /assignable-staff** (`customers.roles.view`): **308 permanent redirect** to `/api/staff/team-members/assignable`, preserving query string. No body logic. Port as redirect.

### Deals CRUD (makeCrudRoute)

`orm`: entity `CustomerDeal`, id/org/tenant, softDeleteField `deletedAt`. `indexer.entityType` `customers:customer_deal`. `enrichers.entityId` `customers.deal`. `list.fields` (snake): id, title, description, status, pipeline_stage, pipeline_id, pipeline_stage_id, value_amount, value_currency, probability, expected_close_at, owner_user_id, source, closure_outcome, loss_reason_id, loss_notes, organization_id, tenant_id, created_at, updated_at. `decorateCustomFields.entityIds` `customers:customer_deal`. `sortFieldMap`: createdAt, updatedAt, title, value→value_amount, probability, expectedCloseAt→expected_close_at. `hooks.afterList` decorates each item with `personIds[]`, `people[{id,label}]`, `companyIds[]`, `companies[{id,label}]`, normalized tenantId/organizationId (on failure tags `_associations:{ok:false,reason}`; does NOT filter). create→command `customers.deals.create` (`dealCreateSchema`), **201** `{id: dealId??id??null}`. update→`customers.deals.update` (`dealUpdateSchema`), `{ok:true}`. delete→`customers.deals.delete`, id body/parsed/query/`?id=`, **400** `{error:'Deal id is required'}`, `{ok:true}`.

`dealListQuerySchema` (`.passthrough()`): page(≥1,1), pageSize(1–100,50), id(uuid), search (token index over title/description/status/pipeline_stage/source/value_amount/value_currency/cf:competitive_risk/cf:implementation_complexity; encrypted no-hit→zero; else ILIKE title/description), status(string|string[]→eq/in), pipelineStage(eq), pipelineId(string|string[]→eq/in), pipelineStageId(uuid|`"__unassigned"`→`pipeline_stage_id={$eq:null}`), ownerUserId(string|string[]), expectedCloseAtFrom/To, isStuck(bool→`fetchStuckDealIds` intersect), isOverdue(bool→status open + expected_close_at.$lt today00:00), needsAttention(bool→overdue-open ∪ open-stuck via `fetchNeedAttentionDealIds`), valueCurrency(upper 3-letter, eq/in), sortField, sortDir, personId/personEntityId(uuid[]→EXISTS customer_deal_people), companyId/companyEntityId(uuid[]→EXISTS customer_deal_companies), advanced filter params. Association filters: OR within category, AND across person+company; pre-pagination raw SQL so `total` stays correct.

`dealCreateSchema` = scoped + title(1–200,req), description(≤4000), status(≤50), pipelineStage(≤100), pipelineId(uuid), pipelineStageId(uuid), valueAmount(coerce ≥0), valueCurrency(len3), probability(0–100), expectedCloseAt(coerce date), ownerUserId(uuid, **nullable**), source(≤150), closureOutcome(enum won|lost), lossReasonId(uuid), lossNotes(≤4000), companyIds(uuid[]→customer_deal_companies), personIds(uuid[]→customer_deal_people). `dealUpdateSchema` = `{id:uuid}` + `dealCreateSchema.partial()`.

Command behavior (`commands/deals.ts`): on stage change (pipelineStageId changed & non-null) upserts `customer_deal_stage_transitions` (unique per deal+stageId), re-derives pipeline_stage label; explicit `em.flush()` before link syncs (MikroORM v7, SPEC-018). On create/update when status transitions to `win`/`won` or `loose`/`lost` (normalized): emits **`customers.deal.won`**/**`customers.deal.lost`** (persistent) payload `{id,tenantId,organizationId,ownerUserId,title,valueAmount,valueCurrency}`. Delete removes stage transitions. Invalid stage→CrudHttpError 400 `'Pipeline stage not found'`. Not found→404 `'Deal not found'`. Tolerant of missing `customer_deal_stage_transitions` table.

### Deals detail / sub / aggregations

- **GET /deals/[id]** — `getAuthFromRequest` (401 `'Authentication required'`), in-handler `rbac.userHasAllFeatures(['customers.deals.view'])` (403 `'Access denied'`), tenant mismatch→404, scope guard 403. Query `include` (comma; `stages`→pipeline history), `view` (`lite`|`detail-lite`→preview max 3 people/companies). **200** (`dealDetailResponseSchema`): `deal`{…}, `people[{id,label,subtitle|null,kind:'person'}]`, `companies[…kind:'company']`, `linkedPersonIds[]`, `linkedCompanyIds[]`, `counts{people,companies}`, `customFields`, `viewer{userId|null,name|null,email|null}`, `pipelineStages[{id,label,order,color|null,icon|null}]`, `pipelineName|null`, `stageTransitions[{stageId,stageLabel,stageOrder,transitionedAt}]`, `owner{id,name,email}|null`. `deal.closureOutcome`∈`won|lost|null`. Missing stage-transitions table tolerated.
- **GET /deals/[id]/people | /companies** — 401 `'Unauthorized'`, 404 `'Deal not found'`, 403. Query page(1)/pageSize(1–100,20)/search/sort(label-asc|label-desc|name-asc|name-desc|recent; name→label). In-memory. **200** `{items:[{id,label,subtitle|null,kind,linkedAt}], total,page,pageSize,totalPages}`. companies joins `CustomerCompanyProfile` domain subtitle.
- **GET /deals/[id]/stats** — same auth. Requires closed deal: `!closureOutcome`→**400** `{error, code:'DEAL_NOT_CLOSED'}`. 200 (`dealStatsResponseSchema`): dealValue, dealCurrency, closureOutcome(won|lost), closedAt(=updatedAt), pipelineName, dealsClosedThisPeriod, salesCycleDays(floor((updated−created)/86400000,≥0)), dealRankInQuarter(won-only|null), lossReason(dictionary `sales.deal_loss_reason`|null).
- **GET /deals/aggregate** (kanban lanes) — raw SQL, unencrypted cols. 401 `'Unauthorized'`; `scope.filterIds` multi-org. Query: pipelineId(uuid), search(token index), status(enum[] open|closed|win|loose), ownerUserId(uuid[]), personId(uuid[]), companyId(uuid[]), isStuck, isOverdue, expectedCloseAtFrom/To. Invalid→400 `'Invalid query parameters'`. Groups by (pipeline_stage_id, UPPER(value_currency)); null stage→`__unassigned`. Converts to base currency via `exchangeRateService.getRates({maxDaysBack:60,autoFetch:false})`. **200** `{baseCurrencyCode|null, perStage:[{stageId,count,openCount,totalInBaseCurrency,byCurrency:[{currency,total,count}],convertedAll,missingRateCurrencies[]}]}`.
- **GET /deals/summary** (4 KPI cards) — raw SQL, multi-org. Constants OPEN_STATUSES `['open','in_progress']`, TRAILING_MONTHS 6, TOP_OWNERS 5. Won: `status='win' OR closure_outcome='won'`; lost: `status='loose' OR closure_outcome='lost'`. **200** `{baseCurrencyCode,convertedAll,missingRateCurrencies, pipelineValue{value,delta{value,direction},stages[]}, activeDeals{value,delta,ownersCount,needAttention,owners[top5],ownersOverflow}, wonThisQuarter{value,delta,dealsClosed,avgDeal}, winRate{value(0-100),deltaPp,direction,previousValue,series[{period'YYYY-MM',rate(0-1)}](6mo)}}`.
- **POST /deals/bulk-update-owner | -stage** — async. 401→`{ok:false,progressJobId:null,message}`. Body `dealsBulkUpdateOwnerSchema` `{ids:uuid[] 1..10000, ownerUserId:uuid|null}` / `dealsBulkUpdateStageSchema` `{ids:uuid[] 1..10000, pipelineStageId:uuid}`. Invalid→**400** `{ok:false,progressJobId:null,message:'Invalid payload'}`. ids deduped, mutation-guard best-effort, `progressService.createJob` (jobType `customers.deals.bulk_update_owner`/`…_stage`, totalCount=ids.length, cancellable:false), enqueue (failure→mark failed, 500). **Success→202** `{ok:true,progressJobId:uuid,message}`.

### Interactions

Hybrid: writes via `makeCrudRoute`, GET hand-written (kysely cursor). `orm` entity `CustomerInteraction`, softDelete `deletedAt`. `enrichers.entityId` `customers.interaction`; indexer `customers:customer_interaction`. create→`customers.interactions.create` (`interactionCreateSchema`), **201** `{id: interactionId??id??null}`. update→`customers.interactions.update` (`interactionUpdateSchema`); before update runs legacy-activity bridge `resolveCanonicalActivityTargetId`; `{ok:true}`. delete→`customers.interactions.delete`, 400 `'Interaction id is required'`, `{ok:true}`.

**GET list (cursor, NOT paged envelope):** query `limit`(1–100,25), cursor(base64), entityId(uuid), dealId(uuid), status, interactionType, type(comma→IN), excludeInteractionType, search(ILIKE title/body; encrypted→none), from/to(coerce date over coalesce(occurred_at,scheduled_at,created_at)), pinned(true|false), sortField(scheduledAt|occurredAt|createdAt|updatedAt|status|priority|interactionType|title), sortDir. 401 `'Unauthorized'`. Email-visibility filter (`applyEmailVisibilityFilter`, apiKey viewer=null fail-closed). Invalid cursor→400 `'Invalid cursor'`. **200** `{items:interactionListItemSchema[], nextCursor?}` (base64 `{id,sortValue}`); items decrypt title/body, decorate authorName/authorEmail/dealTitle/customValues.

`interactionStatusValues=['planned','done','canceled']`. `interactionCreateSchema` = scoped + `{id?, entityId(uuid,req), interactionType(1–100,req), title?(≤500 nullable), body?(≤10000 nullable), status(enum def 'planned'), date?, time?, phoneNumber?(≤50), scheduledAt?(nullable), occurredAt?(nullable), priority?(int 0–100 nullable), authorUserId?/ownerUserId?/dealId?(uuid nullable), appearanceIcon?(≤100), appearanceColor?(#hex6), source?(≤100), durationMinutes?(int≥0), location?(≤500), allDay?, recurrenceRule?(≤500), recurrenceEnd?, participants?[{userId,name?,email?,status?}], reminderMinutes?(int≥0), visibility?(≤50), linkedEntities?[{id:uuid,type:company|deal|offer,label≤500}], guestPermissions?{canInviteOthers?,canModify?,canSeeList?} strict}`. superRefine: `interactionType='call'` requires valid phoneNumber. transform: derives scheduledAt from date+time. `interactionUpdateSchema` = `{id}` + partial (+`pinned?`).

**Lifecycle:** complete/cancel (command-bus, 401 no tenant, mutation-guard). `interactionCompleteSchema` `{id:uuid, occurredAt?:coerce date}`; `interactionCancelSchema` `{id:uuid}`. Commands `customers.interactions.complete` (status='done', occurredAt=parsed??now, emit **`customers.interaction.completed`**) / `.cancel` (status='canceled', emit **`customers.interaction.canceled`**); undo emits **`customers.interaction.reverted`**. **200** `{ok:true}` + `withOperationMetadata`. 404 `'Interaction not found'`.

**PATCH /interactions/[id]/visibility** — `metadata.path='/customers/interactions/[id]/visibility'`, feature `customers.email.compose`. 400 `'Invalid interaction id'`, 401. Body `{visibility: enum 'private'|'shared'}` strict; invalid→**422** `{error}`. Loads interaction filtered `interactionType='email'` (404 `'Email not found'`). **Author-only** (non-author→404, not 403). No-op→`{ok:true,changed:false}`. Else command `customers.interactions.update`, emit **`customers.email.visibility_changed`** (best-effort) `{interactionId,previousVisibility,nextVisibility,authorUserId,actorUserId,adminBypass:false,tenantId,organizationId}`. **200** `{ok:true,changed:true}`.

**GET /interactions/conflicts** — 401. Query `{date'YYYY-MM-DD'(req), startTime'HH:MM'(req), duration(int 1–1440,req), excludeId?(uuid), userId?(uuid def auth.userId), timezoneOffsetMinutes?(int −900..900)}`. Overlap query on `customer_interactions` status='planned', scheduled_at not null, author OR owner, limit 10. Invalid→400 `'Invalid date/time'`. **200** `{ok:true, result:{hasConflicts, conflicts:[{id,title|null,startTime'HH:MM',endTime,type}]}}`.
**GET /interactions/counts** — 401. Query `{entityId(uuid,req), status?(enum done|planned)}`. Email-visibility filter. **200** `{ok:true, result:{call,email,meeting,note,task,total}}`.
**GET /interactions/tasks** — Query `{page(1),pageSize(50,1–100),search?,all?,entityId?(uuid)}`. `resolveCustomerInteractionFeatureFlags`: unified→canonical only; else merge legacy `customer_todo_links` + canonical bridge (source `customer:interaction:todo-adapter`, cap 2000, dedup). **200 paged** `{items:todoItemSchema[],total,page,pageSize,totalPages}`; `all=true`→unpaginated.

### Activities / Comments / Todos

- **activities** (DEPRECATED bridge, sunset 2026-06-30): headers `Deprecation:true`, `Sunset:Tue, 30 Jun 2026 00:00:00 GMT`, `Link:</api/customers/interactions>; rel="successor-version"`; when `flags.legacyAdapters` off→**410**. Delegates writes to `customers.interactions.*` (interactionType=activityType, source `customer:interaction:activity-adapter`, create status = occurredAt?'done':'planned'). GET paged. create `activityCreateBodySchema` `{entityId,activityType(1–100),subject?≤200,body?≤8000,date?,time?,phoneNumber?,occurredAt?,dealId?,authorUserId?,appearanceIcon?,appearanceColor?(#hex6)}`→201 `{id}`; PUT/DELETE `{ok:true}`. Mutation-guard resourceKind `customers.activity`.
- **comments** (makeCrudRoute): entity `CustomerComment`, indexer `customers:customer_comment`. list `{page,pageSize,entityId?,dealId?,sortField?,sortDir?}`; fields id,entity_id,deal_id,body,author_user_id,appearance_icon,appearance_color,organization_id,tenant_id,created_at,updated_at. create `customers.comments.create` (`commentCreateSchema` scoped+`{entityId,dealId?,body(1–8000),authorUserId?,appearanceIcon?≤100 nullable,appearanceColor?#hex6 nullable}`), **201** `{id: commentId??id??null, authorUserId}`. update `customers.comments.update` `{ok:true}`. delete 400 `'Comment id is required'`. afterList enriches `dealTitle`/`deal_title` (throws 400 `'Tenant context is required'` if deal ids but no tenant).
- **todos** (DEPRECATED bridge): same deprecation/410. GET feature `customers.view`, writes `customers.interactions.manage`. Merges legacy + canonical bridge. POST→201 `{linkId,todoId}` (both=interaction id); PUT/DELETE `{ok:true}`. interactionType `task`, resourceKind `customers.todoLink`.

### Pipelines / stages (hand-written command-bus)

- **pipelines**: `buildContext` (401 `'Unauthorized'`); GET requires org+tenant (400 `'Organization and tenant context required'`). GET `?isDefault`→`{items:[{id,name,isDefault,organizationId,tenantId,createdAt,updatedAt}],total}`. POST `pipelineCreateSchema` scoped+`{name(1–200),isDefault?}`→command `customers.pipelines.create`, **201** `{id: pipelineId??null}`. PUT `pipelineUpdateSchema` `{id:uuid,name?,isDefault?}`→`customers.pipelines.update`, **200** `{ok:true}`. DELETE `{id}`→`customers.pipelines.delete` (404 `'Pipeline not found'` / **409** `'Cannot delete pipeline with active deals'`), `{ok:true}`.
- **pipeline-stages**: GET `?pipelineId`→joins `CustomerDictionaryEntry` (kind `pipeline_stage`) for color/icon→`{items:[{id,pipelineId,label,order,color|null,icon|null,organizationId,tenantId,createdAt,updatedAt}],total}`. POST `pipelineStageCreateSchema` scoped+`{pipelineId,label(1–200),order?(int≥0),color?≤20 nullish,icon?≤100 nullish}`→`customers.pipeline-stages.create`, **201** `{id}`. PUT `{id,label?,order?,color?,icon?}`→`.update` `{ok:true}`. DELETE `{id}`→`.delete` (404 / 409 `'Cannot delete pipeline stage with active deals'`).
- **reorder** (`customers.pipelines.manage`): `pipelineStageReorderSchema` scoped+`{stages:[{id:uuid,order:int≥0}] min1}`→`customers.pipeline-stages.reorder`, **200** `{ok:true}`.

### Dictionaries & settings

Dictionary kind system (`api/dictionaries/context.ts`): `{kind}` route param validated by `dictionaryKindSchema` = union(builtin route kinds, kebab-case custom). `KIND_MAP` route→stored kind: statuses→status, sources→source, lifecycle-stages→lifecycle_stage, address-types→address_type, activity-types→activity_type, deal-statuses→deal_status, pipeline-stages→pipeline_stage, job-titles→job_title, industries→industry, temperature→temperature, renewal-quarters→renewal_quarter, person-company-roles→person_company_role. Stored `person_company_role` bypasses cache and its list items carry `usageCount` (from `loadRoleTypeUsageMap`, key `${orgId}::${value}`); entries with usage can't be renamed/deleted (409 `role_type_in_use`). Cache prefix `customers:dictionaries`, TTL 300000ms.

- **GET /dictionaries/{kind}** — query `organizationId?`. 400 `'Organization context is required'` if no org. Reads `CustomerDictionaryEntry` where `{tenantId, kind:mapped, organizationId:{$in: readableOrgIds}}` (org inheritance via ancestors), orderBy label asc, inheritance dedup (local wins over inherited by normalizedValue), sorted by `settings.dictionarySortModes[routeKind]`. Cached unless person_company_role. **200** `{sortMode?, items:[{id,value,label,color|null,icon|null,organizationId,isInherited,createdAt,updatedAt, usageCount(only person_company_role)}]}`. Catch→400 `'Failed to load dictionary entries'`.
- **POST /dictionaries/{kind}** (`customers.settings.manage`) — body `{value(1–150 req), label?(≤150), color?(#hex6 nullable), icon?(1–48 nullable)}`. Mutation-guard resourceKind `customers.dictionary_entry`. Command `customers.dictionaryEntries.create` (upsert by tenant+org+kind+normalizedValue, returns mode created|updated|unchanged). **Status 201 when mode==='created', else 200**. `{id,value,label,color,icon,organizationId,isInherited:false}`. 409 duplicate (OpenAPI).
- **PATCH /dictionaries/{kind}/{id}** — body (≥1 field, refine 'No changes provided') `{value?,label?,color?,icon?}`. Command `.update`: 404 `'Dictionary entry not found'`; **409 `role_type_in_use`** (preserves body, translated with `{{count}}`); 409 other `'An entry with this value already exists'`; 400 `'Failed to save dictionary entry'`. **200** `{id,value,label,color,icon,organizationId,isInherited:false}`.
- **DELETE /dictionaries/{kind}/{id}** — command `.delete`: 404; 409 `role_type_in_use` (body preserves code,usageCount,ownerAssignments,relationshipAssignments). **200** `{success:true}`.
- **GET /dictionaries/currency** (`customers.people.view`) — reads generic `dictionaries` module: `Dictionary` key∈{currency,currencies}, then `DictionaryEntry`. **200** `{id:uuid, entries:[{id,value,label?}]}`. 404 `'Currency dictionary is not configured yet.'`; 500 `'Failed to load currency dictionary.'`.
- **GET /dictionaries/kind-settings** — query organizationId?. Reads `CustomerDictionaryKindSetting`. **200** `{items:[{id,kind,selectionMode('single'|'multi'),visibleInTags,sortOrder}]}`. Table-missing (`42P01`)→**200 `{items:[]}`**. Else 500 `'Failed to load kind settings'`.
- **PATCH /dictionaries/kind-settings** (`customers.settings.manage`) — body `{kind(1–100 req), selectionMode?, visibleInTags?, sortOrder?(int≥0)}`. Mutation-guard resourceKind `customers.settings`. Command `customers.dictionaryKindSettings.upsert` (defaults selectionMode 'single', visibleInTags true, sortOrder 0). **Emits CRUD side-effects** (events `customers`/`dictionary_kind_setting`, persistent, indexer `customers:customer_dictionary_kind_setting`). **200** `{id,kind,selectionMode,visibleInTags,sortOrder}`. 500 `'Failed to update kind setting'`.

Settings routes (each resolves own context; 401 `'Unauthorized'`, **400 `'Organization context is required'`** if no org):
- **address-format** (`customers.settings.manage`): GET **200** `{addressFormat: record??'line_first'}`. PUT `customerSettingsUpsertSchema` scoped+`{addressFormat: enum('line_first','street_first')}`→command `customers.settings.save`, **200** `{addressFormat}`. No mutation-guard.
- **dictionary-sort-modes** (`customers.settings.manage`): GET **200** `{dictionarySortModes: Record<routeKind,sortMode>}` (invalid keys/modes dropped). PATCH `{dictionarySortModes}` merged→mutation-guard→command `customers.settings.save_dictionary_sort_modes`→invalidates dictionary cache for all builtin+changed kinds→**200** `{dictionarySortModes}`.
- **stuck-threshold** (`customers.deals.manage` — note different feature): GET **200** `{stuckThresholdDays: record??14}`. PUT `customerStuckThresholdUpsertSchema` scoped+`{stuckThresholdDays: int 1–365}`→mutation-guard→command `customers.settings.save_stuck_threshold`, **200** `{stuckThresholdDays}`.

### Dashboard widgets

All GET, auth `dashboards.view` + widget feature; `resolveWidgetScope` (401 `dashboards.errors.unauthorized`, 400 `dashboards.errors.tenant_required`/`organization_required`). Invalid query→400 `{error:'Invalid query parameters'}`. Common query: `limit`(1–20,def5), tenantId?, organizationId?.
- **customer-todos** (`customers.widgets.todos`): unified flag→canonical, else merge legacy+canonical (dedup by bridgeIds). **200** `{items:[{id,todoId,todoSource,todoTitle|null,createdAt,organizationId|null,_integrations?,entity{id,displayName,kind,ownerUserId} passthrough}]}`. 500 `customers.widgets.todos.error`.
- **new-customers** (`customers.widgets.new-customers`): extra `kind?(person|company)`. `CustomerEntity` where tenant, deletedAt null, org?, kind?, orderBy createdAt desc. **200** `{items:[{id,displayName,kind,organizationId,createdAt(ISO),ownerUserId|null}]}`.
- **new-deals** (`customers.widgets.new-deals`): `CustomerDeal` orderBy createdAt desc. **200** `{items:[{id,title,status,organizationId,createdAt(ISO),ownerUserId|null,valueAmount:string|null,valueCurrency:string|null}]}`.
- **next-interactions** (`customers.widgets.next-interactions`): extra `includePast?(true|false)`. `CustomerEntity` nextInteractionAt (includePast?`{$ne:null}`:`{$gte:now}`), orderBy asc. **200** `{items:[{id,displayName,kind,organizationId,nextInteractionAt(ISO|null),nextInteractionName|null,nextInteractionIcon|null,nextInteractionColor|null,ownerUserId|null}], now(ISO)}`.

---

## Entities (25 tables)

All PKs `id uuid not null default gen_random_uuid()`. `created_at`/`updated_at` `timestamptz not null` (no DB default; ORM `onCreate`/`onUpdate`). Tenancy `organization_id uuid not null` + `tenant_id uuid not null` **except** `customer_deal_people`/`customer_deal_companies` (join tables — no tenancy cols). Soft delete = `deleted_at timestamptz null` where noted. Source of truth = `data/entities.ts` reconciled with `migrations/**` (migrations win on DDL).

**Migration-vs-entity discrepancies (migrations authoritative):** (1) `customer_entities` 4 indexes `idx_ce_tenant_org_person_id`/`…_company_id`/`idx_ce_tenant_person_id`/`…_company_id` are declared partial-by-kind in decorators but created as **plain b-tree** (`(tenant_id,id)` ×2, `(tenant_id,organization_id,id)` ×2, NO `WHERE`, NO kind col). (2) `customer_addresses.latitude/longitude` = `real` (float4). (3) `customer_pipeline_stages` physical cols `name`/`position` map to props `label`/`order`. (4) `customer_todo_links.todo_source` default finalized `'customers:interaction'` (was `'example:todo'`).

| # | entity / table | key columns (beyond id + tenancy + timestamps) | soft-del | unique / notable |
|---|---|---|---|---|
| 1 | CustomerEntity `customer_entities` | kind(text NN — property `type`), display_name(text NN), description, owner_user_id, primary_email, primary_phone, status, lifecycle_stage, source, temperature, renewal_quarter, next_interaction_at, next_interaction_name/ref_id/icon/color, is_active(bool def true) | ✓ | `customer_entities_org_tenant_kind_idx(org,tenant,kind)` + 4 plain indexes |
| 2 | CustomerPersonProfile `customer_people` | first_name, last_name, preferred_name, job_title, department, seniority, timezone, linked_in_url, twitter_url; entity_id(NN), company_entity_id(null) | — | **UNIQUE entity_id**; FK entity_id→customer_entities (ON UPDATE CASCADE); company_entity_id FK **ON DELETE SET NULL** |
| 3 | CustomerCompanyProfile `customer_companies` | legal_name, brand_name, domain, website_url, industry, size_bucket, annual_revenue(numeric 16,2); entity_id(NN) | — | **UNIQUE entity_id**; FK→customer_entities |
| 4 | CustomerPersonCompanyLink `customer_person_company_links` | is_primary(bool def false); person_entity_id(NN), company_entity_id(NN) | ✓ | **partial UNIQUE (person,company) WHERE deleted_at IS NULL**; scope/person/company idx |
| 5 | CustomerPersonCompanyRole `customer_person_company_roles` | person_entity_id, company_entity_id, role_value(text); created_at only | — | **UNIQUE (person,company,role_value)** |
| 6 | CustomerCompanyBilling `customer_company_billing` | entity_id(NN), bank_name, bank_account_masked, payment_terms, preferred_currency | — | **UNIQUE entity_id** (1 per company) |
| 7 | CustomerEntityRole `customer_entity_roles` | entity_type(text), entity_id(uuid), user_id(uuid), role_type(text) | ✓ | **partial UNIQUE (entity_type,entity_id,role_type) WHERE deleted_at IS NULL**; entity/scope idx; no FKs |
| 8 | CustomerAddress `customer_addresses` | name, purpose, company_name, address_line1(NN), address_line2, city, region, postal_code, country, building_number, flat_number, latitude/longitude(real), is_primary(bool def false); entity_id(NN) | — | entity idx; FK→customer_entities |
| 9 | CustomerTag `customer_tags` | slug(text), label(text), color, description | — | **UNIQUE (org,tenant,slug)** |
| 10 | CustomerTagAssignment `customer_tag_assignments` | tag_id, entity_id; created_at only | — | **UNIQUE (tag,entity)**; entity idx |
| 11 | CustomerLabel `customer_labels` | user_id(uuid), slug, label | — | **UNIQUE (user_id,tenant,org,slug)**; scope idx |
| 12 | CustomerLabelAssignment `customer_label_assignments` | user_id, label_id, entity_id; created_at only | — | **UNIQUE (label,entity)**; FK label_id→customer_labels, entity_id→customer_entities |
| 13 | CustomerDeal `customer_deals` | title(NN), description, status(text def 'open'), pipeline_stage, pipeline_id(uuid), pipeline_stage_id(uuid), value_amount(numeric 14,2), value_currency, probability(int), expected_close_at, owner_user_id, source, closure_outcome(won\|lost), loss_reason_id(uuid), loss_notes | ✓ | `customer_deals_closure_stats_idx(org,tenant,closure_outcome,updated_at)`; pipeline refs plain uuid (no FK) |
| 14 | CustomerDealStageTransition `customer_deal_stage_transitions` | pipeline_id(NN), stage_id(NN), stage_label(NN), stage_order(int NN), transitioned_at(onCreate), transitioned_by_user_id, is_active(bool def true), deal_id | ✓ | **UNIQUE (deal_id,stage_id)**; FK deal_id→customer_deals |
| 15 | CustomerDealPersonLink `customer_deal_people` (**no tenancy**) | role(text), created_at only, deal_id, person_entity_id | — | **UNIQUE (deal,person)**; FKs to customer_deals/customer_entities |
| 16 | CustomerDealCompanyLink `customer_deal_companies` (**no tenancy**) | created_at only, deal_id, company_entity_id | — | **UNIQUE (deal,company)** |
| 17 | CustomerActivity `customer_activities` | activity_type(NN), subject, body, occurred_at, author_user_id, appearance_icon/color, entity_id(NN), deal_id | — | entity/occurred idx; FK deal_id **ON DELETE SET NULL** |
| 18 | CustomerInteraction `customer_interactions` | interaction_type(NN), title, body, status(text def 'planned'), scheduled_at, occurred_at, priority, author_user_id, owner_user_id, appearance_icon/color, source, deal_id(plain uuid no FK), duration_minutes, location, all_day, recurrence_rule, recurrence_end, participants(jsonb), reminder_minutes, visibility, linked_entities(jsonb), guest_permissions(jsonb), external_message_id(uuid), channel_provider_key, pinned(bool def false), entity_id(NN) | ✓ | partial UNIQUE email_dedupe (entity_id,external_message_id) WHERE not null & not deleted; partial email_visibility idx; type/status idx |
| 19 | CustomerComment `customer_comments` | body(NN), author_user_id, appearance_icon/color, entity_id(NN), deal_id | ✓ | entity/created idx; FK deal_id **ON DELETE SET NULL** |
| 20 | CustomerTodoLink `customer_todo_links` | todo_id(NN), todo_source(text def **'customers:interaction'**), created_by_user_id, created_at only, entity_id(NN) | — | **UNIQUE (entity,todo_id,todo_source)** |
| 21 | CustomerPipeline `customer_pipelines` | name(NN), is_default(bool def false) | — | org/tenant idx |
| 22 | CustomerPipelineStage `customer_pipeline_stages` | pipeline_id(NN plain uuid), **col `name`→prop `label`** (NN), **col `position`→prop `order`** (int def 0) | — | (pipeline_id,position) idx |
| 23 | CustomerSettings `customer_settings` | address_format(text def 'line_first'), stuck_threshold_days(int def 14), dictionary_sort_modes(jsonb) | — | **UNIQUE (org,tenant)** |
| 24 | CustomerDictionaryEntry `customer_dictionary_entries` | kind(text NN), value(NN), normalized_value(NN), label(NN), color, icon | — | **UNIQUE (org,tenant,kind,normalized_value)**; scope idx |
| 25 | CustomerDictionaryKindSetting `customer_dictionary_kind_settings` | kind(NN), selection_mode(text def 'single'), visible_in_tags(bool def true), sort_order(int def 0) | — | **UNIQUE (org,tenant,kind)** |

**Soft-delete tables:** customer_entities, customer_deals, customer_deal_stage_transitions, customer_interactions, customer_comments, customer_person_company_links, customer_entity_roles. All others hard-delete.
**FK on-delete SET NULL:** customer_people.company_entity_id, customer_activities.deal_id, customer_comments.deal_id. All other FKs `ON UPDATE CASCADE` default on-delete. Plain-uuid-NO-FK: customer_interactions.deal_id, customer_deals.pipeline_id/pipeline_stage_id, customer_pipeline_stages.pipeline_id, customer_todo_links.todo_id, customer_entity_roles.*.
**Cross-module extension** (`data/extensions.ts`, no raw FK): `customers:customer_interaction.external_message_id` → `communication_channels:message_channel_link.id`.
**Data-layer guard** (`data/guards.ts`): `MutationGuard customers.optimistic-lock` (targetEntity `*`, update/delete, priority 100) → HTTP **409** `{error: OPTIMISTIC_LOCK_CONFLICT_ERROR, code: OPTIMISTIC_LOCK_CONFLICT_CODE, currentUpdatedAt, expectedUpdatedAt}`; controlled by `OPTIMISTIC_LOCK_ENV_VAR`. See specs/03.

Known dictionary `kind` values: status, source, lifecycle_stage, address_type, activity_type, deal_status, pipeline_stage, job_title, industry, temperature, renewal_quarter, person_company_role (+ custom `/^[a-z0-9]+(?:[-_][a-z0-9]+)*$/`). `pipeline_stage` color tones: success, warning, info, error, neutral, brand, pink.

---

## Custom entities & field sets

`ce.ts` registers **5 CE entities** using **4 distinct field-set constants** (`customer_interaction` reuses `CUSTOMER_ACTIVITY_CUSTOM_FIELDS`). All `showInSidebar:false`; activity+interaction `defaultEditor:false`. Installed via `ensureCustomFieldDefinitions` at `organizationId: null` (tenant-global). No field is `required` (cf.* default false).

| CE id | labelField | field set | fields (key · kind · opts) |
|---|---|---|---|
| `customers:customer_person_profile` | displayName | CUSTOMER_PERSON_CUSTOM_FIELDS | buying_role (select, filterable: economic_buyer/champion/technical_evaluator/influencer); preferred_pronouns (text); newsletter_opt_in (boolean, default false) |
| `customers:customer_company_profile` | displayName | CUSTOMER_COMPANY_CUSTOM_FIELDS | relationship_health (select, filterable: healthy/monitor/at_risk); renewal_quarter (select, filterable: Q1/Q2/Q3/Q4); executive_notes (multiline, listVisible:false); customer_marketing_case (boolean, default false) |
| `customers:customer_deal` | title | CUSTOMER_DEAL_CUSTOM_FIELDS | competitive_risk (select, filterable: low/medium/high); implementation_complexity (select: light/standard/complex); estimated_seats (integer, filterable); requires_legal_review (boolean, default false) |
| `customers:customer_activity` | subject | CUSTOMER_ACTIVITY_CUSTOM_FIELDS | engagement_sentiment (select, filterable: positive/neutral/negative); shared_with_leadership (boolean, default false); follow_up_owner (text) |
| `customers:customer_interaction` | title | CUSTOMER_ACTIVITY_CUSTOM_FIELDS (reused) | (same 3 fields as activity) |

Note: routes reference indexer/CF entity ids `customers:customer_entity`, `customers:customer_address`, `customers:customer_tag`, `customers:customer_comment`, `customers:customer_deal`, `customers:customer_interaction` — some are query-index entity types (not CE registrations). CE registrations are the 5 above.

---

## Notifications

`notifications.ts` — 2 types (module `customers`, both `expiresAfterHours:168`):

| type id | severity | icon | action | link | triggered by |
|---|---|---|---|---|---|
| customers.deal.won | success | trophy | view | /backend/customers/deals/{sourceEntityId} | subscriber `deal-closure-notification` |
| customers.deal.lost | warning | x-circle | view | /backend/customers/deals/{sourceEntityId} | subscriber `deal-lost-notification` |

Delivered via `lib/dealClosureNotification.deliverDealClosureNotification`. Declare-now per specs/10 even if delivery deferred.

---

## Events

`events.ts` = `createModuleEvents({moduleId:'customers', events})` — **51 events**. Standard CRUD triples (`created/updated/deleted`, category `crud`) for: person, company, deal, comment, address, activity, tag, todo, entity_role, label, label_assignment, person_company_link, interaction. Plus lifecycle/special:

| event | category | clientBroadcast | payload / emitted from |
|---|---|---|---|
| customers.deal.won | lifecycle | — | `{id,tenantId,organizationId,ownerUserId,title,valueAmount,valueCurrency}`, persistent — `commands/deals.ts` |
| customers.deal.lost | lifecycle | — | same shape — `commands/deals.ts` |
| customers.interaction.completed | lifecycle | — | `{id,entityId?,status,occurredAt(ISO\|null),…}` — `commands/interactions.ts` |
| customers.interaction.canceled | lifecycle | — | `commands/interactions.ts` |
| customers.interaction.reverted | lifecycle | — | undo of complete/cancel |
| customers.next_interaction.updated | lifecycle | — | `commands/interactions.ts` recompute_next projection |
| customers.email.linked | lifecycle | ✓ | link-channel subscribers |
| customers.email.visibility_changed | lifecycle | ✓ | `{interactionId,previousVisibility,nextVisibility,authorUserId,actorUserId,adminBypass:false,tenantId,organizationId}` — visibility route (best-effort) |
| customers.tag.assigned / .removed | — | — | tag assign/unassign commands |
| customers.person_company_link.created/updated/deleted | crud | ✓ | personCompanyLinks commands |

Standard CRUD events emitted by shared command helpers (`emitCrudSideEffects`, `persistent:true`, `buildPayload`→`{id,organizationId,tenantId}`) — spec/04. Exports `emitCustomersEvent`, type `CustomersEventId`.

**Consumed** (subscribers, auto-id `customers:<subdirs>:<basename>`, all persistent):

| subscriber | consumes | effect |
|---|---|---|
| deal-closure-notification | customers.deal.won | deliver won notification |
| deal-lost-notification | customers.deal.lost | deliver lost notification |
| link-channel-message-received | **communication_channels.message.received** | create `CustomerInteraction` for inbound email (address match / In-Reply-To threading) |
| link-channel-message-sent | **communication_channels.message.sent** | create outbound interaction; visibility from channelMetadata.crmVisibility |

---

## Workers & queues

Both in `lib/bulkDeals.ts`, depend on `progress` module; enqueued by the two bulk-deal routes. Concurrency 1. Payload `{progressJobId, ids, ownerUserId|pipelineStageId, scope:{organizationId,tenantId,userId}}`.

| queue name (const) | worker id | concurrency | behavior |
|---|---|---|---|
| `customers-deals-bulk-update-owner` (CUSTOMERS_DEALS_BULK_UPDATE_OWNER_QUEUE) | customers:deals-bulk-update-owner | 1 | `runBulkDealUpdate`→per-id `commandBus.execute('customers.deals.update')`, collects failed `{id,message}`, completes/fails progress job |
| `customers-deals-bulk-update-stage` (CUSTOMERS_DEALS_BULK_UPDATE_STAGE_QUEUE) | customers:deals-bulk-update-stage | 1 | pre-verifies stage (`verifyPipelineStageExists`→`BulkDealsPreflightError('pipeline_stage_not_found')`) then same as owner |

Queue concurrency default = env `CUSTOMERS_QUEUE_CONCURRENCY` or 3 (`getCustomersQueue`); workers pin concurrency 1.

---

## ACL features

`acl.ts` — 21 features, all `module:'customers'`. `dependsOn` in parens.

| id | title | dependsOn |
|---|---|---|
| customers.people.view | View people | — |
| customers.people.manage | Manage people | people.view |
| customers.companies.view | View companies | — |
| customers.companies.manage | Manage companies | companies.view |
| customers.deals.view | View deals | people.view |
| customers.deals.manage | Manage deals | deals.view |
| customers.activities.view | View activities | — |
| customers.activities.manage | Manage activities | activities.view |
| customers.settings.manage | Manage customer settings | — |
| customers.pipelines.view | View pipelines | — |
| customers.pipelines.manage | Manage pipelines | pipelines.view |
| customers.widgets.todos | Use customer todos widget | activities.view |
| customers.widgets.next-interactions | Use customer next interactions widget | interactions.view |
| customers.widgets.new-customers | Use customer new customers widget | people.view |
| customers.widgets.new-deals | Use customer new deals widget | deals.view |
| customers.interactions.view | View interactions | — |
| customers.interactions.manage | Manage interactions | interactions.view |
| customers.roles.view | View entity roles | — |
| customers.roles.manage | Manage entity roles | roles.view |
| customers.email.compose | Compose / send emails from CRM | people.view |
| customers.email.view_private | View other users' private emails (reserved — **INERT in v1**) | interactions.view |

**Preserve verbatim:** `customers.email.view_private` is declared but has NO effect (strict owner-only email model, no admin bypass) — register but grant no behavior. `todos` GET metadata references **`customers.view`** and `analytics.ts` references `customers.view` — this is **NOT a declared acl feature** (only granular `*.view` exist). Do NOT "fix" it; preserve as-is.

---

## Setup & seeding

`setup.ts` `ModuleSetupConfig`. `index.ts` metadata: `{name:'customers', title:'Customer Relationship Management', version:'0.1.0', description:'Core CRM capabilities for people, companies, deals, and activities.', author:'Open Mercato Team', license:'Proprietary', ejectable:true}`; no `requires` array (deps implicit via imports). `import './commands'` registers all handlers at load.

**seedDefaults(ctx={tenantId,organizationId})**, in order:
1. `seedCustomerDictionaries` (cli.ts) — see seed-data below.
2. `seedCurrencyDictionary` — generic `currency` Dictionary (key='currency', name='Currencies', isSystem=true) + one DictionaryEntry per ISO-4217 code from `Intl.supportedValuesOf('currency')`, priority-ordered EUR, USD, GBP, PLN first, labels via `Intl.DisplayNames`.
3. `seedDefaultPipeline` — one `CustomerPipeline {name:'Default Pipeline', isDefault:true}` + 8 `CustomerPipelineStage` from `PIPELINE_STAGE_DEFAULTS` (label + order 0..7). Idempotent (skips if a default pipeline exists).
4. `ensureCustomerCustomFieldDefinitions(em, tenantId)` — installs CF defs at `organizationId:null`.
5. `seedInteractionFeatureToggles(em)` — 3 `FeatureToggle` (category `customers`): `customers.interactions.unified` (bool def **false**), `customers.interactions.legacy-adapters` (bool def **true**), `customers.interactions.external-sync` (bool def **false**).
6. `customer_role_type` dictionary from `DEFAULT_CUSTOMER_ROLE_TYPES`: sales_owner (Sales Owner, #2563eb, lucide:briefcase), service_owner (Service Owner, #16a34a, lucide:headphones), account_manager (Account Manager, #f59e0b, lucide:user-check).

**seedExamples(ctx)** → `seedCustomerExamples` — see seed-data.

**defaultRoleFeatures:** `admin`→`['customers.*']` (wildcard). `employee`→14 explicit: people.view/manage, companies.view/manage, deals.view/manage, activities.view/manage, pipelines.view, interactions.view, widgets.todos, widgets.next-interactions, widgets.new-customers, widgets.new-deals, roles.view, roles.manage, email.compose (NOT settings.manage, interactions.manage, pipelines.manage, email.view_private).

### Seed-data (for a faithful .NET seeder)

**Dictionaries** (`cli.ts seedCustomerDictionaries` via `ensureDictionaryEntry`, tenant/org-scoped `{value,label,color?,icon?}`):
- `status`: active, inactive, pending, archived
- `lifecycle_stage`: lead, prospect, customer, subscriber, churned, other
- `source`: linkedin, email, web_form, referral, customer_referral, partner_referral, event, cold_outreach, facebook, typeform, other
- `address_type`: office, work, billing, shipping, home
- `activity_type`: call, email, meeting, note, task
- `job_title`: Director of Operations, VP of Partnerships, Founder & Principal, Senior Project Manager, Chief Revenue Officer, Director of Retail Partnerships
- `deal_status`: open, closed, win, loose, in_progress
- `pipeline_stage`: opportunity, marketing_qualified_lead, sales_qualified_lead, offering, negotiations, win, loose, stalled
- `industry`: Renewable Energy, Software, Interior Design, SaaS, E-commerce, Healthcare, Manufacturing, Logistics, Financial Services, Retail, Hospitality, Energy, Media (13)
- `temperature`: hot, high, medium, low, cold
- `renewal_quarter`: current year + 2 future years × Q1–Q4, value `${year}_q${q}`
- `person_company_role`: decision_maker, influencer, budget_holder, technical_evaluator, primary_contact, end_user
- `customer_role_type`: (seeded in setup.ts, not here) sales_owner, service_owner, account_manager

**CustomerTag free-pool** (`CUSTOM_TAG_SEED_DEFAULTS`, NOT a dictionary — rows in `customer_tags`), 14 slugs: architecture, hospitality, retail, healthcare, tech, manufacturing, decision-maker, influencer, end-user, blocker, vip, strategic-account, reference-customer, case-study-candidate.

**Default pipeline stages** (`PIPELINE_STAGE_DEFAULTS`, order 0..7): opportunity, marketing_qualified_lead, sales_qualified_lead, offering, negotiations, win, loose, stalled (labels + order). (Note: same tokens as the `pipeline_stage` dictionary.)

**Currency dictionary:** generic module, key `currency`, all ISO-4217, EUR/USD/GBP/PLN prioritized.

**Feature toggles:** 3 rows above.

**seedExamples** (`CUSTOMER_EXAMPLES`, idempotent guard by example deal titles) — 3 companies with people/deals/activities/notes/addresses + custom-field values (written via `DefaultDataEngine.setCustomFields`):
- **Brightside Solar** (Renewable Energy, customer) — people mia-johnson, daniel-cho; deals redwood-residences (in_progress/negotiations $185k), sunset-lofts-battery (open/offering $82k).
- **Harborview Analytics** (Software, prospect) — people arjun-patel, lena-ortiz; deals blue-harbor-pilot (win $96k), midwest-outfitters (open/opportunity $210k).
- **Copperleaf Design Co.** (Interior Design, customer) — people taylor-brooks, naomi-harris; deals wanderstay-renovation (in_progress/sales_qualified_lead $145k), cedar-creek-retreat (loose $98k).

---

## DI services

`di.ts` `register(container)`:
- Binds `CustomerEntity`, `CustomerAddress`, `CustomerInteraction` as values (`asValue`).
- At module load (before register) `registerOptimisticLockReaders({...})` for the polymorphic table (kind can't auto-derive): `customers.company`→`readCustomerCompanyUpdatedAt` (kind='company'); `customers.person` AND `customers.people`→`readCustomerPersonUpdatedAt` (kind='person') — both keys registered because the CRUD factory does not singularize "people"→"person".
- No module-local mutation-guard binding (resolves global store). See specs/03.

Command handlers registered via `import './commands'` (spec/04 command bus): people, companies, deals, activities, comments, addresses, tags(+assign/unassign), todos, settings, dictionaries, pipelines, pipeline-stages, interactions(+complete/cancel/recompute_next), entity-roles, labels(+assign/unassign), personCompanyLinks, dictionaryKindSettings. Key rules cited in route detail above.

---

## CLI commands

`cli.ts` default `customersCliCommands`: `seed-dictionaries`, `seed-examples`, `seed-stresstest` (default count 6000, `--lite`), `interactions:backfill` (legacy activity/todo → interaction migration, batch 100/50; refreshes `query_index.coverage.refresh`).

---

## Configuration

| env var / config | default | used by |
|---|---|---|
| CUSTOMERS_QUEUE_CONCURRENCY | 3 | `getCustomersQueue` (workers pin concurrency 1) |
| OPTIMISTIC_LOCK_ENV_VAR (modes off/all/per-entity) | — | `customers.optimistic-lock` guard (specs/03) |
| Feature toggle `customers.interactions.unified` | false | canonical vs legacy interaction model |
| Feature toggle `customers.interactions.legacy-adapters` | true | activities/todos bridge routes (410 when off) |
| Feature toggle `customers.interactions.external-sync` | false | external email sync |
| Dictionary cache TTL `DICTIONARY_CACHE_TTL_MS` | 300000 (5 min) | dictionary list caching |

---

## Not ported

- **UI** (out of scope): `backend/**` (page.tsx/page.meta.ts/hooks), `frontend/`, `components/**` (detail/, create/, kpi/, linking/, list/), `widgets/**` (`*.client.tsx` dashboard + injection widgets), `ai-tools/**`, `ai-tools.ts`, `ai-agents.ts`, `ai-agents-context.ts`, `agentic/`, `i18n/*.json`. NOTE: dashboard-widget **API routes** (`api/dashboard/widgets/*/route.ts`) ARE in HTTP scope.
- **Deferred data-engine surfaces:** `search.ts` (6 search index sources), `vector.ts` (`vectorConfig=null`), `message-objects.ts` (3 types), `inbox-actions.ts` (depends inbox_ops), `encryption.ts` (field masking — masking behavior noted where relevant), `data/enrichers.ts`, `analytics.ts` (2 entity configs, requiredFeatures `customers.view`/`customers.deals.view`).
- **Tests** (`__tests__`, `__integration__`, 60+ TC-CRM specs): NOT ported but READ to pin behavior (SPEC-018 flush, undo custom-fields, POST 201-vs-200 mode, table-missing tolerance, todo merge/dedup, tenant scoping).
- **Deprecated bridges** `api/activities`, `api/todos`: DEPRECATED (410 when `legacyAdapters` off, sunset 2026-06-30) — port only if compatibility flags are in scope.
- `x-om-operation` undo/redo header population is an artifact of the command-bus + audit/operation-log subsystem — declare the header, the undo engine is a separate concern (audit_logs).
- Snapshots `.snapshot-*.json` are MikroORM schema snapshots (not authoritative DDL).

---

## Porting checklist

- [ ] Port prerequisites first: **entities/custom-fields**, **dictionaries**, **currencies** (all lack ports). Confirm directory/auth/feature_toggles/query_index/progress/dashboards available.
- [ ] **Phase 1 — migrations + entities:** 25 tables. Verify migration-vs-decorator discrepancies (plain indexes on customer_entities; `real` lat/long; `name`/`position`→`label`/`order`; todo_source default; partial-unique on person_company_links & entity_roles). Wire polymorphic `customer_entities.kind` discriminator (ambiguity #1).
- [ ] Phase 1 commands: people, companies, addresses, tags(+assign/unassign), labels(+assign/unassign), entity-roles, personCompanyLinks — with 201/200 + error-body contracts.
- [ ] Phase 1 routes: people(+detail/companies/links/enriched/roles/check-phone), companies(+detail/people/roles), addresses, tags, labels, assignable-staff (308 redirect).
- [ ] **Phase 2:** customer_settings/dictionary tables; dictionary kind-map + cache + inheritance; settings + dictionaries + kind-settings routes (201/200-by-mode, table-missing tolerance, `role_type_in_use` 409).
- [ ] **Phase 3:** deals + pipeline + interactions + comments tables; deals CRUD/detail/aggregate/summary/stats + bulk workers (queues `customers-deals-bulk-update-owner`/`-stage`, progress jobs); interactions CRUD (cursor GET) + complete/cancel/visibility/conflicts/counts/tasks; comments; deprecated activities/todos bridges; pipelines/stages/reorder. Emit deal.won/lost + interaction lifecycle events.
- [ ] **Phase 4:** 4 dashboard-widget endpoints.
- [ ] Declare-now surface (specs/10): 21 ACL features (incl inert `email.view_private`), 2 notification types, 5 CE registrations / 4 field sets, 51 events, optimistic-lock guard.
- [ ] Subscribers: 2 deal-closure + 2 communication_channels link subscribers.
- [ ] Setup/seed: dictionaries + currency + default pipeline + CF defs + 3 toggles + role types + example dataset (see seed-data). defaultRoleFeatures admin/employee.
- [ ] DI: optimistic-lock readers (`customers.company`/`customers.person`/`customers.people`); value bindings.
- [ ] CLI: seed-dictionaries, seed-examples, seed-stresstest, interactions:backfill.
- [ ] Tests + `om-verify-parity` run.

---

## Upstream ambiguities

1. **`customer_entities` discriminator** — routes filter `kind:'person'|'company'` but the decorator column is `type` (property `type` at entities.ts:42; `kind` is the ORM property alias in queries; migration-created plain indexes carry no `kind` column). Confirm the physical column name against `migrations/**` before porting the person/company split. Source treats `kind` as the query alias; port must reproduce whichever physical column the migration created.
2. **Deal-status literals diverge across routes** — `people/[id]/companies/enriched` + `deals/aggregate` use `'win'`/`'loose'`; `companies/[id]` KPIs + `deals/summary` use both `'win'`/`'loose'` (status) AND `'won'`/`'lost'`/`'closed'` (closure_outcome). Preserve BOTH signals verbatim; do NOT normalize.
3. **assign 201-vs-200 asymmetry** — `labels/assign` returns 201-vs-200 based on command `result.created`; `tags/assign` always 201; dictionary POST 201-vs-200 by command `mode` (`unchanged`→200, not 204); deletes return `{success:true}`/`{ok:true}` (200), not 204. Keep exactly.
4. **`customers.view` is not a real ACL feature** — referenced by `todos` GET metadata and `analytics.ts` `requiredFeatures`, but `acl.ts` declares only granular `*.view`. Preserve verbatim (do not add the feature, do not rewrite the reference).
5. **Entity count** — task brief said "26"; source `grep '@Entity(' data/entities.ts` = **25** physical tables (verified). This contract documents all 25. The wiring analyst's "24" undercounted; the "26" overcounted.
6. **`customers.email.view_private`** declared but INERT in v1 — register the feature, grant no effect (strict owner-only email visibility, no admin bypass).
7. **Interactions GET cursor envelope** `{items,nextCursor}` is NOT the standard paged envelope (tasks/activities/comments use paged). Do not homogenize.
