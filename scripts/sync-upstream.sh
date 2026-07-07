#!/usr/bin/env bash
# sync-upstream.sh — refresh the Open Mercato upstream clone cache and diff it
# against the pinned commit recorded in upstream/UPSTREAM.md.
#
# Usage:
#   ./scripts/sync-upstream.sh [ref]     # ref defaults to origin/main
#
# Idempotent and non-destructive: only clones/fetches into .upstream-cache/
# (gitignored) and prints information. It never modifies UPSTREAM.md, the
# analysis docs, or anything inside the cache working tree — bumping the pin
# is the job of the om-sync-upstream skill.

set -euo pipefail

UPSTREAM_URL="https://github.com/open-mercato/open-mercato.git"
REF="${1:-origin/main}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
CACHE_DIR="${REPO_ROOT}/.upstream-cache"
UPSTREAM_MD="${REPO_ROOT}/upstream/UPSTREAM.md"

# --- 1. Parse the pinned commit from upstream/UPSTREAM.md -------------------
if [[ ! -f "${UPSTREAM_MD}" ]]; then
  echo "ERROR: ${UPSTREAM_MD} not found — cannot determine pinned commit." >&2
  exit 1
fi

PINNED="$(grep -oE '[0-9a-f]{40}' "${UPSTREAM_MD}" | head -n1 || true)"
if [[ -z "${PINNED}" ]]; then
  echo "ERROR: no 40-char commit SHA found in ${UPSTREAM_MD}." >&2
  exit 1
fi

# --- 2. Clone or update the cache -------------------------------------------
if [[ -d "${CACHE_DIR}/.git" ]]; then
  echo "==> Updating existing clone in .upstream-cache/ ..."
  git -C "${CACHE_DIR}" fetch --tags --prune origin
else
  echo "==> Cloning ${UPSTREAM_URL} into .upstream-cache/ ..."
  git clone "${UPSTREAM_URL}" "${CACHE_DIR}"
fi

# --- 3. Resolve the requested ref -------------------------------------------
if ! TARGET="$(git -C "${CACHE_DIR}" rev-parse --verify --quiet "${REF}^{commit}")"; then
  echo "ERROR: ref '${REF}' not found in upstream clone." >&2
  exit 1
fi
TARGET_DATE="$(git -C "${CACHE_DIR}" show -s --format=%cs "${TARGET}")"
TARGET_SUBJECT="$(git -C "${CACHE_DIR}" show -s --format=%s "${TARGET}")"

if ! git -C "${CACHE_DIR}" cat-file -e "${PINNED}^{commit}" 2>/dev/null; then
  echo "ERROR: pinned commit ${PINNED} not present in the clone (shallow or rewritten history?)." >&2
  exit 1
fi
PINNED_DATE="$(git -C "${CACHE_DIR}" show -s --format=%cs "${PINNED}")"

# --- 4. Report ---------------------------------------------------------------
echo
echo "Pinned commit : ${PINNED} (${PINNED_DATE})"
echo "Candidate ref : ${REF}"
echo "Candidate     : ${TARGET} (${TARGET_DATE}) — ${TARGET_SUBJECT}"
echo

if [[ "${TARGET}" == "${PINNED}" ]]; then
  echo "Up to date: candidate equals the pinned commit. Nothing to do."
  exit 0
fi

BEHIND_AHEAD="$(git -C "${CACHE_DIR}" rev-list --left-right --count "${PINNED}...${TARGET}")"
echo "Commits (pinned-only <-> candidate-only): ${BEHIND_AHEAD}"
echo
echo "==> Summarized diff (pinned -> candidate):"
git -C "${CACHE_DIR}" diff --stat=120 "${PINNED}" "${TARGET}" | tail -n 40
echo
echo "(full stat: git -C .upstream-cache diff --stat ${PINNED} ${TARGET})"
echo
echo "==> Next steps"
echo "  1. Review the diff above, focusing on packages/core/src/modules and packages/{shared,queue,events,cache}."
echo "  2. Run the om-sync-upstream skill (/om-sync-upstream) to bump the pin in upstream/UPSTREAM.md"
echo "     and regenerate stale docs in upstream/analysis/."
echo "  3. Re-review ported modules in MODULES.md against the new pin (om-verify-parity)."
