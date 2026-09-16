#!/usr/bin/env python3
"""Audit card-art filenames against the class of the row that points at them.

The convention (Sam, 2026-08-06) — expansion at the directory level, class at the
filename level. This docstring is the spec; it left CLAUDE.md on 2026-08-22 to sit next
to the only thing that checks it:

    Assets/Art/Cards/<setId>/<classToken>[_<role>]_<name>.png

NEUTRAL is a classToken like any other -- `neutral_stone_sentry`, `neutral_token_wisp` -- since
Sam's 2026-08-24 ruling. It was previously spelled by ABSENCE (a neutral artId was just the card
name), which made neutral the one class you identify by what is NOT there, and it read as
ambiguous exactly where it mattered: `stone_sentry` is the NEUTRAL card "Stone Sentry", and this
script called it a STONEWARDEN mismatch for as long as the check existed. The row was always
right and the rule was the problem. Within the neutrals the prefixed rows were the outliers;
across the corpus the bare ones were, and the corpus is what a reader holds in their head.
tooling/scripts/migrate_neutral_art_prefix.py did the migration.

`<role>` is `hero_power` or `token`. An artId IS the filename — there is no `art/`
namespace and no `art_` prefix; both were dropped because the namespace had one value
across every row and so distinguished nothing. `ArtIds.ToFileName` stays a
`Replace('/','_')` against a future namespaced id, but nothing today has a separator to
flatten.

Every row carries a class token, so a missing one is as wrong as the wrong one. Class tokens are
abbreviations that do not match their class names, so this derives the map from the data rather
than hardcoding it, and prints what it derived -- except NEUTRAL, which is pinned as a constant
because "the dominant leading segment" cannot bootstrap a token that is being introduced.

Three further checks, each for something no existing gate reports:

* **Wrong set folder.** `AddressablesSetup` builds one `CardArt_<setId>` group per folder on disk,
  so a file under the wrong `<setId>/` ships in the wrong bundle. It still RENDERS — the address is
  a flat `card_art/<fileName>` with no set in it — and CardCi still passes, which is exactly why
  nothing catches it. CI runs this script with --fail-missing-only, which suppresses this
  finding, so it is only ever seen by someone running the script deliberately.
* **Orphan pngs.** A file no card row points at. CardCi only walks rows outward to art, so art with
  no row is invisible to it. This is how a rename leaves its own predecessor behind.
* **Byte-identical duplicates.** Two names for one image, compared by SHA-256 rather than by size.
  An orphan that duplicates a live file is a leftover; an orphan that duplicates nothing may be the
  only copy of something.

* **Stranded seeds** (--check-seeds). A key under tooling/card-art-seeds/ matching no row's
  artId. Seeds are hand-authored art direction, and every consumer of them is a dict lookup that
  MISSES SILENTLY: the card reads as unseeded, generates on placeholder direction, and nothing
  anywhere says so. That is how 18 of them sat stranded from 2026-08-15 to 2026-08-24, when the
  TEMPEST and STONEWARDEN handle renames moved the key they were stored under. The seeds are
  keyed by the numeric card id now, for exactly that reason — see
  gen_card_prompt_briefs.seed_key.

**Never deletes anything.** Orphans are reported, never removed — that call is Sam's.

Exit 1 if any mismatch is found. Orphans and duplicates are reported but do NOT fail the run: they
are bookkeeping, not breakage.

--check-seeds is opt-in but BLOCKING when passed, including alongside --fail-missing-only: unlike
a class-token or set-folder finding it needs no Unity Editor to fix, so there is no reason to
downgrade it to advisory. CI and rebake_card_data.py both pass it.

Pass --fail-missing-only to restrict the exit-1 condition to artIds with no matching file on disk
(useful in CI when class-token mismatches are pre-existing and need Unity-Editor intervention to
fix properly — renaming a PNG without the Editor mints a new guid and orphans the Addressables
entry).
"""
import collections
import hashlib
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
CARD_DIR = ROOT / "unity" / "Emberfall" / "Assets" / "CardData"
ART_ROOT = ROOT / "unity" / "Emberfall" / "Assets" / "Art" / "Cards"

ROLES = ("hero_power", "token")

# NEUTRAL is a class token like any other (Sam, 2026-08-24). It used to be spelled by ABSENCE --
# a neutral artId was just the card name -- which made neutral the one class you identify by what
# is not there, and it read as ambiguous exactly where it mattered: `stone_sentry` is the NEUTRAL
# card "Stone Sentry", and this check called it a STONEWARDEN mismatch for as long as it existed.
# Within the neutrals the prefixed rows were the outliers; across the corpus the bare ones were.
NEUTRAL_TOKEN = "neutral"


def rows():
    for path in sorted(CARD_DIR.glob("*.cards.json")):
        doc = json.loads(path.read_text(encoding="utf-8"))
        cards = doc["cards"] if isinstance(doc, dict) and "cards" in doc else doc
        for card in cards:
            yield path.name, card


def first_segment(art_id):
    """Leading token of an artId, skipping a leading role prefix."""
    rest = art_id
    for role in ROLES:
        if rest.startswith(role + "_"):
            rest = rest[len(role) + 1:]
    return rest.split("_", 1)[0]


def main():
    import argparse
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--fail-missing-only", action="store_true",
                        help="exit 1 only when artIds have no matching file; do not fail on class-token or set-folder issues")
    parser.add_argument("--check-seeds", action="store_true",
                        help="also report seed keys under tooling/card-art-seeds/ that match no "
                             "row's artId. ALWAYS contributes to the exit code when passed, "
                             "including under --fail-missing-only.")
    args = parser.parse_args()

    all_rows = [(f, c) for f, c in rows() if c.get("artId")]

    # Derive classToken from the data: the dominant leading segment per non-NEUTRAL class.
    by_class = collections.defaultdict(collections.Counter)
    for _, card in all_rows:
        cls = card.get("class", "NEUTRAL")
        if cls != "NEUTRAL":
            by_class[cls][first_segment(card["artId"])] += 1

    token_of = {}
    print("derived class tokens (dominant leading segment per class):")
    for cls in sorted(by_class):
        counts = by_class[cls].most_common()
        token, n = counts[0]
        token_of[cls] = token
        total = sum(c for _, c in counts)
        print(f"  {cls:<12} -> {token:<8} ({n}/{total} rows)")
    token_of["NEUTRAL"] = NEUTRAL_TOKEN
    known_tokens = {t: c for c, t in token_of.items()}
    print()

    findings = []
    for file_name, card in all_rows:
        cls = card.get("class", "NEUTRAL")
        set_id = card.get("setId", "CORE")
        art = card["artId"]
        seg = first_segment(art)
        owner = known_tokens.get(seg)          # class this leading token belongs to, if any
        expected = token_of.get(cls)           # NEUTRAL included, pinned above

        if cls == "NEUTRAL" and seg != NEUTRAL_TOKEN:
            findings.append((card["id"], cls, set_id, art,
                             f"NEUTRAL row must lead with '{NEUTRAL_TOKEN}_' (has '{seg}')"))
        elif cls != "NEUTRAL" and owner != cls:
            if owner is None:
                findings.append((card["id"], cls, set_id, art,
                                 f"{cls} row has no class token (expected '{expected}_')"))
            else:
                findings.append((card["id"], cls, set_id, art,
                                 f"{cls} row carries the {owner} token '{seg}' (expected '{expected}_')"))

    # Where the file actually sits vs the setId the row declares.
    paths = {}
    on_disk = {}
    for png in sorted(ART_ROOT.rglob("*.png")):
        # Cosmetic card backs have no card-data row and are not set illustrations.
        if png.relative_to(ART_ROOT).parts[0] == "Backs":
            continue
        on_disk.setdefault(png.stem, []).append(png.parent.name)
        paths.setdefault(png.stem, []).append(png)

    referenced = {c["artId"] for _, c in all_rows}
    misplaced, missing = [], []
    for file_name, card in all_rows:
        art, set_id = card["artId"], card.get("setId", "CORE")
        # A row's art belongs under its own setId, with no exception. The 2026-08-08 rule
        # carved one out for non-hero-power CORE rows, whose art shipped under EMBERFALL;
        # Sam's 2026-08-15 ruling made setId CORE mean HERO_POWER and moved those 24 rows
        # to EMBERFALL in the data, so the carve-out became unreachable and was removed.
        # RETIREMENT TRIGGER: a legitimate non-hero-power CORE row needs it back here, and
        # retires the CORE paragraph in dev-docs/specs/card-schema.md.
        expected_folder = set_id
        folders = on_disk.get(art)
        if not folders:
            missing.append((card["id"], set_id, art))
        elif expected_folder not in folders:
            misplaced.append((card["id"], set_id, art, ", ".join(sorted(folders))))

    orphans = sorted(stem for stem in on_disk if stem not in referenced)

    # SHA-256 every file so an orphan can be told from a leftover copy of a live one.
    digests = collections.defaultdict(list)
    for stem, files in paths.items():
        for f in files:
            digests[hashlib.sha256(f.read_bytes()).hexdigest()].append(stem)

    print(f"card rows with an artId : {len(all_rows)}")
    print(f"png files under Art/Cards: {sum(len(v) for v in on_disk.values())}")
    print(f"class-token mismatches  : {len(findings)}")
    print(f"wrong set folder        : {len(misplaced)}")
    print(f"artId with no png       : {len(missing)}")
    print(f"orphan pngs (no row)    : {len(orphans)}")

    if findings:
        print()
        print("CLASS-TOKEN MISMATCHES")
        print(f"{'card id':<36} {'class':<12} {'set':<11} {'artId':<34} problem")
        print("-" * 130)
        for row in sorted(findings):
            print(f"{row[0]:<36} {row[1]:<12} {row[2]:<11} {row[3]:<34} {row[4]}")

    if misplaced:
        print()
        print("WRONG SET FOLDER (ships in the wrong Addressables bundle; renders fine, CardCi passes)")
        print(f"{'card id':<36} {'row setId':<11} {'artId':<34} found in")
        print("-" * 110)
        for row in sorted(misplaced):
            print(f"{row[0]:<36} {row[1]:<11} {row[2]:<34} {row[3]}")

    if missing:
        print()
        print("ARTID WITH NO PNG — these artIds resolve to no file on disk (BLOCKING)")
        print(f"{'card id':<36} {'set':<11} artId")
        print("-" * 90)
        for row in sorted(missing):
            print(f"{row[0]:<36} {row[1]:<11} {row[2]}")

    if orphans:
        print()
        print("ORPHAN PNGs — no card row points at these. NOT deleted; reported only.")
        print(f"{'file':<38} {'set folder':<12} status")
        print("-" * 96)
        for stem in orphans:
            twins = [s for s in digests[hashlib.sha256(paths[stem][0].read_bytes()).hexdigest()]
                     if s != stem]
            live = [t for t in twins if t in referenced]
            if live:
                status = "byte-identical to LIVE " + ", ".join(sorted(live)) + " — leftover"
            elif twins:
                status = "byte-identical to orphan " + ", ".join(sorted(twins))
            else:
                status = "unique image — the only copy"
            print(f"{stem + '.png':<38} {on_disk[stem][0]:<12} {status}")

    stranded = []
    if args.check_seeds:
        # WHY THIS EXISTS. Seeds are hand-authored art direction, keyed since 2026-08-27 by
        # the row's NUMERIC ID. They were keyed by HANDLE first, and when the TEMPEST and
        # STONEWARDEN handles were renamed (tempest_/storm_, warden_/stone_) 18 seeds stopped
        # matching any row at once. Nothing reported it: the cards simply read as unseeded and
        # generated on placeholder direction. `stone_earthwardens_maul` was one of them and had
        # to be redrawn by hand. Then by artId, which a reclass or a corrected prefix still moves.
        #
        # An id cannot be renamed, so this check no longer guards against a rename -- it guards
        # against a DELETED row, and against a key hand-edited into the file by mistake. Both are
        # the same failure and both are invisible everywhere else: every consumer does a dict
        # lookup that quietly misses.
        seed_dir = ROOT / "tooling" / "card-art-seeds"
        known = {str(card["id"]) for _, card in all_rows}
        for fp in sorted(seed_dir.glob("*.json")):
            for key in json.loads(fp.read_text(encoding="utf-8")):
                if key not in known:
                    stranded.append((key, fp.name))

        print()
        print(f"seed keys matching no row's id    : {len(stranded)}")
        if stranded:
            print()
            print("STRANDED SEEDS — hand-authored direction no consumer can find (BLOCKING)")
            print(f"{'seed key':<40} file")
            print("-" * 72)
            for key, fname in stranded:
                print(f"{key:<40} {fname}")

    if args.fail_missing_only:
        return 1 if (missing or stranded) else 0
    return 1 if (findings or misplaced or missing or stranded) else 0


if __name__ == "__main__":
    sys.exit(main())
