"""API host: FastAPI app serving /healthz and all module routers under /api.

Run: uvicorn om.api:app --reload
"""

import logging
from contextlib import asynccontextmanager

from fastapi import FastAPI

from om.shared.db import dispose_engine
from om.shared.redis import close_redis
from om.shared.registry import load_modules

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("om.api")


@asynccontextmanager
async def lifespan(app: FastAPI):
    modules = load_modules()
    logger.info("api starting with modules: %s", [m.id for m in modules])
    yield
    await dispose_engine()
    await close_redis()


app = FastAPI(title="open-mercato-python", lifespan=lifespan)


@app.get("/healthz")
async def healthz() -> dict[str, str]:
    """Liveness probe — must not touch the database or Redis."""
    return {"status": "ok", "service": "python-api"}


for module in load_modules():
    if module.router is not None:
        app.include_router(module.router, prefix="/api")
