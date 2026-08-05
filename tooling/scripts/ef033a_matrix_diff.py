#!/usr/bin/env python3
"""EF-033a (NOR-186) — diff two balance-matrix CSVs cell by cell.

Both files are `matrix-winrate.csv` as written by
`dotnet run --project src/Emberfall.Tools.NetSoak -- matrix <N> <outDir>`.

Investigation-only. It reads; it changes nothing. The question it exists to answer is
narrow: does a degenerate cell (<=10% or >=90%) stay degenerate when the same harness
is re-run on a later engine, and does it stay degenerate *in the same direction*? A cell
that swings 60 points between two clean runs is not measuring class cost.

    python3 tooling/scripts/ef033a_matrix_diff.py OLD.csv NEW.csv
"""
import csv
import sys


def load(path):
    with open(path, newline="", encoding="utf-8-sig") as fh:
        return {(r["row_class"], r["col_class"]): r for r in csv.DictReader(fh)}


def milli(row):
    return int(row["row_winrate_milli"])


def degenerate(m):
    return m <= 100 or m >= 900


def main(argv):
    if len(argv) != 3:
        print(__doc__)
        return 2
    old, new = load(argv[1]), load(argv[2])

    shared = sorted(set(old) & set(new))
    print(f"cells: old={len(old)} new={len(new)} shared={len(shared)}")
    print()

    print("=== every non-mirror cell, sorted by absolute swing ===")
    print(f"{'row':<12} {'col':<12} {'old':>7} {'new':>7} {'delta':>7}  flags")
    rows = []
    for k in shared:
        if k[0] == k[1]:
            continue
        o, n = milli(old[k]), milli(new[k])
        rows.append((abs(n - o), k, o, n))
    rows.sort(reverse=True)
    for _, (a, b), o, n in rows:
        flags = []
        if degenerate(o):
            flags.append("was-degenerate")
        if degenerate(n):
            flags.append("is-degenerate")
        if degenerate(o) and not degenerate(n):
            flags.append("RESOLVED")
        if (o >= 500) != (n >= 500):
            flags.append("FLIPPED-SIDE")
        print(f"{a:<12} {b:<12} {o/10:6.1f}% {n/10:6.1f}% {(n-o)/10:+6.1f}%  {' '.join(flags)}")

    def summarise(tag, src):
        nm = [milli(src[k]) for k in shared if k[0] != k[1]]
        deg = [m for m in nm if degenerate(m)]
        ext = [m for m in nm if m <= 50 or m >= 950]
        means = {}
        for k in shared:
            if k[0] == k[1]:
                continue
            means.setdefault(k[0], []).append(milli(src[k]))
        per = {c: sum(v) / len(v) / 10 for c, v in means.items()}
        lo, hi = min(per.values()), max(per.values())
        print()
        print(f"=== {tag} ===")
        print(f"  non-mirror cells        : {len(nm)}")
        print(f"  degenerate (<=10/>=90)  : {len(deg)}")
        print(f"  extreme    (<=5/>=95)   : {len(ext)}")
        print(f"  class spread (max-min)  : {hi - lo:.1f} points")
        for c in sorted(per, key=lambda c: -per[c]):
            print(f"    {c:<12} {per[c]:5.1f}% mean")

    summarise("OLD " + argv[1], old)
    summarise("NEW " + argv[2], new)

    swings = [abs(n - o) for _, _, o, n in rows]
    print()
    print("=== swing between the two runs (non-mirror cells) ===")
    print(f"  mean absolute swing : {sum(swings)/len(swings)/10:.1f} points")
    print(f"  max                 : {max(swings)/10:.1f} points")
    print(f"  cells swinging >=25 : {sum(1 for s in swings if s >= 250)} of {len(swings)}")
    print(f"  cells changing side : {sum(1 for _, _, o, n in rows if (o >= 500) != (n >= 500))} of {len(swings)}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
