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


@dataclass(frozen=True)
class Module:
    """One backend module (upstream: packages/core/src/modules/<id>/)."""

    id: str
    router: APIRouter | None = None
    entities: list[type] = field(default_factory=list)
    workers: list[WorkerSpec] = field(default_factory=list)
    subscribers: list[SubscriberSpec] = field(default_factory=list)
    acl_features: list[str] = field(default_factory=list)


def load_modules() -> list[Module]:
    """Return all enabled modules (upstream: apps/mercato/src/modules.ts)."""
    from om.modules import MODULES

    return MODULES


def all_workers() -> list[WorkerSpec]:
    return [w for m in load_modules() for w in m.workers]


def all_subscribers() -> list[SubscriberSpec]:
    return [s for m in load_modules() for s in m.subscribers]
