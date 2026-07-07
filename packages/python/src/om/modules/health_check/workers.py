"""Workers — upstream analogue: workers/*.ts with metadata { queue, id }.

Registered on queue 'health_check' via WorkerSpec in __init__.py.
The handler receives a BullMQ job (redis strategy) or a LocalJob
(local strategy); both expose `.name` and `.data`.
"""

import logging
from typing import Any

logger = logging.getLogger("om.health_check.worker")


async def noop(job: Any) -> dict[str, Any]:
    """No-op reference worker: logs the job and returns its payload."""
    logger.info("health_check.noop processed job name=%s data=%r", job.name, job.data)
    return {"ok": True, "echo": job.data}
