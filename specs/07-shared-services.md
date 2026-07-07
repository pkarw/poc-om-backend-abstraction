# Shared Services & Cross-Cutting Helpers — Requirements Spec

> Derived from upstream commit adc9da27759e357febe9ed8d4b7182040d127349. Source analysis: ../upstream/analysis/07-shared-services.md

## Scope

This spec covers the cross-cutting "shared services" layer that nearly every business module depends on:

1. **Shared helper library** (upstream `packages/shared`) — env-flag parsing, Redis URL resolution, SSRF-safe outbound HTTP, Standard-Webhooks signing, rate limiting, i18n + localization overlay, token-search config/tokenizer, operation metadata (`x-om-operation`), canonical HTTP error mapping, wildcard matching, and the cross-module type contracts (module registry, typed events, search configs, notification types).
2. **Small core service modules**: `translations`, `notifications`, `attachments` (+ S3 storage driver), `audit_logs` (undo/redo), `dictionaries`, `configs` (`module_configs`), `feature_toggles`, `progress`.
3. **The `search` package** (strategy-pattern search: tokens / Meilisearch fulltext / vector) and the per-module `search.ts` declaration contract.
4. **Summary-level requirements** for `business_rules` and `workflows` (full specs deferred to their module port; their tables and route surfaces are inventoried here).

Anything observable by an API client or by a Node process sharing the same Postgres/Redis (HTTP shapes, status codes, table/column names, cache keys, queue names, advisory-lock strings, signature formats) is normative (MUST). Helper internals only need behavioral equivalence (SHOULD), implemented idiomatically per language.

The module inventory in the source analysis (37 core modules + 20 packages with surface counts and dependency graph) feeds the porting tracker; it is reference material, not requirements.

Out of scope: queue/event mechanics (spec 04), auth/RBAC and JWT (spec 05), CRUD factory + query index (spec 03), HTTP routing conventions (spec 02).

## Requirements

### Shared helpers

- **SHAREDSERVICES-R1** — Redis URL resolution MUST follow `getRedisUrl(prefix?)` semantics: `<PREFIX>_REDIS_URL` (e.g. `QUEUE_REDIS_URL`, `CACHE_REDIS_URL`) → `REDIS_URL` → null. A throwing variant MUST exist for callers where Redis is mandatory. The port MUST NOT default to localhost.
- **SHAREDSERVICES-R2** — Boolean env parsing MUST use the shared token sets (trimmed, case-insensitive): true = `1,true,yes,y,on,enable,enabled`; false = `0,false,no,n,off,disable,disabled`; unrecognized tokens fall back to the caller's default.
- **SHAREDSERVICES-R3** — All outbound HTTP to user-supplied URLs (webhooks, integrations) MUST pass an SSRF guard equivalent to `assertSafeOutboundUrl`/`safeOutboundFetch`: block private/reserved IPv4 and IPv6 ranges, block metadata/localhost hostnames, and pin DNS — the IP validated during the check MUST be the IP actually connected to (no TOCTOU re-resolution).
- **SHAREDSERVICES-R4** — Webhook signing MUST implement the Standard Webhooks spec exactly as upstream: signature = `v1,` + **base64** (not hex) HMAC-SHA256 over the string `${msgId}.${timestamp}.${body}` where `timestamp` is unix **seconds**; emitted headers `webhook-id`, `webhook-timestamp`, `webhook-signature`. During secret rotation the port MUST dual-sign, joining signatures with a single space in `webhook-signature`.
- **SHAREDSERVICES-R5** — The port MUST provide a rate limiter with `memory` and `redis` strategies configured by window (`windowMs`) and max count, selectable per call site, with Redis URL resolution per R1.
- **SHAREDSERVICES-R6** — Wildcard matching for event patterns and feature globs MUST be behaviorally identical to `matchWildcardPattern` (`shared/lib/patterns/wildcard.ts`) — a shared fixture table between the port and upstream MUST produce identical booleans.
- **SHAREDSERVICES-R7** — A canonical throw-to-HTTP error type equivalent to `CrudHttpError(status, body)` MUST exist and be used by service code so that services can dictate exact status + JSON body from any depth.
- **SHAREDSERVICES-R8** — Undoable mutations MUST emit the `x-om-operation` response header only when the action log entry has `id`, `undoToken`, and `commandId`; value = base64(JSON `{ id, undoToken, commandId, actionLabel, resourceKind, resourceId, executedAt }`) per `serializeOperationMetadata`.
- **SHAREDSERVICES-R9** — Token-search config MUST be resolved from env: `OM_SEARCH_ENABLED` (default true), `OM_SEARCH_MIN_LEN` (default 3, min 1), `OM_SEARCH_ENABLE_PARTIAL` (default true), `OM_SEARCH_HASH_ALGO` (`sha256`|`sha1`|`md5`, default sha256), `OM_SEARCH_STORE_RAW_TOKENS` (default false), `OM_SEARCH_FIELD_BLOCKLIST` (comma list). The effective blocklist MUST always include `password`, `token`, `secret`, `hash` regardless of the env value; entries are lower-cased and deduped.
- **SHAREDSERVICES-R10** — Token hashing (`hashToken`) and tokenization (`tokenizeText`) MUST produce byte-identical hashes to upstream for the same config, since token indexes in Postgres are shared across runtimes.
- **SHAREDSERVICES-R11** — Phone validation SHOULD accept 7–15 digits with structured reason codes; slugify, relative-time formatting, `trimToUndefined` and similar primitives SHOULD be behaviorally equivalent where their output is persisted or returned over HTTP (e.g. attachment file slugs).
- **SHAREDSERVICES-R12** — Reads of possibly-encrypted entities MUST go through decryption-aware find wrappers (equivalent of `findWithDecryption`/`findOneWithDecryption`) so tenant data-at-rest encryption is transparent to callers.

### Translations

- **SHAREDSERVICES-R13** — The port MUST store per-record translations in table `entity_translations` as a single JSONB blob `{ [locale]: { [field]: value|null } }` scoped by `(entity_type, entity_id, tenant_id, organization_id)`, with `entity_id` typed **text** (not uuid).
- **SHAREDSERVICES-R14** — All translation reads and writes MUST match tenant/org scope NULL-safely (`tenant_id IS NOT DISTINCT FROM $1 AND organization_id IS NOT DISTINCT FROM $2`); plain `=` comparison is a defect.
- **SHAREDSERVICES-R15** — The translations HTTP API MUST match the Contracts table below, including: DELETE returns **204 with empty body**; GET of a missing record returns 404 `{"error":"Not found"}`; malformed JSON body on PUT returns 400 `{"error":"Invalid JSON body"}` **before** validation; validation failures return 400 `{"error":"Validation failed","details":[...]}`.
- **SHAREDSERVICES-R16** — Translation writes MUST go through the command bus (`translations.translation.save` / `.delete`) so they are undoable and emit `x-om-operation` per R8.
- **SHAREDSERVICES-R17** — PUT body validation MUST enforce: locale keys 2–10 chars, max 50 locales; field keys 1–100 chars; values string (max 10000) or null; `entityType` path param matches `/^[a-z_]+:[a-z_]+$/`.
- **SHAREDSERVICES-R18** — `PUT /api/translations/locales` MUST lower-case, trim and dedupe entries, validate each as ISO 639-1, accept 1–50 entries, and persist via the configs service under module `translations`, key `supported_locales`; `GET /locales` falls back to the built-in locale list when unset.
- **SHAREDSERVICES-R19** — Modules MUST be able to declare translatable fields (upstream: module-root `translations.ts` exporting `Record<'module:entity', string[]>`); all declarations MUST be registered before serving requests, and list endpoints MUST overlay translations onto records for the requested locale (equivalent of `applyTranslationOverlays` + `applyLocalizedContent`), batch-loading translation rows per page.
- **SHAREDSERVICES-R20** — A non-persistent subscriber on event `query_index.delete_one` MUST delete matching `entity_translations` rows (NULL-safe scope per R14); its failures are logged, never thrown.

### Notifications

- **SHAREDSERVICES-R21** — Notifications MUST be stored as per-recipient rows in one `notifications` table, i18n-first: `title_key`/`body_key` + `title_variables`/`body_variables` JSONB with plain `title`/`body` fallback; actions in `action_data` JSONB as `{ actions: NotificationAction[], primaryActionId? }`.
- **SHAREDSERVICES-R22** — `POST /api/notifications` MUST return **201** with body exactly `{ "id": "<uuid>" }`; it requires `recipientUserId` (uuid) and at least one of `titleKey`/`title`.
- **SHAREDSERVICES-R23** — `GET /api/notifications` MUST by default exclude `dismissed` rows (`status != 'dismissed'` unless the caller passes a `status` filter), order by `createdAt DESC`, and return `{ items, total, page, pageSize, totalPages }`.
- **SHAREDSERVICES-R24** — Any `linkHref` or action `href` MUST be a same-origin **relative** path starting with `/`. This MUST be enforced both at request validation and inside the service (`assertSafeNotificationHref`), because the service is callable without HTTP validation.
- **SHAREDSERVICES-R25** — Group-key dedupe: when `groupKey` is set, the port MUST take a Postgres advisory transaction lock `pg_advisory_xact_lock(hashtext(<lockKey>))` with `lockKey` exactly `notifications:{tenantId}:{organizationId or "global"}:{recipientUserId}:{type}:{groupKey}`, then refresh the newest active row (status `unread|read|actioned`) in place instead of inserting; `dismissed` rows are NOT refreshed (a new row is created). Advisory-lock failure MUST degrade to best-effort dedupe, never a request failure.
- **SHAREDSERVICES-R26** — State machine: `read` transitions only from `unread` (idempotent otherwise; the `read` event fires only on actual transition); `dismiss` always sets `dismissed` + `dismissedAt`; `restore` acts only on `dismissed` rows (default target `read`, backfilling `readAt` if null; target `unread` clears `readAt`); `action` sets `actioned`, records `actionTaken`/`actionResult`, backfills `readAt`, and executes the action's bound `commandId` via the command bus (unknown `actionId` → error). Expiry cleanup flips rows with `expires_at < now()` and status not in (`actioned`,`dismissed`) to `dismissed`.
- **SHAREDSERVICES-R27** — Fan-out endpoints MUST exist: `POST /batch` (`recipientUserIds` 1–1000), `POST /role` (`roleId`), `POST /feature` (`requiredFeature`) — recipients resolved by querying auth tables; plus `POST /:id/read|dismiss|restore|action`, `POST /mark-all-read` (bulk update returning count), `GET /unread-count`, and `GET|PUT /settings` for per-user delivery preferences.
- **SHAREDSERVICES-R28** — Async notification creation MUST run through the BullMQ-compatible queue named **`notifications`** with job payload union `{type: 'create'|'create-role'|'create-feature'|'cleanup-expired', ...}` so Node and port workers are interchangeable.
- **SHAREDSERVICES-R29** — The port MUST emit events `notifications.created|read|actioned|dismissed|restored|expired`, and the SSE-bridge events `notifications.notification.created` (payload `{tenantId, organizationId, recipientUserId, notification: NotificationDto}`) and `notifications.notification.batch_created` (`{tenantId, organizationId, recipientUserIds, count}`). `mark-all-read` re-emits `notifications.notification.created` for each updated row (quirky but observable contract).
- **SHAREDSERVICES-R30** — A persistent subscriber on `notifications.created` MUST perform email delivery according to delivery strategies/config and per-user settings.
- **SHAREDSERVICES-R31** — Modules SHOULD be able to declare reusable notification types (upstream module-root `notifications.ts`, `NotificationTypeDefinition`: `type`, `module`, `titleKey`, `icon`, `severity`, `actions[]`, `linkHref` with `{sourceEntityId}` placeholders, `expiresAfterHours`).
- **SHAREDSERVICES-R32** — When only `titleKey` is supplied, the stored `title` column is set to the key string (`input.title || input.titleKey || ''`); DTO consumers prefer `titleKey` + variables when present. Ports MUST replicate this storage behavior.

### Attachments & storage drivers

- **SHAREDSERVICES-R33** — Two tables MUST exist: `attachment_partitions` (code-unique buckets with `storage_driver` `local|s3|legacy-public`, `config_json`, `is_public`, `requires_ocr`, `ocr_model`) and `attachments` (polymorphic `entity_id`/`record_id` **text** columns, tenant/org scope, `partition_code`, `storage_driver`, `storage_path`, `storage_metadata` JSONB holding `{tags[], assignments[]}`, extracted `content` text, `url`). Default partitions `productsMedia` (public) and `privateAttachments` (private fallback) MUST be seeded; default partition resolution: catalog products → `productsMedia`, everything else → `privateAttachments`.
- **SHAREDSERVICES-R34** — Storage MUST be abstracted behind a `StorageDriver` interface — `store({partitionCode, orgId, tenantId, fileName, buffer}) → {storagePath, driverMeta?}`, `read`, `delete`, `toLocalPath → {filePath, cleanup}` — resolved per partition. Local and S3-compatible drivers MUST exist; S3 keys sanitized and path-segmented `tenant_<id>/org_<id>/…`.
- **SHAREDSERVICES-R35** — `POST /api/attachments` (multipart) MUST perform checks in exactly this short-circuit order with these statuses: 401 no org in auth → 400 non-multipart → 413 content-length precheck → 400 missing fields → 400 field-config extension violation → 400 dangerous executable extension → 413 over size limit → 413 tenant quota (sum of `file_size`) → magic-byte MIME sniffing → 400 active content (HTML/SVG scripts) → 400 partition unconfigured → **403 explicit public-partition selection via form field** → 500 driver store failure → transactional insert (attachment + custom fields atomic, 500 on failure). Success: 200 `{ "ok": true, "item": {...} }`.
- **SHAREDSERVICES-R36** — A public partition MUST be reachable via field config or default resolution but MUST NOT be selectable via an explicit `partitionCode` form override (403) — prevents smuggling private files into anonymously-served partitions.
- **SHAREDSERVICES-R37** — `GET /api/attachments/file/:id` MUST be anonymous-capable (route metadata `requireAuth: false`); access enforced by an attachment-access check: public partition → anonymous OK; otherwise tenant/org scoping with 401/403/404 as appropriate; superadmins bypass scope filters. Responses stream bytes with the **sniffed** content type and a safe `Content-Disposition` (inline only for allowlisted renderable types; `?download=1` forces attachment).
- **SHAREDSERVICES-R38** — `DELETE /api/attachments?id=<uuid>` MUST return 200 `{ok:true}` or 404 outside caller scope (scope filter includes both tenant and org), deleting the DB row first, then thumbnails (failure logged, non-fatal), then the driver object.
- **SHAREDSERVICES-R39** — List endpoint `GET /api/attachments?entityId&recordId[&page&pageSize(max 100)]` MUST return items newest-first with `url = /api/attachments/file/<id>` and `thumbnailUrl = /api/attachments/image/<id>/<slug>?width=320&height=320`; pagination applies only when both page params are present. Routes `/image/:id/[...slug]?width&height` (cached thumbnails), `/library` CRUD (`entityId='attachments:library'`), `/partitions` CRUD and `/transfer` MUST exist with upstream semantics.
- **SHAREDSERVICES-R40** — Text extraction for common document types MUST run on upload; LLM OCR (when partition `requires_ocr`) requires `OPENAI_API_KEY` and MUST fall back to plain text extraction with a logged warning when unset. Upload limits/quota/OCR defaults come from env-driven resolvers.

### Audit logs & undo

- **SHAREDSERVICES-R41** — Table `action_logs` MUST record one row per executed command: `command_id`, actor, tenant/org, `action_label`, `action_type`, resource kind/id (+ parent + related), `execution_state` (`done|undoing|undone|failed|redone`), `undo_token`, `command_payload`, `snapshot_before`/`snapshot_after`, `changes_json`, `changed_fields text[]` (GIN indexed), `context_json`, timestamps + `deleted_at`. Table `access_logs` records read access (resource kind/id, `access_type`, `fields_json`).
- **SHAREDSERVICES-R42** — `POST /api/audit_logs/audit-logs/actions/undo` (and `/redo`) with body `{ "undoToken": "..." }` MUST return 200 `{ ok: true, logId }` on success, and **400 `{"error":"Undo token not available"}` for every failure mode** (unknown/consumed token, wrong execution state, actor mismatch, tenant mismatch, org mismatch) — deliberately indistinguishable; do not leak which check failed.
- **SHAREDSERVICES-R43** — Undo authorization MUST be fail-closed: feature `audit_logs.undo_self` required; actor must match unless the caller has `audit_logs.undo_tenant`, which widens scope **within, never across** tenants; a caller with null tenant can never undo tenant-scoped rows; org mismatch is rejected. Undo executes the command's registered undo handler via the command bus and flips `execution_state`.

### Dictionaries

- **SHAREDSERVICES-R44** — Tables `dictionaries` (unique `(organization_id, tenant_id, key)`; `is_system`, `is_active`, `manager_visibility` `default|hidden`, `entry_sort_mode` default `label_asc`) and `dictionary_entries` (unique `(dictionary, organization_id, tenant_id, normalized_value)`; `value`, `normalized_value`, `label`, `color`, `icon`, `position`, `is_default`) MUST exist with these constraints.
- **SHAREDSERVICES-R45** — `/api/dictionaries` MUST support: list, create (201; 409 on duplicate key), `/:dictionaryId` get/put/delete, `/:dictionaryId/entries` CRUD, `/entries/reorder`, `/entries/set-default`. Writes are commands (undoable, R8/R16 semantics). Entry `label` is translatable per R19.

### Configs (module settings)

- **SHAREDSERVICES-R46** — `module_configs` MUST be a **global** (no tenant column) key-value store: unique `(module_id, name)`, `value_json` JSONB. A `ModuleConfigService` equivalent MUST provide `getRecord` / `getValue<T>(moduleId, name, {defaultValue})` / `setValue` / `restoreDefaults(defaults, {force})` / `invalidate`.
- **SHAREDSERVICES-R47** — Config reads MUST be cache-aside with key `module-config:v1:<moduleId>:<name>`, TTL 60 s, invalidation tag `module-config:module:<moduleId>`, and **negative caching** (a `{found:false}` marker cached on miss). `setValue(null)` stores JSON null; `getValue` then falls back to `defaultValue`.
- **SHAREDSERVICES-R48** — The configs module also hosts `upgrade_action_runs` (once-per-scope upgrade actions keyed `(version, actionId, orgId, tenantId)`), `/api/configs/system-status`, cache admin `/api/configs/cache`, and `/api/configs/upgrade-actions`. There is **no** generic tenant-scoped settings CRUD API — module settings routes wrap the config service themselves. Module setup hooks call `restoreDefaults` to seed defaults.

### Feature toggles

- **SHAREDSERVICES-R49** — Tables `feature_toggles` (unique `identifier`; `name`, `category`, `type` `boolean|string|number|json`, `default_value` JSONB, soft-delete) and `feature_toggle_overrides` (unique `(toggle_id, tenant_id)`, JSONB `value`) MUST exist. Feature toggles are runtime flags with tenant overrides — distinct from ACL features (spec 05), despite the shared word "feature".
- **SHAREDSERVICES-R50** — `GET /api/feature_toggles/check/{boolean,string,number,json}?identifier=...` MUST: require auth; for **boolean** checks short-circuit super-admins to `true` with `resolution.source: "override"`, `toggleId: "superadmin"`, `tenantId: "superadmin"` **before** tenant resolution; return 400 `{"error":"Missing required parameter: identifier"}` when identifier missing and 400 `{"error":"Tenant context required. Please select a tenant."}` when tenant missing; return 404 with the `{ok:false, error:{code:'MISSING_TOGGLE', ...}}` result object as body for unknown identifiers; on success return `{ ok: true, value, resolution: { valueType, value, source: 'default'|'override', toggleId, identifier, tenantId } }`.
- **SHAREDSERVICES-R51** — Global CRUD MUST exist under `/api/feature_toggles/global` (+ `/global/:id/override`) and overrides listing under `/api/feature_toggles/overrides`.

### Progress

- **SHAREDSERVICES-R52** — Table `progress_jobs` MUST track long-running jobs: status `pending|running|completed|failed|cancelled`, `progress_percent` smallint, processed/total counts, `eta_seconds`, `heartbeat_at`, cancellation fields, parent/partition fan-out fields, tenant/org scope.
- **SHAREDSERVICES-R53** — Progress math MUST match upstream constants and formulas: heartbeat interval 5000 ms; stale timeout 60 s; percent = `min(100, round(processed/total*100))`, 0 when total is null/0; ETA = `ceil(remaining / rate / 1000)` seconds, null when nothing processed yet.
- **SHAREDSERVICES-R54** — `GET /api/progress/active` MUST first mark stale jobs failed (heartbeat older than 60 s), then return `{ active: Job[], recentlyCompleted: Job[] }`; unauthenticated requests get status **401 with body `{"active":[],"recentlyCompleted":[]}`** (same shape, empty). Jobs CRUD lives at `/api/progress/jobs`, `/api/progress/jobs/:id` (get/cancel).
- **SHAREDSERVICES-R55** — All six progress events (`progress.job.created|started|updated|completed|failed|cancelled`) MUST be declared `clientBroadcast: true` so the SSE bridge (spec 04) pushes them to browsers.

### Search package

- **SHAREDSERVICES-R56** — The search service MUST merge results from up to three strategies — `tokens` (hash-token index in Postgres, always available), `fulltext` (Meilisearch driver), `vector` (pgvector / Qdrant / ChromaDB drivers) — using RRF-style merging with `ResultMergeConfig { duplicateHandling: 'highest_score'|'first'|'merge_scores', strategyWeights, minScore }`.
- **SHAREDSERVICES-R57** — Org scoping MUST honor: `organizationId` (single) wins over `organizationIds`; an **empty `organizationIds` array denies all (zero results)**; `null`/`undefined` means tenant-wide.
- **SHAREDSERVICES-R58** — Modules declare searchable entities via a `SearchModuleConfig` contract (per-entity `entityId`, `enabled`, `strategies`, `priority`, `buildSource(ctx) → { text, fields?, presenter?, links?, checksumSource? }`, `formatResult`, `resolveUrl`, `resolveLinks`, `fieldPolicy { searchable[], hashOnly[], excluded[] }`, `aclFeatures[]`). All configs MUST be registered before serving requests. Entities without `aclFeatures` MUST be fail-closed for data-returning AI tools.
- **SHAREDSERVICES-R59** — Indexing MUST be event-driven: subscribers on `search.index_record` / `search.delete_record` enqueue jobs on the fulltext and vector indexing queues (spec 04 reserved names `fulltext-indexing`, `vector-indexing`); checksums skip unchanged records; reindex uses a lock plus progress-module jobs, exposed via `/api/search/reindex` (+ `/cancel`).
- **SHAREDSERVICES-R60** — HTTP surface MUST include `/api/search/search` (+ `/search/global`), `/api/search/index`, `/api/search/reindex*`, `/api/search/embeddings*`, `/api/search/settings*` (settings persisted through the configs service, R46).

### Business rules & workflows (summary level)

- **SHAREDSERVICES-R61** — The port MUST create the business-rules tables (`business_rules` with unique-per-tenant `rule_id`, `rule_type`, entity/event binding, JSONB `condition_expression`, JSONB `success_actions`/`failure_actions`, `priority` default 100, `enabled`, versioning + effective-date windows; `rule_sets`, `rule_set_members`, `rule_execution_logs`) and expose `/api/business_rules/{rules,sets,logs,execute}` (9 routes); every evaluation is logged; rule discovery is cache-backed with invalidation on rule CRUD.
- **SHAREDSERVICES-R62** — The port MUST create the seven workflow tables (`workflow_definitions`, `workflow_instances`, `workflow_branch_instances`, `step_instances`, `user_tasks`, `workflow_events`, `workflow_event_triggers`) and expose the 21 routes under `/api/workflows/{definitions,instances,tasks,events,signals}`, with one queue worker for async step execution; workflows delegate condition evaluation to business_rules. (Detailed engine semantics: port from module source at porting time.)

### Cross-module contracts

- **SHAREDSERVICES-R63** — The port MUST provide equivalents of the shared type contracts: module registry metadata (`ModuleInfo`), typed event declaration (`createModuleEvents` — event ids `module.entity.verb`, `clientBroadcast` flag; see spec 04 R28), `SearchModuleConfig` (R58), `NotificationTypeDefinition` (R31), and the `module:entity` `EntityId` convention. Upstream keeps the search-config and translatable-fields registries on `globalThis` to survive bundler duplication; ports SHOULD use a proper DI-scoped registry but MUST load all module declarations before serving requests.

## Contracts

### Translations HTTP (`/api/translations`)

| Method & path | ACL feature | Success | Errors |
|---|---|---|---|
| `GET /translations/:entityType/:entityId` | `translations.view` | 200 record | 400, 401, 404 `{"error":"Not found"}`, 500 |
| `PUT /translations/:entityType/:entityId` | `translations.manage` | 200 record + `x-om-operation` | 400 `{"error":"Invalid JSON body"}` / `{"error":"Validation failed","details":[...]}`, 401, 500 |
| `DELETE /translations/:entityType/:entityId` | `translations.manage` | **204 empty body** + `x-om-operation` | 400, 401, 500 |
| `GET /translations/locales` | `translations.view` | 200 `{"locales":["en","de",...]}` | 401, 500 |
| `PUT /translations/locales` | `translations.manage_locales` | 200 `{"locales":[...]}` (lower-cased, deduped) | 400 invalid ISO 639-1, 401 |

Record shape (GET/PUT 200):

```json
{
  "entityType": "dictionaries:dictionary_entry",
  "entityId": "3f1c...",
  "translations": { "de": { "label": "Farbe" }, "pl": { "label": "Kolor" } },
  "createdAt": "2026-05-01T10:00:00.000Z",
  "updatedAt": "2026-05-02T10:00:00.000Z"
}
```

### Notifications

- `POST /api/notifications` → **201** `{ "id": "<uuid>" }` (feature `notifications.create`).
- `NotificationDto`: `{ id, type, title, body?, titleKey?, bodyKey?, titleVariables?, bodyVariables?, icon?, severity, status, actions: [{id,label,labelKey?,variant?,icon?}], primaryActionId?, sourceModule?, sourceEntityType?, sourceEntityId?, linkHref?, createdAt, readAt?, actionTaken? }`.
- List response: `{ items: NotificationDto[], total, page, pageSize, totalPages }`, default filter `status != 'dismissed'`, order `createdAt DESC`.
- Advisory lock key (verbatim format string): `notifications:{tenantId}:{organizationId ?? 'global'}:{recipientUserId}:{type}:{groupKey}` locked via `select pg_advisory_xact_lock(hashtext($lockKey))`.
- Queue name: `notifications`; job payload discriminator `type: 'create' | 'create-role' | 'create-feature' | 'cleanup-expired'`.
- Events: `notifications.created|read|actioned|dismissed|restored|expired`; SSE: `notifications.notification.created` `{tenantId, organizationId, recipientUserId, notification}`; `notifications.notification.batch_created` `{tenantId, organizationId, recipientUserIds, count}`.

### Feature toggle check

```
GET /api/feature_toggles/check/boolean?identifier=my.flag
200 { "ok": true, "value": true, "resolution": { "valueType": "boolean", "value": true,
      "source": "override", "toggleId": "...", "identifier": "my.flag", "tenantId": "..." } }
400 { "error": "Missing required parameter: identifier" }
400 { "error": "Tenant context required. Please select a tenant." }
404 <the {ok:false, error:{code:"MISSING_TOGGLE", ...}} result object>
```

Super-admin boolean short-circuit: `{ ..., "source": "override", "toggleId": "superadmin", "tenantId": "superadmin" }`.

### Progress

`GET /api/progress/active` → `{ "active": Job[], "recentlyCompleted": Job[] }`; Job = `{ id, jobType, name, description, status, progressPercent, processedCount, totalCount, etaSeconds, cancellable, meta, startedAt, finishedAt, errorMessage }` (ISO strings or null). Unauthenticated → status 401, body `{"active":[],"recentlyCompleted":[]}`. Constants: heartbeat 5000 ms, stale 60 s.

### Audit undo

`POST /api/audit_logs/audit-logs/actions/undo` body `{ "undoToken": "..." }` → 200 `{ "ok": true, "logId": "..." }`; **every** failure → 400 `{"error":"Undo token not available"}`. Header `x-om-operation` = base64 of `{ "id", "undoToken", "commandId", "actionLabel", "resourceKind", "resourceId", "executedAt" }`.

### Attachments

- Upload check order and statuses per R35 (401 → 400 → 413 → 400 → 400 → 400 → 413 → 413 → sniff → 400 → 400 → 403 → 500 → 500).
- List item: `{ id, url, fileName, fileSize, createdAt, mimeType, partitionCode, content, thumbnailUrl, tags[], assignments[] }`; `url = /api/attachments/file/<id>`, `thumbnailUrl = /api/attachments/image/<id>/<slug>?width=320&height=320`.
- Default partitions: `productsMedia` (public), `privateAttachments` (private fallback).
- Library sentinel: `entityId = 'attachments:library'`.

### Webhook signature (Standard Webhooks)

```
signed_content = "${msgId}.${timestamp}.${body}"        # timestamp = unix seconds
signature      = "v1," + base64(HMAC_SHA256(secret, signed_content))
headers        = webhook-id: <msgId>
                 webhook-timestamp: <timestamp>
                 webhook-signature: <sig1>[ <sig2>]      # space-joined when dual-signing
```

### Redis / cache keys

| Key / name | Contract |
|---|---|
| `module-config:v1:<moduleId>:<name>` | Config cache entry, TTL 60 s, negative-cached `{found:false}` on miss |
| `module-config:module:<moduleId>` | Invalidation tag for all keys of a module |
| Queue `notifications` | BullMQ-compatible (spec 04 envelope/options) |
| Queues `fulltext-indexing`, `vector-indexing` | Search index jobs (spec 04 reserved names) |
| `<PREFIX>_REDIS_URL` → `REDIS_URL` → null | Redis URL resolution, never localhost |

### DB tables owned by these services

`entity_translations`, `notifications`, `attachments`, `attachment_partitions`, `action_logs`, `access_logs`, `dictionaries`, `dictionary_entries`, `module_configs`, `upgrade_action_runs`, `feature_toggles`, `feature_toggle_overrides`, `progress_jobs`, `business_rules`, `rule_execution_logs`, `rule_sets`, `rule_set_members`, `workflow_definitions`, `workflow_instances`, `workflow_branch_instances`, `step_instances`, `user_tasks`, `workflow_events`, `workflow_event_triggers`. All PKs `uuid DEFAULT gen_random_uuid()`. `entity_translations.entity_id`, `attachments.entity_id`, `attachments.record_id` are **text**, not uuid.

### Environment variables

| Var | Contract |
|---|---|
| `OM_SEARCH_ENABLED` | Token search on/off, default true |
| `OM_SEARCH_MIN_LEN` | Min token length, default 3, min 1 |
| `OM_SEARCH_ENABLE_PARTIAL` | Partial tokens, default true |
| `OM_SEARCH_HASH_ALGO` | `sha256`\|`sha1`\|`md5`, default sha256 |
| `OM_SEARCH_STORE_RAW_TOKENS` | Default false |
| `OM_SEARCH_FIELD_BLOCKLIST` | Comma list; `password,token,secret,hash` always appended |
| `CACHE_REDIS_URL` / `QUEUE_REDIS_URL` / `REDIS_URL` | Prefix-first resolution per R1 |
| `OPENAI_API_KEY` | Required for LLM OCR of attachments; missing → text-extraction fallback + warning |

## Concept mapping

| Upstream TS concept | Technology-agnostic concept a port implements |
|---|---|
| `packages/shared/src/lib/redis/connection.ts` `getRedisUrl(OrThrow)` | Prefix-first Redis URL resolver, no localhost default (R1) |
| `shared/lib/boolean.ts` token sets | Shared boolean env parser (R2) |
| `shared/lib/url-safety.ts` + `network.ts` (`safeOutboundFetch`, pinned DNS) | SSRF-guarded outbound HTTP client with DNS pinning (R3) |
| `shared/lib/webhooks/sign.ts` | Standard-Webhooks HMAC signer/verifier with rotation dual-sign (R4) |
| `shared/lib/ratelimit/` `RateLimiterService` | memory/redis window rate limiter (R5) |
| `shared/lib/patterns/wildcard.ts` | Glob matcher for events/features (R6) |
| `shared/lib/crud/errors.ts` `CrudHttpError` | Canonical status+body error type (R7) |
| `shared/lib/commands/operationMetadata.ts` | base64-JSON `x-om-operation` header codec (R8) |
| `shared/lib/search/config.ts`, `tokenize.ts` | Env-driven token-search config + byte-identical hash tokenizer (R9–R10) |
| `shared/lib/localization/` + `translations/lib/apply.ts` | Locale overlay of `entity_translations` onto records, batch per list page (R19) |
| module-root `translations.ts` files + `registerTranslatableFields` | Translatable-field declaration registry loaded at startup (R19, R63) |
| `shared/lib/encryption/find.ts` | Decryption-aware entity read wrappers (R12) |
| `notifications/lib/notificationService.ts` | Notification service: create/batch/role/feature fan-out, state machine, dedupe lock (R21–R32) |
| `notifications/lib/safeHref.ts` | Same-origin relative href validator (R24) |
| module-root `notifications.ts` (`NotificationTypeDefinition[]`) | Declarative notification type registry (R31) |
| `attachments/lib/drivers/` + `packages/storage-s3` | `StorageDriver` abstraction with local + S3 implementations (R34) |
| `attachments/lib/security.ts` (`detectAttachmentMimeType`, `hasDangerousExecutableExtension`, `isActiveContentAttachment`, ...) | Upload/serving security suite in the R35 order |
| `attachments/lib/partitions.ts` | Partition resolution + default seeding (R33, R36) |
| `audit_logs` services + undo route | Command log with opaque undo tokens, uniform-400 undo endpoint (R41–R43) |
| `configs/lib/module-config-service.ts` | Global KV settings service with 60 s tag cache + negative caching (R46–R47) |
| `feature_toggles` check routes | Typed flag resolution with tenant overrides + superadmin short-circuit (R49–R51) |
| `progress/lib/progressService.ts` | Progress job service with heartbeat/stale/ETA math (R52–R55) |
| `packages/search/src/service.ts` + strategies | Multi-strategy search with RRF merge and fail-closed scoping (R56–R60) |
| module-root `search.ts` (`SearchModuleConfig`) | Searchable-entity declaration registry (R58, R63) |
| `shared/src/modules/{registry,events,search,notifications}` | Cross-module type contracts (R63) |
| `business_rules` module (evaluator + action executor) | Rules engine per R61 |
| `workflows` module (+ `shared/src/modules/workflows` builder DSL) | Workflow engine per R62 |

## Allowed deviations

Idiomatic replacements are welcome when the HTTP/DB/Redis surface is unchanged. Document each as a decision.

**MAY deviate:**

- **Helper implementation**: any language-native library for slugify, phone validation, ISO 639 checks, relative time, rate limiting, HMAC, base64 — as long as persisted/wire outputs match (slug strings, signature bytes, token hashes).
- **Validation library**: language-native validation instead of Zod, provided status codes, error envelopes (`{"error":"Validation failed","details":[...]}`), and limits (R17, R22, R27) are preserved. The `details` array structure MAY be the native library's issue format.
- **Registries**: DI-scoped registries instead of `globalThis` singletons for translatable fields, search configs, notification types (R63) — declarations just must all be loaded before first request.
- **Storage drivers**: any S3 SDK; additional drivers may be added behind the `StorageDriver` interface. Thumbnail rendering/caching internals are free as long as URLs and default 320×320 params match.
- **Text extraction / OCR engines**: any equivalent extractors; the OPENAI fallback-with-warning behavior must stay.
- **Search strategy internals**: alternative fulltext/vector drivers behind the same strategy interface and settings APIs; the tokens strategy hashes MUST stay byte-identical (R10) because the Postgres token index is shared.
- **Cache backend plumbing** for module configs, provided key/tag/TTL/negative-cache semantics (R47) hold on the shared Redis.
- **Business rules / workflows internals** (expression evaluator, builder DSL): summary-level here; ports work from module source and may restructure internals if tables and routes match (R61–R62).

**MUST NOT change:**

- HTTP paths, methods, status codes and JSON bodies in Contracts — including translations DELETE 204, notifications POST 201 `{id}`, uniform undo 400 message, feature-toggle response envelope, progress 401-with-empty-shape, and the attachments upload check order/statuses.
- Table names, column names/types (including the **text** entity-id columns), uniqueness constraints, and enum value sets listed above.
- The advisory-lock key strings (`notifications:{tenant}:{orgOr'global'}:{recipient}:{type}:{groupKey}` via `hashtext`) — required for cross-runtime dedupe on a shared DB.
- Cache key/tag formats `module-config:v1:*` / `module-config:module:*`, the 60 s TTL, and negative caching.
- Queue name `notifications` and its job payload discriminators; search indexing queue names.
- Webhook signature format (`v1,` + base64, unix-seconds timestamp, dot-joined content, space-joined dual signatures) and header names.
- NULL-safe (`IS NOT DISTINCT FROM`) tenant/org matching in translations, and fail-closed semantics: undo checks, empty `organizationIds` → deny, missing `aclFeatures` → deny, public-partition override → 403.
- The always-on search field blocklist (`password,token,secret,hash`) and token hash compatibility.
- Event ids and SSE payload shapes (notifications, progress).

## Verification

How `om-verify-parity` checks these requirements (shared Postgres + Redis, Node upstream and the port attached side by side):

1. **HTTP golden suite (R15, R17–R18, R22–R23, R27, R35, R37–R39, R42, R45, R50–R51, R54, R60–R62)** — replay a recorded request corpus against upstream and the port; diff status codes, headers (`x-om-operation` presence + decodability), and JSON bodies. Includes the negative matrix: malformed JSON PUT (400 before validation), missing translation (404), translations DELETE (204 empty), undo with unknown/foreign/consumed tokens (all byte-identical 400 bodies), feature-toggle checks with missing identifier / missing tenant / unknown identifier / superadmin caller, unauthenticated `/api/progress/active` (401 + empty shape), attachments upload fixtures triggering each check in R35 order (oversize, dangerous extension, active-content SVG, explicit public partition → 403).
2. **Schema parity (R13, R33, R41, R44, R46, R49, R52, R61–R62)** — `pg_dump --schema-only` diff of every owned table: columns, types (assert `entity_id`/`record_id` are text), defaults, unique constraints, GIN index on `changed_fields`.
3. **NULL-scope semantics (R14, R20)** — seed translations with NULL tenant/org via upstream, read/update/delete via the port (and vice versa); assert NULL-scoped rows are matched. Delete a record, emit `query_index.delete_one`, assert translation rows removed by the port's subscriber.
4. **Dedupe lock interop (R25)** — concurrently create notifications with the same groupKey from a Node process and the port; assert exactly one active row remains and, under `pg_locks` inspection, both runtimes lock the same `hashtext` value for the documented key string. Dismissed-row case must produce a new row.
5. **State machine (R26)** — drive read/dismiss/restore/action transitions through the port; assert column changes, `readAt` backfill/clear rules, idempotent re-read emits no event (event spy), and expiry cleanup dismisses only non-actioned/non-dismissed rows.
6. **Queue interop (R28, R59)** — port enqueues a `notifications` create job → Node worker consumes it (and vice versa); raw `bull:notifications:*` job inspected for the spec-04 envelope and the `type` discriminator; same round-trip for `fulltext-indexing`/`vector-indexing`.
7. **Cache interop (R47)** — port writes a module config; assert Redis key `module-config:v1:<mod>:<name>` with tag `module-config:module:<mod>` and TTL ≤ 60 s; read a missing key twice and assert a negative-cache entry prevented the second DB hit; `setValue` from Node invalidates the port's cached read via the tag.
8. **Webhook signature fixtures (R4)** — shared fixture table (secret, msgId, timestamp, body → expected `v1,...` signature) must produce identical signatures in both runtimes; rotation case asserts two space-joined signatures and that upstream's verifier accepts the port's headers.
9. **Token hash fixtures (R9–R10)** — fixture texts tokenized under each `OM_SEARCH_HASH_ALGO`; assert identical token hash sets; assert `password`-named fields are never indexed even with an empty `OM_SEARCH_FIELD_BLOCKLIST`.
10. **SSRF guard (R3)** — table of URLs (RFC1918, IPv6 ULA/link-local, `169.254.169.254`, `localhost`, DNS names resolving to private IPs) must all be rejected; a DNS-rebinding double-resolution test asserts the connected IP equals the validated IP.
11. **Search scoping (R56–R58)** — index fixtures across two orgs; query with `organizationIds: []` → zero results; single `organizationId` overrides list; entity without `aclFeatures` absent from AI-tool results.
12. **Progress math (R53)** — property tests comparing `calculateEta`/`calculateProgressPercent` outputs to upstream for randomized inputs; stale-job test: insert a running job with `heartbeat_at` 61 s old, call `GET /active`, assert it returns as failed.
13. **Env resolution (R1–R2)** — start with only `CACHE_REDIS_URL` / only `REDIS_URL` / neither; assert correct target or error, and that no localhost connection is ever attempted; boolean env fuzz over the full token table.
