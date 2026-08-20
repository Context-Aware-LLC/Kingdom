#!/usr/bin/env python3
"""Resolve bare C# CALLS and INHERITS targets in the code-review-graph database.

WHY THIS EXISTS
---------------
`code-review-graph` already ships a generic bare-target resolver
(`GraphStore._resolve_bare_endpoints`). It resolves a bare call target only when
exactly one candidate lives in the call-site file or in a file that file imports
-- a globally unique name is explicitly NOT accepted as evidence. That rule is
correct and this script keeps it verbatim.

It builds "files that this file imports" by taking each IMPORTS_FROM target and
splitting on `::` to get a file path. For Python or TypeScript that works: an
import target IS a module path. For C#, `using Emberfall.Core.Contracts;` names a
NAMESPACE, and a namespace is not a file. Measured on 2026-08-20:

    IMPORTS_FROM targets that resolve to a known file_path :     5 / 1845

So for every C# file the imported-file set is empty and the upstream resolver can
only ever resolve same-file calls. Cross-file C# resolution is not failing -- it
is unreachable. Downstream, that is the whole story of this graph:

    edges whose target is not a node                : 16814 / 25114  (67%)
    INHERITS edges unresolved                       :   278 / 278   (100%)
    edges with both endpoints inside a community    :  5956 / 25114  (23.7%)
    communities / median size                       :   547 / 3
    distinct cross-community pairs, whole repo      :     9

Leiden clusters on node-to-node edges, so a graph with almost none produces 547
disconnected islands, no level-1 hierarchy, and an edgeless `--mode community`
view. `graph_view.py` retreated to a treemap for exactly this reason.

The missing input is already in the database. The C# parser records
`csharp_namespaces` in `nodes.extra` on File nodes -- 41 distinct namespaces --
and every internal import target maps to at least one declaring file:

    internal IMPORTS_FROM targets mapping to >=1 file :   374 / 374

This script supplies that namespace -> files index and re-runs upstream's own
evidence rule over it. It adds no new heuristic.

WHAT COUNTS AS EVIDENCE
-----------------------
A candidate is admissible only if its defining file is one of:

  * the call-site file itself,
  * a file declaring a namespace the call-site file `using`s,
  * a file declaring a namespace the call-site file itself declares
    (same-namespace types are visible in C# with no using directive).

If two admissible candidates remain, the edge is SKIPPED, not guessed. A wrong
edge is worse than a missing one here, because CLAUDE.md instructs agents to
trust this graph.

DELIBERATE OMISSIONS
--------------------
* Dotted CALLS targets (`System.Array.Empty<T>`, `builder.Services.AddSingleton`)
  are left alone. They are 92 of 14560 bare targets (0.6%) and are almost all
  external or receiver-qualified; matching on their last segment would invent a
  false-positive class for no measurable gain.
* IMPORTS_FROM edges are NOT rewritten. One namespace maps to many files, so
  expanding a using-directive into file->file edges would multiply 374 authored
  dependencies into thousands of synthetic ones. The project-level dependency
  view is better derived by projecting namespaces onto assemblies at read time.

    python tooling/scripts/csharp_resolver.py --dry-run
    python tooling/scripts/csharp_resolver.py
"""

from __future__ import annotations

import argparse
import json
import re
import sqlite3
import sys
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path

DEFAULT_DB = Path(".code-review-graph") / "graph.db"

#: Node kinds a CALLS target may legitimately land on.
CALL_TARGET_KINDS = ("Function", "Test", "Class")

#: A base type is a type. Never resolve INHERITS onto a function.
INHERIT_TARGET_KINDS = ("Class",)

#: Members every C# receiver has, via System.Object or IDisposable. A bare
#: occurrence of one carries no information about the receiver's type, so it can
#: never be evidence -- `digest[i].ToString("x2")` is not a call into your code.
UNIVERSAL_MEMBERS = frozenset(
    {
        "ToString",
        "Equals",
        "GetHashCode",
        "GetType",
        "Finalize",
        "MemberwiseClone",
        "Dispose",
        "DisposeAsync",
    }
)

_GENERIC_ARGS = re.compile(r"<.*>")


@dataclass
class ResolveStats:
    """Outcome of one resolver pass. Every skipped edge lands in exactly one bucket."""

    resolved_calls: int = 0
    resolved_inherits: int = 0
    skipped_ambiguous: int = 0
    skipped_no_evidence: int = 0
    skipped_no_candidate: int = 0
    skipped_receiver_unproven: int = 0
    skipped_universal_member: int = 0
    skipped_self: int = 0

    @property
    def resolved(self) -> int:
        return self.resolved_calls + self.resolved_inherits

    @property
    def skipped(self) -> int:
        return (
            self.skipped_ambiguous
            + self.skipped_no_evidence
            + self.skipped_no_candidate
            + self.skipped_receiver_unproven
            + self.skipped_universal_member
            + self.skipped_self
        )


def build_namespace_index(conn: sqlite3.Connection) -> dict[str, set[str]]:
    """Map each C# namespace to every file that declares it."""
    index: dict[str, set[str]] = defaultdict(set)
    rows = conn.execute(
        "SELECT file_path, extra FROM nodes WHERE kind = 'File' AND language = 'csharp'"
    ).fetchall()
    for row in rows:
        try:
            extra = json.loads(row["extra"] or "{}")
        except (TypeError, ValueError):
            continue
        for namespace in extra.get("csharp_namespaces") or ():
            if namespace:
                index[namespace].add(row["file_path"])
    return dict(index)


def _build_visible_files(
    conn: sqlite3.Connection, namespace_index: dict[str, set[str]]
) -> dict[str, set[str]]:
    """Map each C# file to the files whose types are visible from it."""
    declared_by: dict[str, set[str]] = defaultdict(set)
    for namespace, files in namespace_index.items():
        for file_path in files:
            declared_by[file_path].add(namespace)

    imported: dict[str, set[str]] = defaultdict(set)
    rows = conn.execute(
        "SELECT DISTINCT file_path, target_qualified FROM edges WHERE kind = 'IMPORTS_FROM'"
    ).fetchall()
    for row in rows:
        imported[row["file_path"]].add(row["target_qualified"])

    visible: dict[str, set[str]] = {}
    for file_path in set(declared_by) | set(imported):
        namespaces = imported[file_path] | declared_by[file_path]
        files = {file_path}
        for namespace in namespaces:
            files |= namespace_index.get(namespace, set())
        visible[file_path] = files
    return visible


def _candidate_index(
    conn: sqlite3.Connection, kinds: tuple[str, ...]
) -> dict[str, list[tuple[str, str, str]]]:
    """Map a bare symbol name to (qualified_name, defining file, owning type)."""
    placeholders = ",".join("?" * len(kinds))
    index: dict[str, list[tuple[str, str, str]]] = defaultdict(list)
    rows = conn.execute(
        f"SELECT name, qualified_name, file_path, parent_name FROM nodes "  # noqa: S608
        f"WHERE language = 'csharp' AND kind IN ({placeholders})",
        kinds,
    ).fetchall()
    for row in rows:
        index[row["name"]].append(
            (row["qualified_name"], row["file_path"], row["parent_name"] or "")
        )
    return dict(index)


def _read_source_from_disk(file_path: str) -> str | None:
    try:
        return Path(file_path).read_text(encoding="utf-8", errors="ignore")
    except OSError:
        return None


class _WordCache:
    """Whole-word membership over call-site source, read at most once per file."""

    def __init__(self, read_source) -> None:
        self._read = read_source
        self._words: dict[str, set[str]] = {}

    def mentions(self, file_path: str, word: str) -> bool:
        if file_path not in self._words:
            text = self._read(file_path)
            self._words[file_path] = (
                set(re.findall(r"[A-Za-z_][A-Za-z0-9_]*", text)) if text else set()
            )
        return word in self._words[file_path]


def _normalise_type_name(target: str) -> str:
    """`IEquatable<RuleRef>` -> `IEquatable`; `Core.RuleAction` -> `RuleAction`."""
    return _GENERIC_ARGS.sub("", target).split(".")[-1].strip()


def resolve_csharp_edges(
    conn: sqlite3.Connection, dry_run: bool = False, read_source=None
) -> ResolveStats:
    """Resolve bare C# CALLS and INHERITS targets. Returns per-bucket counts.

    `read_source` maps a file path to its text; it defaults to reading from disk
    and exists so tests can supply call-site source without touching the
    filesystem.
    """
    conn.row_factory = sqlite3.Row
    stats = ResolveStats()
    source = _WordCache(read_source or _read_source_from_disk)

    csharp_files = {
        row["file_path"]
        for row in conn.execute(
            "SELECT DISTINCT file_path FROM nodes WHERE language = 'csharp'"
        ).fetchall()
    }
    if not csharp_files:
        return stats

    namespace_index = build_namespace_index(conn)
    visible_files = _build_visible_files(conn, namespace_index)
    candidates_by_kind = {
        "CALLS": _candidate_index(conn, CALL_TARGET_KINDS),
        "INHERITS": _candidate_index(conn, INHERIT_TARGET_KINDS),
    }

    edges = conn.execute(
        "SELECT id, kind, source_qualified, target_qualified, file_path FROM edges "
        "WHERE kind IN ('CALLS', 'INHERITS') AND target_qualified NOT LIKE '%::%'"
    ).fetchall()

    updates: list[tuple[str, int]] = []
    for edge in edges:
        context_file = edge["file_path"]
        if context_file not in csharp_files:
            continue

        target = edge["target_qualified"]
        if edge["kind"] == "CALLS":
            # Dotted targets are external or receiver-qualified. See module docstring.
            if "." in target or "<" in target:
                continue
            if target in UNIVERSAL_MEMBERS:
                stats.skipped_universal_member += 1
                continue
            name = target
        else:
            name = _normalise_type_name(target)

        candidates = candidates_by_kind[edge["kind"]].get(name, [])
        if not candidates:
            stats.skipped_no_candidate += 1
            continue

        visible = visible_files.get(context_file, {context_file})
        in_scope = {
            qualified
            for qualified, defining_file, _ in candidates
            if defining_file in visible
        }
        in_scope.discard(edge["source_qualified"])

        if not in_scope:
            if any(
                qualified == edge["source_qualified"] for qualified, _, _ in candidates
            ):
                stats.skipped_self += 1
            else:
                stats.skipped_no_evidence += 1
            continue

        if edge["kind"] == "CALLS":
            # The parser drops the receiver, so `sb.Append(x)` and
            # `writer.Append(x)` are both recorded as the bare name `Append`.
            # Require the candidate's own type to be named at the call site --
            # otherwise a call on a BCL receiver binds to an internal method.
            admissible = {
                qualified
                for qualified, _, owner in candidates
                if qualified in in_scope
                and owner
                and source.mentions(context_file, owner)
            }
            if not admissible:
                stats.skipped_receiver_unproven += 1
                continue
        else:
            # A base type is written at the declaration site by construction,
            # so the receiver check is vacuous for INHERITS.
            admissible = in_scope
        if len(admissible) > 1:
            stats.skipped_ambiguous += 1
            continue

        updates.append((admissible.pop(), edge["id"]))
        if edge["kind"] == "CALLS":
            stats.resolved_calls += 1
        else:
            stats.resolved_inherits += 1

    if updates and not dry_run:
        conn.executemany(
            "UPDATE edges SET target_qualified = ? WHERE id = ?", updates
        )
        conn.commit()

    return stats


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--db", type=Path, default=DEFAULT_DB, help="path to graph.db"
    )
    parser.add_argument(
        "--dry-run", action="store_true", help="report without writing"
    )
    args = parser.parse_args(argv)

    if not args.db.exists():
        print(f"error: no graph database at {args.db}", file=sys.stderr)
        return 1

    conn = sqlite3.connect(args.db)
    try:
        stats = resolve_csharp_edges(conn, dry_run=args.dry_run)
    finally:
        conn.close()

    mode = "would resolve" if args.dry_run else "resolved"
    print(f"{mode}: {stats.resolved} edges "
          f"({stats.resolved_calls} CALLS, {stats.resolved_inherits} INHERITS)")
    print(f"skipped:  {stats.skipped} edges")
    print(f"  no candidate by name : {stats.skipped_no_candidate}")
    print(f"  no import evidence   : {stats.skipped_no_evidence}")
    print(f"  receiver unproven    : {stats.skipped_receiver_unproven}")
    print(f"  universal member     : {stats.skipped_universal_member}")
    print(f"  ambiguous            : {stats.skipped_ambiguous}")
    print(f"  self-reference       : {stats.skipped_self}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
