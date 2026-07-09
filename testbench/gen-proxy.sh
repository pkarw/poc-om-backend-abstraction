#!/usr/bin/env bash
# Regenerate the Caddyfile's ported-module route matcher from ported-modules.txt.
# Run after porting a new module to .NET so the testbench proxy sends its /api/*
# to the .NET service instead of Open Mercato.
set -euo pipefail
cd "$(dirname "$0")"

modules=$(grep -vE '^\s*#' ported-modules.txt | grep -vE '^\s*$' | tr -d '\r')
[ -n "$modules" ] || { echo "No modules in ported-modules.txt" >&2; exit 1; }

paths=""
while IFS= read -r m; do
  [ -n "$m" ] || continue
  paths="$paths /api/$m/*"
done <<< "$modules"
paths="${paths# }"

block=$(cat <<EOF
	# --- BEGIN generated ported-module routes (./gen-proxy.sh) ---
	# NOTE: /api/auth/admin/nav (the sidebar) is served by .NET, which fetches OM's own nav
	# (OM_NAV_UPSTREAM) and FILTERS it to the ported modules — so the testbench sidebar shows only
	# ported modules. See OpenMercato.Modules.Auth/Api/AdminNavRouteGroup.cs.
	@ported path $paths
	handle @ported {
		reverse_proxy dotnet-api:8080
	}
	# --- END generated ported-module routes ---
EOF
)

# Replace the block between the BEGIN/END markers in place.
awk -v repl="$block" '
  /BEGIN generated ported-module routes/ {print repl; skip=1; next}
  /END generated ported-module routes/ {skip=0; next}
  skip {next}
  {print}
' Caddyfile > Caddyfile.tmp && mv Caddyfile.tmp Caddyfile

echo "Updated Caddyfile ported routes:$paths"
