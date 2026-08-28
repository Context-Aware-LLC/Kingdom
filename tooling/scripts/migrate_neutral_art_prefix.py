#!/usr/bin/env python3
"""Give every NEUTRAL card's artId the `neutral_` prefix every other class already has.

RULING (Sam, 2026-08-24). The convention used to say the ABSENCE of a class token is what means
neutral. That made neutral the one class whose art you identify by what is missing, and it read as
ambiguous exactly where it mattered: `stone_sentry` is the NEUTRAL card "Stone Sentry", but its
leading segment is STONEWARDEN's token, and `audit_card_art_names.py` reported it as a mismatch for
as long as that check existed. The row was always right and the rule was the problem.

`neutral` now occupies the classToken slot like any other class, roles included -- the classed
spelling is `<classToken>_<role>_<name>` (`tide_token_wisp`, `stone_token_shard`), so a neutral
token is `neutral_token_wisp`. Within the neutrals the prefixed rows were the outliers; across the
corpus the UNPREFIXED ones were, and the corpus is what a reader is holding in their head.

WHAT MOVES, and why it has to be one script. These four are one fact stored four places, and any
one of them left behind is silent:

  1. the png + its .meta, RENAMED TOGETHER      -- the .meta carries the guid the Addressables
                                                   entry resolves by. Moving the pair keeps the
                                                   guid; moving the png alone mints a new one and
                                                   orphans the entry with no error anywhere.
  2. the row's artId in CardData/*.cards.json   -- what the game asks for.
  3. the prompt-manifest key                    -- the re-roll history for that image.
  4. the art-pipeline candidate directory       -- gitignored and disposable, but moving it keeps
                                                   the candidates findable next to the card.

The SEED used to be a fifth, and is not one any more (Sam, 2026-08-27): hand-authored direction now
keys on the row's numeric id, which no rename can move. See gen_card_prompt_briefs.seed_key. That
is the whole point of the split -- everything left in this list is about the IMAGE, so an artId
rename is now only ever about the picture.

AFTERWARDS, IN UNITY: run `Emberfall/Addressables/Setup Groups + Migrate Card Data`. Group entries
store `m_GUID` + `m_Address` and NO path, and the address is a flat `card_art/<fileName>`; the guid
survives this script but the ADDRESS does not, and only that menu item rebuilds it from disk. The
game loads by address, so skipping it leaves 38 cards resolving to nothing.

Refuses to run while unity/Emberfall/Temp/UnityLockfile exists. An open Editor owns the asset
database; renaming assets underneath it races the importer and can remint the guids this whole
script exists to preserve.

  python tooling/scripts/migrate_neutral_art_prefix.py --check    # print the plan, touch nothing
  python tooling/scripts/migrate_neutral_art_prefix.py --write
"""
import argparse
import collections
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
CARD_DIR = ROOT / "unity" / "Emberfall" / "Assets" / "CardData"
ART_ROOT = ROOT / "unity" / "Emberfall" / "Assets" / "Art" / "Cards"
MANIFEST = ROOT / "art-pipeline" / "card-art" / "prompt-manifest.json"
CANDIDATES = ROOT / "art-pipeline" / "card-art"
LOCKFILE = ROOT / "unity" / "Emberfall" / "Temp" / "UnityLockfile"

PREFIX = "neutral_"

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass


def card_files():
    return sorted(CARD_DIR.glob("*.cards.json"))


def load(path):
    doc = json.loads(path.read_text(encoding="utf-8"))
    return doc, (doc["cards"] if isinstance(doc, dict) and "cards" in doc else doc)


def build_plan():
    """[(row, set_id, old_art, new_art)] for every NEUTRAL row that still lacks the prefix."""
    plan, taken = [], set()
    for path in card_files():
        _, cards = load(path)
        for card in cards:
            if card.get("artId"):
                taken.add(card["artId"])
    for path in card_files():
        _, cards = load(path)
        for card in cards:
            art = card.get("artId")
            if not art or card.get("class") != "NEUTRAL" or art.startswith(PREFIX):
                continue
            plan.append((card, card.get("setId", "CORE"), art, PREFIX + art))

    problems = []
    dupes = [k for k, n in collections.Counter(n for *_, n in plan).items() if n > 1]
    if dupes:
        problems.append(f"two rows would land on the same artId: {dupes}")
    clash = [n for *_, n in plan if n in taken]
    if clash:
        problems.append(f"new artId already used by another row: {clash}")
    for _, set_id, old, _ in plan:
        png = ART_ROOT / set_id / f"{old}.png"
        if not png.exists():
            problems.append(f"no png for {old} (looked in {set_id}/)")
        elif not png.with_suffix(".png.meta").exists():
            problems.append(f"{old}.png has NO .meta - renaming it would mint a new guid")
        if (ART_ROOT / set_id / f"{PREFIX}{old}.png").exists():
            problems.append(f"{PREFIX}{old}.png already on disk")
    return plan, problems


def rename_art(plan, write):
    moved = 0
    for _, set_id, old, new in plan:
        src = ART_ROOT / set_id / f"{old}.png"
        dst = ART_ROOT / set_id / f"{new}.png"
        src_meta = ART_ROOT / set_id / f"{old}.png.meta"
        dst_meta = ART_ROOT / set_id / f"{new}.png.meta"
        print(f"  {set_id}/{old}.png(+.meta) -> {new}.png(+.meta)")
        if write:
            # The pair moves together, in one breath. The .meta is NOT regenerated: its guid is
            # the whole reason this is a rename and not a delete-and-readd.
            src.rename(dst)
            src_meta.rename(dst_meta)
        moved += 1
    return moved


def rewrite_rows(plan, write):
    """Edit the artId VALUES in place, textually. Never re-serialise the file.

    These three files are not authored the same way -- two are one row per line and one is
    pretty-printed -- and a caller who round-trips them through json.dumps imposes ONE of those
    on all three. Doing that turned an 8-line change to Rekindling.cards.json into a 4344-line
    one, on a file being hand-edited at the time. A reformat also buries the real change: a
    reviewer cannot see 38 artIds inside 4000 moved lines, and the Unity authoring window owns
    these files besides.

    So: find the artId string, replace the artId string, leave every other byte alone. Both
    quotings are tried because the compact and pretty files differ in exactly that.
    """
    by_art = {old: new for _, _, old, new in plan}
    ids = {card["id"]: (old, new) for card, _, old, new in plan}
    touched = 0
    for path in card_files():
        _, cards = load(path)
        if not any(c["id"] in ids for c in cards):
            continue
        text = path.read_text(encoding="utf-8")
        n = 0
        for card in cards:
            if card["id"] not in ids:
                continue
            old, new = ids[card["id"]]
            for fmt in ('"artId":"{}"', '"artId": "{}"'):
                needle, repl = fmt.format(old), fmt.format(new)
                if needle in text:
                    text = text.replace(needle, repl)
                    n += 1
                    break
            else:
                raise SystemExit(f"{path.name}: could not find artId {old!r} to edit")
        if write:
            path.write_text(text, encoding="utf-8", newline="")
        print(f"  {path.name}: {n} artId(s)")
        touched += n
    return touched


def rekey_json(path, by_art, write, label):
    if not path.exists():
        print(f"  {label}: absent, skipped")
        return 0
    doc = json.loads(path.read_text(encoding="utf-8"))
    entries = doc.get("cards", doc) if isinstance(doc, dict) else doc
    if not isinstance(entries, dict):
        print(f"  {label}: not a keyed object, skipped")
        return 0
    out, moved = {}, 0
    for key, value in entries.items():
        new = by_art.get(key, key)
        if new in out:
            raise SystemExit(f"{label}: collision on {new}")
        out[new] = value
        moved += new != key
    if write:
        if isinstance(doc, dict) and "cards" in doc:
            doc["cards"] = out
            payload = doc
        else:
            payload = out
        path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"  {label}: {moved} key(s) of {len(entries)}")
    return moved


def move_candidate_dirs(plan, write):
    moved = 0
    for _, set_id, old, new in plan:
        src = CANDIDATES / set_id / old
        if not src.is_dir():
            continue
        dst = CANDIDATES / set_id / new
        if dst.exists():
            print(f"  ! {dst.relative_to(ROOT)} exists, left {old} in place")
            continue
        print(f"  {src.relative_to(ROOT)} -> {new}/")
        if write:
            src.rename(dst)
        moved += 1
    return moved


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    mode = ap.add_mutually_exclusive_group(required=True)
    mode.add_argument("--check", action="store_true")
    mode.add_argument("--write", action="store_true")
    args = ap.parse_args()
    write = args.write

    if write and LOCKFILE.exists():
        print("REFUSING: unity/Emberfall/Temp/UnityLockfile exists, so an Editor is open on this\n"
              "project. It owns the asset database; renaming assets underneath it races the\n"
              "importer and can remint the guids this script exists to preserve. Close it and\n"
              "rerun. Do not delete the lockfile.", file=sys.stderr)
        return 2

    plan, problems = build_plan()
    if problems:
        print("REFUSING - the plan is not safe:", file=sys.stderr)
        for p in problems:
            print(f"  {p}", file=sys.stderr)
        return 2
    if not plan:
        print("Nothing to do: every NEUTRAL row already carries the prefix.")
        return 0

    by_art = {old: new for _, _, old, new in plan}
    print(f"{'WRITE' if write else 'CHECK'}: {len(plan)} NEUTRAL artId(s) gain '{PREFIX}'\n")
    print("1/4  art files (png + .meta together, guid preserved)")
    rename_art(plan, write)
    print("\n2/4  card rows")
    rewrite_rows(plan, write)
    print("\n3/4  prompt manifest")
    rekey_json(MANIFEST, by_art, write, "prompt-manifest.json")
    print("\n4/4  art-pipeline candidate dirs")
    if not move_candidate_dirs(plan, write):
        print("  none on disk")

    print()
    if write:
        print("DONE. Two things this script cannot do for you:\n"
              "  * In Unity, run `Emberfall/Addressables/Setup Groups + Migrate Card Data`.\n"
              "    Group entries carry an ADDRESS (card_art/<fileName>) that this rename\n"
              "    invalidated and only that menu item rebuilds. The game loads by address.\n"
              "  * Re-run audit_card_art_names.py --check-seeds and rebake_card_data.py.\n"
              "    (Seeds no longer move with an artId -- they key on the card id -- but the\n"
              "    audit is still what proves the png, the row and the manifest agree.)")
    else:
        print("Nothing was written. Rerun with --write (Editor closed).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
