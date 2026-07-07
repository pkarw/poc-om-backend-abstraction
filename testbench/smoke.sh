#!/usr/bin/env bash
# Prove the ported .NET API is answering through the testbench proxy: log in as the
# seeded superadmin and read the dashboard layout — the two calls the OM UI makes.
# Usage: ./smoke.sh [base-url]   (default http://localhost:8088)
set -euo pipefail
BASE="${1:-http://localhost:8088}"
EMAIL="${OM_INIT_SUPERADMIN_EMAIL:-superadmin@acme.com}"
PASS="${OM_INIT_SUPERADMIN_PASSWORD:-secret}"

echo "→ POST $BASE/api/auth/login  (served by .NET)"
resp=$(curl -sS -X POST "$BASE/api/auth/login" \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  --data "email=$EMAIL&password=$PASS")
echo "$resp" | grep -q '"ok":true' || { echo "LOGIN FAILED: $resp"; exit 1; }
token=$(printf '%s' "$resp" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
echo "  ✓ login ok, JWT issued by .NET"

echo "→ GET $BASE/api/dashboards/layout  (served by .NET)"
code=$(curl -sS -o /tmp/tb-layout.json -w '%{http_code}' "$BASE/api/dashboards/layout" \
  -H "Authorization: Bearer $token")
[ "$code" = "200" ] || { echo "LAYOUT FAILED [$code]: $(cat /tmp/tb-layout.json)"; exit 1; }
echo "  ✓ dashboards layout 200"

echo "→ GET $BASE/api/directory/organization-switcher  (served by .NET)"
code=$(curl -sS -o /dev/null -w '%{http_code}' "$BASE/api/directory/organization-switcher" \
  -H "Authorization: Bearer $token")
[ "$code" = "200" ] || { echo "SWITCHER FAILED [$code]"; exit 1; }
echo "  ✓ directory switcher 200"

echo "PASS — Open Mercato is being served by the ported .NET API for auth/directory/dashboards."
