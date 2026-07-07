"""Environment configuration.

Uses the exact same variable names as upstream Open Mercato
(DATABASE_URL, REDIS_URL, QUEUE_STRATEGY, QUEUE_REDIS_URL, JWT_SECRET).
Values are read from the process environment and, when present, a local
`.env` file (pydantic-settings).
"""

from functools import lru_cache
from typing import Literal

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    database_url: str = "postgres://postgres:postgres@localhost:5432/mercato"
    redis_url: str = "redis://localhost:6379"
    queue_strategy: Literal["local", "redis"] = "local"
    queue_redis_url: str | None = None
    jwt_secret: str = "dev-secret-do-not-use-in-production"
    port: int = 8000

    @property
    def sqlalchemy_database_url(self) -> str:
        """Upstream uses `postgres://` URLs (MikroORM); SQLAlchemy async
        needs the `postgresql+asyncpg://` scheme. Normalize here so the
        env var value stays byte-identical with the TS stack."""
        url = self.database_url
        for prefix in ("postgres://", "postgresql://"):
            if url.startswith(prefix):
                return "postgresql+asyncpg://" + url[len(prefix):]
        return url

    @property
    def effective_queue_redis_url(self) -> str:
        return self.queue_redis_url or self.redis_url


@lru_cache
def get_settings() -> Settings:
    return Settings()
