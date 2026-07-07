# Python Port — Stack

| Component        | Choice                          | Version   | Rationale                                                                 |
| ---------------- | ------------------------------- | --------- | ------------------------------------------------------------------------- |
| Runtime          | Python                          | 3.12      | Current stable with mature async ecosystem support.                        |
| Web framework    | FastAPI                         | >=0.115   | Async, router composition maps 1:1 to module route mounting, built-in OpenAPI (analogue of upstream's required `openApi` exports). |
| ASGI server      | uvicorn[standard]               | >=0.34    | De-facto standard ASGI server with hot reload for `make dev`.              |
| Package manager  | uv                              | latest    | Fast, lockfile-based, single tool for venv + deps (`uv sync` / `uv run`).  |
| ORM              | SQLAlchemy 2 (async) + asyncpg  | >=2.0 / >=0.30 | The Python MikroORM equivalent; async engine matches upstream's async data layer. |
| Migrations       | Alembic                         | >=1.14    | Real migration tooling with autogenerate from module entity metadata.      |
| Redis client     | redis-py (redis.asyncio)        | >=5.2     | Official client, async API.                                                |
| Queues           | bullmq (official PyPI, taskforcesh) | >=2.11 | Full wire compatibility with Node BullMQ jobs — see ADR 0003.              |
| Validation       | pydantic v2                     | >=2.10    | The Zod equivalent; also powers FastAPI request/response schemas.          |
| Config           | pydantic-settings               | >=2.7     | Env + `.env` loading with the exact upstream variable names.               |
| Tests            | pytest + pytest-asyncio + httpx | >=8.3     | httpx ASGITransport tests routes without a running server.                 |
