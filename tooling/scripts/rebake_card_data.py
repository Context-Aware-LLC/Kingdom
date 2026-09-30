"""One command to bring every artifact derived from the canonical card rows back in sync.

Usage:
  python tooling/scripts/rebake_card_data.py --check    # report staleness, change nothing
  python tooling/scripts/rebake_card_data.py --write    # regenerate whatever is stale
  python tooling/scripts/rebake_card_data.py --check --skip-gates      # no CI-gate replay

Editing `unity/Assets/CardData/*.cards.json` silently stales ONE derived
artifact, and nothing at edit time forces it. This is the controller that runs it, along with
the blocking content gates, so "did I remember everything?" is one line.

  1. Card-admin aggregate          -> tooling/card-admin/build_data.py  (data.json + data.js)

Two other artifacts used to sit in this list and are gone, both 2026-08-22. Baked
CardDefinitionSO assets under Assets/Cards: nothing read them at runtime, they were in no
Addressables group, and they covered only the 36 cards of the original vertical slice while a
pre-commit hook blocked commits on drift. Starter deck fixtures: the starter deck is generated
LIVE from the card database on every character creation, nothing loads a fixture at runtime, and
the fixtures plus DefaultDeckFixtureTests were archived in a9eaa0e3 (2026-08-18).

REGENERATING IS NOT THE SAME AS BEING SHIPPABLE, which is the trap this script grew a third
step for. Every artifact above can be perfectly in sync with a card row that CI will still
reject, because the blocking content gates read the ROWS, not the derived copies — so this
script reported CLEAN on 2026-08-18 for a row whose artId was missing a separator and whose
art therefore resolved to nothing. Step 2 replays the three blocking gates from
.github/workflows/ci.yml Stage 6 so the failure lands here instead of on the runner:

  2a. Emberfall.Tools.CardCi          — rows parse; C-series validation
  2b. audit_card_art_names.py         — every artId resolves to a png, and every seed key
                                        resolves to a row (--fail-missing-only --check-seeds)

They VERIFY and never write, so they behave identically under --check and --write. A gate
failure is a real defect rather than staleness: it exits 2, not 1. `--skip-gates` exists for
the mid-authoring case where a card is deliberately ahead of its art — but CI is still
blocking there, so skipping only defers the same failure.

Every child's stdout is reproduced verbatim. Do not summarise it away — the per-tool counters are
the trustworthy numbers, and they are the deliverable rather than a description of them.
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
ID_DRIFT = ROOT / "tooling" / "scripts" / "check_card_id_drift.py"
ADMIN_BUILD = ROOT / "tooling" / "card-admin" / "build_data.py"
ADMIN_OUTPUTS = (
    ROOT / "tooling" / "card-admin" / "data.json",
    ROOT / "tooling" / "card-admin" / "data.js",
)
CARD_CI_PROJECT = ROOT / "src" / "Emberfall.Tools.CardCi"
ART_AUDIT = ROOT / "tooling" / "scripts" / "audit_card_art_names.py"
CARD_DIR = ROOT / "unity" / "Emberfall" / "Assets" / "CardData"
ART_DIR = ROOT / "unity" / "Emberfall" / "Assets" / "Art"
ART_CARDS = ART_DIR / "Cards"



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
    banner("0/3  CARD ID DRIFT  (advisory — saved decks live outside this repo)")
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
    banner("1/3  CARD-ADMIN AGGREGATE  (data.json + data.js)")
    # --no-deploy on purpose. build_data.py run on its own brings the LIVE server in line with
    # the working tree; this controller runs the blocking gates and must stay able to report a
    # red one, which a run that has already pushed to production cannot do. It still names the
    # drift (SERVER_CORPUS=stale) -- deploying it is a deliberate second command.
    argv = [sys.executable, str(ADMIN_BUILD)] + (["--no-deploy"] if write else ["--check"])
    code, _ = run(argv)
    if write:
        return "OK" if code == 0 else "ERROR"
    if code == 0:
        return "OK"
    return "STALE" if code == 1 else "ERROR"


def step_gates(write, skip):
    """Replay the blocking content gates from CI Stage 6 against the rows as they stand.

    These verify and never regenerate, so --check and --write reach the same verdict; --write
    only differs in that it may repair an art-name typo before re-asking 2b.
    """
    banner("2/3  CONTENT GATES  (what CI Stage 6 will fail on)")
    if skip:
        print("SKIPPED by --skip-gates. CI still runs all three; skipping only defers the failure.")
        return "SKIPPED"

    failed = []

    print("-- 2a  Emberfall.Tools.CardCi --")
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

    # --check-seeds rides along with the gate run rather than being a separate step: a stranded
    # seed is caused by exactly the thing this stage already watches (an artId or a handle
    # moving), and it is the one art failure with NO visible symptom -- the card just reads as
    # unseeded and generates on placeholder direction. 18 seeds sat stranded until 2026-08-24.
    print("\n-- 2b  audit_card_art_names.py --fail-missing-only --check-seeds --")
    code, _ = run([sys.executable, str(ART_AUDIT), "--fail-missing-only", "--check-seeds"])
    if code != 0:
        # The auto-rename that used to run here went with df309c46, which deleted
        # repair_art_names/art_name_repairs and left this call standing -- so every 2b failure
        # died with a NameError instead of reporting the failure it had just found. The audit
        # above still names every unresolvable artId; renaming is manual now, and the .meta moves
        # with the .png in the same breath (see the art rules in CLAUDE.md).
        failed.append("art-names")

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
        "--skip-gates", action="store_true",
        help="skip the CI content gates (CardCi, art names, trigger selectors)",
    )
    args = parser.parse_args()

    results = {
        "card_id_drift": step_id_drift(),
        "card_admin_aggregate": step_admin_aggregate(args.write),
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
