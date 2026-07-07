.PHONY: up down dev worker migrate test

up:
	docker compose up --build -d

down:
	docker compose down -v

dev:
	uv sync && uv run uvicorn om.api:app --reload --port $${PORT:-8000}

worker:
	uv sync && uv run python -m om.worker

migrate:
	uv sync && uv run alembic upgrade head

test:
	uv sync && uv run pytest
