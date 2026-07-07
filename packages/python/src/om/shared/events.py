"""Minimal in-process event bus.

Upstream (packages/events) supports local and persistent (queue-backed)
subscribers. This scaffold dispatches events in-process to subscribers
registered via SubscriberSpec; persistent delivery through the queue is
an explicit porting task (see AGENTS.md -> Queues & Events).
"""

import logging
from dataclasses import dataclass
from typing import Any

from om.shared.registry import all_subscribers

logger = logging.getLogger("om.events")


@dataclass(frozen=True)
class Event:
    name: str
    data: dict[str, Any]


async def emit(event_name: str, data: dict[str, Any] | None = None) -> None:
    event = Event(name=event_name, data=data or {})
    for spec in all_subscribers():
        if spec.event != event_name:
            continue
        try:
            await spec.handler(event)
        except Exception:  # noqa: BLE001 - subscribers must not break emitters
            logger.exception("subscriber %s failed for event %s", spec.id, event_name)
