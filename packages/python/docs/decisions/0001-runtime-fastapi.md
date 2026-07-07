# 0001 — Python 3.12 + FastAPI + uvicorn + uv

## Status

Accepted

## Context

Upstream Open Mercato is a Next.js app whose backend modules expose HTTP API routes discovered from `packages/core/src/modules/<module>/api/<method>/<path>.ts`, each required to export `openApi` metadata. The port needs an async Python web stack that can (a) mount per-module routers so the module registry stays the unit of composition, (b) reproduce paths, methods, status codes and JSON shapes byte-compatibly, and (c) generate OpenAPI documentation as a first-class artifact.

## Decision

- Python 3.12 as the runtime baseline.
- FastAPI as the web framework; each module contributes an `APIRouter` that the api host mounts under `/api`, mirroring upstream route discovery.
- uvicorn as the ASGI server (`--reload` backs `make dev`).
- uv as the package manager: `pyproject.toml` is the single dependency manifest, and the Makefile uses `uv sync` / `uv run` exclusively.

## Consequences

- FastAPI's auto-generated OpenAPI replaces upstream's mandatory `openApi` exports — response models (pydantic) must be declared on every route to keep the docs truthful.
- FastAPI's default error shapes (e.g. 422 validation payloads) differ from upstream's; byte-compatible ports of real modules must override exception handlers per module contract (tracked in AGENTS.md → API Compatibility Rules).
- uv requires no pre-installed Python for contributors (it can provision 3.12), but the Docker image pins `python:3.12-slim` explicitly.
