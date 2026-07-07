"""Module registry.

Mirrors Open Mercato's auto-discovered module surface
(packages/core/src/modules/<module>/). In the TS stack files are
discovered by convention (api/<method>/<path>.ts, workers/*.ts, ...);
here each module explicitly builds one `Module` object in its
__init__.py and lists it in om.modules.MODULES. Same concepts, explicit
registration instead of filesystem scanning.
"""

from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field
from typing import Any

from fastapi import APIRouter

# A worker/subscriber handler receives a job-like object exposing
# `.name` and `.data` (a BullMQ Job in redis mode, a LocalJob in local mode).
Handler = Callable[[Any], Awaitable[Any]]


@dataclass(frozen=True)
class WorkerSpec:
    """Equivalent of upstream workers/*.ts `metadata = { queue, id?, concurrency? }`."""

    queue: str
    handler: Handler
    id: str
    concurrency: int = 1


@dataclass(frozen=True)
class SubscriberSpec:
    """Equivalent of upstream subscribers/*.ts `metadata = { event, id?, persistent? }`."""

    event: str
    handler: Handler
    id: str
    persistent: bool = False


# --- Declaration surfaces (spec 10 — module contract parity) --------------
# These mirror the upstream per-module convention files that declare — but do
# not themselves deliver — cross-cutting surfaces. Each is plain data hanging
# off the Module so the registry can aggregate it in enabled-module order.
# "Declare now, engine later": the storage/delivery engine behind each may be
# a PORT-TODO until the owning module (notifications / entities) is ported.


@dataclass(frozen=True)
class NotificationType:
    """Equivalent of upstream notifications.ts `NotificationTypeDefinition`."""

    type: str
    module: str
    severity: str = "info"
    title_key: str | None = None
    icon: str | None = None
    expires_after_hours: int | None = None
    link_href: str | None = None


@dataclass(frozen=True)
class CustomField:
    """One field in a custom-field set (upstream CustomFieldDefinition)."""

    key: str
    kind: str
    label: str | None = None
    required: bool = False


@dataclass(frozen=True)
class CustomFieldSet:
    """Equivalent of upstream data/fields.ts `CustomFieldSet`."""

    entity_id: str  # "<module>:<entity>" target
    source: str  # declaring module id
    fields: list[CustomField] = field(default_factory=list)


@dataclass(frozen=True)
class CustomEntity:
    """Equivalent of an upstream ce.ts custom entity (fields feed field sets)."""

    id: str
    label: str | None = None
    description: str | None = None
    fields: list[CustomField] = field(default_factory=list)


@dataclass(frozen=True)
class DeclaredEvent:
    """Equivalent of an upstream events.ts declared event.

    `name` is the byte-exact `<module>.<entity>.<verb>` id; `persistent` marks
    durable (queue-backed) events; `client_broadcast` marks SSE-forwarded ones.
    """

    name: str
    persistent: bool = False
    client_broadcast: bool = False


@dataclass(frozen=True)
class Module:
    """One backend module (upstream: packages/core/src/modules/<id>/)."""

    id: str
    router: APIRouter | None = None
    entities: list[type] = field(default_factory=list)
    workers: list[WorkerSpec] = field(default_factory=list)
    subscribers: list[SubscriberSpec] = field(default_factory=list)
    # Declaration surfaces (spec 10). Default to empty so the contract shape is
    # identical across every module and aggregation is total.
    acl_features: list[str] = field(default_factory=list)
    notification_types: list[NotificationType] = field(default_factory=list)
    custom_field_sets: list[CustomFieldSet] = field(default_factory=list)
    custom_entities: list[CustomEntity] = field(default_factory=list)
    declared_events: list[DeclaredEvent] = field(default_factory=list)


def load_modules() -> list[Module]:
    """Return all enabled modules (upstream: apps/mercato/src/modules.ts)."""
    from om.modules import MODULES

    return MODULES


def all_workers() -> list[WorkerSpec]:
    return [w for m in load_modules() for w in m.workers]


def all_subscribers() -> list[SubscriberSpec]:
    return [s for m in load_modules() for s in m.subscribers]


# --- Declaration-surface aggregators (spec 10 §MODCONTRACT-R7) ------------
# Pure folds over the enabled-module list, in enabled-module order.


def all_acl_features() -> list[str]:
    return [f for m in load_modules() for f in m.acl_features]


def all_notification_types() -> list[NotificationType]:
    return [n for m in load_modules() for n in m.notification_types]


def all_custom_field_sets() -> list[CustomFieldSet]:
    return [s for m in load_modules() for s in m.custom_field_sets]


def all_custom_entities() -> list[CustomEntity]:
    return [e for m in load_modules() for e in m.custom_entities]


def all_declared_events() -> list[DeclaredEvent]:
    return [e for m in load_modules() for e in m.declared_events]
