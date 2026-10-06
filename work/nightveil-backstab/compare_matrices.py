"""Compare two matrix runs cell by cell. Read-only.

    python compare_matrices.py <base-run-dir> <variant-run-dir> [<CLASS>]

"row mean" is the figure the balance proposals use: the mean of a class's seven off-diagonal row
cells. "all games" pools the row AND the column, so every game the class played outside its
mirror counts once; it is the larger sample (560 games at 40 per cell). With a CLASS given, the
class's pairings are listed with both seat-orders pooled.
"""
import csv
import os
import sys

base_dir, variant_dir = sys.argv[1], sys.argv[2]
focus = sys.argv[3] if len(sys.argv) > 3 else None


def load(run_dir):
    cells = {}
    with open(os.path.join(run_dir, "matrix-winrate.csv"), encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            cells[(row["row_class"], row["col_class"])] = (
                int(row["row_wins"]), int(row["row_played"]), row["core_clean"], int(row["hero_powers"]))
    return cells


def row_mean(cells, klass):
    rates = [w / p for (a, b), (w, p, _, _) in cells.items() if a == klass and b != klass]
    return sum(rates) / len(rates)


def pooled(cells, klass, other=None):
    wins = played = 0
    for (a, b), (w, p, _, _) in cells.items():
        if a == b:
            continue
        if a == klass and (other is None or b == other):
            wins, played = wins + w, played + p
        elif b == klass and (other is None or a == other):
            wins, played = wins + (p - w), played + p
    return wins, played


base, variant = load(base_dir), load(variant_dir)
classes = sorted({a for a, _ in base})
unclean = [key for key, value in variant.items() if value[2].lower() != "true"]
print(f"base    {base_dir}\nvariant {variant_dir}\nvariant cells not clean: {len(unclean)} {unclean[:4]}")
print(f"\n{'class':<12} {'row mean':>19} {'all games (wins/played)':>42}")
for klass in classes:
    bw, bp = pooled(base, klass)
    vw, vp = pooled(variant, klass)
    print(f"{klass:<12} {row_mean(base, klass):>7.1%} -> {row_mean(variant, klass):>6.1%}   "
          f"{bw:>3}/{bp} {bw / bp:>6.1%} -> {vw:>3}/{vp} {vw / vp:>6.1%}  {(vw / vp - bw / bp) * 100:>+5.1f}")

if focus:
    print(f"\n{focus} by opponent, both seat orders pooled (80 games each)")
    for other in classes:
        if other == focus:
            continue
        bw, bp = pooled(base, focus, other)
        vw, vp = pooled(variant, focus, other)
        print(f"  vs {other:<12} {bw:>2}/{bp} {bw / bp:>6.1%} -> {vw:>2}/{vp} {vw / vp:>6.1%}")
    print(f"hero powers pressed in the {focus} mirror cell: {base[(focus, focus)][3]} -> {variant[(focus, focus)][3]}")
