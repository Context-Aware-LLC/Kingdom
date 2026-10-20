#!/usr/bin/env bash
# Run the three balance matrices (class only, + neutral, shipped) against one card-data directory.
#
#   run_matrices.sh <card-data-dir> <out-root> <label>
#
# Writes <out-root>/<label>-{primary-only,no-secondary,shipped}/ and a console log beside each.
# The three run at once; each is 64 cells of 40 games on fixed seeds, so two card sets run through
# this script differ only by their cards. NetSoak must already be built Release.
set -u
CARDS="$1"; OUT="$2"; LABEL="$3"
EXE="C:/Users/User/Documents/tcg-hearthlike/.dotnet-build/bin/Emberfall.Tools.NetSoak/Release/net10.0/Emberfall.Tools.NetSoak.exe"
mkdir -p "$OUT"
run() {  # name, extra flag
  KINGDOM_CARDS="$CARDS" "$EXE" matrix 40 "$OUT/$LABEL-$1" $2 > "$OUT/$LABEL-$1.log" 2>&1
  echo "$LABEL-$1 exit $? $(tail -n 1 "$OUT/$LABEL-$1.log")"
}
run primary-only --primary-only &
run no-secondary --no-secondary &
run shipped "" &
wait
