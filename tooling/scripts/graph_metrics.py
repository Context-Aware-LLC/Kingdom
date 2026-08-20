#!/usr/bin/env python3
"""Report whether the code-review-graph database carries architecture.

Read-only. Prints the handful of numbers that decide whether the graph's edges
can be trusted -- the same numbers `graph_view.py` names in its RETIREMENT
TRIGGER, and the before/after evidence for `csharp_resolver.py`.

    python tooling/scripts/graph_metrics.py
    python tooling/scripts/graph_metrics.py --db some/other/graph.db

An edge whose target is not a node in the graph is invisible to every consumer:
Leiden community detection, blast radius, callers_of, dead-code. A high
unresolved percentage means those tools are not merely incomplete, they are
confidently wrong.
"""

from __future__ import annotations

import argparse
import collections
import sqlite3
import sys
from pathlib import Path

DEFAULT_DB = Path(".code-review-graph") / "graph.db"


def project_of(file_path: str) -> str:
    """Bucket a file into its assembly / top-level project. Mirrors graph_view.py."""
    path = (file_path or "").replace("\\", "/").lower()
    path = path.split("tcg-hearthlike/")[-1]
    parts = path.split("/")
    if parts[0] == "src" and len(parts) > 1:
        return parts[1]
    if parts[0] == "unity" and len(parts) > 5:
        return "unity/" + parts[5]
    if parts[0] in ("tooling", "tools", "tests") and len(parts) > 1:
        return parts[0] + "/" + parts[1]
    return parts[0]


def report(conn: sqlite3.Connection) -> None:
    conn.row_factory = sqlite3.Row
    query = lambda sql: conn.execute(sql).fetchall()  # noqa: E731

    file_of = {r["qualified_name"]: r["file_path"] for r in query(
        "SELECT qualified_name, file_path FROM nodes")}
    community_of = {r["qualified_name"]: r["community_id"] for r in query(
        "SELECT qualified_name, community_id FROM nodes WHERE community_id IS NOT NULL")}

    total = unresolved = both_in_community = 0
    by_kind: dict[str, list[int]] = collections.defaultdict(lambda: [0, 0])
    cross_project: collections.Counter = collections.Counter()
    cross_community: set[tuple[int, int]] = set()

    for row in query("SELECT kind, source_qualified, target_qualified FROM edges"):
        total += 1
        source, target = row["source_qualified"], row["target_qualified"]
        by_kind[row["kind"]][0] += 1
        if target not in file_of:
            unresolved += 1
            by_kind[row["kind"]][1] += 1
            continue
        if source in file_of:
            src, dst = project_of(file_of[source]), project_of(file_of[target])
            if src != dst:
                cross_project[(src, dst)] += 1
        src_community, dst_community = community_of.get(source), community_of.get(target)
        if src_community is not None and dst_community is not None:
            both_in_community += 1
            if src_community != dst_community:
                cross_community.add((src_community, dst_community))

    communities = [r["size"] for r in query(
        "SELECT size FROM communities ORDER BY size DESC")]
    levels = query("SELECT level, COUNT(*) AS n FROM communities GROUP BY level ORDER BY level")

    pct = lambda n: f"{100 * n / total:.1f}%" if total else "n/a"  # noqa: E731
    print(f"edges                              : {total}")
    print(f"  unresolved target                : {unresolved} ({pct(unresolved)})")
    print(f"  both endpoints in a community    : {both_in_community} ({pct(both_in_community)})")
    print()
    print("unresolved by edge kind:")
    for kind, (kind_total, kind_unresolved) in sorted(
        by_kind.items(), key=lambda kv: -kv[1][0]
    ):
        share = 100 * kind_unresolved / kind_total if kind_total else 0
        print(f"  {kind:14s} {kind_unresolved:6d} / {kind_total:6d}  {share:5.1f}%")
    print()
    print(f"cross-PROJECT node-to-node edges   : {sum(cross_project.values())}"
          f"  ({len(cross_project)} distinct pairs)")
    print(f"distinct cross-community pairs     : {len(cross_community)}")
    print(f"communities                        : {len(communities)}"
          f"  (median size {communities[len(communities) // 2] if communities else 0})")
    level_summary = ", ".join(f"L{row['level']}={row['n']}" for row in levels)
    print(f"  by level                         : {level_summary}")
    if cross_project:
        print()
        print("top cross-project dependencies:")
        for (src, dst), count in cross_project.most_common(15):
            print(f"    {src:34s} -> {dst:34s} {count}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--db", type=Path, default=DEFAULT_DB, help="path to graph.db")
    args = parser.parse_args(argv)

    if not args.db.exists():
        print(f"error: no graph database at {args.db}", file=sys.stderr)
        return 1

    conn = sqlite3.connect(args.db)
    try:
        report(conn)
    finally:
        conn.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
