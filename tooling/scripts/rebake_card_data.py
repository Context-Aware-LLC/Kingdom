"""One command to bring every artifact derived from the canonical card rows back in sync.

Usage:
  python tooling/scripts/rebake_card_data.py --check    # report staleness, change nothing
  python tooling/scripts/rebake_card_data.py --write    # regenerate whatever is stale
  python tooling/scripts/rebake_card_data.py --write --skip-fixtures   # no dotnet test

Editing `unity/Emberfall/Assets/CardData/*.cards.json` silently staleness THREE derived
artifacts, and nothing at edit time forces any of them. This is the controller that runs all
three, so the answer to "did I remember everything?" is one line instead of three.

  1. Baked CardDefinitionSO YAML   -> rebake_card_definition_so_assets.py
  2. Card-admin aggregate          -> tooling/card-admin/build_data.py  (data.json + data.js)
  3. Starter deck fixtures         -> [Explicit] DefaultDeckFixtureTests.Regenerate_Fixtures

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
import pathlib
import re
import subprocess
import sys


ROOT = pathlib.Path(__file__).resolve().parents[2]
SO_REBAKE = ROOT / "tooling" / "scripts" / "rebake_card_definition_so_assets.py"
DRIFT_PROBE = ROOT / "tooling" / "scripts" / "check_deck_pool_drift.py"
ADMIN_BUILD = ROOT / "tooling" / "card-admin" / "build_data.py"
ADMIN_OUTPUTS = (
    ROOT / "tooling" / "card-admin" / "data.json",
    ROOT / "tooling" / "card-admin" / "data.js",
)
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
    """Run a child, echo its output verbatim, and hand back (returncode, stdout)."""
    done = subprocess.run(
        argv, cwd=str(cwd), capture_output=True, text=True, encoding="utf-8", errors="replace"
    )
    if done.stdout:
        print(done.stdout, end="" if done.stdout.endswith("\n") else "\n")
    if done.stderr:
        print(done.stderr, end="" if done.stderr.endswith("\n") else "\n", file=sys.stderr)
    return done.returncode, done.stdout or ""


def step_so_assets(write):
    banner("1/3  BAKED CardDefinitionSO ASSETS")
    code, _ = run([sys.executable, str(SO_REBAKE), "--write" if write else "--check"])
    if code == 2:
        return "ERROR"
    return "OK" if code == 0 else "STALE"


def step_admin_aggregate(write):
    banner("2/3  CARD-ADMIN AGGREGATE  (data.json + data.js)")
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
    banner("3/3  STARTER DECK FIXTURES")
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
    args = parser.parse_args()

    results = {
        "baked_so_assets": step_so_assets(args.write),
        "card_admin_aggregate": step_admin_aggregate(args.write),
        "deck_fixtures": step_deck_fixtures(args.write, args.skip_fixtures),
    }

    banner("SUMMARY")
    for name, state in results.items():
        print(f"{name:<24} {state}")
    print(f"CARD_DATA_REBAKE_MODE={'write' if args.write else 'check'}")

    if "ERROR" in results.values():
        print("CARD_DATA_REBAKE=ERROR")
        return 2
    if "STALE" in results.values():
        print("CARD_DATA_REBAKE=STALE")
        return 1
    print("CARD_DATA_REBAKE=CLEAN")
    return 0


if __name__ == "__main__":
    sys.exit(main())
