# Shared Services, Cross-Cutting Helpers & Module Inventory

> Analyzed at upstream commit adc9da27759e357febe9ed8d4b7182040d127349 (2026-07-01). Regenerate via the om-sync-upstream skill.

## Purpose

This document covers the cross-cutting "shared services" layer of Open Mercato that nearly every business module depends on, plus a full inventory of all modules/packages for the porting tracker. Concretely:

1. **`packages/shared`** — the utility/contract package (~208 non-test TS files) exporting helpers by area: strings, booleans, URLs/SSRF-safety, Redis connection resolution, rate limiting, webhook signing (Standard Webhooks), i18n/localization, search tokenization, DI container, OpenAPI doc types, module/event/search/notification type contracts.
2. **Small core "service" modules** that other modules consume: `translations`, `notifications`, `attachments` (+ `storage-s3` driver package), `audit_logs`, `dictionaries`, `configs`, `feature_toggles`, `progress`, `business_rules`, `workflows`, and the `search` package.
3. **Module inventory** — every module under `packages/core/src/modules` and every other `packages/*` package, with purpose, surface size, and cross-module dependencies.

A port must mirror the public HTTP contracts and DB schemas of these services 1:1; the shared helpers only need behavioral equivalents (idiomatic implementations are fine, documented as decisions).

## Key source locations

| Path (repo-relative) | Contains |
|---|---|
| `packages/shared/src/lib/*` | Generic helpers: `boolean.ts`, `string.ts`, `slugify.ts`, `json.ts`, `time.ts`, `phone.ts`, `url.ts`, `url-safety.ts`, `network.ts`, plus subdirs `auth/`, `redis/`, `ratelimit/`, `webhooks/`, `i18n/`, `localization/`, `search/`, `crud/`, `commands/`, `query/`, `encryption/`, `di/`, `openapi/`, `db/`, `patterns/` |
| `packages/shared/src/modules/*` | Cross-module TYPE contracts: `registry.ts` (ModuleInfo), `events.ts` (createModuleEvents), `search.ts` (SearchModuleConfig + global registry), `notifications/types.ts` (NotificationTypeDefinition, DTOs), `workflows/`, `integrations/types.ts`, `entities.ts`, `payment_gateways/` |
| `packages/core/src/modules/translations/` | `entity_translations` JSONB storage, locale overlay for CRUD reads, locales config API |
| `packages/core/src/modules/<mod>/translations.ts` | Per-module declaration of translatable fields (e.g. `dictionaries/translations.ts`) |
| `packages/shared/src/lib/localization/translatable-fields.ts` | Global registry (`globalThis`) of translatable-field declarations |
| `packages/core/src/modules/notifications/` | `notifications` table, NotificationService, delivery subscriber (email), BullMQ worker (queue `notifications`), 12 API routes |
| `packages/core/src/modules/<mod>/notifications.ts` | Per-module `notificationTypes: NotificationTypeDefinition[]` declarations (catalog, sales, staff, communication_channels) |
| `packages/core/src/modules/attachments/` | `attachments` + `attachment_partitions` tables, StorageDriver abstraction (`lib/drivers/`), upload security, OCR/text extraction, file/image serving routes |
| `packages/storage-s3/src/modules/storage_s3/` | S3 StorageDriver implementation + Integration Marketplace definition + 5 admin API routes |
| `packages/core/src/modules/audit_logs/` | `action_logs` (command log w/ undo/redo tokens & snapshots) and `access_logs` tables; undo/redo API |
| `packages/core/src/modules/dictionaries/` | Org-scoped `dictionaries` + `dictionary_entries` (enumerations with color/icon/position/default) |
| `packages/core/src/modules/configs/` | `module_configs` key-value JSONB settings store (`ModuleConfigService`), `upgrade_action_runs`, system-status & cache admin APIs |
| `packages/core/src/modules/feature_toggles/` | `feature_toggles` (global defs) + `feature_toggle_overrides` (per-tenant), typed check APIs |
| `packages/core/src/modules/progress/` | `progress_jobs` table, ProgressService (heartbeats, ETA, cancellation), SSE-broadcast progress events |
| `packages/core/src/modules/business_rules/` | Rules engine: `business_rules`, `rule_execution_logs`, `rule_sets`, `rule_set_members`; expression evaluator + action executor |
| `packages/core/src/modules/workflows/` | Workflow engine: 7 tables (`workflow_definitions`, `workflow_instances`, `workflow_branch_instances`, `step_instances`, `user_tasks`, `workflow_events`, `workflow_event_triggers`), 21 API routes |
| `packages/search/src/` | SearchService + strategies (`tokens`, `fulltext`/Meilisearch, `vector`/pgvector-qdrant-chromadb), indexer, workers, 13 API routes |
| `packages/shared/src/lib/search/config.ts`, `tokenize.ts` | Env-driven token search config (`OM_SEARCH_*`), hash-based tokenizer |
| `packages/core/src/modules/<mod>/search.ts` | Per-module `SearchModuleConfig` (searchable entities, buildSource, presenters, aclFeatures) |

## How it works

### packages/shared — helper areas to enumerate

`@open-mercato/shared` has no barrel with everything; consumers deep-import paths (`@open-mercato/shared/lib/...`, `.../modules/...` — see wildcard `exports` map in `packages/shared/package.json`). Areas a port needs (grouped; see "Helpers to mirror" for signatures):

- **Primitives**: `lib/boolean.ts` (env-flag parsing with TRUE/FALSE token sets), `lib/string.ts` (`trimToUndefined`, `toOptionalString`), `lib/slugify.ts`, `lib/json.ts` (JsonValue type), `lib/time.ts` (relative time formatting), `lib/phone.ts` (7–15 digit validation), `lib/utils.ts` (`cn`, `isRecord`).
- **URL / SSRF safety**: `lib/url.ts` (app-origin allowlisting `assertAllowedAppOrigin`, `getAppBaseUrl`, security-email URL building) and `lib/url-safety.ts` (`parseOutboundUrl`, `assertSafeOutboundUrl`, `safeOutboundFetch` with pinned-DNS lookup) + `lib/network.ts` (`isPrivateIpAddress`, `isBlockedHostname`, `isPrivateUrl`). Any port doing outbound webhooks/integrations MUST replicate private-IP/hostname blocking and DNS-pinning semantics.
- **Redis**: `lib/redis/connection.ts` — canonical env resolution `getRedisUrl(prefix?)`: `<PREFIX>_REDIS_URL` → `REDIS_URL` → `null` (never defaults to localhost); `getRedisUrlOrThrow`; URL→`{host,port,password,db,tls}` parser for BullMQ/ioredis.
- **Rate limiting**: `lib/ratelimit/` — `RateLimiterService` with `memory | redis` strategies (`RateLimitStrategy`), `RateLimitConfig { windowMs, max, ... }`.
- **Webhooks (Standard Webhooks spec)**: `lib/webhooks/` — HMAC-SHA256 over `${msgId}.${timestamp}.${body}`, signature format `v1,<base64>`, headers `webhook-id`, `webhook-timestamp`, `webhook-signature`; dual-signing during secret rotation; secret format helpers (`generateWebhookSecret`, `parseWebhookSecret`).
- **i18n**: `lib/i18n/` — `config.ts` (locales list, `defaultLocale`), `iso639.ts` (`isValidIso639`), `server.ts` (`resolveTranslations()`, `loadDictionary(locale)`), `translate.ts` (`createFallbackTranslator(dict)` → `t(key, fallback, vars)` with `{var}` interpolation). Module UI strings live in `<module>/i18n/{en,de,es,pl}.json`.
- **Localization overlay**: `lib/localization/` — `resolver.ts#applyLocalizedContent(item, translations, locale)` merges `entity_translations` JSONB over base records; `translatable-fields.ts` global registry (keyed by `module:entity`).
- **Search token config**: `lib/search/config.ts` (`resolveSearchConfig()` reading `OM_SEARCH_ENABLED`, `OM_SEARCH_MIN_LEN` default 3, `OM_SEARCH_ENABLE_PARTIAL`, `OM_SEARCH_HASH_ALGO` sha256|sha1|md5, `OM_SEARCH_STORE_RAW_TOKENS`, `OM_SEARCH_FIELD_BLOCKLIST` + built-in blocklist `password,token,secret,hash`) and `tokenize.ts` (`tokenizeText`, `hashToken`).
- **DB helpers**: `lib/db/` — `buildIlikeTerm` (contains/startsWith/endsWith), `escapeLikePattern`, SSL config, MikroORM helpers.
- **DI**: `lib/di/container.ts` — Awilix `createRequestContainer()`; every API route resolves services from it.
- **Commands**: `lib/commands/` — CommandBus, undo/redo, `operationMetadata.ts` (`serializeOperationMetadata` → base64 JSON emitted as `x-om-operation` response header on mutating routes).
- **CRUD factory & errors**: `lib/crud/` — `CrudHttpError(status, body)` / `isCrudHttpError` used pervasively for error responses; crud factory, interceptors, custom-field split (`splitCustomFieldPayload`).
- **Encryption**: `lib/encryption/` — tenant data-at-rest encryption service; `findWithDecryption` / `findOneWithDecryption` wrappers used instead of raw `em.find` when reading possibly-encrypted entities.
- **Auth**: `lib/auth/` — `getAuthFromRequest(req)`, JWT, feature matching (`featureMatch.ts`), password policy (covered in 05-auth-rbac.md).
- **Patterns**: `lib/patterns/wildcard.ts` — `matchWildcardPattern` (used for event subscriptions and feature globs).
- **Module contracts** (`src/modules/`): `registry.ts` `ModuleInfo` metadata; `events.ts` `createModuleEvents({ moduleId, events })` returning `{ emit }` + typed ids; `search.ts` (below); `notifications/types.ts` (below); `widgets/`, `workflows/`, `integrations/types.ts` (IntegrationDefinition for the marketplace), `entities.ts` (EntityId type `module:entity`).

### translations module

Single table `entity_translations` stores per-record translations as one JSONB blob: `translations: { [locale]: { [field]: value|null } }`, scoped by `(entity_type, entity_id, tenant_id, organization_id)`. Writes go through the CommandBus (`translations.translation.save` / `.delete`) so they are undoable and produce the `x-om-operation` header. Reads/writes match scope with SQL `IS NOT DISTINCT FROM` on tenant/org (NULL-safe equality). Modules declare which fields are translatable in a root-level `translations.ts` file, e.g. `packages/core/src/modules/dictionaries/translations.ts`:

```ts
export const translatableFields: Record<string, string[]> = {
  'dictionaries:dictionary_entry': ['label'],
}
export default translatableFields
```

These files are auto-discovered at build time and registered into the `globalThis`-backed registry (`registerTranslatableFields`). At read time, CRUD list endpoints overlay translations via `translations/lib/apply.ts#applyTranslationOverlays(items, { entityType, locale, tenantId, organizationId, container })` which batch-loads translation rows and calls `applyLocalizedContent` per item. Supported locales are stored via the configs module under key `('translations','supported_locales')`, defaulting to `@open-mercato/shared/lib/i18n/config` locales. Subscribers: `cleanup.ts` deletes translations when a record is deleted (event `query_index.delete_one`, non-persistent); `reindex.ts`/`reindex-on-delete.ts` trigger search reindex when translations change. Modules with `translations.ts` declarations: staff, customer_accounts, catalog, resources, entities, dictionaries, checkout.

### notifications module

One `notifications` table (per-recipient rows). i18n-first: `title_key`/`body_key` + `title_variables`/`body_variables` JSONB, with plain `title`/`body` fallback. `action_data` JSONB stores `{ actions: NotificationAction[], primaryActionId? }`; each action may carry a `commandId` (executed via CommandBus on `POST /:id/action`) or an `href`. Modules declare reusable notification types in root-level `notifications.ts` files (`notificationTypes: NotificationTypeDefinition[]` with `type`, `module`, `titleKey`, `icon`, `severity`, `actions[]`, `linkHref` with `{sourceEntityId}` placeholders, `expiresAfterHours`) — see `packages/core/src/modules/catalog/notifications.ts`. `NotificationService` (`lib/notificationService.ts`) implements create / createBatch / createForRole / createForFeature (recipient resolution via Kysely queries against auth tables), read/dismiss/restore/action state machine, poll data, expiry cleanup, deleteBySource. Group-key dedupe: when `groupKey` set, take a Postgres advisory xact lock on `hashtext('notifications:{tenant}:{orgOr"global"}:{recipient}:{type}:{groupKey}')` and refresh the existing active (`unread|read|actioned`) row in place instead of inserting. Async creation goes through BullMQ queue **`notifications`** (`workers/create-notification.worker.ts`, job union `{type: 'create'|'create-role'|'create-feature'|'cleanup-expired'}`). Delivery: persistent subscriber `notifications:deliver` on event `notifications.created` sends email (via `shared/lib/email/send` + React Email template) according to delivery strategies/config (`lib/deliveryStrategies.ts`, `lib/deliveryConfig.ts`, per-user settings API `/api/notifications/settings`). Realtime: emits SSE-bridge events `notifications.notification.created` / `.batch_created` carrying full DTO.

### attachments module + storage drivers

Two tables: `attachment_partitions` (code-unique storage buckets: `storage_driver` local|s3|legacy-public, `config_json`, `is_public`, `requires_ocr`, `ocr_model`) and `attachments` (entityId/recordId polymorphic owner, tenant/org scope, `partition_code`, `storage_driver`, `storage_path`, `storage_metadata` JSONB holding `{tags[], assignments[]}`, extracted `content` text, `url`). Default partitions seeded by `ensureDefaultPartitions`: `productsMedia` (public) and `privateAttachments` (private, fallback). Storage is abstracted by `StorageDriver` (`lib/drivers/types.ts`): `store/read/delete/toLocalPath`, resolved per-partition by `StorageDriverFactory` (local driver, legacy public driver, or S3 from `packages/storage-s3` — registered via Integration Marketplace with `authMode: access_keys|ambient`, bucket/region/endpoint/pathPrefix/forcePathStyle; S3 keys sanitized, paths segmented `tenant_<id>/org_<id>/…`). Upload path (`POST /api/attachments`, multipart): content-length pre-check → per-field CustomFieldDef constraints (acceptExtensions, maxAttachmentSizeMb, partitionCode) → dangerous-executable-extension block → size limit (413) → tenant quota (sum of `file_size`, 413) → magic-byte MIME sniffing (`detectAttachmentMimeType`) → active-content block (HTML/SVG scripts) → explicit public-partition selection block (403) → driver store → text extraction (pdf/docx/etc.) or queued LLM OCR → atomic insert + custom fields in one transaction → CRUD side effects (events + index). Serving: `GET /api/attachments/file/:id` (auth optional in metadata but access enforced by `checkAttachmentAccess`; public partitions serve anonymously) with safe `Content-Disposition` (inline only for `canRenderInlineAttachment` types); `GET /api/attachments/image/:id/[...slug]` renders/caches thumbnails (`?width&height`). Library endpoints (`/api/attachments/library`) manage the tenant-wide library (`entityId = 'attachments:library'`); `/api/attachments/transfer` moves files across partitions; `/api/attachments/partitions` CRUD for partitions.

### audit_logs module

Two tables. `action_logs`: one row per executed command via CommandBus — `command_id`, actor, tenant/org, `action_label`, `action_type` projection, resource kind/id (+ parent + related), `execution_state` (`done|undoing|undone|failed|redone`), `undo_token`, `command_payload`, `snapshot_before/after`, `changes_json`, `changed_fields text[]` (GIN indexed), `context_json`, timestamps + `deleted_at`. `access_logs`: read-access audit (resource kind/id, `access_type`, `fields_json`). Services `actionLogService.ts` / `accessLogService.ts`. HTTP: list/export routes plus `POST /api/audit_logs/audit-logs/actions/undo` and `/redo` — body `{ undoToken }`, requires feature `audit_logs.undo_self`; `audit_logs.undo_tenant` widens scope within (never across) tenants; fail-closed checks: token must map to a `done` log, actor match unless tenant-undoer, tenant must match exactly (null caller tenant can never undo tenant-scoped rows), org mismatch rejected. Undo executes the command's registered undo handler via CommandBus and flips `execution_state`.

### dictionaries module

Org+tenant-scoped enumerations. `dictionaries` (unique `(organization_id, tenant_id, key)`, `is_system`, `is_active`, `manager_visibility default|hidden`, `entry_sort_mode` default `label_asc`) and `dictionary_entries` (unique `(dictionary, org, tenant, normalized_value)`, `value`, `normalized_value`, `label`, `color`, `icon`, `position`, `is_default`). API under `/api/dictionaries`: list/create (201, 409 on duplicate key), `/:dictionaryId` get/put/delete, `/:dictionaryId/entries` CRUD, `/entries/reorder`, `/entries/set-default`. Writes are commands (undo support). Entry `label` is translatable (see translations above). Consumed by entities module custom-field kind `dictionary`, customers, catalog, planner, staff, workflows.

### configs module (settings storage)

`module_configs` = global (NOT tenant-scoped) key-value store: unique `(module_id, name)`, `value_json` JSONB. `ModuleConfigService` (`lib/module-config-service.ts`, DI name `moduleConfigService`): `getRecord/getValue<T>(moduleId, name, {defaultValue})/setValue/restoreDefaults(defaults, {force})/invalidate`, with cache-aside via the cache package — key `module-config:v1:<moduleId>:<name>`, TTL 60 s, tag `module-config:module:<moduleId>`, and negative caching (`{found:false}`). Keys validated by `moduleConfigKeySchema`. Also hosts `upgrade_action_runs` (once-per-scope upgrade actions keyed `(version, actionId, orgId, tenantId)`), the system-status endpoint (`/api/configs/system-status`), cache admin (`/api/configs/cache`), and `/api/configs/upgrade-actions`. Module `setup.ts` files call `restoreDefaults` to seed defaults.

### feature_toggles module

Feature flags (distinct from ACL features!). `feature_toggles`: unique `identifier`, `name`, `category`, `type` (`boolean|string|number|json`), `default_value` JSONB, soft-delete. `feature_toggle_overrides`: unique `(toggle_id, tenant_id)` with JSONB `value`. Check endpoints `GET /api/feature_toggles/check/{boolean,string,number,json}?identifier=...`: require auth; super-admin short-circuits boolean checks to `true` with `source: "override", toggleId: "superadmin"`; missing tenant → 400; unknown identifier → 404 `{ok:false, error:{code:'MISSING_TOGGLE'}}`; success → `{ ok: true, value, resolution: { valueType, value, source: 'default'|'override', toggleId, identifier, tenantId } }`. Global CRUD under `/api/feature_toggles/global` (+ `/global/:id/override`), overrides listing `/api/feature_toggles/overrides`. Service DI name `featureTogglesService`.

### progress module

Generic long-running job progress for the UI top bar. `progress_jobs` table (status `pending|running|completed|failed|cancelled`, `progress_percent` smallint, processed/total counts, `eta_seconds`, `heartbeat_at`, cancellation fields, parent/partition for fan-out jobs, tenant/org). `ProgressService` (DI `progressService`) contract in `lib/progressService.ts` with constants `HEARTBEAT_INTERVAL_MS = 5000`, `STALE_JOB_TIMEOUT_SECONDS = 60` and pure helpers `calculateEta` / `calculateProgressPercent` (percent = `min(100, round(processed/total*100))`, 0 when no total). `GET /api/progress/active` first calls `markStaleJobsFailed(tenantId)` then returns `{ active: [...], recentlyCompleted: [...] }` (401 returns the same empty shape). Jobs CRUD under `/api/progress/jobs`, `/jobs/:id` (get/cancel). All 6 events (`progress.job.created|started|updated|completed|failed|cancelled`) are declared with `clientBroadcast: true` so the SSE bridge pushes them to browsers. Used by query_index reindex, sync_excel, search reindex, sync-akeneo.

### business_rules module (one paragraph)

A tenant+org-scoped rules engine: `business_rules` rows hold a `rule_id` (unique per tenant), `rule_type`, `entity_type`/`event_type` binding, a JSONB `condition_expression` (evaluated by `lib/expression-evaluator.ts` + `lib/rule-evaluator.ts` with `lib/value-resolver.ts` for context lookups) and JSONB `success_actions`/`failure_actions` executed by `lib/action-executor.ts`; rules have `priority` (default 100), `enabled`, versioning and effective-date windows, can be grouped via `rule_sets`/`rule_set_members`, and each evaluation is recorded in `rule_execution_logs`. Programmatic entry points are exported from the module root (`executeRules`, `executeRuleById`, `executeRuleByRuleId`, `executeSingleRule`, `findApplicableRules`) with a cache-backed rule-discovery layer (invalidated by two subscribers on rule CRUD); HTTP surface is `/api/business_rules/{rules,sets,logs,execute}` (9 routes). Workflows call into it for condition evaluation.

### workflows module (one paragraph)

A state-machine workflow engine orchestrating long-running business processes: `workflow_definitions` (versioned process definitions with states/transitions/activities, built via the fluent builder in `packages/shared/src/modules/workflows/builder.ts`), `workflow_instances` + `workflow_branch_instances` + `step_instances` (runtime state incl. parallel branches), `user_tasks` (human approvals/inputs surfaced in the backend UI), and `workflow_events` / `workflow_event_triggers` (start or signal workflows from event-bus events). It exposes 21 API routes under `/api/workflows/{definitions,instances,tasks,events,signals}`, one queue worker for async step execution, and 2 subscribers; depends on business_rules (conditions), dictionaries, directory and sales. Shared package `modules/workflows/{types,builder,factory}.ts` defines the definition DSL that module code uses to register workflows.

### search package

Strategy-pattern search service (`packages/search/src/service.ts`) merging results from up to three strategies: `tokens` (hash-token index inside Postgres — privacy-preserving, always available), `fulltext` (Meilisearch driver), `vector` (drivers: pgvector, Qdrant, ChromaDB; embeddings via `vector/services/embedding.ts`). Modules declare searchable entities in root-level `search.ts` files exporting `SearchModuleConfig` (`packages/shared/src/modules/search.ts`): per-entity `entityId`, `enabled`, `strategies`, `priority`, `buildSource(ctx) → { text, fields?, presenter?, links?, checksumSource? }`, `formatResult`, `resolveUrl`, `resolveLinks`, `fieldPolicy { searchable[], hashOnly[], excluded[] }`, and `aclFeatures[]` (fail-closed gate for data-returning AI tools). Configs registered at bootstrap via `registerSearchModuleConfigs` (global registry). Indexing is event-driven: subscribers react to `search.index_record` / `search.delete_record` payloads and enqueue BullMQ jobs (queues in `src/queue/fulltext-indexing.ts` and `vector-indexing.ts`, workers `fulltext-index.worker.ts`, `vector-index.worker.ts`); checksums (`vector/services/checksum.ts`) skip unchanged records. Modules with `search.ts`: sales, catalog, resources, staff, customer_accounts, inbox_ops, messages, planner, customers. HTTP: `/api/search/search` (+ `/search/global`), `/api/search/index`, `/api/search/reindex` (+ `/cancel`, lock via `lib/reindex-lock.ts`, progress via progress module), `/api/search/embeddings*`, `/api/search/settings*` (fulltext/vector-store/global-search config persisted through configs module). Result merging: RRF-style with `ResultMergeConfig { duplicateHandling: 'highest_score'|'first'|'merge_scores', strategyWeights, minScore }`; org scoping supports `organizationId` (single) and `organizationIds` allowlist where an **empty array must return no results**.

## Public contracts

### Translations HTTP API (all under `/api/translations`)

| Method & path | Features | Success | Errors |
|---|---|---|---|
| `GET /translations/:entityType/:entityId` | `translations.view` | 200 record | 400 invalid params, 401, 404 `{"error":"Not found"}`, 500 |
| `PUT /translations/:entityType/:entityId` | `translations.manage` | 200 record + `x-om-operation` header | 400 `{"error":"Invalid JSON body"}` or `{"error":"Validation failed","details":[...]}` , 401, 500 |
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

Validation (`translations/data/validators.ts`): body = record of locale (string 2–10) → record of field (string 1–100) → `string(max 10000) | null`; max 50 locales. `entityType` param must match `/^[a-z_]+:[a-z_]+$/`.

### Notifications HTTP API (all under `/api/notifications`)

- `GET /` — list; query `listNotificationsSchema` (page/pageSize, `status` single or array, `type`, `severity`, `sourceEntityType`, `sourceEntityId`, `since` ISO). Default filter **excludes `dismissed`** (`status != 'dismissed'`); ordered `createdAt desc`. Response: `{ items: NotificationDto[], total, page, pageSize, totalPages }`.
- `POST /` — feature `notifications.create`; body `createNotificationSchema` (requires `recipientUserId` uuid; either `titleKey` or `title`; `linkHref`/action `href` must pass `isSafeNotificationHref` — same-origin relative path starting with `/`). Returns **201** `{ "id": "<uuid>" }`.
- `POST /batch` (`recipientUserIds` 1–1000), `POST /role` (`roleId`), `POST /feature` (`requiredFeature`) — fan-out variants.
- `POST /:id/read`, `POST /:id/dismiss`, `POST /:id/restore` (body `{status?: 'read'|'unread'}`, default `read`), `POST /:id/action` (body `{actionId, payload?}` — executes bound command; unknown actionId → error), `POST /mark-all-read` (bulk Kysely update, returns count), `GET /unread-count`.
- `GET /settings` / `PUT /settings` — per-user delivery preferences.

`NotificationDto` (from `shared/modules/notifications/types.ts`): `{ id, type, title, body?, titleKey?, bodyKey?, titleVariables?, bodyVariables?, icon?, severity, status, actions: [{id,label,labelKey?,variant?,icon?}], primaryActionId?, sourceModule?, sourceEntityType?, sourceEntityId?, linkHref?, createdAt, readAt?, actionTaken? }`.

Events: `notifications.created|read|actioned|dismissed|restored|expired`; SSE-bridge: `notifications.notification.created` (payload `{tenantId, organizationId, recipientUserId, notification: NotificationDto}`) and `.batch_created` (`{tenantId, organizationId, recipientUserIds, count}`). Queue name: `notifications`.

### Attachments HTTP API (all under `/api/attachments`)

- `GET /?entityId&recordId[&page&pageSize(max 100)]` — feature `attachments.view`; paged only when both page params given; items ordered newest first; item: `{ id, url, fileName, fileSize, createdAt, mimeType, partitionCode, content, thumbnailUrl, tags[], assignments[] }` where `url = /api/attachments/file/<id>` and `thumbnailUrl = /api/attachments/image/<id>/<slug>?width=320&height=320`.
- `POST /` (multipart) — feature `attachments.manage`; fields `entityId`, `recordId`, `file`, optional `fieldKey`, `partitionCode`, `tags` (JSON array), `assignments` (JSON), `customFields` (JSON). 200 `{ ok: true, item: {...} }`. Errors: 400 (missing fields / bad type / dangerous extension / active content / partition unconfigured / non-multipart), **403** explicit public-partition selection, **413** over max size or tenant quota, 500 persist failure. Requires `auth.orgId` (401 otherwise).
- `DELETE /?id=<uuid>` — 200 `{ok:true}`, 404 if not found in caller scope; deletes DB row first, then thumbnails + driver object.
- `GET /file/:id` — `requireAuth: false` in metadata; access via `checkAttachmentAccess` (public partition → anonymous OK; else tenant/org scoping; 401/403/404 as appropriate); 200 streams bytes with sniffed content type and safe `Content-Disposition` (`?download=1` forces attachment).
- `GET /image/:id/[...slug]?width&height` — thumbnail rendering with FS cache.
- `GET|POST /library`, `GET|PATCH|DELETE /library/:id` — tenant attachment library (`entityId='attachments:library'`).
- `GET|POST|PATCH /partitions`, `POST /transfer`.

Env knobs: upload limit (`resolveAttachmentMaxBytes`), tenant quota (`willExceedAttachmentTenantQuota`), OCR defaults `resolveDefaultAttachmentOcrEnabled()`; LLM OCR requires `OPENAI_API_KEY` (falls back to text extraction with a console warning).

### Feature toggles check contract

```
GET /api/feature_toggles/check/boolean?identifier=my.flag
200 { "ok": true, "value": true, "resolution": { "valueType": "boolean", "value": true, "source": "override", "toggleId": "...", "identifier": "my.flag", "tenantId": "..." } }
400 { "error": "Missing required parameter: identifier" } | { "error": "Tenant context required. Please select a tenant." }
404 (result.error.code === 'MISSING_TOGGLE') body = the {ok:false,error:{...}} result object
```

### Progress contract

`GET /api/progress/active` → `{ active: Job[], recentlyCompleted: Job[] }`; Job = `{ id, jobType, name, description, status, progressPercent, processedCount, totalCount, etaSeconds, cancellable, meta, startedAt, finishedAt, errorMessage }` (ISO strings or null). Unauthenticated → **401 with `{active:[],recentlyCompleted:[]}` body**.

### Audit logs undo

`POST /api/audit_logs/audit-logs/actions/undo` body `{ "undoToken": "..." }` → 200 `{ ok: true, logId }`; all failure modes (unknown/consumed token, actor mismatch without `audit_logs.undo_tenant`, tenant/org mismatch) return **400 `{"error":"Undo token not available"}`** (deliberately indistinguishable). `x-om-operation` response header on mutating routes = base64(JSON `{ id, undoToken, commandId, actionLabel, resourceKind, resourceId, executedAt }`) via `serializeOperationMetadata`.

### DB tables owned by these services

`entity_translations`, `notifications`, `attachments`, `attachment_partitions`, `action_logs`, `access_logs`, `dictionaries`, `dictionary_entries`, `module_configs`, `upgrade_action_runs`, `feature_toggles`, `feature_toggle_overrides`, `progress_jobs`, `business_rules`, `rule_execution_logs`, `rule_sets`, `rule_set_members`, `workflow_definitions`, `workflow_instances`, `workflow_branch_instances`, `step_instances`, `user_tasks`, `workflow_events`, `workflow_event_triggers`. All PKs are `uuid DEFAULT gen_random_uuid()`. Redis structures: cache keys `module-config:v1:<module>:<name>` (+tags), BullMQ queues `notifications`, search fulltext/vector indexing queues.

## Helpers to mirror

| Helper (source) | Signature / behavior a port needs |
|---|---|
| `getRedisUrl(prefix?)` / `getRedisUrlOrThrow(prefix?)` (`shared/lib/redis/connection.ts`) | `<PREFIX>_REDIS_URL` → `REDIS_URL` → null; throw variant with descriptive message; never default to localhost |
| `parseBooleanWithDefault(raw, fallback)` etc. (`shared/lib/boolean.ts`) | TRUE set `1,true,yes,y,on,enable,enabled`; FALSE set `0,false,no,n,off,disable,disabled` |
| `slugify(value, options)` (`shared/lib/slugify.ts`) | URL-safe slugs; used for attachment file slugs |
| `signWebhookPayload(msgId, timestamp, body, secret)` → `` `v1,${base64hmac}` ``; `buildWebhookHeaders(...)`; `verifyWebhookSignature(...)` (`shared/lib/webhooks/`) | Standard Webhooks: HMAC-SHA256 over `msgId.timestamp.body`; dual-sign with previous secret during rotation |
| `assertSafeOutboundUrl` / `safeOutboundFetch` / `createPinnedDnsLookup` (`shared/lib/url-safety.ts`) + `isPrivateIpAddress`/`isBlockedHostname` (`network.ts`) | SSRF guard: block private IPv4/IPv6 ranges & metadata hostnames, pin DNS resolution for the actual request |
| `assertAllowedAppOrigin(req)`, `getAppBaseUrl(req)`, `toAbsoluteUrl(req, path)` (`shared/lib/url.ts`) | Origin allowlisting + absolute URL construction for emails |
| `resolveSearchConfig()` / `resolveSearchMinTokenLength()` / `tokenizeText(text, config)` / `hashToken(token, config)` (`shared/lib/search/`) | Env-driven token search; default min token length 3, sha256 hashing, field blocklist always includes `password,token,secret,hash` |
| `applyLocalizedContent(item, translations, locale)` + `registerTranslatableFields` / `getTranslatableFields(entityType)` (`shared/lib/localization/`) | Locale overlay of `entity_translations` JSONB onto records |
| `applyTranslationOverlays(items, opts)` (`core/modules/translations/lib/apply.ts`) | Batch translation load + overlay for list endpoints |
| `createModuleEvents({moduleId, events})` (`shared/src/modules/events`) | Typed event declaration + `emit`; event ids `module.entity.verb`; `clientBroadcast: true` marks SSE-forwarded events |
| `serializeOperationMetadata(payload)` / `deserializeOperationMetadata` (`shared/lib/commands/operationMetadata.ts`) | base64 JSON for the `x-om-operation` header |
| `CrudHttpError(status, body)` / `isCrudHttpError` (`shared/lib/crud/errors.ts`) | Canonical throw-to-HTTP error mapping used by every service |
| `createModuleConfigService(container)` (`configs/lib/module-config-service.ts`) | `getValue<T>(module, name, {defaultValue})` etc. with 60 s tag-based cache + negative caching |
| `resolveNotificationService(container)` / `createNotificationService(deps)` (`notifications/lib/notificationService.ts`) | Full `NotificationService` interface (see mechanics) |
| `sanitizeNotificationActions` / `assertSafeNotificationHref` / `isSafeNotificationHref` (`notifications/lib/safeHref.ts`) | Only same-origin relative `/...` hrefs allowed in notifications |
| `StorageDriver` interface + `StorageDriverFactory.resolveForPartition(code, {tenantId, organizationId})` (`attachments/lib/drivers/`) | `store({partitionCode, orgId, tenantId, fileName, buffer}) → {storagePath, driverMeta?}`, `read`, `delete`, `toLocalPath → {filePath, cleanup}` |
| `detectAttachmentMimeType(buf, name, claimed)`, `hasDangerousExecutableExtension`, `isActiveContentAttachment`, `sanitizeUploadedFileName`, `buildAttachmentContentDisposition`, `canRenderInlineAttachment` (`attachments/lib/security.ts`) | Upload/serving security suite |
| `resolveDefaultPartitionCode(entityId)`, `sanitizePartitionCode`, `ensureDefaultPartitions(em)` (`attachments/lib/partitions.ts`) | catalog products → `productsMedia`, everything else → `privateAttachments` |
| `buildAttachmentFileUrl(id, {download?})`, `buildAttachmentImageUrl(id, {width,height,slug})`, `slugifyAttachmentFileName` (`attachments/lib/imageUrls.ts`) | Canonical asset URL builders |
| `calculateEta(processed, total, startedAt)`, `calculateProgressPercent(processed, total)` (`progress/lib/progressService.ts`) | ETA in seconds (ceil), percent capped at 100; constants heartbeat 5 s / stale 60 s |
| `getRecipientUserIdsForRole(db, tenantId, roleId)` / `...ForFeature(db, tenantId, feature)` (`notifications/lib/notificationRecipients.ts`) | Recipient fan-out queries against auth tables |
| `validatePhoneNumber` (`shared/lib/phone.ts`) | 7–15 digits, structured reason codes |
| `matchWildcardPattern(value, pattern, opts)` (`shared/lib/patterns/wildcard.ts`) | Glob matching for events/features |
| `findWithDecryption` / `findOneWithDecryption` (`shared/lib/encryption/find.ts`) | Entity reads with tenant data-at-rest decryption |
| `isValidIso639(code)` (`shared/lib/i18n/iso639.ts`) | Locale validation for PUT /translations/locales |

## Module inventory (feeds the porting tracker)

Surface counts computed from source (`api` = `.ts` files under `api/` excluding tests; `ent` = `@Entity` decorators in `data/entities.ts`; `wrk` = files in `workers/`; `sub` = files in `subscribers/`). Dependencies = imports of `@open-mercato/core/modules/<other>` (or other packages) found in the module's source.

### Core modules (`packages/core/src/modules/`)

| Module | Purpose (1 line) | api | ent | wrk | sub | Depends on |
|---|---|---:|---:|---:|---:|---|
| api_docs | Auto-generated documentation for all HTTP endpoints | 1 | 0 | 0 | 0 | — |
| api_keys | Manage access tokens for external API access | 1 | 1 | 0 | 0 | auth, directory |
| attachments | File attachments and media management | 8 | 2 | 0 | 0 | entities |
| audit_logs | Tracks user actions and data accesses with undo/redo | 6 | 2 | 0 | 0 | auth, directory |
| auth | User accounts, sessions, roles and password resets | 19 | 11 | 0 | 0 | api_keys, dashboards, directory, entities, notifications |
| business_rules | Rules engine: conditions + actions on entity events | 9 | 4 | 0 | 2 | — |
| catalog | Products, variants, and pricing used by sales | 15 | 12 | 1 | 1 | attachments, customers, dictionaries, directory, entities, sales |
| communication_channels | Hub bridging external chat/email channels (Slack/WhatsApp/Email) to Messages | 20 | 8 | 8 | 3 | integrations |
| configs | Shared module settings storage (module_configs) + system status | 4 | 2 | 0 | 0 | catalog, directory, query_index |
| core | Framework glue module (no api/entities) | 0 | 0 | 0 | 0 | — |
| currencies | Currencies and exchange-rate management | 6 | 3 | 0 | 0 | — |
| customer_accounts | Customer-facing auth with two-tier identity and RBAC | 42 | 10 | 4 | 13 | auth, customers, directory, notifications |
| customers | CRM: people, companies, deals, activities | 66 | 25 | 2 | 4 | audit_logs, auth, communication_channels, currencies, dashboards, dictionaries, directory, entities, feature_toggles, query_index |
| dashboards | Configurable admin dashboard with module widgets | 9 | 3 | 0 | 0 | auth, catalog, customers, directory, sales |
| data_sync | Streaming data-sync hub for import/export integrations | 12 | 4 | 3 | 0 | — |
| dictionaries | Org-scoped enumerations with appearance presets | 8 | 2 | 0 | 0 | directory, translations |
| directory | Multi-tenant directory: tenants and organizations | 7 | 2 | 0 | 1 | auth |
| entities | User-defined entities, custom fields, dynamic records | 10 | 6 | 0 | 0 | auth, currencies, dictionaries, directory, translations |
| feature_toggles | Global feature flags with tenant-level overrides | 9 | 2 | 0 | 0 | directory |
| inbox_ops | LLM extraction of action proposals from forwarded emails (HITL) | 19 | 5 | 0 | 3 | catalog, customers, sales |
| integrations | Integration framework: external ID mapping, registry | 10 | 4 | 2 | 0 | — |
| messages | Internal messaging with attachments, actions, email forwarding | 18 | 5 | 1 | 2 | attachments, auth, notifications |
| notifications | In-app notifications with extensible types and actions | 12 | 1 | 1 | 1 | configs |
| payment_gateways | Payment gateway adapter contract + transactions + webhooks | 12 | 2 | 2 | 0 | — |
| perspectives | Persistence for DataTable saved views | 4 | 2 | 0 | 0 | auth |
| planner | Availability schedules, rulesets, planning rules | 7 | 2 | 0 | 0 | dictionaries, directory |
| portal | Self-service customer portal framework (frontend-heavy) | 0 | 0 | 0 | 0 | feature_toggles |
| progress | Server-side progress tracking for long-running ops | 4 | 1 | 0 | 0 | — |
| query_index | Hybrid query layer with full-text and vector index maintenance | 4 | 6 | 0 | 6 | directory, progress |
| resources | Assets and resources with scheduling policies | 9 | 6 | 0 | 0 | auth, dictionaries, directory, entities, planner, sales |
| sales | Quoting, ordering, fulfillment, billing | 41 | 27 | 0 | 4 | attachments, audit_logs, auth, catalog, customers, dashboards, dictionaries, directory, entities |
| shipping_carriers | Carrier adapter hub: rates, shipments, tracking, webhooks | 9 | 2 | 2 | 0 | — |
| staff | Teams, roles, employee rosters | 29 | 12 | 0 | 0 | auth, customers, dashboards, dictionaries, directory, entities, planner, translations |
| sync_excel | CSV/Excel upload import on top of data_sync | 3 | 1 | 0 | 0 | customers, data_sync, directory, integrations, progress |
| translations | Entity translation storage + locale overlay | 4 | 1 | 0 | 3 | configs, directory |
| widgets | Widget injection registry (frontend-only) | 0 | 0 | 0 | 0 | — |
| workflows | Workflow engine: state machines, transitions, user tasks | 21 | 7 | 1 | 2 | business_rules, dictionaries, directory, sales |

(`__tests__` and `core`/`portal`/`widgets` have no backend API surface; portal/widgets are frontend frameworks.)

### Other packages (`packages/*`)

| Package | Module(s) | Purpose (1 line) | api | ent | wrk | sub | Depends on |
|---|---|---|---:|---:|---:|---:|---|
| shared | — | Cross-cutting helpers & type contracts (this doc) | 0 | 0 | 0 | 0 | — |
| queue | — | Multi-strategy job queue (local \| BullMQ/Redis) | 0 | 0 | 0 | 0 | shared |
| events | — | Event bus (subscribers, persistent events, worker) | 2 | 0 | 1 | 0 | queue |
| cache | — | Multi-strategy cache with tag-based invalidation | 0 | 0 | 0 | 0 | shared (redis) |
| cli | — | `mercato` CLI host (module CLI discovery) | 0 | 0 | 0 | 0 | shared |
| ui | — | React component library (frontend only, no port needed) | 0 | 0 | 0 | 0 | — |
| search | search | Multi-strategy search (tokens/Meilisearch/vector) + indexer | 13 | 0 | 2 | 5 | configs, directory, progress, query_index, queue |
| storage-s3 | storage_s3 | S3-compatible StorageDriver for attachments + admin APIs | 5 | 0 | 0 | 0 | attachments, directory, integrations |
| scheduler | scheduler | DB-managed scheduled jobs (cron) with admin UI | 7 | 1 | 1 | 0 | queue |
| webhooks | webhooks | Standard-Webhooks outbound/inbound delivery | 18 | 3 | 1 | 3 | directory, events, integrations, notifications, queue |
| ai-assistant | ai_assistant | MCP server for AI assistant integration (multi-tenant) | 27 | 10 | 2 | 1 | api_keys, attachments, auth, configs, directory, notifications, queue, search |
| checkout | checkout | Pay links, checkout templates, public payment pages | 14 | 3 | 2 | 8 | auth, customers, entities, notifications, payment_gateways, queue, sales |
| content | content | Static informational pages (ToS, privacy) | 0 | 0 | 0 | 0 | — |
| enterprise | record_locks, sso, security, system_status_overlays | Record locking w/ conflict resolution, SSO, security overlays | 60 | 14 | 0 | 15 | audit_logs, auth, configs, directory, notifications |
| gateway-stripe | gateway_stripe | Stripe payment gateway adapter (cards, wallets, transfers) | 0 | 0 | 1 | 0 | integrations, payment_gateways, queue |
| channel-gmail | channel_gmail | Gmail adapter for communication_channels | 0 | 0 | 0 | 0 | communication_channels |
| channel-imap | channel_imap | IMAP/SMTP adapter for communication_channels | 0 | 0 | 0 | 0 | communication_channels |
| sync-akeneo | sync_akeneo | Akeneo PIM import into catalog | 4 | 0 | 2 | 0 | attachments, catalog, data_sync, entities, integrations, progress, queue, sales |
| onboarding | onboarding | Self-service tenant/organization onboarding flow | 4 | 1 | 0 | 0 | auth, search |
| create-app | — | `create-mercato-app` scaffolding CLI (not a runtime port target) | 0 | 0 | 0 | 0 | — |

## Behavioral details a port MUST replicate

1. **Tenant/org NULL-safe scoping in translations**: reads/writes match with `tenant_id IS NOT DISTINCT FROM $1 AND organization_id IS NOT DISTINCT FROM $2` — plain `=` breaks NULL-scoped rows.
2. **Translations DELETE returns 204 with empty body**; GET missing record → 404 `{"error":"Not found"}`; malformed JSON on PUT → 400 `{"error":"Invalid JSON body"}` before validation; Zod validation errors → 400 with `details: issues`.
3. **`x-om-operation` header** must be set on undoable mutations (translations PUT/DELETE and all command-backed routes) only when the log entry has `undoToken`, `id` and `commandId`; value is `serializeOperationMetadata` output.
4. **Notification list default excludes dismissed** (`status != 'dismissed'` unless `status` filter provided); ordering `createdAt DESC`; `POST /` returns 201 with only `{id}`.
5. **Notification group-key dedupe** uses a pg advisory transaction lock over `hashtext(lockKey)` with lock key `notifications:{tenantId}:{orgId||'global'}:{recipientUserId}:{type}:{groupKey}`, refreshing the newest active row (`unread|read|actioned` — dismissed rows are NOT refreshed, a new one is created); advisory-lock failure degrades to best-effort dedupe, never a request failure.
6. **Notification state machine**: markAsRead only transitions from `unread` (idempotent otherwise, event only emitted on actual transition); dismiss always sets `dismissed` + `dismissedAt`; restore only acts on `dismissed` rows (default target `read`; restoring to `read` backfills `readAt` if null; restoring to `unread` clears `readAt`); executeAction sets `actioned`, `actionTaken`, `actionResult`, backfills `readAt`. `cleanupExpired` flips rows with `expires_at < now()` and status not in `('actioned','dismissed')` to `dismissed`.
7. **Notification href safety**: any `linkHref`/action `href` must be a same-origin relative path starting with `/` — both at validation (Zod refine) and at service level (`assertSafeNotificationHref`), because service is callable without HTTP validation.
8. **Attachments upload ordering of checks** (each short-circuits with its own status): 401 no org → 400 non-multipart → 413 content-length precheck → 400 missing fields → field-config extension check 400 → dangerous executable 400 → 413 size → 413 tenant quota → MIME sniff → active-content 400 → partition resolution 400 → 403 explicit public-partition override → driver store 500 → transactional insert (attachment + custom fields atomic) 500.
9. **Attachment deletion order**: DB row removed first, thumbnail cache cleanup (failure logged, not fatal), then driver delete; scope filter includes both tenantId and organizationId (404 outside scope).
10. **`GET /api/attachments/file/:id` is anonymous-capable** (metadata `requireAuth: false`); superadmins bypass scope filters; non-superadmin scoping applies tenant/org filters only when present in auth.
11. **Undo endpoint returns the same 400 `Undo token not available`** for every failure mode (unknown token, wrong state, actor mismatch, tenant mismatch, org mismatch) — do not leak which check failed. `audit_logs.undo_tenant` never crosses tenants; caller with null tenant can never undo tenant-scoped rows.
12. **module_configs is global (no tenant column)**; cache TTL 60 s with negative caching; `setValue(null)` stores JSON null (getValue then falls back to defaultValue).
13. **Feature toggle boolean check short-circuits for super-admin** to `true` with `source:'override', toggleId:'superadmin', tenantId:'superadmin'` — before tenant resolution; missing tenant → 400; MISSING_TOGGLE → 404 with the `{ok:false,...}` result as body.
14. **Progress**: `GET /active` calls `markStaleJobsFailed` first (heartbeat older than 60 s ⇒ failed); 401 returns 401 status with `{active:[],recentlyCompleted:[]}` body; percent formula `min(100, round(p/t*100))`, 0 when total null/0; ETA `ceil(remaining / rate / 1000)` seconds, null when nothing processed.
15. **Search org scoping**: `organizationId` (single) wins over `organizationIds`; an **empty `organizationIds` array must return zero results** (deny), while `null`/`undefined` means tenant-wide. Entities without `aclFeatures` are fail-closed for data-returning AI tools.
16. **Token search field blocklist** always contains `password, token, secret, hash` regardless of `OM_SEARCH_FIELD_BLOCKLIST`; entries lower-cased and deduped.
17. **Locales PUT** lower-cases, trims and dedupes; each entry must be a valid ISO 639-1 code (2–10 chars pre-check), 1–50 entries.
18. **Translation cleanup subscriber** listens to `query_index.delete_one` (non-persistent) and deletes matching `entity_translations` rows with NULL-safe scope matching; failures are logged, never thrown.
19. **Redis env resolution**: subsystem prefix override first (`QUEUE_REDIS_URL`, `CACHE_REDIS_URL`), then `REDIS_URL`, else null/throw — never a localhost default.
20. **Webhook signatures**: `v1,` prefix, base64 (not hex); timestamp is unix seconds; dual signatures (space-separated in `webhook-signature`) during rotation.

## Gotchas

- **`translations.ts` is overloaded**: at module root it declares translatable fields; `translations/commands/translations.ts` is the command implementation of the translations module. Auto-discovery only picks up module-root `translations.ts` files.
- **Feature toggles ≠ ACL features.** `feature_toggles` are runtime flags with tenant overrides; `acl.ts` `features` (e.g. `translations.view`) are RBAC permissions. Both use the word "feature".
- **Notifications `title` fallback**: when only `titleKey` is provided, the stored `title` column is set to the key string itself (`input.title || input.titleKey || ''`) — DTO consumers must prefer `titleKey` + variables when present.
- **`markAllAsRead` emits `notifications.notification.created` SSE events for updated rows** (reusing the "created" SSE channel to push refreshed DTOs) — quirky but part of the observable contract.
- **Attachments module `AGENTS.md` exists** (`packages/core/src/modules/attachments/AGENTS.md`) with module-specific conventions; consult before porting.
- **Partition public/private asymmetry**: a public partition can be reached via field config or default resolution but NOT via explicit `partitionCode` form override (403) — prevents smuggling private files into anonymous-serving partitions.
- **configs module has no generic tenant-scoped settings API** — module settings routes (e.g. translations locales) each wrap `moduleConfigService` themselves; the 4 configs API files are cache/system-status/upgrade-actions, not a settings CRUD.
- **Search module configs are process-global state** (`registerSearchModuleConfigs` on `globalThis`-adjacent module singleton), as is the translatable-fields registry (explicitly `globalThis` to survive bundler module duplication). Ports should use a proper DI-scoped registry but must load all module declarations before serving requests.
- **Advisory locks**: notifications dedupe (and search reindex lock) rely on PostgreSQL advisory locks (`pg_advisory_xact_lock(hashtext(...))`) — ports must use the same hash input strings if multiple runtimes (TS + port) ever share a database.
- **`entity_translations.entity_id` is `text`, not `uuid`** — supports non-UUID record ids; keep it text in ported schemas. Same for `attachments.entity_id`/`record_id`.
- **Counting caveat**: enterprise/customer_accounts/customers api counts include some route-helper files under `api/`; treat the inventory sizes as approximations (±10%) for effort planning, not exact route counts.
