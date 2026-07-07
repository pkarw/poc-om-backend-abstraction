"""Subscribers — upstream analogue: subscribers/*.ts with metadata { event }."""

import logging
from typing import Any

logger = logging.getLogger("om.health_check.subscriber")


async def on_pinged(event: Any) -> None:
    logger.info("health_check.pinged received: %r", event.data)
