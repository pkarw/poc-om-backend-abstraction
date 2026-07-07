"""API routes — upstream analogue: api/<method>/<path>.ts.

Upstream: api/get/health_check.ts -> GET /api/health_check.
Here: an APIRouter mounted by the api host under /api.
"""

import logging

from fastapi import APIRouter, Depends
from sqlalchemy import func, select
from sqlalchemy.ext.asyncio import AsyncSession

from om.modules.health_check.entities import OmHealthPing
from om.modules.health_check.validators import HealthCheckResponse, HealthChecks
from om.shared.db import get_session
from om.shared.events import emit
from om.shared.redis import get_redis

logger = logging.getLogger("om.health_check")

router = APIRouter(tags=["health_check"])


@router.get("/health_check", response_model=HealthCheckResponse)
async def health_check(
    session: AsyncSession = Depends(get_session),
) -> HealthCheckResponse:
    database_ok = False
    redis_ok = False

    try:
        # Real query against the module's own table (created by migration 0001).
        await session.execute(select(func.count()).select_from(OmHealthPing))
        database_ok = True
    except Exception:  # noqa: BLE001
        logger.exception("database ping failed")

    try:
        redis_ok = bool(await get_redis().ping())
    except Exception:  # noqa: BLE001
        logger.exception("redis ping failed")

    if database_ok and redis_ok:
        await emit("health_check.pinged", {"database": True, "redis": True})

    return HealthCheckResponse(
        status="ok" if database_ok and redis_ok else "error",
        module="health_check",
        checks=HealthChecks(database=database_ok, redis=redis_ok),
    )
