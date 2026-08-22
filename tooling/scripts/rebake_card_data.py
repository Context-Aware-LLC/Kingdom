"""One command to bring every artifact derived from the canonical card rows back in sync.

Usage:
  python tooling/scripts/rebake_card_data.py --check    # report staleness, change nothing
  python tooling/scripts/rebake_card_data.py --write    # regenerate whatever is stale
  python tooling/scripts/rebake_card_data.py --write --skip-fixtures   # no dotnet test
  python tooling/scripts/rebake_card_data.py --check --skip-gates      # no CI-gate replay

Editing `unity/Emberfall/Assets/CardData/*.cards.json` silently stales TWO derived
artifacts, and nothing at edit time forces either of them. This is the controller that runs
both, so the answer to "did I remember everything?" is one line instead of two.

  1. Card-admin aggregate          -> tooling/card-admin/build_data.py  (data.json + data.js)
  2. Starter deck fixtures         -> [Explicit] DefaultDeckFixtureTests.Regenerate_Fixtures

A third artifact used to sit at the top of this list: baked CardDefinitionSO assets under
Assets/Cards. They were deleted 2026-08-22 — nothing read them at runtime, they were in no
Addressables group, and they covered only the 36 cards of the original vertical slice while a
pre-commit hook blocked commits when they drifted.

REGENERATING IS NOT THE SAME AS BEING SHIPPABLE, which is the trap this script grew a third
step for. Every artifact above can be perfectly in sync with a card row that CI will still
reject, because the blocking content gates read the ROWS, not the derived copies — so this
script reported CLEAN on 2026-08-18 for a row whose artId was missing a separator and whose
art therefore resolved to nothing. Step 3 replays the three blocking gates from
.github/workflows/ci.yml Stage 6 so the failure lands here instead of on the runner:

  3a. Emberfall.Tools.CardCi          — rows parse; C-series validation
  3b. audit_card_art_names.py         — every artId resolves to a png (--fail-missing-only)
  3c. scan_unresolvable_trigger_selectors.py — no fired trigger with a player-choice selector

They VERIFY and never write, so they behave identically under --check and --write. A gate
failure is a real defect rather than staleness: it exits 2, not 1. `--skip-gates` exists for
the mid-authoring case where a card is deliberately ahead of its art — but CI is still
blocking there, so skipping only defers the same failure.

DELIBERATELY NOT TOUCHED: tests/Emberfall.Core.Tests/GoldenReplay/cardsets/M1.cards.snapshot.json
is a frozen point-in-time copy that the golden replays read from. Leaving it stale is the
correct behaviour — regenerating it would silently drift every pinned golden.

Step 3 costs a `dotnet test`, so it is gated on check_deck_pool_drift.py, which reads the fields
that actually reach DefaultDeckGenerator (cost, rarity, class, collectible, type, id) off the
generator itself. A name or artId edit cannot move a fixture and will not pay for the test run.
When the drift probe cannot compare (no HEAD copy), the step runs rather than being skipped.

Every child's stdout is reproduced verbatim. Do not summarise it away — the per-tool counters
(CARD_DEFINITION_SO_MISMATCHES, DECK_POOL_DRIFT_CARDS) are the trustworthy numbers, and the
NUnit gates they replace report only the FIRST stale item in their loop.
"""

import argparse
import json
import os
import pathlib
import re
import subprocess
import sys

# Children are read as UTF-8 (see run()), so this side has to be able to write it back out.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):  # already redirected to something without reconfigure
        pass


ROOT = pathlib.Path(__file__).resolve().parents[2]
DRIFT_PROBE = ROOT / "tooling" / "scripts" / "check_deck_pool_drift.py"
ID_DRIFT = ROOT / "tooling" / "scripts" / "check_card_id_drift.py"
ADMIN_BUILD = ROOT / "tooling" / "card-admin" / "build_data.py"
ADMIN_OUTPUTS = (
    ROOT / "tooling" / "card-admin" / "data.json",
    ROOT / "tooling" / "card-admin" / "data.js",
)
CARD_CI_PROJECT = ROOT / "src" / "Emberfall.Tools.CardCi"
ART_AUDIT = ROOT / "tooling" / "scripts" / "audit_card_art_names.py"
TRIGGER_SCAN = ROOT / "tooling" / "scripts" / "scan_unresolvable_trigger_selectors.py"
CARD_DIR = ROOT / "unity" / "Emberfall" / "Assets" / "CardData"
ART_DIR = ROOT / "unity" / "Emberfall" / "Assets" / "Art"
ART_CARDS = ART_DIR / "Cards"
TEST_PROJECT = ROOT / "tests" / "Emberfall.Core.Tests" / "Emberfall.Core.Tests.csproj"
REGENERATE = "DefaultDeckFixtureTests.Regenerate_Fixtures"
VERIFY = "DefaultDeckFixtureTests.Every_Class_Fixture_Is_Legal_And_Matches_The_Generator"

DRIFT_COUNT = re.compile(r"^DECK_POOL_DRIFT_CARDS=(\d+)$", re.MULTILINE)
DRIFT_UNKNOWN = re.compile(r"^DECK_POOL_DRIFT=UNKNOWN$", re.MULTILINE)


def banner(title):
    print()
    print("=" * 72)
    print(title)
    print("=" * 72)


def run(argv, cwd=ROOT):
    """Run a child, echo its output verbatim, and hand back (returncode, stdout).

    Both encodings are pinned deliberately. A Python child on Windows writes its stdout in the
    console codepage when it is piped, so an em dash comes back as bytes this side decodes to
    U+FFFD; PYTHONIOENCODING makes the child emit UTF-8 instead. Echoing it then needs a UTF-8
    stdout of our own (set at import), or the re-encode raises UnicodeEncodeError and takes down
    the whole rebake over a dash in some other tool's banner.
    """
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    done = subprocess.run(
        argv, cwd=str(cwd), capture_output=True, text=True,
        encoding="utf-8", errors="replace", env=env,
    )
    if done.stdout:
        print(done.stdout, end="" if done.stdout.endswith("\n") else "\n")
    if done.stderr:
        print(done.stderr, end="" if done.stderr.endswith("\n") else "\n", file=sys.stderr)
    return done.returncode, done.stdout or ""


def step_id_drift():
    """Report ids that vanished or were repointed. Advisory here, never a blocker.

    This runs FIRST because it is the only step that can tell you the edit reached data this
    script cannot fix. The three artifacts below all live in the repo and are regenerable; a
    renamed id also breaks every saved deck under the player's LocalLow folder, which is outside
    the repo, invisible to `dotnet test`, and silently hidden by the deckbuilder when it fails to
    resolve. `--write` cannot repair that, so this step's job is to say so out loud.
    """
    banner("0/4  CARD ID DRIFT  (advisory — saved decks live outside this repo)")
    code, _ = run([sys.executable, str(ID_DRIFT)])
    if code == 0:
        return "OK"
    print(
        "\nAdvisory only: the rebake continues. But a removed or repointed id is NOT fixed by\n"
        "anything below — repair the saved decks in the same change, or they keep an entry that\n"
        "resolves to nothing and cannot be seen or removed in the deckbuilder."
    )
    return "DRIFT"


def step_admin_aggregate(write):
    banner("1/4  CARD-ADMIN AGGREGATE  (data.json + data.js)")
    argv = [sys.executable, str(ADMIN_BUILD)] + ([] if write else ["--check"])
    code, _ = run(argv)
    if write:
        return "OK" if code == 0 else "ERROR"
    if code == 0:
        return "OK"
    return "STALE" if code == 1 else "ERROR"


def fixtures_need_work():
    """Ask the drift probe whether a fixture-moving field actually changed."""
    code, out = run([sys.executable, str(DRIFT_PROBE), "--worktree"])
    if code != 0 or DRIFT_UNKNOWN.search(out):
        print("DECK_POOL_DRIFT probe inconclusive — treating fixtures as needing regeneration.")
        return True
    found = DRIFT_COUNT.search(out)
    if found is None:
        print("DECK_POOL_DRIFT probe printed no count — treating fixtures as needing regeneration.")
        return True
    return int(found.group(1)) > 0


def dotnet_test(filter_expr):
    return run([
        "dotnet", "test", str(TEST_PROJECT),
        "--filter", f"FullyQualifiedName~{filter_expr}",
        "--nologo", "--verbosity", "minimal",
    ])[0]


def step_deck_fixtures(write, skip):
    banner("2/4  STARTER DECK FIXTURES")
    if skip:
        print("SKIPPED by --skip-fixtures. The Unit lane still gates this; run it before you push.")
        return "SKIPPED"

    if not fixtures_need_work():
        print("No fixture-moving field changed. Nothing to regenerate.")
        return "OK"

    if not write:
        print("Fixtures are stale — rerun with --write to regenerate them.")
        return "STALE"

    print("\n-- regenerating --")
    if dotnet_test(REGENERATE) != 0:
        return "ERROR"
    print("\n-- verifying against the generator --")
    return "OK" if dotnet_test(VERIFY) == 0 else "ERROR"


def art_name_repairs():
    """Missing artIds paired with the orphan png that is almost certainly the same file.

    Only a pairing that is unambiguous BOTH ways is offered: one missing artId, and one orphan
    whose name matches it once every separator is dropped. That is the whole class of typo this
    can safely fix - a dropped or doubled underscore between the class token and the name, which
    is what `stone_heartstone_herald` vs `stoneheartstone_herald.png` was on 2026-08-18. Anything
    looser would be guessing at which art belongs to which card, and renaming the wrong file is
    worse than the failing gate: the gate then passes over a card wearing someone else's portrait.
    """
    wanted = {}
    for path in sorted(CARD_DIR.glob("*.cards.json")):
        doc = json.loads(path.read_text(encoding="utf-8"))
        rows = doc["cards"] if isinstance(doc, dict) and "cards" in doc else doc
        for row in rows:
            if row.get("artId"):
                wanted[row["artId"]] = row.get("id")

    on_disk = {png.stem: png for png in sorted(ART_CARDS.rglob("*.png"))}
    missing = {a: c for a, c in wanted.items() if a not in on_disk}
    orphans = {stem: png for stem, png in on_disk.items() if stem not in wanted}

    def squash(name):
        return name.replace("_", "").lower()

    repairs = []
    for art_id in sorted(missing):
        hits = [png for stem, png in orphans.items() if squash(stem) == squash(art_id)]
        claims = [a for a in missing if squash(a) == squash(art_id)]
        if len(hits) == 1 and len(claims) == 1:
            repairs.append((missing[art_id], art_id, hits[0]))
    return missing, orphans, repairs


def repair_art_names(write):
    """Rename unambiguous art typos into place. Returns True if anything was renamed.

    The .meta moves with the .png in the same breath. That pair carries the guid every
    Addressables entry points at, so splitting them mints a new guid and silently orphans the
    entry - see the art rules in CLAUDE.md.
    """
    missing, orphans, repairs = art_name_repairs()
    if not missing:
        return False

    if not repairs:
        print(
            "\nNo unambiguous rename to offer. Each artId below resolves to no file, so either the\n"
            "row's artId is wrong or the art has not been generated yet:"
        )
        for art_id in sorted(missing):
            print(f"  {missing[art_id]:<36} artId '{art_id}' -> no {art_id}.png under Art/Cards/")
        if orphans:
            print("Orphan pngs that no row claims (NOT deleted, reported only):")
            for stem in sorted(orphans):
                print(f"  {stem}.png")
        return False

    print("\nUnambiguous art-name typos - the file and the artId differ only in separators:")
    for card_id, art_id, png in repairs:
        print(f"  {card_id}: {png.name}  ->  {art_id}.png")
    if not write:
        print("Rerun with --write to rename them (the .meta moves with each .png).")
        return False

    for _card_id, art_id, png in repairs:
        meta = png.with_name(png.name + ".meta")
        png.rename(png.with_name(art_id + ".png"))
        if meta.exists():
            meta.rename(meta.with_name(art_id + ".png.meta"))
        print(f"renamed {png.name} -> {art_id}.png (+ .meta)")
    return True


def step_gates(write, skip):
    """Replay the blocking content gates from CI Stage 6 against the rows as they stand.

    These verify and never regenerate, so --check and --write reach the same verdict; --write
    only differs in that it may repair an art-name typo before re-asking 4b.
    """
    banner("3/4  CONTENT GATES  (what CI Stage 6 will fail on)")
    if skip:
        print("SKIPPED by --skip-gates. CI still runs all three; skipping only defers the failure.")
        return "SKIPPED"

    failed = []

    print("-- 4a  Emberfall.Tools.CardCi --")
    code, _ = run([
        "dotnet", "run", "--project", str(CARD_CI_PROJECT), "-c", "Release",
        "--", str(CARD_DIR), str(ART_DIR),
    ])
    if code != 0:
        failed.append("CardCi")
        print(
            "\nCardCi rejected the corpus. Read its ERROR lines above: `row failed to parse` means\n"
            "the row never loaded at all and is absent from every count printed beside it, while a\n"
            "C-series code is a named validation rule you can look up in CardValidator."
        )

    print("\n-- 4b  audit_card_art_names.py --fail-missing-only --")
    code, _ = run([sys.executable, str(ART_AUDIT), "--fail-missing-only"])
    if code != 0:
        if repair_art_names(write):
            print("\n-- re-running 4b after the rename --")
            code, _ = run([sys.executable, str(ART_AUDIT), "--fail-missing-only"])
        if code != 0:
            failed.append("art-names")

    print("\n-- 4c  scan_unresolvable_trigger_selectors.py --")
    code, _ = run([sys.executable, str(TRIGGER_SCAN)])
    if code != 0:
        failed.append("trigger-selectors")
        print(
            "\nA fired trigger declares a selector only a player can resolve. Every trigger except\n"
            "ON_PLAY and ON_CAST fires with an empty target list, so the card is a live crash the\n"
            "moment it fires. Give the rule an auto-resolvable selector, or move it to ON_PLAY."
        )

    if failed:
        print(f"\nGATES FAILED: {', '.join(failed)}")
        return "FAILED"
    return "OK"


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--check", action="store_true", help="report staleness, change nothing")
    mode.add_argument("--write", action="store_true", help="regenerate whatever is stale")
    parser.add_argument(
        "--skip-fixtures", action="store_true",
        help="skip the deck-fixture step and its dotnet test",
    )
    parser.add_argument(
        "--skip-gates", action="store_true",
        help="skip the CI content gates (CardCi, art names, trigger selectors)",
    )
    args = parser.parse_args()

    results = {
        "card_id_drift": step_id_drift(),
        "card_admin_aggregate": step_admin_aggregate(args.write),
        "deck_fixtures": step_deck_fixtures(args.write, args.skip_fixtures),
        "content_gates": step_gates(args.write, args.skip_gates),
    }

    banner("SUMMARY")
    for name, state in results.items():
        print(f"{name:<24} {state}")
    print(f"CARD_DATA_REBAKE_MODE={'write' if args.write else 'check'}")

    # A gate failure is a DEFECT, not staleness - no amount of regenerating fixes it - so it
    # shares the error exit rather than the stale one. It keeps a distinct word in the summary
    # because "the corpus is invalid" and "a tool crashed" want different next moves.
    if "ERROR" in results.values() or "FAILED" in results.values():
        print("CARD_DATA_REBAKE=ERROR")
        return 2
    if "STALE" in results.values():
        print("CARD_DATA_REBAKE=STALE")
        return 1
    # DRIFT does not fail the run — the three derived artifacts really are clean. It changes the
    # headline so `CLEAN` can never be read as "everything a card edit touched is now consistent",
    # which is exactly how a renamed id sat unrepaired in seven saved decks on 2026-08-15.
    if "DRIFT" in results.values():
        print("CARD_DATA_REBAKE=CLEAN_WITH_ID_DRIFT")
        return 0
    print("CARD_DATA_REBAKE=CLEAN")
    return 0


if __name__ == "__main__":
    sys.exit(main())
