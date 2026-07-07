# 0011 — Extended module contract: first-class declaration surface

Status: accepted
Date: 2026-07-07

## Context

Open Mercato modules declare a rich, consistent surface via optional files
(`upstream/analysis/07-shared-services.md`, `01-module-system.md`):

- `acl.ts` — RBAC feature declarations (`<module>.<action>` ids + titles)
- `events.ts` — `createModuleEvents({ moduleId, events })`: declared typed event names + payload shapes
- `notifications.ts` — `notificationTypes: NotificationTypeDefinition[]` (type, severity, expiresAfterHours, templates)
- `ce.ts` / `data/fields.ts` — custom entities & custom-field sets attached to entities (EAV)

The .NET `IModule` contract (`OpenMercato.Core.Modules.IModule`) previously carried only `Id`,
`AclFeatures` (bare `string[]`), `ConfigureServices`, `ConfigureModel`, `MapRoutes`. A ported module
could not express its notifications, typed events, richer ACL titles, or custom-field sets the same
way upstream does. This ADR makes those pieces first-class so every ported module declares the same
surface identically — mirroring upstream — and defines the shape to be replicated 1:1 in the
python/golang packages.

## Decision

### 1. Core declaration types (`OpenMercato.Core.Modules`)

New records, each mapped to its upstream file:

- `NotificationTypeDefinition(Type, Severity, ExpiresAfterHours?, TitleTemplate?, BodyTemplate?)` — `notifications.ts`.
- `CustomFieldDefinition(Key, Kind, Label, Required, Multi, Options?)` and
  `CustomFieldSet(EntityId, Fields)` — `ce.ts` / `data/fields.ts`.
- `AclFeatureDefinition(Id, Title)` — the richer form of `acl.ts` (the existing `AclFeatures`
  `string[]` stays for back-compat).
- `EventDeclaration(Name, PayloadShape?, Persistent)` — `events.ts`.

### 2. `IModule` extended with OPTIONAL members via C# default interface implementations

Existing modules (HealthCheck) compile unchanged; a module opts in only where it has something to
declare:

```csharp
IReadOnlyList<AclFeatureDefinition> AclFeatureDefinitions =>
    AclFeatures.Select(f => new AclFeatureDefinition(f, f)).ToList();
IReadOnlyList<NotificationTypeDefinition> NotificationTypes => Array.Empty<NotificationTypeDefinition>();
IReadOnlyList<CustomFieldSet> CustomFieldSets => Array.Empty<CustomFieldSet>();
IReadOnlyList<EventDeclaration> DeclaredEvents => Array.Empty<EventDeclaration>();
```

`AclFeatureDefinitions` defaults to deriving `{ id, title = id }` from the bare `AclFeatures`, so a
module that only declares ids still surfaces in the richer aggregation.

### 3. `ModuleRegistry` aggregations

`AllAclFeatureDefinitions` (deduped by id, first module wins), `AllNotificationTypes`,
`AllDeclaredEvents` and `AllCustomFieldSets` flatten across modules. Notifications and events are
validated for duplicate names across modules and throw `InvalidOperationException` on conflict —
mirroring the existing duplicate module-id guard.

### 4. Runtime "supported" catalogs (Core)

- `INotificationCatalog` + `NotificationCatalog` (built from `ModuleRegistry.AllNotificationTypes`):
  `bool IsKnown(string type)`, `NotificationTypeDefinition? Get(string type)`, `All`.
- `ICustomFieldRegistry` + `CustomFieldRegistry` (built from `ModuleRegistry.AllCustomFieldSets`):
  `ForEntity(entityId)`, `All`.

Both are registered as singletons in **both** hosts (`OpenMercato.Api/Program.cs` and
`OpenMercato.Worker/Program.cs`). They are the declared-lookup surface only. The delivery/storage
engines (notification fan-out + persistence; EAV value read/write) arrive with the notifications and
entities module ports later — marked `// PORT-TODO`.

### 5. Auth declares its real surface

`AuthModule` now declares, from the auth port contract
(`upstream/analysis/modules/auth.md`):

- `AclFeatureDefinitions`: the 8 features with exact titles.
- `NotificationTypes`: the 6 declared types (2 emitted, 4 declared-only upstream).
- `DeclaredEvents`: the 12 declared event ids with payload-shape docs + persistent flags (user/role
  CRUD persistent; login/logout/password fire-and-forget).
- `CustomFieldSets`: none — auth reads/writes custom-field *values* on `auth:user`/`auth:role` but
  declares no field *sets* (no `ce.ts`/`data/fields.ts` upstream).

## Consequences

- Ported modules express notifications, events, ACL titles and custom-field sets the same way,
  declaratively, mirroring upstream's optional files.
- The default-interface-implementation approach keeps the contract additive and non-breaking.
- Interface-mapping gotcha: a class that inherits `IModule` through an abstract base and merely
  shadows a member gets the *default* implementation, not its own. To override, the concrete type
  must re-list `IModule` in its base list (or implement the member on the type that lists the
  interface). This affects test stubs and any future base-class module hierarchies.
- The python/golang packages must define the identical shape: four declaration types, optional
  per-module declarations, registry aggregation with duplicate guards, and the two runtime catalogs.

## Verification

`dotnet build OpenMercato.sln` — 0 errors. `dotnet test OpenMercato.sln` — 71 passed (9 new tests
covering auth's 6 notifications / 12 events / 8 feature defs, `NotificationCatalog.IsKnown`,
`CustomFieldRegistry.ForEntity`, and the duplicate-name guards).
