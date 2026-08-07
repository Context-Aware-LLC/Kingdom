"""Canonical art naming migration: expansion at the directory level, class at the filename level.

RULE (Sam, 2026-08-06):
    unity/Emberfall/Assets/Art/Cards/<setId>/<classToken>[_<role>]_<name>.png
    unity/Emberfall/Assets/Art/Cards/<setId>/[<role>_]<name>.png          (NEUTRAL)

    - The DIRECTORY is the expansion (setId). Nothing else keys a directory.
    - The FILENAME leads with the card's CLASS token. NEUTRAL carries no token at all -
      the ABSENCE of one is what means neutral.
    - There is no "art_" prefix. It was the flattened form of an "art/" namespace that had
      exactly one value across all 260 rows, so it distinguished nothing. Unity never needed
      it: ArtIds.ToFileName is a bare Replace('/', '_').
    - <role> is "hero_power" or "token" for the non-collectible rows, so a token still
      reads as a token once its old "token_" prefix is no longer carrying that meaning.
    - <name> is the card's NAME slugified, not its id. Deriving from the name is lossless:
      the old prefixes were sometimes part of the real name ("Stone Sentry" is a NEUTRAL
      card, so stripping "stone" as a class prefix would have destroyed it). When the
      slugified name already begins with the class token it is not repeated, which is why
      Ember Recruit lands at ember_recruit rather than ember_ember_recruit.

Class tokens are abbreviations, not class names: STONEWARDEN is "stone" (it was "ward",
which collided with EMBERWARD's "ember" in the worst possible way — a prefix match on
"ward" selected the wrong class) and TEMPEST is "storm".

WHAT THIS TOUCHES, all of which must move together:
    1. artId in unity/Emberfall/Assets/CardData/*.cards.json
    2. the art_*.png and its art_*.png.meta, renamed as a PAIR - the .meta carries the guid
       the Addressables entry points at, so the pair moves or the entry orphans
    3. tests/.../GoldenReplay/cardsets/M1.cards.snapshot.json, which carries an immutable copy

NOT hash-canonical. ArtId appears zero times in GameStateHasher and GameStateSerializer, so
golden replays and the pinned digest are unaffected by this rename.

STILL REQUIRES UNITY AFTERWARDS. Addressable addresses are path-derived
("card_art/art_grave_sable_watcher"), so the entries must be regenerated in the Editor via
AddressablesArtCatalog once this has run. Until that happens the art resolves in the Editor
and is BLANK in a player build.

Usage:
  python tooling/scripts/migrate_art_naming.py              # dry run, prints the full plan
  python tooling/scripts/migrate_art_naming.py --apply      # perform it
"""

import argparse
import collections
import json
import pathlib
import re
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
CARD_DATA = REPO / "unity" / "Emberfall" / "Assets" / "CardData"
CARD_ART = REPO / "unity" / "Emberfall" / "Assets" / "Art" / "Cards"
SNAPSHOT = (REPO / "tests" / "Emberfall.Core.Tests" / "GoldenReplay" / "cardsets"
            / "M1.cards.snapshot.json")

CLASS_TOKEN = {
    "EMBERWARD": "ember",
    "TIDECALLER": "tide",
    "GRAVEKEEP": "grave",
    "VERDANT": "verdant",
    "CLOCKWRIGHT": "clock",
    "NIGHTVEIL": "night",
    "STONEWARDEN": "stone",
    "TEMPEST": "storm",
    "NEUTRAL": "neutral",
}


def slug(value: str) -> str:
    """Apostrophes are DROPPED, not treated as separators - "Sorrow's Blessing" is
    sorrows_blessing, never sorrow_s_blessing. Everything else non-alphanumeric separates."""
    value = value.lower().replace("'", "").replace("’", "")
    return re.sub(r"_+", "_", re.sub(r"[^a-z0-9]+", "_", value)).strip("_")


def role_of(card: dict) -> str:
    """From the DATA, not from the old artId. The old string was unreliable: one token was
    authored as 'rekind_token_risen_guardian', so a leading-'token_' test missed it."""
    if card["id"].startswith("hero_power_"):
        return "hero_power"
    if card.get("collectible") is False:
        return "token"
    return ""


def new_art_id(card: dict) -> str:
    """NO "art/" NAMESPACE, AND NEUTRAL CARDS CARRY NO TOKEN (Sam, 2026-08-06).

    The "art/" segment was in all 260 artIds and never had a second value, so it encoded
    nothing — it existed only to be flattened back into the "art_" filename prefix. Unity never
    required it: ArtIds.ToFileName is a bare Replace('/', '_'), and AddressablesArtCatalog's
    StartsWith("art_") branch already falls through to using the filename as the artId.

    Absence of a class token now MEANS neutral, so neutral cards are just their name. The one
    place that reads ambiguously is EMBERFALL/stone_sentry.png — the NEUTRAL card Stone Sentry,
    which scans as a STONEWARDEN ("stone") card. Known and accepted; it is one card, not the
    systemic prefix collision this scheme replaced.
    """
    cls = (card.get("class") or "NEUTRAL").upper()
    base = slug(card["name"])
    if cls == "NEUTRAL":
        return "_".join(p for p in (role_of(card), base) if p)
    token = CLASS_TOKEN[cls]
    if base == token:
        base = ""
    elif base.startswith(token + "_"):
        base = base[len(token) + 1:]
    return "_".join(p for p in (token, role_of(card), base) if p)


def art_file(art_id: str) -> str:
    return art_id.replace("/", "_")


def load_rows():
    rows = []
    for fp in sorted(CARD_DATA.glob("*.cards.json")):
        rows.append((fp, json.loads(fp.read_text(encoding="utf-8"))))
    return rows


def build_plan(rows):
    plan, seen = [], collections.defaultdict(list)
    for fp, cards in rows:
        for c in cards:
            old, new = c["artId"], new_art_id(c)
            seen[new].append(c["id"])
            plan.append({
                "src": fp.name, "id": c["id"], "name": c["name"],
                "setId": c.get("setId", "EMBERFALL"),
                "class": (c.get("class") or "NEUTRAL").upper(),
                "old": old, "new": new, "changed": old != new,
            })
    collisions = {k: v for k, v in seen.items() if len(v) > 1}
    return plan, collisions


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--apply", action="store_true",
                    help="perform the migration (default is a dry run)")
    args = ap.parse_args()

    rows = load_rows()
    plan, collisions = build_plan(rows)

    if collisions:
        print("REFUSING: the new naming collides. Two cards cannot share one art file.",
              file=sys.stderr)
        for new, ids in sorted(collisions.items()):
            print(f"  {new}  <-  {', '.join(ids)}", file=sys.stderr)
        return 2

    changed = [p for p in plan if p["changed"]]
    print(f"cards: {len(plan)}   renaming: {len(changed)}   already canonical: "
          f"{len(plan) - len(changed)}   collisions: 0")

    # Pair up the files on disk before touching anything.
    moves, missing, blocked = [], [], []
    for p in changed:
        d = CARD_ART / p["setId"]
        src_png, dst_png = d / f"{art_file(p['old'])}.png", d / f"{art_file(p['new'])}.png"
        if not src_png.exists():
            missing.append(p)
            continue
        if dst_png.exists():
            blocked.append((src_png, dst_png))
            continue
        moves.append((src_png, dst_png))

    print(f"\nfiles: {len(moves)} png+meta pairs to rename, {len(missing)} card rows with "
          f"no art on disk, {len(blocked)} blocked by an existing destination")
    if blocked:
        print("REFUSING: destination already exists (would overwrite art).", file=sys.stderr)
        for s, d in blocked:
            print(f"  {s.name} -> {d.name}", file=sys.stderr)
        return 2

    by_class = collections.Counter(p["class"] for p in changed)
    print("\nrenames by class:")
    for cls, n in sorted(by_class.items()):
        tok = "(none - absence means neutral)" if cls == "NEUTRAL" else CLASS_TOKEN[cls]
        print(f"   {cls:12s} {n:3d}   token={tok}")

    print("\nsample (first 3 per class):")
    shown = collections.Counter()
    for p in changed:
        if shown[p["class"]] >= 3:
            continue
        shown[p["class"]] += 1
        print(f"   {p['old']:38s} -> {p['new']:40s} [{p['name']}]")

    if missing:
        print(f"\n{len(missing)} card row(s) have no art file on disk (renamed in data only):")
        for p in missing[:15]:
            print(f"   {p['setId']}/{art_file(p['old'])}.png  [{p['name']}]")

    if not args.apply:
        print("\nDRY RUN - nothing written. Re-run with --apply.")
        return 0

    # 1. rename png + meta as a pair
    renamed = 0
    for src_png, dst_png in moves:
        src_meta = src_png.with_name(src_png.name + ".meta")
        dst_meta = dst_png.with_name(dst_png.name + ".meta")
        src_png.rename(dst_png)
        if src_meta.exists():
            src_meta.rename(dst_meta)
        renamed += 1

    # 2. rewrite artId in the card data
    remap = {p["id"]: p["new"] for p in plan if p["changed"]}
    for fp, cards in rows:
        touched = False
        for c in cards:
            if c["id"] in remap and c["artId"] != remap[c["id"]]:
                c["artId"] = remap[c["id"]]
                touched = True
        if touched:
            fp.write_text(json.dumps(cards, indent=2, ensure_ascii=False) + "\n",
                          encoding="utf-8")

    # 3. the immutable snapshot the behaviour tests load
    snap_n = 0
    if SNAPSHOT.exists():
        snap = json.loads(SNAPSHOT.read_text(encoding="utf-8"))
        for c in snap:
            if c.get("id") in remap and c.get("artId") != remap[c["id"]]:
                c["artId"] = remap[c["id"]]
                snap_n += 1
        SNAPSHOT.write_text(json.dumps(snap, indent=2, ensure_ascii=False) + "\n",
                            encoding="utf-8")

    print(f"\nAPPLIED: {renamed} png+meta pairs renamed, {len(remap)} artId values rewritten "
          f"in card data, {snap_n} rewritten in {SNAPSHOT.name}")
    print("NEXT: regenerate Addressables in the Unity Editor (AddressablesArtCatalog). "
          "Addresses are path-derived, so until that runs the art is BLANK in a build.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
