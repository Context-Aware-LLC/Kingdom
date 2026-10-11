from __future__ import annotations

import json
import shutil
import sqlite3
import sys
import tempfile
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT))

from tooling.scripts.csharp_resolver import (  # noqa: E402
    build_namespace_index,
    resolve_csharp_edges,
)


SCHEMA = """
CREATE TABLE nodes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    qualified_name TEXT NOT NULL UNIQUE,
    file_path TEXT NOT NULL,
    line_start INTEGER,
    line_end INTEGER,
    language TEXT,
    parent_name TEXT,
    params TEXT,
    return_type TEXT,
    modifiers TEXT,
    is_test INTEGER DEFAULT 0,
    file_hash TEXT,
    extra TEXT DEFAULT '{}',
    updated_at REAL NOT NULL,
    signature TEXT,
    community_id INTEGER
);
CREATE TABLE edges (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    kind TEXT NOT NULL,
    source_qualified TEXT NOT NULL,
    target_qualified TEXT NOT NULL,
    file_path TEXT NOT NULL,
    line INTEGER DEFAULT 0,
    extra TEXT DEFAULT '{}',
    confidence REAL DEFAULT 1.0,
    confidence_tier TEXT DEFAULT 'EXTRACTED',
    updated_at REAL NOT NULL
);
"""


class CsharpResolverTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = Path(tempfile.mkdtemp(prefix="emberfall-resolver-tests-"))
        self.conn = sqlite3.connect(self.temp / "graph.db")
        self.conn.row_factory = sqlite3.Row
        self.conn.executescript(SCHEMA)

    def tearDown(self) -> None:
        self.conn.close()
        shutil.rmtree(self.temp, ignore_errors=True)

    # ---- fixture helpers -------------------------------------------------

    def _file(self, path: str, namespaces: list[str], language: str = "csharp") -> None:
        self.conn.execute(
            "INSERT INTO nodes (kind, name, qualified_name, file_path, language,"
            " extra, updated_at) VALUES ('File', ?, ?, ?, ?, ?, 0)",
            (
                path.rsplit("/", 1)[-1],
                path,
                path,
                language,
                json.dumps({"csharp_namespaces": namespaces}) if namespaces else "{}",
            ),
        )

    def _symbol(
        self,
        file_path: str,
        name: str,
        kind: str = "Function",
        language: str = "csharp",
        parent: str | None = None,
    ) -> str:
        qualified = f"{file_path}::{name}"
        self.conn.execute(
            "INSERT INTO nodes (kind, name, qualified_name, file_path, language,"
            " parent_name, updated_at) VALUES (?, ?, ?, ?, ?, ?, 0)",
            (kind, name, qualified, file_path, language, parent),
        )
        return qualified

    def _edge(self, kind: str, source: str, target: str, file_path: str) -> int:
        cur = self.conn.execute(
            "INSERT INTO edges (kind, source_qualified, target_qualified, file_path,"
            " updated_at) VALUES (?, ?, ?, ?, 0)",
            (kind, source, target, file_path),
        )
        return int(cur.lastrowid)

    def _target(self, edge_id: int) -> str:
        row = self.conn.execute(
            "SELECT target_qualified FROM edges WHERE id = ?", (edge_id,)
        ).fetchone()
        return row["target_qualified"]

    def _resolve(self, dry_run: bool = False, **sources: str):
        """Resolve with call-site source supplied inline instead of read from disk.

        Keys name a fixture file with `/` written as `__` and the extension
        dropped, so `src__Core__A` means `src/Core/A.cs`.
        """
        known = {}
        for row in self.conn.execute("SELECT DISTINCT file_path FROM nodes"):
            path = row["file_path"]
            known[path.rsplit(".", 1)[0].replace("/", "__")] = path
        by_path = {
            known[key]: text for key, text in sources.items() if key in known
        }
        unknown = set(sources) - set(known)
        self.assertFalse(unknown, f"fixture source for unknown file(s): {unknown}")
        return resolve_csharp_edges(
            self.conn, dry_run=dry_run, read_source=by_path.get
        )

    # ---- namespace index -------------------------------------------------

    def test_namespace_index_maps_namespace_to_every_declaring_file(self) -> None:
        self._file("src/Core/A.cs", ["Kingdom.Core"])
        self._file("src/Core/B.cs", ["Kingdom.Core"])
        self._file("src/Net/C.cs", ["Kingdom.Net"])
        self._file("tooling/x.py", [], language="python")

        index = build_namespace_index(self.conn)

        self.assertEqual({"src/Core/A.cs", "src/Core/B.cs"}, index["Kingdom.Core"])
        self.assertEqual({"src/Net/C.cs"}, index["Kingdom.Net"])
        self.assertNotIn("", index)

    # ---- CALLS -----------------------------------------------------------

    def test_resolves_bare_call_through_imported_namespace(self) -> None:
        self._file("src/Net/Server.cs", ["Kingdom.Net"])
        self._file("src/Core/Rules.cs", ["Kingdom.Core"])
        caller = self._symbol("src/Net/Server.cs", "Handle", parent="Server")
        callee = self._symbol("src/Core/Rules.cs", "ApplyDamage", parent="RuleEngine")
        self._edge("IMPORTS_FROM", "src/Net/Server.cs", "Kingdom.Core", "src/Net/Server.cs")
        edge = self._edge("CALLS", caller, "ApplyDamage", "src/Net/Server.cs")

        stats = self._resolve(src__Net__Server="RuleEngine engine; engine.ApplyDamage(x);")

        self.assertEqual(callee, self._target(edge))
        self.assertEqual(1, stats.resolved_calls)

    def test_resolves_bare_call_within_same_namespace_without_using(self) -> None:
        """C# makes same-namespace types visible with no using directive."""
        self._file("src/Core/A.cs", ["Kingdom.Core"])
        self._file("src/Core/B.cs", ["Kingdom.Core"])
        caller = self._symbol("src/Core/A.cs", "Run", parent="A")
        callee = self._symbol("src/Core/B.cs", "Helper", parent="B")
        edge = self._edge("CALLS", caller, "Helper", "src/Core/A.cs")

        self._resolve(src__Core__A="void Run() { B.Helper(); }")

        self.assertEqual(callee, self._target(edge))

    def test_skips_call_whose_owning_type_is_absent_from_the_call_site(self) -> None:
        """`sb.Append(x)` on a StringBuilder must not bind to an internal Append."""
        self._file("src/Corpus/Files.cs", ["Kingdom.Corpus"])
        self._file("src/Corpus/Writer.cs", ["Kingdom.Corpus"])
        caller = self._symbol("src/Corpus/Files.cs", "Hex", parent="CorpusFiles")
        self._symbol("src/Corpus/Writer.cs", "Append", parent="ManifestWriter")
        edge = self._edge("CALLS", caller, "Append", "src/Corpus/Files.cs")

        stats = self._resolve(
            src__Corpus__Files="var result = new StringBuilder(); result.Append(b);"
        )

        self.assertEqual("Append", self._target(edge))
        self.assertEqual(0, stats.resolved_calls)
        self.assertEqual(1, stats.skipped_receiver_unproven)

    def test_owning_type_must_match_as_a_whole_word(self) -> None:
        self._file("src/Core/A.cs", ["Kingdom.Core"])
        self._file("src/Core/B.cs", ["Kingdom.Core"])
        caller = self._symbol("src/Core/A.cs", "Run", parent="A")
        self._symbol("src/Core/B.cs", "Tick", parent="Clock")
        edge = self._edge("CALLS", caller, "Tick", "src/Core/A.cs")

        stats = self._resolve(src__Core__A="var x = new ClockFace(); x.Tick();")

        self.assertEqual("Tick", self._target(edge))
        self.assertEqual(1, stats.skipped_receiver_unproven)

    def test_skips_call_when_call_site_source_is_unreadable(self) -> None:
        self._file("src/Core/A.cs", ["Kingdom.Core"])
        self._file("src/Core/B.cs", ["Kingdom.Core"])
        caller = self._symbol("src/Core/A.cs", "Run", parent="A")
        self._symbol("src/Core/B.cs", "Helper", parent="B")
        edge = self._edge("CALLS", caller, "Helper", "src/Core/A.cs")

        stats = self._resolve()  # no source supplied for any path

        self.assertEqual("Helper", self._target(edge))
        self.assertEqual(1, stats.skipped_receiver_unproven)

    def test_reads_call_site_source_from_disk_when_no_reader_is_given(self) -> None:
        source_file = self.temp / "A.cs"
        source_file.write_text("void Run() { B.Helper(); }", encoding="utf-8")
        self._file(str(source_file), ["Kingdom.Core"])
        self._file("src/Core/B.cs", ["Kingdom.Core"])
        caller = self._symbol(str(source_file), "Run", parent="A")
        callee = self._symbol("src/Core/B.cs", "Helper", parent="B")
        edge = self._edge("CALLS", caller, "Helper", str(source_file))

        stats = resolve_csharp_edges(self.conn)

        self.assertEqual(callee, self._target(edge))
        self.assertEqual(1, stats.resolved_calls)

    def test_skips_ambiguous_candidates_rather_than_guessing(self) -> None:
        self._file("src/Net/Server.cs", ["Kingdom.Net"])
        self._file("src/Core/A.cs", ["Kingdom.Core"])
        self._file("src/Core/B.cs", ["Kingdom.Core"])
        caller = self._symbol("src/Net/Server.cs", "Handle", parent="Server")
        self._symbol("src/Core/A.cs", "Apply", parent="Alpha")
        self._symbol("src/Core/B.cs", "Apply", parent="Beta")
        self._edge("IMPORTS_FROM", "src/Net/Server.cs", "Kingdom.Core", "src/Net/Server.cs")
        edge = self._edge("CALLS", caller, "Apply", "src/Net/Server.cs")

        stats = self._resolve(src__Net__Server="Alpha a; Beta b; a.Apply(); b.Apply();")

        self.assertEqual("Apply", self._target(edge))
        self.assertEqual(0, stats.resolved_calls)
        self.assertEqual(1, stats.skipped_ambiguous)

    def test_skips_candidate_with_no_import_evidence(self) -> None:
        """A globally unique name is not evidence -- upstream's rule, kept."""
        self._file("src/Net/Server.cs", ["Kingdom.Net"])
        self._file("src/Core/Rules.cs", ["Kingdom.Core"])
        caller = self._symbol("src/Net/Server.cs", "Handle", parent="Server")
        self._symbol("src/Core/Rules.cs", "ApplyDamage", parent="RuleEngine")
        edge = self._edge("CALLS", caller, "ApplyDamage", "src/Net/Server.cs")

        stats = self._resolve(src__Net__Server="RuleEngine engine; engine.ApplyDamage(x);")

        self.assertEqual("ApplyDamage", self._target(edge))
        self.assertEqual(0, stats.resolved_calls)
        self.assertEqual(1, stats.skipped_no_evidence)

    def test_external_target_with_no_candidate_is_counted_separately(self) -> None:
        self._file("src/Net/Server.cs", ["Kingdom.Net"])
        caller = self._symbol("src/Net/Server.cs", "Handle", parent="Server")
        edge = self._edge("CALLS", caller, "FromSeconds", "src/Net/Server.cs")

        stats = self._resolve(src__Net__Server="TimeSpan.FromSeconds(3);")

        self.assertEqual("FromSeconds", self._target(edge))
        self.assertEqual(1, stats.skipped_no_candidate)

    def test_never_creates_a_self_edge(self) -> None:
        self._file("src/Core/A.cs", ["Kingdom.Core"])
        recurse = self._symbol("src/Core/A.cs", "Recurse", parent="A")
        edge = self._edge("CALLS", recurse, "Recurse", "src/Core/A.cs")

        stats = self._resolve(src__Core__A="void Recurse() { Recurse(); }")

        self.assertEqual("Recurse", self._target(edge))
        self.assertEqual(0, stats.resolved_calls)
        self.assertEqual(1, stats.skipped_self)

    def test_dotted_call_targets_are_left_alone(self) -> None:
        self._file("src/Core/A.cs", ["Kingdom.Core"])
        self._file("src/Core/B.cs", ["Kingdom.Core"])
        caller = self._symbol("src/Core/A.cs", "Run", parent="A")
        self._symbol("src/Core/B.cs", "Empty", parent="Array")
        edge = self._edge("CALLS", caller, "System.Array.Empty", "src/Core/A.cs")

        stats = self._resolve(src__Core__A="System.Array.Empty<int>();")

        self.assertEqual("System.Array.Empty", self._target(edge))
        self.assertEqual(0, stats.resolved_calls)

    def test_universal_object_members_are_never_resolved(self) -> None:
        """`digest[i].ToString("x2")` is not a call into your code."""
        self._file("src/Core/Ident.cs", ["Kingdom.Core"])
        self._file("src/Core/Hash.cs", ["Kingdom.Core"])
        caller = self._symbol("src/Core/Hash.cs", "HashOf", parent="DeckIdentity")
        self._symbol("src/Core/Ident.cs", "ToString", parent="MatchId")
        edge = self._edge("CALLS", caller, "ToString", "src/Core/Hash.cs")

        stats = self._resolve(src__Core__Hash="MatchId id; sb.Append(digest[i].ToString());")

        self.assertEqual("ToString", self._target(edge))
        self.assertEqual(0, stats.resolved_calls)
        self.assertEqual(1, stats.skipped_universal_member)

    # ---- INHERITS --------------------------------------------------------

    def test_resolves_inherits_against_class_nodes(self) -> None:
        self._file("src/Core/Rules.cs", ["Kingdom.Core"])
        child = self._symbol("src/Core/Rules.cs", "DamageAction", kind="Class")
        parent = self._symbol("src/Core/Rules.cs", "TargetedRuleAction", kind="Class")
        edge = self._edge("INHERITS", child, "TargetedRuleAction", "src/Core/Rules.cs")

        stats = self._resolve()

        self.assertEqual(parent, self._target(edge))
        self.assertEqual(1, stats.resolved_inherits)

    def test_strips_generic_arguments_before_matching(self) -> None:
        self._file("src/Core/Model.cs", ["Kingdom.Core"])
        self._file("src/Core/Compare.cs", ["Kingdom.Core"])
        child = self._symbol("src/Core/Model.cs", "RuleRef", kind="Class")
        iface = self._symbol("src/Core/Compare.cs", "IComparableRef", kind="Class")
        edge = self._edge("INHERITS", child, "IComparableRef<RuleRef>", "src/Core/Model.cs")

        self._resolve()

        self.assertEqual(iface, self._target(edge))

    def test_inherits_from_external_type_is_left_alone(self) -> None:
        self._file("src/Core/Model.cs", ["Kingdom.Core"])
        child = self._symbol("src/Core/Model.cs", "RuleRef", kind="Class")
        edge = self._edge("INHERITS", child, "IEquatable<RuleRef>", "src/Core/Model.cs")

        self._resolve()

        self.assertEqual("IEquatable<RuleRef>", self._target(edge))

    def test_inherits_does_not_match_a_function_of_the_same_name(self) -> None:
        self._file("src/Core/Rules.cs", ["Kingdom.Core"])
        child = self._symbol("src/Core/Rules.cs", "DamageAction", kind="Class")
        self._symbol("src/Core/Rules.cs", "RuleAction", kind="Function", parent="X")
        edge = self._edge("INHERITS", child, "RuleAction", "src/Core/Rules.cs")

        stats = self._resolve()

        self.assertEqual("RuleAction", self._target(edge))
        self.assertEqual(0, stats.resolved_inherits)

    # ---- blast radius ----------------------------------------------------

    def test_leaves_non_csharp_edges_untouched(self) -> None:
        self._file("tooling/a.py", [], language="python")
        self._file("tooling/b.py", [], language="python")
        caller = self._symbol("tooling/a.py", "run", language="python", parent="A")
        self._symbol("tooling/b.py", "helper", language="python", parent="B")
        edge = self._edge("CALLS", caller, "helper", "tooling/a.py")

        self._resolve(tooling__a="B.helper()")

        self.assertEqual("helper", self._target(edge))

    def test_leaves_already_qualified_targets_untouched(self) -> None:
        self._file("src/Core/A.cs", ["Kingdom.Core"])
        caller = self._symbol("src/Core/A.cs", "Run", parent="A")
        callee = self._symbol("src/Core/A.cs", "Helper", parent="A")
        edge = self._edge("CALLS", caller, callee, "src/Core/A.cs")

        stats = self._resolve(src__Core__A="A.Helper();")

        self.assertEqual(callee, self._target(edge))
        self.assertEqual(0, stats.resolved_calls)

    def test_dry_run_reports_without_writing(self) -> None:
        self._file("src/Net/Server.cs", ["Kingdom.Net"])
        self._file("src/Core/Rules.cs", ["Kingdom.Core"])
        caller = self._symbol("src/Net/Server.cs", "Handle", parent="Server")
        self._symbol("src/Core/Rules.cs", "ApplyDamage", parent="RuleEngine")
        self._edge("IMPORTS_FROM", "src/Net/Server.cs", "Kingdom.Core", "src/Net/Server.cs")
        edge = self._edge("CALLS", caller, "ApplyDamage", "src/Net/Server.cs")

        stats = self._resolve(
            dry_run=True, src__Net__Server="RuleEngine engine; engine.ApplyDamage(x);"
        )

        self.assertEqual(1, stats.resolved_calls)
        self.assertEqual("ApplyDamage", self._target(edge))

    def test_is_idempotent(self) -> None:
        self._file("src/Net/Server.cs", ["Kingdom.Net"])
        self._file("src/Core/Rules.cs", ["Kingdom.Core"])
        caller = self._symbol("src/Net/Server.cs", "Handle", parent="Server")
        callee = self._symbol("src/Core/Rules.cs", "ApplyDamage", parent="RuleEngine")
        self._edge("IMPORTS_FROM", "src/Net/Server.cs", "Kingdom.Core", "src/Net/Server.cs")
        edge = self._edge("CALLS", caller, "ApplyDamage", "src/Net/Server.cs")

        source = "RuleEngine engine; engine.ApplyDamage(x);"
        first = self._resolve(src__Net__Server=source)
        second = self._resolve(src__Net__Server=source)

        self.assertEqual(1, first.resolved_calls)
        self.assertEqual(0, second.resolved_calls)
        self.assertEqual(callee, self._target(edge))


if __name__ == "__main__":
    unittest.main(verbosity=2)
