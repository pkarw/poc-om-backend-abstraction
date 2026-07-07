# Spec 10 — Module Contract Parity

> Derived from upstream commit adc9da27759e357febe9ed8d4b7182040d127349. Source analysis: ../upstream/analysis/01-module-system.md and ../upstream/analysis/07-shared-services.md.
>
> Normative for every `packages/<tech>/` module abstraction. Companion to spec 09 (Technology Package Standard). Requirement ids are `MODCONTRACT-R<n>`. Terminology: MUST / SHOULD / MAY per RFC 2119. `<tech>` = the package directory name (`python`, `dotnet`, `golang`, …).

## Scope

Open Mercato modules are self-contained vertical slices that declare a **rich, consistent surface by file convention** (spec 01 §2). Beyond HTTP routes, ORM entities, migrations, queue workers, event subscribers and DI registration, a module also *declares*, in dedicated convention files at its root:

| Upstream file | Declares |
|---|---|
| `acl.ts` | RBAC feature definitions (`<module>.<action>` id + human title) |
| `notifications.ts` | reusable notification **type** definitions (type, severity, expiry, templates, actions) |
| `ce.ts` + `data/fields.ts` | custom entities (EAV) and custom-field **sets** attached to an entity |
| `events.ts` | the module's declared, typed events (`module.entity.verb`, payload shape, persistence/broadcast flags) |

This spec fixes the requirement that **every technology's module abstraction expose these four declaration surfaces the same way** — as first-class members of the core module contract, declared in **one consistent place per module**, and aggregated by the runtime into cross-module registries. This mirrors the upstream `Module` type (`packages/shared/src/modules/registry.ts`, spec 01 §"Public contracts"), whose `features`, `customFieldSets`, `customEntities` (and, via the generator extensions, notification types and declared events) are all fields of one composed unit.

Out of scope: the delivery/storage engines behind these declarations (the notifications table + worker, the EAV storage, the event bus persistence). Those belong to the notifications / entities / events modules and their specs (04, 07). This spec governs only the **declaration contract** — see MODCONTRACT-R9 ("declare-now, engine-later").

Secondary declaration surfaces (`translations.ts` translatable-field sets, `search.ts` search configs) follow the same aggregation pattern and SHOULD reuse it, but are specified in spec 07 and are not required by this document.

## Requirements

### The four declaration surfaces

- **MODCONTRACT-R1** — The core module contract of every technology (upstream `Module`; .NET `IModule`; Python `Module` dataclass; Go `registry.Module`) MUST let a module declare **all four** of the following, in the same single per-module place it already declares routes/entities/workers/subscribers, with no separate side-channel or global mutation:
  1. **ACL feature definitions** (MODCONTRACT-R2),
  2. **Notification type definitions** (MODCONTRACT-R3),
  3. **Custom-field sets and custom entities** (MODCONTRACT-R4),
  4. **Declared events** (MODCONTRACT-R5).

  A module that declares none of a given surface expresses it as an **empty collection**, never as an omitted/undefined member — so the contract shape is identical across all modules and the aggregator (MODCONTRACT-R7) is total.

- **MODCONTRACT-R2 (ACL features)** — Upstream `acl.ts` exports `Array<{ id, title, module }>` where `id` is `<module>.<area>.<action>` (spec 05). Each technology MUST expose an ACL-feature declaration on the module contract. A technology MAY carry the full `{ id, title, module }` triple (as .NET does) or, transitionally, a list of feature-id strings where `title` defaults to the id and `module` defaults to the module id (as Python/Go do today). Feature ids MUST be preserved byte-for-byte from upstream. Role-feature seeding and route enforcement consume the aggregated set (spec 05).

- **MODCONTRACT-R3 (notification types)** — Upstream `notifications.ts` exports `notificationTypes: NotificationTypeDefinition[]`. Each technology MUST expose a notification-type declaration on the module contract. A declared notification type MUST carry at least: `type` (stable id), `module` (owning module id), `severity`, and MUST be able to carry `titleKey`, `icon`, `expiresAfterHours`, `actions[]` and `linkHref` template fields (spec 07 §notifications). Field names map to each tech's naming convention (camelCase JSON ↔ snake_case Python ↔ PascalCase Go/.NET) but the concept set is identical.

- **MODCONTRACT-R4 (custom fields & custom entities)** — Upstream declares custom-field sets in `data/fields.ts` (`CustomFieldSet` = `{ entityId, fields[], source? }`) and custom entities in `ce.ts` (`{ id, label?, description?, fields? }`); a `ce.ts` entity with a non-empty `fields` list also contributes a custom-field set with `source: <moduleId>` (spec 01 §2.5, §"entities"). Each technology MUST expose **both** a custom-field-set declaration and a custom-entity declaration on the module contract. A custom-field set MUST carry `entityId` (the `<module>:<entity>` target), a `source` (declaring module id) and a list of field definitions (each at least `key` + `kind`/type, plus `label?`, `required?`). Techs MAY fold the `ce.ts`→`customFieldSets` derivation into the aggregator or keep the two lists independent, provided the aggregated result is equivalent.

- **MODCONTRACT-R5 (declared events)** — Upstream `events.ts` calls `createModuleEvents({ moduleId, events })`, producing typed event ids `<module>.<entity>.<verb>` and marking `persistent` (durable, queue-backed) and `clientBroadcast`/`clientBroadcast: true` (SSE-forwarded) events (spec 01 §5, spec 04). Each technology MUST expose a declared-event list on the module contract. A declared event MUST carry `name` and MUST be able to carry `persistent` and `clientBroadcast` flags and a payload-shape reference (a schema/type handle idiomatic to the tech). Event names MUST be preserved byte-for-byte — they are a cross-stack wire contract (spec 04).

### Already-required surfaces (restated for completeness)

- **MODCONTRACT-R6** — The contract MUST continue to expose, alongside R2–R5, the previously required members: the module **id**, HTTP **routes**, ORM **entities** + **migrations**, queue **workers**, event **subscribers**, and **DI/service registration** (spec 01, 03, 04). R1–R5 extend this set; they do not replace it. No declaration surface may be expressed by a mechanism that bypasses the single module contract object (e.g. import-time global registration) — declarations MUST be data hanging off the module, so the aggregator and any override layer (spec 01 §9) can see them.

### Aggregation

- **MODCONTRACT-R7 (registry aggregation)** — The runtime MUST aggregate each declaration surface across **all enabled modules, in enabled-module order** (spec 01 §"Behavioral details" #1) into a single cross-module registry per surface, exposed by the platform/registry layer: `all ACL features`, `all notification types`, `all custom-field sets`, `all custom entities`, `all declared events`. Aggregation MUST be a pure fold over the enabled-module list (the upstream `Module[]` → registry step, minus the globalThis workaround). Duplicate-id handling MUST follow the relevant subsystem spec (e.g. duplicate encryption maps throw, spec 01 #14; feature ids union additively, spec 05).

- **MODCONTRACT-R8** — Aggregators MUST be total over R1's empty-collection rule: a module contributing an empty list contributes nothing but never breaks aggregation. The aggregated registries are what downstream engines (RBAC seeding, notification catalog, EAV schema, event-bus declared-event registry) consume — never the raw per-module files.

### Declare-now, engine-later

- **MODCONTRACT-R9 (declaration mandatory, engine deferrable)** — Declaring all four surfaces on the module contract is **mandatory now**, for every ported module, even in technologies where the consuming engine is not yet ported. The full **delivery/storage engine** behind a surface MAY be a documented `PORT-TODO` until the owning module is ported:
  - notification **types** MUST be declarable now; the notifications table + delivery worker (spec 07) MAY be deferred until the `notifications` module is ported.
  - custom-field **sets** / custom **entities** MUST be declarable now; the EAV storage + query overlay (spec 03/07) MAY be deferred until the `entities` module is ported.
  - declared **events** MUST be declarable now; persistent/queue-backed and SSE-broadcast delivery (spec 04) MAY be deferred where the event/queue infra is still local-only.
  - ACL features are **not** deferrable in this sense: features declared by a module MUST be enforceable/seedable as soon as that module ships guarded routes (spec 05).

  A deferred engine MUST be recorded as a `PORT-TODO` in the package's `AGENTS.md` or an ADR, and MUST NOT change the declaration shape — when the engine lands it consumes the already-declared data unchanged.

## Cross-technology mapping

Same concept, one declaration place per module, aggregated in enabled-module order. Naming is tech-idiomatic; the concept set is identical.

| Upstream file → concept | .NET `IModule` member | Python `Module` (dataclass) attribute + registry | Go `registry.Module` field + registry aggregator |
|---|---|---|---|
| `acl.ts` → RBAC feature defs (`<module>.<action>`) | `IReadOnlyList<AclFeature> AclFeatures` (id+title; today `string[]`) | `acl_features: list[str]` (id-only today; `all_acl_features()`) | `Features []string` (`registry.Features(mods)`) |
| `notifications.ts` → notification type defs | `IReadOnlyList<NotificationTypeDefinition> NotificationTypes` | `notification_types: list[NotificationType]` (`all_notification_types()`) | `NotificationTypes []NotificationType` (`registry.NotificationTypes(mods)`) |
| `data/fields.ts` → custom-field sets | `IReadOnlyList<CustomFieldSet> CustomFieldSets` | `custom_field_sets: list[CustomFieldSet]` (`all_custom_field_sets()`) | `CustomFieldSets []CustomFieldSet` (`registry.CustomFieldSets(mods)`) |
| `ce.ts` → custom entities (feed field sets) | `IReadOnlyList<CustomEntitySpec> CustomEntities` | `custom_entities: list[CustomEntity]` (`all_custom_entities()`) | `CustomEntities []CustomEntity` (`registry.CustomEntities(mods)`) |
| `events.ts` → declared typed events | `IReadOnlyList<EventDefinition> DeclaredEvents` | `declared_events: list[DeclaredEvent]` (`all_declared_events()`) | `DeclaredEvents []EventDef` (`registry.DeclaredEvents(mods)`) |
| *(already required)* routes / entities / workers / subscribers / DI | `MapRoutes` / `ConfigureModel` / `ConfigureServices` (registers `IJobHandler`, `IEventSubscriber`) | `router` / `entities` / `workers` / `subscribers` / DI via `om.shared.*` | `Routes` / `entities.go` structs / `Workers` / `Subscribers` / `Deps` |

The three ports keep the shapes **obviously parallel**: one contract object per module, four new declaration collections (plus custom entities), each defaulting to empty, each aggregated by a `registry`-level fold that parallels the existing feature/worker/subscriber aggregation.

## References

- Spec 01 — Module System & App Composition (the `Module` contract, generator extensions, enabled-module order, override domains).
- Spec 04 — Events, Queues & Scheduler (declared-event registry, persistent + `clientBroadcast` semantics).
- Spec 05 — Auth & RBAC (ACL feature ids, role-feature seeding, route enforcement).
- Spec 07 — Shared Services (`NotificationTypeDefinition`, custom fields/EAV, translatable fields, search configs).
- Spec 09 — Technology Package Standard (package layout, `AGENTS.md` outline, reference `health_check` module).
