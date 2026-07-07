"""Validators — upstream analogue: data/validators.ts (Zod).

Pydantic v2 models are the Zod equivalent: they validate inputs and
declare response shapes (also feeding FastAPI's generated OpenAPI, the
analogue of the required `openApi` export on upstream routes).
"""

from pydantic import BaseModel


class HealthChecks(BaseModel):
    database: bool
    redis: bool


class HealthCheckResponse(BaseModel):
    status: str
    module: str
    checks: HealthChecks
