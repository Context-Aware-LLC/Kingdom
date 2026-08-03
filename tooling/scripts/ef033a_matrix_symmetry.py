#!/usr/bin/env python3
"""EF-033a (NOR-186) — the balance matrix's own internal control.

Cell (A,B) and cell (B,A) are the SAME experiment. `ResolveSoakSetups` keys each deck to
its primary class, and every cell alternates seats internally, so both cells play
deck(A) against deck(B) with the seats swapped an equal number of times. Only the seed
base differs (`0xE17BEEF5EED + cellIndex * 100003`).

Two independent samples of one experiment must therefore report win rates that sum to
100%. However far a pair lands from 100, that distance is sampling noise the harness
generated against itself — no card, class or engine change involved. It is the cleanest
available estimate of how much of any single cell is signal.

    python3 tooling/scripts/ef033a_matrix_symmetry.py matrix-winrate.csv [more.csv ...]
"""
import csv
import sys


def load(path):
    with open(path, newline="", encoding="utf-8-sig") as fh:
        return {(r["row_class"], r["col_class"]): int(r["row_winrate_milli"])
                for r in csv.DictReader(fh)}


def report(path):
    m = load(path)
    pairs = []
    seen = set()
    for (a, b), v in m.items():
        if a == b or (b, a) in seen or (b, a) not in m:
            continue
        seen.add((a, b))
        pairs.append((a, b, v, m[(b, a)]))

    print(f"=== {path} ===")
    print(f"{'A':<12} {'B':<12} {'A vs B':>8} {'B vs A':>8} {'sum':>8} {'error':>7}")
    errs = []
    for a, b, ab, ba in sorted(pairs, key=lambda p: -abs(p[2] + p[3] - 1000)):
        s = ab + ba
        err = abs(s - 1000)
        errs.append(err)
        print(f"{a:<12} {b:<12} {ab/10:7.1f}% {ba/10:7.1f}% {s/10:7.1f}% {err/10:6.1f}")

    n = len(errs)
    print()
    print(f"  pairs                        : {n}")
    print(f"  mean |sum - 100|             : {sum(errs)/n/10:.1f} points")
    print(f"  max                          : {max(errs)/10:.1f} points")
    print(f"  pairs off by >= 20 points    : {sum(1 for e in errs if e >= 200)}")
    print(f"  pairs exactly consistent     : {sum(1 for e in errs if e == 0)}")
    print()


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    for p in sys.argv[1:]:
        report(p)
