#!/usr/bin/env bash
# docs-touched.sh — which spec sections does this code change describe?
#
# WHY THIS EXISTS
#   The 2026-07-22 docs audit found the same failure three times in unrelated folders: a
#   ruling landed in code and never reached the spec it invalidated. HelloMsg shipped and
#   was documented in none of the four protocol docs. b1 §7.3 called a shipped stall guard
#   "unwritten in code" on the day it landed. Neither was ever an item on a to-do list, so
#   no plan-time doc pointer would have caught them.
#
#   But the link already exists and nobody reads it back: 55 of 74 source files cite doc
#   sections in their own comments. This harvests those citations from whatever you changed
#   and prints the list. No memory required, no discipline required, nothing to remember.
#
# USAGE
#   tools/ci/docs-touched.sh                 # staged changes (what a pre-commit hook uses)
#   tools/ci/docs-touched.sh --range HEAD~3  # everything in a commit range
#   tools/ci/docs-touched.sh src/Foo.cs ...  # explicit files
#
#   EMBERFALL_DOCS overrides the docs-repo location.
#
# IT NEVER BLOCKS. It prints and exits 0 — including when a cited doc is missing. A gate
# that fails a commit over a doc reference gets bypassed with --no-verify within a week and
# then teaches everyone to ignore it. This is a reminder, not a gate.

set -uo pipefail

DOCS_ROOT="${EMBERFALL_DOCS:-c:/Users/User/Documents/Project Docs/Emberfall/docs}"

# ── Which files changed ──────────────────────────────────────────────────────
MODE="staged"
if   [[ "${1:-}" == "--range" ]];    then MODE="range"; RANGE="${2:-HEAD~1}"
elif [[ "${1:-}" == "--unpushed" ]]; then MODE="unpushed"
elif [[ $# -gt 0 ]]; then MODE="explicit"; fi

case "$MODE" in
  staged)   mapfile -t FILES < <(git diff --cached --name-only --diff-filter=ACM -- '*.cs') ;;
  range)    mapfile -t FILES < <(git diff --name-only --diff-filter=ACM "$RANGE" -- '*.cs') ;;
  explicit) FILES=("$@") ;;
  unpushed)
    # Everything built but not yet pushed — the useful question on folder-open: what have
    # we landed that the docs may not know about? Falls back to unstaged+staged when there
    # is no upstream (the docs repo had none until 2026-07-22).
    if UP=$(git rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' 2>/dev/null); then
      mapfile -t FILES < <(git diff --name-only --diff-filter=ACM "$UP..HEAD" -- '*.cs')
      BANNER="unpushed work ($UP..HEAD)"
    else
      mapfile -t FILES < <(git diff --name-only --diff-filter=ACM HEAD -- '*.cs')
      BANNER="uncommitted work (no upstream set)"
    fi ;;
esac

[[ ${#FILES[@]} -eq 0 ]] && exit 0

# ── Shorthand → filename ─────────────────────────────────────────────────────
# The codebase cites docs three ways: full "a5-wire-protocol-design.md §5", shorthand
# "b1 §11", and a bare "design §N" meaning *this assembly's own* design doc.
resolve() {
  case "$1" in
    a2)  echo "engineering/a2-serializer-design.md" ;;
    a2b) echo "engineering/a2b-scoped-review-2026-07-15.md" ;;
    a3)  echo "engineering/a3-projector-design.md" ;;
    a4)  echo "engineering/a4-session-seam-design.md" ;;
    a5)  echo "engineering/a5-wire-protocol-design.md" ;;
    b1)  echo "engineering/b1-match-server-design.md" ;;
    b3)  echo "engineering/b3-networked-session-design.md" ;;
    netcode)        echo "multiplayer/netcode-architecture.md" ;;
    state-sync)     echo "multiplayer/state-sync-and-security.md" ;;
    match-lifecycle) echo "multiplayer/match-lifecycle.md" ;;
    interfaces)     echo "engineering/interfaces.md" ;;
    card-system)    echo "engineering/card-system.md" ;;
    card-schema)    echo "design/card-schema.md" ;;
    telemetry-spec) echo "design/telemetry-spec.md" ;;
    golden-replay)  echo "testing/golden-replay.md" ;;
    *) echo "" ;;
  esac
}

# "design §N" resolves by which assembly the citing file lives in.
own_design() {
  case "$1" in
    */Emberfall.Net.Protocol/*) resolve a5 ;;
    */Emberfall.Net.Server/*)   resolve b1 ;;
    */Emberfall.Net.Client/*)   resolve b3 ;;
    */Emberfall.Core.Serialization/*) resolve a2 ;;
    */Emberfall.Core.Projection/*)    resolve a3 ;;
    *) echo "" ;;
  esac
}

# Find a bare "foo.md" anywhere under the docs root.
locate() {
  local hit
  hit=$(cd "$DOCS_ROOT" 2>/dev/null && find . -name "$1" -type f 2>/dev/null | head -1)
  [[ -n "$hit" ]] && echo "${hit#./}"
}

# ── Harvest ──────────────────────────────────────────────────────────────────
# One "doc<TAB>section<TAB>citing-file" row per citation, then collapse.
TMP=$(mktemp)
trap 'rm -f "$TMP"' EXIT

for f in "${FILES[@]}"; do
  [[ -f "$f" ]] || continue

  # Form 1 — full filename: "a5-wire-protocol-design.md §5.1"
  grep -ohE "[a-z0-9-]+\.md ?§[0-9]+(\.[0-9]+)*" "$f" 2>/dev/null | while read -r cite; do
    doc="${cite%%.md*}.md"; sec="§${cite##*§}"
    path=$(locate "$doc"); [[ -z "$path" ]] && path="!MISSING!$doc"
    printf '%s\t%s\t%s\n' "$path" "$sec" "$f" >> "$TMP"
  done

  # Form 2 — shorthand: "b1 §11", "state-sync §6.2"
  grep -ohE "\b(a2b?|a3|a4|a5|b1|b3|netcode|state-sync|match-lifecycle|interfaces|card-system|card-schema|telemetry-spec|golden-replay) §[0-9]+(\.[0-9]+)*" "$f" 2>/dev/null | while read -r cite; do
    path=$(resolve "${cite%% §*}"); [[ -z "$path" ]] && continue
    printf '%s\t§%s\t%s\n' "$path" "${cite##*§}" "$f" >> "$TMP"
  done

  # Form 3 — bare "design §N", meaning this assembly's own design doc
  own=$(own_design "$f")
  if [[ -n "$own" ]]; then
    grep -ohE "\bdesign §[0-9]+(\.[0-9]+)*" "$f" 2>/dev/null | while read -r cite; do
      printf '%s\t§%s\t%s\n' "$own" "${cite##*§}" "$f" >> "$TMP"
    done
  fi
done

[[ -s "$TMP" ]] || exit 0

# ── Report ───────────────────────────────────────────────────────────────────
echo
echo "┌─ DOCS DESCRIBING THIS CHANGE ────────────────────────────────────────────"
[[ -n "${BANNER:-}" ]] && echo "│  Scope: $BANNER"
echo "│  Harvested from doc citations in the changed source files."
echo "│  Check each section still tells the truth. Not a gate — nothing is blocked."
echo "│"

sort -u "$TMP" | awk -F'\t' '
  { secs[$1] = secs[$1] " " $2; if (!seen[$1"\t"$3]++) files[$1] = files[$1] " " $3 }
  END {
    for (d in secs) {
      n = split(secs[d], a, " "); delete u; o = ""
      for (i = 1; i <= n; i++) if (a[i] != "" && !u[a[i]]++) o = o " " a[i]
      printf "│  %s\n│      sections:%s\n│      cited by:%s\n│\n", d, o, files[d]
    }
  }' | sed 's/!MISSING!/⚠ BROKEN CITATION — no such doc: /'

echo "└──────────────────────────────────────────────────────────────────────────"
exit 0
