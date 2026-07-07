"""health_check — reference module showing the full wiring pattern.

Upstream analogue: packages/core/src/modules/<module>/ with api/,
data/entities.ts, data/validators.ts, workers/, subscribers/, acl.ts.
"""

from om.modules.health_check.acl import features
from om.modules.health_check.api import router
from om.modules.health_check.entities import OmHealthPing
from om.modules.health_check.subscribers import on_pinged
from om.modules.health_check.workers import noop
from om.shared.registry import Module, SubscriberSpec, WorkerSpec

MODULE = Module(
    id="health_check",
    router=router,
    entities=[OmHealthPing],
    workers=[
        WorkerSpec(queue="health_check", handler=noop, id="health_check.noop"),
    ],
    subscribers=[
        SubscriberSpec(
            event="health_check.pinged", handler=on_pinged, id="health_check.on_pinged"
        ),
    ],
    acl_features=features,
    # Declaration surfaces (spec 10). health_check declares none — empty, not
    # omitted, so the contract shape matches every other module.
    notification_types=[],
    custom_field_sets=[],
    custom_entities=[],
    declared_events=[],
)
