#!/usr/bin/env bash
# Customers integration test — proves the ported .NET customers module serves the SEEDED CRM dataset
# through the testbench proxy against a real Open Mercato (shared Postgres). Because the .NET API now
# seeds the shared schema itself (OM migrate-only), the content is readable (not garbled), unlike the
# old OM-seeds path where per-tenant DEK PII was undecryptable by the port.
#
# Usage: ./integration-customers.sh [base-url]   (default http://localhost:8088)
set -euo pipefail
BASE="${1:-http://localhost:8088}"
EMAIL="${OM_INIT_SUPERADMIN_EMAIL:-superadmin@acme.com}"
PASS="${OM_INIT_SUPERADMIN_PASSWORD:-secret}"

pass=0; fail=0
say()  { printf '%s\n' "$*"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; pass=$((pass+1)); }
bad()  { printf '  \033[31m✗\033[0m %s\n' "$*"; fail=$((fail+1)); }

# jqlike <json> <python-expr over `d`> — evaluate a boolean/expr against parsed JSON, print result.
pyget() { python3 -c 'import sys,json; d=json.load(sys.stdin); print(eval(sys.argv[1]))' "$1"; }

say "→ POST $BASE/api/auth/login  (served by .NET)"
login=$(curl -sS -X POST "$BASE/api/auth/login" \
  -H 'Content-Type: application/x-www-form-urlencoded' \
  --data "email=$EMAIL&password=$PASS")
echo "$login" | grep -q '"ok":true' || { echo "LOGIN FAILED: $login"; exit 1; }
TOKEN=$(printf '%s' "$login" | python3 -c 'import sys,json; print(json.load(sys.stdin)["token"])')
ok "login ok, JWT issued by .NET"
AUTH=(-H "Authorization: Bearer $TOKEN")

# get <path> -> body written to /tmp/tb-cust.json, http code to CODE (no subshell, so $CODE survives)
CODE=""
get() { CODE=$(curl -sS -o /tmp/tb-cust.json -w '%{http_code}' "$BASE$1" "${AUTH[@]}"); }

# assert_list <path> <label> <expected-total> <must-contain-substring>
assert_list() {
  local path="$1" label="$2" want="$3" needle="$4" body total
  get "$path"; body=$(cat /tmp/tb-cust.json)
  [ "$CODE" = "200" ] || { bad "$label: HTTP $CODE ($body)"; return; }
  total=$(printf '%s' "$body" | pyget 'd.get("total")')
  if [ "$total" = "$want" ]; then ok "$label: total=$total"; else bad "$label: total=$total (want $want)"; fi
  if printf '%s' "$body" | grep -qF "$needle"; then ok "$label: content readable — found '$needle'";
  else bad "$label: '$needle' not found (content garbled/empty?): $(printf '%s' "$body" | head -c 300)"; fi
}

say "→ customers dataset seeded by the .NET port (read back through the proxy)"
assert_list "/api/customers/people"    "people"    6 "Mia Johnson"
assert_list "/api/customers/companies" "companies" 3 "Brightside Solar"
assert_list "/api/customers/deals"     "deals"     6 "Redwood Residences Solar Rollout"

say "→ default pipeline (customers seedDefaults)"
get "/api/customers/pipelines"; body=$(cat /tmp/tb-cust.json)
if [ "$CODE" = "200" ] && printf '%s' "$body" | grep -qF "Default Pipeline"; then
  ok "pipelines: 'Default Pipeline' present"
else bad "pipelines: HTTP $CODE, body=$(printf '%s' "$body" | head -c 200)"; fi

say "→ dictionaries + summary endpoints answer 200"
get "/api/customers/dictionaries/status"; body=$(cat /tmp/tb-cust.json)
if [ "$CODE" = "200" ] && printf '%s' "$body" | grep -qF "active"; then ok "dictionaries/status has 'active'"; else bad "dictionaries/status HTTP $CODE"; fi
get "/api/customers/deals/summary"; [ "$CODE" = "200" ] && ok "deals/summary 200" || bad "deals/summary HTTP $CODE"

say ""
if [ "$fail" -eq 0 ]; then
  printf '\033[32mPASS\033[0m — %d checks; the .NET-seeded customers CRM is fully readable through the OM testbench.\n' "$pass"
  exit 0
else
  printf '\033[31mFAIL\033[0m — %d passed, %d failed.\n' "$pass" "$fail"
  exit 1
fi
