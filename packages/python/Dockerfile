# Multi-stage build: one image serves both the api and the worker
# (docker-compose overrides the command for the worker service).

FROM ghcr.io/astral-sh/uv:python3.12-bookworm-slim AS builder
WORKDIR /app
ENV UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy UV_PYTHON_DOWNLOADS=never
COPY pyproject.toml ./
COPY src ./src
COPY alembic.ini ./
COPY migrations ./migrations
RUN uv sync --no-dev

FROM python:3.12-slim AS runtime
WORKDIR /app
COPY --from=builder /app /app
ENV PATH="/app/.venv/bin:$PATH" PYTHONUNBUFFERED=1
EXPOSE 8000
# api (default): run migrations, then serve. worker: see docker-compose.yml.
CMD ["sh", "-c", "alembic upgrade head && uvicorn om.api:app --host 0.0.0.0 --port ${PORT:-8000}"]
