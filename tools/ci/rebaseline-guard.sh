#!/usr/bin/env bash
#
# Rebaseline guard (dev-docs/quality/testing-and-ci.md:31).
# Fails if a GoldenReplay *.replay.json `expected` block changed in this PR WITHOUT its paired
# <name>.rebaseline.md being touched in the same change. This converts "silently rebaselining a
# regression" from a one-keystroke accident into a reviewed, documented action — the whole point of
# an R1 gate.
#
# Usage: rebaseline-guard.sh [BASE_REF]    (default: origin/main)
set -uo pipefail

BASE="${1:-origin/main}"

if ! git rev-parse --verify "$BASE" >/dev/null 2>&1; then
  echo "rebaseline-guard: base ref '$BASE' not found — skipping (no diff to compare against)."
  exit 0
fi

MERGE_BASE="$(git merge-base "$BASE" HEAD 2>/dev/null || echo "$BASE")"
changed="$(git diff --name-only "$MERGE_BASE"...HEAD)"

fail=0
while IFS= read -r f; do
  [ -z "$f" ] && continue
  case "$f" in
    *.replay.json)
      # Did the expected block (hash / event stream) actually change?
      if git diff "$MERGE_BASE"...HEAD -- "$f" | grep -qE '^[+-].*(stateHash|eventStream|"expected")'; then
        pair="${f%.replay.json}.rebaseline.md"
        if ! printf '%s\n' "$changed" | grep -qxF "$pair"; then
          echo "REBASELINE GUARD FAIL: '$f' changed its expected block but paired '$pair' was not touched."
          echo "  A golden rebaseline must be justified — fill in the .rebaseline.md (dev-docs/quality/testing-and-ci.md:31)."
          fail=1
        fi
      fi
      ;;
  esac
done <<< "$changed"

if [ "$fail" -eq 0 ]; then
  echo "rebaseline-guard: OK (no unjustified golden rebaselines)."
fi
exit "$fail"
