"""Worker host: registers one BullMQ worker per WorkerSpec and processes jobs.

Run: python -m om.worker

With QUEUE_STRATEGY=redis this uses the official `bullmq` package, so it
processes jobs enqueued by Node BullMQ producers on the same Redis (and
Node workers can process jobs enqueued by this port).
With QUEUE_STRATEGY=local jobs execute inline in the enqueuing process,
so this host only idles (kept alive to keep container orchestration uniform).
"""

import asyncio
import logging
import signal
from typing import Any

from om.shared.config import get_settings
from om.shared.registry import all_workers

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("om.worker")


def _make_processor(handler: Any) -> Any:
    async def process(job: Any, job_token: str) -> Any:  # bullmq signature
        return await handler(job)

    return process


async def main() -> None:
    settings = get_settings()
    specs = all_workers()
    stop = asyncio.Event()

    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        loop.add_signal_handler(sig, stop.set)

    workers: list[Any] = []
    if settings.queue_strategy == "redis":
        from bullmq import Worker

        for spec in specs:
            worker = Worker(
                spec.queue,
                _make_processor(spec.handler),
                {
                    "connection": settings.effective_queue_redis_url,
                    "concurrency": spec.concurrency,
                },
            )
            workers.append(worker)
            logger.info(
                "worker registered: id=%s queue=%s concurrency=%d",
                spec.id,
                spec.queue,
                spec.concurrency,
            )
        logger.info(
            "worker host started (strategy=redis, %d worker(s), redis=%s)",
            len(workers),
            settings.effective_queue_redis_url,
        )
    else:
        logger.warning(
            "QUEUE_STRATEGY=local: jobs run inline in the enqueuing process; "
            "worker host has nothing to consume and will idle."
        )

    await stop.wait()
    logger.info("shutting down...")
    for worker in workers:
        await worker.close()


if __name__ == "__main__":
    asyncio.run(main())
