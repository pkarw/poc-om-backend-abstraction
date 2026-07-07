"""ACL features — upstream analogue: acl.ts `features` export.

Feature ids follow the upstream `<module>.<action>` convention.
Route-level enforcement middleware is a porting task (see AGENTS.md).
"""

features: list[str] = [
    "health_check.view",
]
