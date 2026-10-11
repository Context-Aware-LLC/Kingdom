"""Generate card illustrations from the reviewed seed corpus.

SINGLE PROVIDER: gpt-image-2. The two-provider gemini/openai split, its fallback policy
and its per-provider style blocks were removed 2026-09-05 (Sam) — they described a process
nobody runs any more. Anything you find elsewhere that still mentions a provider choice for
card art is stale.

ONE PROMPT COMPOSER IN THIS REPO, and it is not this file. `gen_card_prompt_briefs.compose_brief`
builds the prompt: delivery, composition, subject/background opposition, world tone, style,
the scale clause and the negative prompt, from the spec doc and the descriptor files. This
script used to carry a second, subtly different copy of all of that — a copy whose own docstring
admitted it was "intentionally NOT a verbatim mirror" — plus a third composition path reading
`docs/design/card-drafts-*.md`, a directory that stopped existing at the 2026-07-31 docs
reorganisation. Both are gone. If the prompt is wrong, fix it in `gen_card_prompt_briefs.py`
or in the docs it parses; there is nowhere else it can be wrong.

WHERE THE DIRECTION COMES FROM. `tooling/card-art-seeds/<setId>.json`, keyed by the row's
NUMERIC card id, four hand-authored fields per card. A card with no seed is SKIPPED rather
than generated from a placeholder — an unseeded batch comes back weakly directed and samey,
and the seed is the quality constraint, not the model.

FLAT LAYOUT (Sam, 2026-09-05). Candidates and their prompts sit side by side in one directory
per expansion, named after the card, which is the same name the game ships the file under:

    art-pipeline/card-art/<setId>/<artId>.png       the first candidate
    art-pipeline/card-art/<setId>/<artId>.md        the prompt that made it
    art-pipeline/card-art/<setId>/<artId>__cand2.png    a re-roll, beside it, never over it

So promoting art is a copy into unity/Assets/Art/Cards/<setId>/ and never a rename.

NEVER OVERWRITE A GENERATED IMAGE (Sam, 2026-07-26): "don't delete anything you generate — if
something needs deleting, I'll delete it myself." Numbering always continues past what is on
disk. `--to-unity` is the one path that writes a fixed destination, so it copies the outgoing
image aside first — see `preserve_promoted`.

Keys come from the environment only: OPENAI_API_KEY. --dry-run needs none.

Usage:
  gen_card_art.py [options]
    --dry-run                  print the composed prompt per card, generate nothing
    --card <id>                one card (repeatable); accepts artId, handle or numeric id
    --class <name>             one class only (gravekeep, verdant, neutral, ...)
    --set <setId>              one expansion only
    --only-missing-art         restrict to cards with no promoted png
    --candidates N             default 1, clamped to 1-4
    --skip-existing            default ON; --no-skip-existing to add N more
    --sheet-only               rebuild the contact sheets from what is on disk
    --to-unity                 write straight to Assets/, skipping candidates
"""

import argparse
import base64
import json
import os
import pathlib
import sys
import time

import requests

# The prompts are full of em-dashes and section marks, and --dry-run prints all
# of them. A Windows console on cp1252 raises UnicodeEncodeError on anything
# outside it (U+2248 killed the summary line once), which would abort a run
# mid-batch over a printout. Degrade the DISPLAY instead of dying; every file
# this script writes is explicitly utf-8 and unaffected.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(errors="replace")
    except (AttributeError, ValueError):
        pass

REPO = pathlib.Path(__file__).resolve().parents[2]
OUT = REPO / "art-pipeline" / "card-art"
CARD_DATA = REPO / "unity" / "Assets" / "CardData"
SEEDS_DIR = REPO / "tooling" / "card-art-seeds"
UNITY_CARDS = REPO / "unity" / "Assets" / "Art" / "Cards"
MANIFEST = OUT / "prompt-manifest.json"

EDGE = 1024  # dev-docs/pipelines/art-and-rendering.md:9

# gpt-image-2 bills per token ($8/M image input, $30/M image output, $5/M text input), which
# lands at roughly $0.005/image at low quality and $0.165-$0.21 at high. This script always
# asks for `quality: "high"` (see `_post`), so the constant is the top of that band —
# deliberately a slight over-estimate so the spend guard never flatters a run. The wall-clock
# figure is Sam's measured one.
MODEL = "gpt-image-2"
ENV_KEY = "OPENAI_API_KEY"
USD_PER_IMAGE = 0.19
SECS_PER_IMAGE = 300

# hist-docs/source-snapshots/2026-07-31-pre-reorganization/design/art-brief-m1.md §7.1 —
# identity assets first, filler last.
RARITY_ORDER = {"LEGENDARY": 0, "EPIC": 1, "RARE": 2, "COMMON": 3}

# Appended after the composed brief. gpt-image-2 is being paid for depth, so this releases the
# silhouette-only reading that a seed's own words can otherwise impose. It is a RELEASE, not a
# new rule: everything it loosens is a style preference. The hard delivery constraints the frame
# mask and board scale impose — square crop, focal point centred, bleed to edge, no text or UI,
# readable at 220 px — come from compose_brief and are not negotiable by paying more.
PAINT_DETAIL = """PAINT IT FULLY. Use rich saturated color, layered rendering, atmosphere, texture,
and a background that supports the subject. Where the class or subject text above
says "no background scene", "no background detail", "stark black background",
or "minimal detail", read that as a floor on CONTRAST, not a ban on painting:
give the subject a supporting environment that stays subordinate to it in value,
detail and saturation, and never competes for the eye.

The 220px read is still the bar, and it is met by value contrast and a clean
overall shape — not by flattening the subject into a silhouette."""


# ------------------------------------------------------------------ sources

def _briefs_module():
    """gen_card_prompt_briefs, imported from this directory regardless of cwd."""
    sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
    import gen_card_prompt_briefs as briefs

    return briefs


def load_seed_cards() -> list[dict]:
    """Every card row that has a seed, with its prompt already composed.

    The prompt is built by gen_card_prompt_briefs.compose_brief — the same function the CLI
    brief generator and the browser editor use, so all three ask for the identical thing.
    """
    briefs = _briefs_module()

    rows: list[dict] = []
    for fp in sorted(CARD_DATA.glob("*.cards.json")):
        rows.extend(json.loads(fp.read_text(encoding="utf-8")))

    seeds: dict[str, dict] = {}
    for fp in sorted(SEEDS_DIR.glob("*.json")):
        seeds.update(json.loads(fp.read_text(encoding="utf-8")))
    if not seeds:
        raise ValueError(f"no seeds found under {SEEDS_DIR}")

    class_descriptors = briefs.load_class_descriptors()
    world_tone: dict[str, str] = {}

    cards: list[dict] = []
    for row in rows:
        # Seeds are keyed by the NUMERIC CARD ID -- the only identifier a rename cannot
        # move. See gen_card_prompt_briefs.seed_key for the two renames that proved it.
        seed = seeds.get(briefs.seed_key(row))
        if not seed or not row.get("artId"):
            continue
        set_id = row.get("setId", "PENDINGHAM")
        if set_id not in world_tone:
            world_tone[set_id] = briefs.load_world_tone(set_id)

        prompt = briefs.compose_brief(row, class_descriptors, world_tone[set_id], seed)
        cards.append(
            {
                # Keyed by artId: the manifest, --card and the results map are all about the
                # IMAGE, while the seed joins on the card id.
                "id": row["artId"],
                "handle": row.get("handle", ""),
                "cardId": row.get("id", ""),
                "name": row["name"],
                "artId": row["artId"],
                "file": briefs.art_file(row),
                "setId": set_id,
                "rarity": row.get("rarity", "COMMON"),
                "cost": row.get("cost", 0),
                "class": row.get("class", "NEUTRAL").lower(),
                "prompt": prompt + "\n\n" + PAINT_DETAIL,
            }
        )

    cards.sort(key=lambda c: (RARITY_ORDER.get(c["rarity"], 9), -c["cost"], c["id"]))
    return cards


# -------------------------------------------------------------------- paths

def set_dir(card: dict) -> pathlib.Path:
    """One directory per expansion. There is no per-card leaf directory — see the
    module docstring, and gen_card_prompt_briefs.delivery_block for why it went."""
    return OUT / card["setId"]


def final_path(card: dict) -> pathlib.Path:
    return UNITY_CARDS / card["setId"] / f"{card['file']}.png"


def prompt_path(card: dict) -> pathlib.Path:
    return set_dir(card) / f"{card['file']}.md"


def has_promoted_art(card: dict) -> bool:
    return final_path(card).exists()


def existing(card: dict) -> list[pathlib.Path]:
    """Candidates on disk, in generation order. The first one holds the bare canonical
    name so it needs no rename to be promoted; re-rolls land beside it, never over it."""
    stem = card["file"]
    out = []
    bare = set_dir(card) / f"{stem}.png"
    if bare.exists():
        out.append(bare)
    out.extend(sorted(set_dir(card).glob(f"{stem}__cand*.png")))
    return out


def candidate_path(card: dict, index: int) -> pathlib.Path:
    """index is 1-based. Candidate 1 takes the canonical name."""
    stem = card["file"]
    return set_dir(card) / (f"{stem}.png" if index == 1 else f"{stem}__cand{index}.png")


# --------------------------------------------------------------- generation

def _to_png(raw: bytes) -> tuple[bytes | None, str]:
    """JPEG (or anything) in, PNG bytes out. Pass a PNG through untouched."""
    if raw[:8] == b"\x89PNG\r\n\x1a\n":
        return raw, ""
    try:
        import io

        from PIL import Image
    except ImportError:
        return None, "provider returned JPEG and Pillow is not installed to transcode"
    buf = io.BytesIO()
    with Image.open(io.BytesIO(raw)) as im:
        im.convert("RGB").save(buf, format="PNG")
    return buf.getvalue(), ""


def _post(prompt: str, key: str) -> tuple[bytes | None, str]:
    """Returns (png_bytes, error_message)."""
    r = requests.post(
        "https://api.openai.com/v1/images/generations",
        headers={"Authorization": f"Bearer {key}"},
        json={
            "model": MODEL,
            "prompt": prompt,
            "size": f"{EDGE}x{EDGE}",
            "quality": "high",
            "n": 1,
        },
        timeout=600,
    )
    if r.status_code != 200:
        return None, f"HTTP {r.status_code}: {r.text[:400]}"
    data = r.json()["data"][0]
    if "b64_json" in data:
        return _to_png(base64.b64decode(data["b64_json"]))
    return _to_png(requests.get(data["url"], timeout=120).content)


def _conform(dest: pathlib.Path, tag: str) -> str:
    """Measure what actually landed. A square that is merely the wrong size is resampled;
    anything non-square is left alone and reported loudly rather than silently distorted."""
    try:
        from PIL import Image
    except ImportError:
        return "unmeasured (no PIL)"
    with Image.open(dest) as im:
        w, h = im.size
        if (w, h) == (EDGE, EDGE):
            return f"{w}x{h}"
        if w == h:
            im.convert("RGB").resize((EDGE, EDGE), Image.LANCZOS).save(dest)
            print(f"[{tag}] NOTE: provider returned {w}x{h}, resampled to {EDGE}x{EDGE}",
                  flush=True)
            return f"{w}x{h} -> {EDGE}x{EDGE}"
    print(f"[{tag}] WARNING: NOT SQUARE ({w}x{h}) — dev-docs/pipelines/art-and-rendering.md:9 "
          f"wants a {EDGE} square. Left as-is; fix before promoting.", flush=True)
    return f"{w}x{h} NOT SQUARE"


def preserve_promoted(card: dict) -> pathlib.Path | None:
    """Copy the CURRENT promoted image aside before anything overwrites it.

    In candidates mode nothing can destroy an image: numbering always continues past what
    is on disk, so a re-roll lands BESIDE its predecessor. Writing straight into Assets/ has
    no such property — the destination filename is fixed by the card, so a second run
    overwrites the first. The outgoing image is copied into the expansion's pipeline folder
    as <artId>__replaced_<utc>.png first. Deleting it is Sam's call, not the script's.

    Overwrite-in-place is still what happens to the .png itself — the .meta beside it carries
    the guid the Addressables entry points at, and replacing the pair mints a new guid and
    silently orphans that entry.
    """
    dest = final_path(card)
    if not dest.exists():
        return None
    keep_dir = set_dir(card)
    keep_dir.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y%m%dT%H%M%S", time.gmtime())
    keep = keep_dir / f"{card['file']}__replaced_{stamp}.png"
    keep.write_bytes(dest.read_bytes())
    return keep


def check_meta(card: dict) -> str | None:
    """A .png in Assets/ with no .meta beside it is INVISIBLE in a player build, and
    nothing in Unity or CI complains. Generating the .meta is the Editor's job; committing
    it is a separate and easy-to-forget step. Returned so the run summary can say it once,
    loudly, rather than burying it per-card."""
    meta = final_path(card).with_name(final_path(card).name + ".meta")
    return None if meta.exists() else str(meta.relative_to(REPO))


def generate(card: dict, key: str, candidates: int,
             skip: bool, to_unity: bool = False) -> tuple[list[dict], str | None]:
    """Returns (one manifest record per image actually written, skip reason or None)."""
    if to_unity:
        # Straight into Assets/: the destination is a single fixed filename, so "enough
        # candidates on disk" is meaningless. The only question is whether the card is
        # already promoted — and answering "yes, skip" is exactly how a batch comes back
        # looking like it regenerated art it merely left alone.
        if skip and final_path(card).exists():
            reason = "already promoted"
            print(f"[{card['file']}] skip - {reason} (NOT generated this run)", flush=True)
            return [], reason
    else:
        have = existing(card)
        if skip and len(have) >= candidates:
            reason = f"{len(have)} candidate(s) on disk"
            print(f"[{card['file']}] skip ({reason})", flush=True)
            return [], reason

    have = existing(card)
    prompt = card["prompt"]
    set_dir(card).mkdir(parents=True, exist_ok=True)

    base = len(have)
    n_new = (candidates - base) if skip else candidates
    # One fixed destination filename means a second image this run would overwrite the
    # first. Candidates are a candidates-mode concept; direct-to-Unity is always one.
    if to_unity:
        base, n_new = 0, 1

    # The prompt sits beside the picture it produced, under the same stem. Written before
    # the first request, so a run that fails still leaves the direction it asked for.
    if not to_unity:
        prompt_path(card).write_text(prompt, encoding="utf-8")

    records: list[dict] = []
    for k in range(n_new):
        i = base + k + 1
        tag = f"{card['file']} c{i}"
        print(f"[{tag}] requesting {EDGE}x{EDGE} from {MODEL} ...", flush=True)
        t0 = time.time()
        try:
            raw, err = _post(prompt, key)
        except requests.RequestException as e:
            print(f"[{tag}] FAILED (network): {e}", flush=True)
            continue
        if raw is None:
            print(f"[{tag}] FAILED {err}", flush=True)
            continue

        if to_unity:
            kept = preserve_promoted(card)
            dest = final_path(card)
            dest.parent.mkdir(parents=True, exist_ok=True)
            if kept is not None:
                print(f"[{tag}] previous image preserved -> {kept.relative_to(REPO)}",
                      flush=True)
        else:
            dest = candidate_path(card, i)
        dest.write_bytes(raw)
        dims = _conform(dest, tag)
        took = time.time() - t0
        shown = dest.relative_to(REPO) if to_unity else dest.relative_to(OUT)
        print(f"[{tag}] -> {shown}  ({len(raw)/1e6:.1f} MB, {dims}, {took:.0f}s)", flush=True)
        records.append(
            {
                "file": dest.name,
                "model": MODEL,
                "requested": f"{EDGE}x{EDGE}",
                "delivered": dims,
                "seconds": round(took, 1),
                "generatedAt": time.strftime("%Y-%m-%dT%H:%M:%S"),
                "prompt": prompt,
            }
        )
    # (records, skip reason) — the shape the docstring promises and the shape main() unpacks.
    # This used to `return records`, a bare list, which main() then unpacked as a 2-tuple. That
    # only survives when the list happens to hold exactly two items, so a 2-candidate pass
    # worked by coincidence while --to-unity (always 1 image) and any card whose requests all
    # failed (0 images) raised ValueError instead. It raised AFTER the images were written, so
    # the run lost its manifest — the record of which prompt produced which file.
    return records, None


def write_manifest(cards: list[dict], new_records: dict[str, list[dict]]) -> None:
    """Per IMAGE: model + the exact prompt used, so any card can be re-rolled reproducibly."""
    doc = {"cards": {}}
    if MANIFEST.exists():
        doc = json.loads(MANIFEST.read_text(encoding="utf-8"))
    entries = doc.get("cards", {})

    for card in cards:
        e = entries.setdefault(card["id"], {})
        e.update(
            {
                "name": card["name"],
                "artId": card["artId"],
                "class": card["class"],
                "rarity": card["rarity"],
                "candidateDir": str(set_dir(card).relative_to(REPO)).replace("\\", "/"),
                "promotesTo": str(final_path(card).relative_to(REPO)).replace("\\", "/"),
            }
        )
        gens = {g["file"]: g for g in e.get("generations", [])}
        for rec in new_records.get(card["id"], []):
            gens[rec["file"]] = rec
        e["generations"] = [gens[k] for k in sorted(gens)]

    MANIFEST.parent.mkdir(parents=True, exist_ok=True)
    MANIFEST.write_text(
        json.dumps({"schema": 1, "cards": entries}, indent=2), encoding="utf-8"
    )
    total = sum(len(e.get("generations", [])) for e in entries.values())
    print(f"manifest -> {MANIFEST.relative_to(REPO)} "
          f"({len(entries)} card(s), {total} image(s))")


# ----------------------------------------------------------- contact sheet

def contact_sheet(cards: list[dict], cls: str) -> None:
    """One sheet per (setId, class): every candidate side by side, plus a 220px tile —
    the size the spec says the art has to survive. Judged small or not judged."""
    from PIL import Image, ImageDraw, ImageFont

    BG, INK, DIM, PAD, TILE, SMALL = (26, 24, 22), (208, 200, 188), (140, 132, 122), 24, 300, 220

    def font(size: int):
        for name in ("seguisb.ttf", "segoeui.ttf", "arial.ttf"):
            try:
                return ImageFont.truetype(name, size)
            except OSError:
                continue
        return ImageFont.load_default()

    rows = []
    for card in cards:
        paths = existing(card)
        if paths:
            rows.append((card, paths))
    if not rows:
        print(f"{cls}: no candidates on disk, no sheet", file=sys.stderr)
        return

    cols = max(len(p) for _, p in rows)
    w = PAD + cols * (TILE + PAD) + SMALL + PAD
    row_h = PAD + 22 + TILE + 20
    sheet = Image.new("RGB", (w, PAD + 30 + len(rows) * row_h), BG)
    d = ImageDraw.Draw(sheet)
    d.text((PAD, 10), f"CARD ART CANDIDATES — {cls.upper()}", fill=INK, font=font(22))

    y = PAD + 30
    for card, paths in rows:
        d.text((PAD, y), f"{card['file']}  ({card['rarity']}, {card['cost']} mana)",
               fill=INK, font=font(16))
        top = y + 22
        first = None
        for i, p in enumerate(paths):
            im = Image.open(p).convert("RGB")
            first = first or im
            x = PAD + i * (TILE + PAD)
            sheet.paste(im.resize((TILE, TILE), Image.LANCZOS), (x, top))
            label = p.stem.split("__", 1)[1] if "__" in p.stem else "cand1"
            d.text((x, top + TILE + 4), label, fill=DIM, font=font(13))
        x = PAD + cols * (TILE + PAD)
        sheet.paste(first.resize((SMALL, SMALL), Image.LANCZOS), (x, top))
        d.text((x, top + SMALL + 4), f"@ {SMALL}px (board read)", fill=DIM, font=font(13))
        y += row_h

    # Sheet sits alongside the candidates it summarises. One sheet per (setId, class): a class
    # spans several expansions, and keying off rows[0] put the whole sheet in whichever set
    # happened to sort first.
    dest = OUT / rows[0][0]["setId"] / f"SHEET_{cls}.png"
    dest.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(dest)
    print(f"{cls}: {len(rows)} card(s) -> {dest.relative_to(REPO)}  ({sheet.width}x{sheet.height})")


# ------------------------------------------------------------ spend guard

def estimate(images: int) -> str:
    mins = images * SECS_PER_IMAGE / 60
    return (f"{images} image(s) on {MODEL} ~{mins:.0f} min wall-clock, "
            f"~${images * USD_PER_IMAGE:.2f} (~${USD_PER_IMAGE:.2f}/image, high quality)")


def require_explicit_selection(cards: list[dict], candidates: int, selected: bool) -> bool:
    """Generation is opt-in per card, class or set. At ~$0.19 and ~5 minutes an image, a
    fat-fingered whole-corpus run should be impossible, not merely unlikely."""
    if selected:
        return True
    print("REFUSED: generating needs an explicit --card, --class or --set.\n"
          f"  You asked for the whole corpus: {estimate(len(cards) * candidates)}.\n"
          "  Name what you want:\n"
          "    gen_card_art.py --card grave_hushwight\n"
          "    gen_card_art.py --set REKINDLING --only-missing-art",
          file=sys.stderr)
    return False


# ------------------------------------------------------------------- main

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--dry-run", action="store_true",
                    help="print the fully composed prompt per card, generate nothing")
    ap.add_argument("--card", action="append", metavar="ID", help="single card id (repeatable)")
    ap.add_argument("--class", dest="cls", metavar="NAME",
                    help="one class only (gravekeep, verdant, neutral, ...)")
    ap.add_argument("--set", dest="set_id", metavar="SETID", help="one expansion only")
    ap.add_argument("--only-missing-art", dest="only_missing_art", action="store_true",
                    help="restrict to cards with no promoted png under Assets/Art/Cards/<setId>/")
    ap.add_argument("--candidates", type=int, default=1, metavar="N",
                    help="candidates per card (default 1, clamp 1-4)")
    ap.add_argument("--skip-existing", dest="skip", action="store_true", default=True,
                    help="skip cards that already have enough candidates (default)")
    ap.add_argument("--no-skip-existing", dest="skip", action="store_false",
                    help="add N more candidates even if some exist")
    ap.add_argument("--sheet-only", action="store_true",
                    help="rebuild contact sheets from what is already on disk")
    ap.add_argument("--to-unity", dest="to_unity", action="store_true",
                    help="write straight to Assets/Art/Cards/<setId>/ instead of candidates; "
                         "always 1 image per card, and the outgoing image is preserved first")
    args = ap.parse_args()

    candidates = args.candidates

    try:
        cards = load_seed_cards()
    except (OSError, ValueError, KeyError, json.JSONDecodeError) as e:
        print(f"card load failed: {e}", file=sys.stderr)
        return 2

    if args.only_missing_art:
        before = len(cards)
        cards = [c for c in cards if not has_promoted_art(c)]
        print(f"--only-missing-art: {before} seeded card(s), "
              f"{len(cards)} with no promoted png, {before - len(cards)} already have art")

    selected = bool(args.cls or args.card or args.set_id)
    if args.set_id:
        known = sorted({c["setId"] for c in cards})
        if args.set_id not in known:
            print(f"unknown set {args.set_id!r}; seeded sets: {', '.join(known)}",
                  file=sys.stderr)
            return 2
        cards = [c for c in cards if c["setId"] == args.set_id]
    if args.cls:
        known = sorted({c["class"] for c in cards})
        if args.cls not in known:
            print(f"unknown class {args.cls!r}; this selection has: {', '.join(known)}",
                  file=sys.stderr)
            return 2
        cards = [c for c in cards if c["class"] == args.cls]
    if args.card:
        # Accept whatever the caller has to hand. Cards are keyed by artId, but a human typing
        # --card knows the card by its handle, and a caller reading a row has a number.
        alias = {}
        for c in cards:
            alias[c["id"]] = c["id"]
            for extra in (c.get("handle"), str(c.get("cardId", ""))):
                if extra:
                    alias.setdefault(extra, c["id"])
        wanted = {alias[w] for w in args.card if w in alias}
        unknown = [w for w in args.card if w not in alias]
        if unknown:
            print(f"unknown card id(s): {', '.join(sorted(unknown))} "
                  f"(accepts artId, handle or numeric id)", file=sys.stderr)
            return 2
        cards = [c for c in cards if c["id"] in wanted]
    if not cards:
        print("no cards selected", file=sys.stderr)
        return 2

    if args.dry_run:
        for card in cards:
            print("=" * 78)
            print(f"{card['id']}  [{card['class']} / {card['rarity']} / {card['cost']} mana]")
            n = 1 if args.to_unity else candidates
            print(f"  model    {MODEL} x{n} "
                  f"{'image (direct)' if args.to_unity else 'candidate(s)'}")
            print(f"  file     {card['file']}.png  ({EDGE}x{EDGE})")
            if args.to_unity:
                print(f"  writes   {final_path(card).relative_to(REPO)}"
                      f"{'  (OVERWRITES; previous preserved)' if final_path(card).exists() else ''}")
            else:
                print(f"  cands    {candidate_path(card, 1).relative_to(REPO)}")
                print(f"  prompt   {prompt_path(card).relative_to(REPO)}")
                print(f"  final    {final_path(card).relative_to(REPO)}")
            print("-" * 78)
            print(card["prompt"])
            print()
        print("=" * 78)
        print(f"{len(cards)} prompt(s) composed, in generation order "
              f"(legendary -> epic -> rare -> common). Nothing generated.")
        print(estimate(len(cards) * candidates))
        if not selected:
            print("NOTE: a real run would be REFUSED — needs --card, --class or --set.")
        return 0

    if args.sheet_only:
        for set_id, cls in sorted({(c["setId"], c["class"]) for c in cards}):
            contact_sheet([c for c in cards
                           if c["class"] == cls and c["setId"] == set_id], cls)
        return 0

    if not 1 <= candidates <= 4:
        print("--candidates must be 1-4", file=sys.stderr)
        return 2

    if not require_explicit_selection(cards, candidates, selected):
        return 2

    key = os.environ.get(ENV_KEY)
    if not key:
        print(f"{ENV_KEY} is not set — required to generate ({MODEL}).\n"
              f"  Set it in the environment, e.g. PowerShell:\n"
              f"    [Environment]::SetEnvironmentVariable('{ENV_KEY}', '<key>', 'User')\n"
              f"  then start a NEW shell. --dry-run needs no key at all.",
              file=sys.stderr)
        return 2

    print(estimate(len(cards) * (1 if args.to_unity else candidates)))
    OUT.mkdir(parents=True, exist_ok=True)
    results = {c["id"]: generate(c, key, candidates, args.skip, args.to_unity)
               for c in cards}
    new_records = {cid: recs for cid, (recs, _) in results.items()}
    skipped = {cid: why for cid, (_, why) in results.items() if why}
    write_manifest(cards, new_records)
    if not args.to_unity:
        for set_id, cls in sorted({(c["setId"], c["class"]) for c in cards}):
            contact_sheet([c for c in cards
                           if c["class"] == cls and c["setId"] == set_id], cls)

    made = sum(len(v) for v in new_records.values())
    by_id = {c["id"]: c for c in cards}

    # DELIVERED IS NOT GENERATED. A skipped card still has art on disk, so a batch whose
    # output is read as "here is the art" reports images that this run never touched —
    # which reads downstream as the generator emitting duplicates of an earlier run.
    # Say the split out loud, every run, and name the skipped cards.
    print(f"\n{len(cards)} card(s): {made} image(s) generated, {len(skipped)} skipped")
    if skipped:
        print("SKIPPED - art already on disk, NOT generated this run "
              "(pass --no-skip-existing to add more):")
        for cid in sorted(skipped):
            print(f"    {by_id[cid]['file']}  ({skipped[cid]})")

    if args.to_unity:
        print(f"\nWrote directly to {UNITY_CARDS.relative_to(REPO)}/<setId>/<artId>.png")
        missing = [m for m in (check_meta(by_id[cid]) for cid in new_records if new_records[cid])
                   if m]
        if missing:
            print(f"\n{len(missing)} promoted .png has NO .meta beside it. Unity must generate "
                  "it and it must be committed, or the card is INVISIBLE in a player build:")
            for m in missing:
                print(f"    {m}")
    else:
        print(f"\n{made} image(s) -> {OUT}")
        print("Human picks one candidate per card, then promote it to "
              f"{UNITY_CARDS.relative_to(REPO)}/<setId>/<artId>.png")
    return 0 if made or args.skip else 1


if __name__ == "__main__":
    raise SystemExit(main())
