"""Queue abstraction — mirrors upstream packages/queue strategy pattern.

QUEUE_STRATEGY=redis -> official `bullmq` PyPI package (taskforcesh).
    Jobs are wire-compatible with Node BullMQ: anything enqueued here can
    be consumed by a Node worker and vice versa.
QUEUE_STRATEGY=local -> jobs run inline in the enqueuing process
    (upstream's local strategy analogue; no broker required).
"""

import logging
import uuid
from dataclasses import dataclass, field
from typing import Any, Protocol

from om.shared.config import get_settings
from om.shared.registry import all_workers

logger = logging.getLogger("om.queue")


@dataclass(frozen=True)
class LocalJob:
    """Shape-compatible subset of a BullMQ job as seen by handlers."""

    name: str
    data: dict[str, Any]
    id: str = field(default_factory=lambda: str(uuid.uuid4()))


class QueueBackend(Protocol):
    async def enqueue(
        self, queue: str, name: str, data: dict[str, Any] | None = None
    ) -> str:
        """Enqueue a job, returning its id."""
        ...

    async def close(self) -> None: ...


class LocalQueueBackend:
    """Executes the registered worker handler inline (dev/test)."""

    async def enqueue(
        self, queue: str, name: str, data: dict[str, Any] | None = None
    ) -> str:
        job = LocalJob(name=name, data=data or {})
        for spec in all_workers():
            if spec.queue == queue:
                await spec.handler(job)
        return job.id

    async def close(self) -> None:
        return None


class BullMQQueueBackend:
    """Enqueues through the official bullmq package (Node-interoperable)."""

    def __init__(self, redis_url: str) -> None:
        self._redis_url = redis_url
        self._queues: dict[str, Any] = {}

    def _queue(self, name: str) -> Any:
        if name not in self._queues:
            from bullmq import Queue  # imported lazily; needs a live Redis

            self._queues[name] = Queue(name, {"connection": self._redis_url})
        return self._queues[name]

    async def enqueue(
        self, queue: str, name: str, data: dict[str, Any] | None = None
    ) -> str:
        job = await self._queue(queue).add(name, data or {})
        return str(job.id)

    async def close(self) -> None:
        for q in self._queues.values():
            await q.close()
        self._queues.clear()


_backend: QueueBackend | None = None


def get_queue_backend() -> QueueBackend:
    global _backend
    if _backend is None:
        settings = get_settings()
        if settings.queue_strategy == "redis":
            _backend = BullMQQueueBackend(settings.effective_queue_redis_url)
        else:
            _backend = LocalQueueBackend()
        logger.info("queue backend: %s", settings.queue_strategy)
    return _backend
