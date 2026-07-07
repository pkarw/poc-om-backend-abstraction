"""Enabled modules — equivalent of upstream apps/mercato/src/modules.ts.

Add newly ported modules to MODULES to activate their routes, entities,
workers, subscribers and ACL features.
"""

from om.modules.health_check import MODULE as health_check
from om.shared.registry import Module

MODULES: list[Module] = [
    health_check,
]
