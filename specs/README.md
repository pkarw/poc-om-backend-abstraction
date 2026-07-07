# 📐 Specs — Normative, Technology-Agnostic Requirements

Requirement specs for porting Open Mercato backend modules, derived from the pinned upstream commit ([`../upstream/UPSTREAM.md`](../upstream/UPSTREAM.md)). Every requirement has a stable ID (`<PREFIX>-R<n>`) cited by port contracts, ADRs, and parity reports. Start at [00-overview.md](00-overview.md).

| # | Spec | Prefix | Scope |
|---|---|---|---|
| 00 | [Overview](00-overview.md) | — | Goal, upstream architecture summary, spec map, porting loop, compatibility philosophy |
| 01 | [Module System](01-module-system.md) | `MODULESYSTEM` | Module composition, convention artifacts, dispatch pipeline, DI scopes, overrides, setup/seed lifecycle |
| 02 | [API Compatibility](02-api-compatibility.md) | `APIHTTP` | HTTP routing, guards, CRUD contract, error envelopes, custom fields on the wire, interceptors/enrichers, OpenAPI |
| 03 | [Data Layer](03-data-layer.md) | `DATALAYER` | Postgres conventions, per-module migrations, multi-tenancy, custom fields, query engine, command pipeline |
| 04 | [Events & Queues](04-events-and-queues.md) | `EVENTSQUEUES` | BullMQ-compatible queues, worker host, event bus, NOTIFY bridge + SSE, scheduler |
| 05 | [Auth & RBAC](05-auth-and-rbac.md) | `AUTHRBAC` | JWTs, staff/customer/API-key auth, sessions, feature-based RBAC, rate limiting |
| 06 | [Runtime & Startup](06-runtime-and-startup.md) | `RUNTIMESTARTUP` | CLI, init/seed pipeline, migrations tooling, production guards, cache subsystem, container contracts |
| 07 | [Shared Services](07-shared-services.md) | `SHAREDSERVICES` | Shared helpers plus translations, notifications, attachments, audit logs, dictionaries, configs, feature toggles, progress, search |
| 08 | [Parity Testing](08-parity-testing.md) | `PARITY` | How 1:1 is proven: golden request/response tests, schema/queue/event parity, authz matrix, report format |
| 09 | [Technology Package Standard](09-technology-package-standard.md) | `TECHPKG` | The canonical shape of every `packages/<tech>/`: layout, Makefile, compose, AGENTS.md/README outlines, queue honesty rule |

Specs 01–07 share the skeleton **Scope → Requirements → Contracts → Concept mapping → Allowed deviations → Verification**. Specs are normative: if reality forces a deviation, change the spec or record an ADR in `packages/<tech>/docs/decisions/` — never silently diverge (see [../AGENTS.md](../AGENTS.md)).
