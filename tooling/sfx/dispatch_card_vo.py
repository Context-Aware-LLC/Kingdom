#!/usr/bin/env python3
"""Generate per-card voice lines from card-voice-casting.json into the Unity project.

Sibling to dispatch_vo.py, which generates the 8 hero-class voices from voice-lines.json.
This one is per MINION CARD instead of per class -- see card-voice-casting.json's _about
for why the two files are separate. Imports its ElevenLabs plumbing (WAV header, API-key
resolution, the actual call, HTTP error formatting) straight from dispatch_vo.py rather
than duplicating it; only the keying (card handle, not class) and the output paths differ.

Reads tooling/sfx/card-voice-casting.json and writes takes into
    art-pipeline/sfx/voice/Cards/<handle>/vo_card_<handle>_<moment>_<n>_take<T>.wav
A promoted take (via soundweb.py) lands at
    unity/Assets/Sound/Voice/Cards/<handle>/vo_card_<handle>_<moment>_<n>.wav

Subfolder depth does not matter to Addressables: AddressablesSetup.AddFolderAssets scans
the whole Assets/Sound/Voice tree recursively and addresses by FILE NAME
(`vo/<stem>`), same as every hero voice clip. No Editor-side change needed for a new
handle folder to appear under the existing Voice group -- Setup Groups + Migrate Card
Data still has to be RUN, same as ever, and the .meta Unity writes still has to be
committed.

    python tooling/sfx/dispatch_card_vo.py --dry-run
    python tooling/sfx/dispatch_card_vo.py --only night_seraphine
    python tooling/sfx/dispatch_card_vo.py --only night_seraphine --moment death
    python tooling/sfx/dispatch_card_vo.py                      # every cast card, outstanding lines

A card with no voice_id yet (voice_name/voice_id still null) is SKIPPED and listed as not
cast -- that is not an error, it just isn't this run's job.
"""

import argparse
import json
import pathlib
import sys
import urllib.error

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from dispatch_vo import resolve_api_key, speak, http_error_text, CREDITS_PER_CHAR  # noqa: E402

ROOT = pathlib.Path(__file__).resolve().parents[2]
CARDS = pathlib.Path(__file__).resolve().parent / "card-voice-casting.json"
# Takes are candidates, same rule as dispatch_vo.py: never straight into Assets/, because
# the Voice group addresses by file name and a _takeN suffix would claim its own address.
TAKE_ROOT = ROOT / "art-pipeline" / "sfx" / "voice" / "Cards"
FINAL_ROOT = ROOT / "unity" / "Assets" / "Sound" / "Voice" / "Cards"


def plan(data, only_handle, only_moment):
    """Every (handle, moment, index, stem, text, voice_id, voice_name) the run would produce."""
    for handle, entry in data["cards"].items():
        if only_handle and handle != only_handle:
            continue
        vid = entry.get("voice_id")
        vname = entry.get("voice_name") or ""
        for moment, lines in entry.get("lines", {}).items():
            if only_moment and moment != only_moment:
                continue
            for i, text in enumerate(lines, 1):
                stem = "vo_card_%s_%s_%d" % (handle, moment, i)
                yield handle, moment, i, stem, text, vid, vname


def main():
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("--cards", type=pathlib.Path, default=CARDS)
    ap.add_argument("--out", type=pathlib.Path, default=TAKE_ROOT, help="where takes land")
    ap.add_argument("--final-root", type=pathlib.Path, default=FINAL_ROOT,
                    help="checked so a line already finalised into Assets is not regenerated")
    ap.add_argument("--takes", type=int, default=3, help="takes per line (2 minimum, always)")
    ap.add_argument("--api-key")
    ap.add_argument("--only", help="one card handle, e.g. night_seraphine")
    ap.add_argument("--moment", help="one moment, e.g. death")
    ap.add_argument("--skip", action="append", default=[],
                    help="canonical stem to leave alone, repeatable")
    ap.add_argument("--force", action="store_true", help="regenerate clips that exist")
    ap.add_argument("--dry-run", action="store_true", help="print the plan and cost, send nothing")
    args = ap.parse_args()

    data = json.loads(args.cards.read_text(encoding="utf-8"))
    if args.only and args.only not in data["cards"]:
        ap.error("unknown card handle %r; expected one of %s" % (args.only, ", ".join(data["cards"])))

    todo, skipped, uncast = [], 0, set()
    for handle, moment, i, stem, text, vid, vname in plan(data, args.only, args.moment):
        if not vid:
            uncast.add(handle)
            continue
        if stem in set(args.skip):
            skipped += args.takes
            continue
        # A line finalised into Assets is done; do not spend on it again.
        if (args.final_root / handle / (stem + ".wav")).exists() and not args.force:
            skipped += 1
            continue
        for take in range(1, max(1, args.takes) + 1):
            label = "%s_take%d" % (stem, take)
            dest = args.out / handle / (label + ".wav")
            if dest.exists() and not args.force:
                skipped += 1
                continue
            todo.append((handle, label, text, vid, vname, dest))

    chars = sum(len(t) for _, _, t, _, _, _ in todo)
    print("%d line(s) to generate, %d already on disk" % (len(todo), skipped))
    print("~%s characters, ~%s credits" % (format(chars, ","), format(chars * CREDITS_PER_CHAR, ",")))
    if uncast:
        print("SKIPPED, not cast yet: %s" % ", ".join(sorted(uncast)), file=sys.stderr)

    if args.dry_run:
        for handle, stem, text, _, vname, _ in todo:
            print("  %-42s %-22s %s" % (stem, vname[:20], text))
        return 0

    if not todo:
        return 0

    api_key = resolve_api_key(args.api_key)
    if not api_key:
        print(
            "\nNo ElevenLabs API key. Set $ELEVENLABS_API_KEY, write tooling/sfx/.elevenlabs_key,"
            " or pass --api-key.",
            file=sys.stderr,
        )
        return 2

    failures = []
    for handle, stem, text, vid, vname, dest in todo:
        dest.parent.mkdir(parents=True, exist_ok=True)
        try:
            audio = speak(api_key, vid, text)
        except urllib.error.HTTPError as e:
            print("  %-42s FAILED  %s" % (stem, http_error_text(e)))
            failures.append(stem)
            continue
        except urllib.error.URLError as e:
            print("  %-42s FAILED  %s" % (stem, e.reason))
            failures.append(stem)
            continue
        dest.write_bytes(audio)
        print("  %-42s %-20s %s KB   %s" % (stem, vname[:18], format(len(audio) // 1024, ","), text))

    print(
        "\nThese are TAKES, under art-pipeline/. Pick one per line, trim it, and save it into"
        "\n%s/<handle>/ under the canonical stem"
        "\nwith NO _takeN suffix -- a take that reaches Assets claims its own Addressables"
        "\naddress and the rejects ship with the keeper."
        "\n\nThen run Kingdom/Regenerate/Addressables Groups in the Editor and"
        "\ncommit the .meta Unity generates. Until both happen the clip has no address and"
        "\nfails silently at runtime." % args.final_root
    )
    if failures:
        print("\n%d failed: %s" % (len(failures), ", ".join(failures)))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
