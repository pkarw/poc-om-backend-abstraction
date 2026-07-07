"""Liveness + module registry tests (no database/Redis required)."""

import httpx
import pytest

from om.api import app
from om.shared.registry import load_modules


@pytest.mark.asyncio
async def test_healthz_returns_service_identity() -> None:
    transport = httpx.ASGITransport(app=app)
    async with httpx.AsyncClient(transport=transport, base_url="http://test") as client:
        response = await client.get("/healthz")
    assert response.status_code == 200
    assert response.json() == {"status": "ok", "service": "python-api"}


def test_health_check_module_is_registered() -> None:
    modules = {m.id: m for m in load_modules()}
    module = modules["health_check"]
    assert module.router is not None
    assert [w.queue for w in module.workers] == ["health_check"]
    assert "health_check.view" in module.acl_features


def test_module_routes_are_mounted_under_api() -> None:
    paths = {route.path for route in app.routes}
    assert "/api/health_check" in paths
