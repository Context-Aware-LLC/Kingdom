"""Generate the card illustrations for the 22 Gravekeep/Verdant draft cards.

TWO PROVIDERS, and they are not interchangeable (Sam, 2026-07-26):

  GEMINI — fast and cheap, so this is where the volume
      goes: TWO candidates per card, whole batch at once. Buy breadth here.
  OPENAI gpt-image-2 (default for card art and full screen images) — "an ace one-shot," but ~5 minutes per image
      and ~20x the cost. ONE candidate, and only on hand-picked cards whose
      Gemini candidates weren't good enough. The script REFUSES to run this
      provider over the whole batch; see `require_explicit_selection`.

Both are called as plain HTTPS from `requests`, matching gen_card_backs.py /
gen_board_plates.py. Never the Unity wrapper — it silently drops width/height
and caps at 1024 square.

WHERE THE PROMPT COMES FROM. Nothing here is invented. Each prompt is composed,
in this order, from three sources on disk:

  1. STYLE   — dev-docs/specs/card-generation-prompt.md:36 (the mandatory global style prefix,
               verbatim) plus §1's technical delivery rules. ONE PER PROVIDER:
               STYLE_BLOCK for gemini (unchanged, silhouette-first, tuned to the
               board's 137x194 px), STYLE_BLOCK_OPENAI for gpt-image-2, which
               keeps every §1 delivery rule but releases the silhouette-only /
               no-background-scene reading, because that path is chosen and paid
               for to get the painterly detail. §2's negative list follows as an
               "Avoid:" line, because neither image endpoint takes a
               negative-prompt parameter. READ THE COMMENT ON STYLE_BLOCK_OPENAI
               before changing any of it.
  2. CLASS   — the house-look paragraph that opens the "Art prompts" section of
               dev-docs/specs/card-generation-prompt.md. Gravekeep = ash and ember,
               explicitly not Azeroth/WoW-adjacent; Verdant = this world's
               green, overgrown-and-encroaching.
  3. CARD    — the per-card paragraph from that same fenced block, keyed by card
               id and paired with its row in card-drafts-<class>.json.

Sources 2 and 3 are provider-independent — the class house look and the per-card
subject go to both providers byte-identical — so a card re-rolled on the other
provider is a controlled comparison of the two LOOKS, source 1 being the only
thing that moved. The manifest stores provider + model + the exact prompt per
generated image, so what a given file was actually asked for is always on disk.

SCALE IS THE CONSTRAINT, as everywhere else in this repo. A board card is
137x194 px at ortho 4.6 on 1080p, and dev-docs/specs/card-generation-prompt.md:24 sets the bar at
"readable at 220px". Both class house-look paragraphs already say it; they are
passed through rather than restated here.

Geometry: 1024x1024 square. dev-docs/pipelines/art-and-rendering.md:9 — the illustration is the
only per-card asset, a 1024 square that bleeds under the frame mask. OpenAI
takes that as a literal `size`. Gemini takes `aspect_ratio: "1:1"` +
`image_size: "1K"` and publishes NO table mapping that to exact pixels, so every
saved file is measured and a non-1024 square is resampled (loudly).

ORDER OF WORK is hist-docs/source-snapshots/2026-07-31-pre-reorganization/design/art-brief-m1.md §7.1: legendaries first, then epics, rares,
commons — identity assets first, filler last.

CANDIDATES, NOT SINGLES (hist-docs/source-snapshots/2026-07-31-pre-reorganization/design/art-brief-m1.md §7.2): candidates land in

    art-bakeoff/card-art/<setId>/<art_file>/cand_<provider>_N.png

with a per-class contact sheet, and a human picks before anything is promoted to

    unity/Emberfall/Assets/Art/Cards/<setId>/art_<cardId>.png

Filenames are canonical per §1: `art_<cardId>`, i.e. the row's `artId`
("art/grave_last_rite") with `/` flattened to `_`.

Keys come from the environment only: GEMINI_API_KEY, OPENAI_API_KEY. --dry-run
needs neither.

Usage:
  gen_card_art.py [options]
    --provider gemini|openai   default gemini
    --dry-run                  print the composed prompt per card, generate nothing
    --card <id>                one card (repeatable); REQUIRED for --provider openai
    --class <gravekeep|verdant>
    --candidates N             default 2 (gemini) / 1 (openai), clamped to 1-4
    --skip-existing            default ON; --no-skip-existing to re-roll
    --sheet-only               rebuild the contact sheets from what is on disk
"""

import argparse
import base64
import json
import os
import pathlib
import re
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
DOCS = pathlib.Path(
    os.environ.get(
        "EMBERFALL_DOCS",
        pathlib.Path.home() / "Documents" / "Project Docs" / "Emberfall" / "docs",
    )
)
DESIGN = DOCS / "design"

OUT = REPO / "art-bakeoff" / "card-art"
UNITY_CARDS = REPO / "unity" / "Emberfall" / "Assets" / "Art" / "Cards"
MANIFEST = OUT / "prompt-manifest.json"

EDGE = 1024  # dev-docs/pipelines/art-and-rendering.md:9

# ---------------------------------------------------------------- providers
#
# Gemini call shape is the documented Interactions API image-generation form
# (ai.google.dev/gemini-api/docs/interactions/image-generation): POST
# /v1beta/interactions, key in `x-goog-api-key` (NOT a Bearer token), size via
# response_format.aspect_ratio + image_size, bytes returned base64-inline under
# steps[].content[] where type == "image". Neither provider has a
# candidate-count parameter, so N candidates means N requests.
#
# `secs` and `usd_per_image` are ESTIMATES, used only by the spend guard's
# printout.
#
# THE OPENAI PRICE, CORRECTED 2026-07-26. This guard used to print "~20x gemini
# cost units," a ratio derived from a believed ~$6/image. That $6 was never
# gpt-image-2's price: it came from a gbrain note about OpenDesign burning
# tokens through bad context management — a different tool, a different failure.
# gpt-image-2 bills per token ($8/M image input, $30/M image output, $5/M text
# input), which lands at roughly $0.005/image at low quality and $0.165-$0.21 at
# high. This script always asks for `quality: "high"` (see `_post_openai`), so
# the constant is the top of that band: $0.19/image, deliberately a slight
# over-estimate so the guard never flatters a run.
#
# Gemini's per-image price is NOT hardcoded — nobody has verified it for this
# model, and inventing one to keep a tidy ratio is exactly the mistake above.
# `usd_per_image: None` means "unpriced"; the guard says so rather than
# printing a made-up number. The wall-clock figures are Sam's measured ones and
# are unchanged.
PROVIDERS = {
    "gemini": {
        "model": "gemini-3.1-flash-image",
        "env": "GEMINI_API_KEY",
        "candidates": 2,
        "secs": 20,
        "usd_per_image": None,
        "note": "first pass — fast and cheap, buy breadth",
    },
    "openai": {
        "model": "gpt-image-2",
        "env": "OPENAI_API_KEY",
        "candidates": 1,
        "secs": 300,
        "usd_per_image": 0.19,
        "note": "fallback one-shot — ~5 min/image, ~$0.19/image, hand-picked cards only",
    },
}

# hist-docs/source-snapshots/2026-07-31-pre-reorganization/design/art-brief-m1.md §7.1 — identity assets first, filler last.
RARITY_ORDER = {"LEGENDARY": 0, "EPIC": 1, "RARE": 2, "COMMON": 3}

# One entry per DRAFT FILE, not strictly per class. `batch2` carries both
# EMBERWARD and TIDECALLER because they were designed as one balance pass
# against each other (hist-docs/source-snapshots/2026-07-31-pre-reorganization/design/card-drafts-emberward-tidecaller.md §1) and share a single
# house-look paragraph; each card's prompt names its own class palette on its
# last line, which is what actually keeps the two looks apart.
CLASSES = {
    "gravekeep": "card-drafts-gravekeep",
    "verdant": "card-drafts-verdant",
    "batch2": "card-drafts-emberward-tidecaller",
    "batch3": "card-drafts-emberward-tidecaller-b3",
    "weapon-rerolls": "card-drafts-weapon-rerolls",
    # M2 expansion classes (dev-docs/descriptors/classes.md:7)
    "clockwright": "card-drafts-clockwright",
    "nightveil":   "card-drafts-nightveil",
    "stonewarden": "card-drafts-stonewarden",
    "tempest":     "card-drafts-tempest",
    # REKINDLING second expansion set (dev-docs/descriptors/classes.md:7)
    "rekindling":  "card-drafts-rekindling",
}

# WHERE CANDIDATES LAND under art-bakeoff/card-art/ (Sam, 2026-07-26). The
# original layout was one folder per setId, but every card in the game is
# setId EMBERFALL, so all 29 cards piled into a single directory. New work goes
# in its own folder instead: this maps a CLASSES key to its candidate subfolder,
# falling back to the card's setId for the two batches already on disk under it.
#
# This is the BAKE-OFF path only. The promotion target is untouched — a picked
# candidate still goes to unity/.../Art/Cards/<setId>/art_<cardId>.png, the
# folder contract CardArtPostprocessor.cs enforces. See final_path().
OUT_GROUP = {
    "weapon-rerolls": "weapon-rerolls",
    "batch2": "emberward-tidecaller",
    "batch3": "emberward-tidecaller",
    "clockwright": "clockwright",
    "nightveil":   "nightveil",
    "stonewarden": "stonewarden",
    "tempest":     "tempest",
}

# ------------------------------------------------------------- style block
#
# THE ONE PLACE THE HOUSE LOOK IS DEFINED. Everything below is dev-docs/specs/card-generation-prompt.md:36:
# §2's global style prefix verbatim, then §1's technical delivery rules. That
# brief produced the 37 illustrations shipped in the game today, so it is the
# established look. Change it here or nowhere.
#
# §2 AND THE 22 CARD DRAFTS DISAGREE ABOUT BACKGROUNDS, and the ruling is that
# the provider decides (Sam, 2026-07-26). §2 says "background supports but never
# competes with subject"; the drafts say "no background scene", near-black,
# silhouette-first, one accent point of colour. Both are right for the pass they
# were written for, so the split is per-provider: this STYLE_BLOCK (gemini, the
# volume pass) leaves the drafts' silhouette-first wording to stand, and
# STYLE_BLOCK_OPENAI below reinstates §2's supporting background for the pass
# that is paid for to render detail. GLOBAL_STYLE and TECHNICAL are shared by
# both — everything that differs is in OPENAI_DETAIL.

# dev-docs/specs/card-generation-prompt.md:36, verbatim.
GLOBAL_STYLE = (
    "Stylized fantasy trading-card-game illustration, painterly digital art, "
    "dramatic rim lighting, single clear subject with strong silhouette, rich "
    "saturated colors, dynamic composition, centered focal point, background "
    "supports but never competes with subject, professional card game art "
    "quality, no text, no border, no frame, no watermark."
)

# dev-docs/specs/card-generation-prompt.md:12 — the non-negotiable delivery rules, as prompt scaffolding.
TECHNICAL = """
SQUARE CROP. The card frame masks the edges: keep the subject's focal point
inside the CENTRE 70% of the image, because the outer ~15% on every side may be
cropped or overlaid by the frame. The art BLEEDS TO THE EDGE — no borders, no
matting, no vignette-to-black, no drawn card edges.

NO TEXT, no letters, no numerals, no runes resembling writing, no logo, no
wordmark, no signature, no watermark, no frame, no UI element. Ever, anywhere.

READABLE AT 220 PIXELS. One clear subject, strong silhouette, high value
contrast between subject and background. If it needs squinting at full size it
is dead on the board.

Painterly digital illustration: confident bskirmishwork, saturated accent lighting,
readable shapes. NOT photoreal, NOT a 3D render, NOT anime or cel-shaded.

SUBJECT AND BACKGROUND MUST OPPOSE EACH OTHER. The class palette governs the
SUBJECT, not the whole frame. A subject painted in its palette and then dropped
into a setting painted in that same palette becomes one flat mass, and at board
size that mass reads as nothing: a molten creature standing in a volcano is a
single orange blob. Put the subject somewhere its own palette does not belong,
and oppose it in hue, in value, or in temperature — the molten thing wrecking a
cold night village, the black slab against a pale dawn, the frosted thing under
falling embers. Name the subject palette and the background separately and make
them fight.
"""

STYLE_BLOCK = GLOBAL_STYLE + "\n\n" + TECHNICAL.strip()

# --------------------------------------------------- the openai style block
#
# ONE STYLE BLOCK PER PROVIDER, because the two providers are picked for
# opposite reasons (Sam, 2026-07-26).
#
# STYLE_BLOCK above is the Gemini/volume path, and the drafts it composes with
# are silhouette-first on purpose: gpt-image-2's extra detail was found to turn
# to mud at the board's real 137x194 px, so the first pass buys shape and value
# separation and nothing else.
#
# But gpt-image-2 is never reached by accident — `require_explicit_selection`
# makes Sam name the card. He is paying the premium precisely FOR the painterly
# detail, so constraining that path to the minimalism tuned for the cheap one
# spends the money and throws away the thing it bought. On this path §2's own
# sentence stands as written: "background supports but never competes with
# subject" — a supporting background, not an absent one.
#
# WHAT IS UNCHANGED: §1's delivery rules, in full. Square crop, focal point in
# the centre 70%, bleed to the edge, no text/logo/UI, readable at 220px. Those
# are hard delivery constraints the frame mask and the board scale impose on any
# provider — not a style preference, and not negotiable by paying more.
#
# WHAT IS DROPPED: only the silhouette-ONLY reading — "no background scene",
# "no background detail", "everything else in the frame stays near-black". That
# language lives in the per-class house look and the per-card drafts, which are
# still passed through verbatim (build_prompt composes them identically for both
# providers), so the release has to be stated here explicitly or the draft text
# would simply re-impose it.
OPENAI_DETAIL = """
PAINT IT FULLY. You are the expensive pass, chosen for depth: rich saturated
color, layered rendering, atmosphere, texture, and a background that supports
the subject. Where the class or subject text below says "no background scene",
"no background detail", "stark black background", or "minimal detail", read that
as a floor on CONTRAST, not a ban on painting: give the subject a supporting
environment that stays subordinate to it in value, detail and saturation, and
never competes for the eye.

The 220px read is still the bar, and it is met by value contrast and a clean
overall shape — not by flattening the subject into a silhouette. Keep the
silhouette legible; render everything inside it.
"""

STYLE_BLOCK_OPENAI = (
    GLOBAL_STYLE + "\n\n" + TECHNICAL.strip() + "\n\n" + OPENAI_DETAIL.strip()
)

STYLE_BLOCKS = {"gemini": STYLE_BLOCK, "openai": STYLE_BLOCK_OPENAI}

# dev-docs/specs/card-generation-prompt.md:42. Neither endpoint takes a negative
# parameter, so it is stated as an instruction instead of dropped.
NEGATIVE = (
    "Avoid: text, letters, watermark, signature, frame, border, UI, "
    "photorealism, 3D render, anime, blurry, multiple competing subjects, "
    "cropped subject head."
)


# ---------------------------------------------------------------- parsing

def _art_section(md: str) -> str:
    """The closing 'Art prompts' section of a card-drafts .md."""
    m = re.search(r"^##\s+\d+\.\s*Art prompts\s*$", md, re.M)
    if not m:
        raise ValueError("no '## N. Art prompts' heading")
    tail = md[m.end():]
    nxt = re.search(r"^##\s", tail, re.M)
    return tail[: nxt.start()] if nxt else tail


def _house_look(section: str) -> str:
    """The class-level prose between the section heading and the fenced block."""
    head = section.split("```", 1)[0]
    text = re.sub(r"\*{1,2}(.+?)\*{1,2}", r"\1", head).strip()
    if not text:
        raise ValueError("no house-look paragraph before the fenced block")
    return text


def _card_prompts(section: str) -> dict[str, tuple[str, str]]:
    """id -> (head-of-first-line, prompt body) for each entry in the fence."""
    fences = re.findall(r"```[a-z]*\n(.*?)```", section, re.S)
    if not fences:
        raise ValueError("no fenced prompt block")
    out: dict[str, tuple[str, str]] = {}
    for chunk in re.split(r"\n\s*\n", "\n".join(fences)):
        chunk = chunk.strip()
        if not chunk:
            continue
        first, _, rest = chunk.partition("\n")
        m = re.match(r"^([a-z0-9_]+)\s*[—–-]\s*(.*)$", first)
        if not m:
            raise ValueError(f"unparsed prompt chunk: {first[:60]!r}")
        cid, head = m.group(1), m.group(2).strip()
        out[cid] = (head, " ".join(rest.split()))
    return out


def art_file(art_id: str) -> str:
    """artId 'art/grave_last_rite' -> 'art_grave_last_rite' (§1: art_<cardId>)."""
    return art_id.replace("/", "_")


def load_cards() -> list[dict]:
    cards: list[dict] = []
    for cls, stem in CLASSES.items():
        rows = json.loads((DESIGN / f"{stem}.json").read_text(encoding="utf-8"))
        section = _art_section((DESIGN / f"{stem}.md").read_text(encoding="utf-8"))
        house = _house_look(section)
        prompts = _card_prompts(section)

        for row in rows:
            cid = row["id"]
            if cid not in prompts:
                raise ValueError(f"{stem}.md has no art prompt for '{cid}'")
            head, body = prompts[cid]
            # Gravekeep writes "id — Card Name" then the prompt below; Verdant
            # starts the prompt on the id line. Drop the head only when it is
            # the card's name.
            if head and head.lower() != row["name"].lower():
                body = (head + " " + body).strip()
            cards.append(
                {
                    "id": cid,
                    "name": row["name"],
                    "artId": row["artId"],
                    "file": art_file(row["artId"]),
                    "setId": row.get("setId", "EMBERFALL"),
                    "outGroup": OUT_GROUP.get(cls, row.get("setId", "EMBERFALL")),
                    "rarity": row.get("rarity", "COMMON"),
                    "cost": row.get("cost", 0),
                    "class": cls,
                    "house": house,
                    "core": body,
                }
            )

        unused = set(prompts) - {r["id"] for r in rows}
        if unused:
            raise ValueError(f"{stem}.md prompts with no JSON row: {sorted(unused)}")

    cards.sort(key=lambda c: (RARITY_ORDER.get(c["rarity"], 9), -c["cost"], c["id"]))
    return cards


def build_prompt(card: dict, provider: str) -> str:
    """Only the STYLE block differs by provider (see STYLE_BLOCK_OPENAI). The
    class house look and the per-card subject are composed identically for both,
    so a card re-rolled on the other provider is still a controlled comparison
    of the two looks."""
    return "\n\n".join(
        [
            STYLE_BLOCKS[provider],
            "CLASS HOUSE LOOK:\n" + card["house"],
            f"SUBJECT — {card['name']}:\n{card['core']}",
            NEGATIVE,
        ]
    )


# ------------------------------------------------------------- generation

def card_dir(card: dict) -> pathlib.Path:
    return OUT / card["outGroup"] / card["file"]


def final_path(card: dict) -> pathlib.Path:
    return UNITY_CARDS / card["setId"] / f"{card['file']}.png"


def existing(card: dict, provider: str) -> list[pathlib.Path]:
    """Candidates from THIS provider only — an openai fallback must not be
    skipped just because gemini already produced two."""
    return sorted(card_dir(card).glob(f"cand_{provider}_*.png"))


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


def _post_gemini(prompt: str, key: str) -> tuple[bytes | None, str]:
    """Interactions API. Returns (png_bytes, error_message).

    THE ENDPOINT ONLY EMITS JPEG. `response_format.mime_type: "image/png"` is
    rejected outright — HTTP 400, "Supported values: 'image/jpeg'" — so every
    call this client made returned nothing (found 2026-07-26; the file
    extension `.png` had until then been the only evidence anyone had of the
    format). We ask for jpeg and transcode to PNG here, because
    dev-docs/pipelines/art-and-rendering.md:9 and the `CardArtPostprocessor.cs` folder contract
    both want a real PNG on disk, not a JPEG wearing the extension.
    """
    r = requests.post(
        "https://generativelanguage.googleapis.com/v1beta/interactions",
        headers={
            "x-goog-api-key": key,
            "Content-Type": "application/json",
            "Api-Revision": "2026-05-20",
        },
        json={
            "model": PROVIDERS["gemini"]["model"],
            "input": prompt,
            "response_format": {
                "type": "image",
                "mime_type": "image/jpeg",
                "aspect_ratio": "1:1",
                "image_size": "1K",
            },
        },
        timeout=300,
    )
    if r.status_code != 200:
        return None, f"HTTP {r.status_code}: {r.text[:400]}"

    data = r.json()
    # Iterate the steps rather than trusting a convenience field: interleaved
    # text+image output is a documented real case, and a safety refusal comes
    # back as text where the image should be. Never b64decode unchecked.
    texts: list[str] = []
    for step in data.get("steps") or []:
        for part in step.get("content") or []:
            if part.get("type") == "image" and part.get("data"):
                return _to_png(base64.b64decode(part["data"]))
            if part.get("type") == "text" and part.get("text"):
                texts.append(part["text"])
    blurb = " ".join(texts)[:300] if texts else json.dumps(data)[:300]
    return None, f"no image in response (refusal or text-only?): {blurb}"


def _post_openai(prompt: str, key: str) -> tuple[bytes | None, str]:
    r = requests.post(
        "https://api.openai.com/v1/images/generations",
        headers={"Authorization": f"Bearer {key}"},
        json={
            "model": PROVIDERS["openai"]["model"],
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
        return base64.b64decode(data["b64_json"]), ""
    return requests.get(data["url"], timeout=120).content, ""


POST = {"gemini": _post_gemini, "openai": _post_openai}


def _conform(dest: pathlib.Path, tag: str) -> str:
    """Measure what actually landed. Gemini publishes no aspect-ratio-to-pixels
    table, so 1K/1:1 == 1024 square is an assumption until measured; a square
    that is merely the wrong size is resampled, anything non-square is left
    alone and reported loudly rather than silently distorted."""
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
    print(f"[{tag}] WARNING: NOT SQUARE ({w}x{h}) — dev-docs/pipelines/art-and-rendering.md:9 wants a "
          f"{EDGE} square. Left as-is; fix before promoting.", flush=True)
    return f"{w}x{h} NOT SQUARE"


def generate(card: dict, provider: str, key: str, candidates: int,
             skip: bool) -> list[dict]:
    """Returns one manifest record per image actually written."""
    spec = PROVIDERS[provider]
    have = existing(card, provider)
    if skip and len(have) >= candidates:
        print(f"[{card['file']}] skip ({len(have)} {provider} candidate(s) on disk)",
              flush=True)
        return []

    prompt = build_prompt(card, provider)
    dest_dir = card_dir(card)
    dest_dir.mkdir(parents=True, exist_ok=True)

    # NEVER OVERWRITE A GENERATED IMAGE (Sam, 2026-07-26): "don't delete anything
    # you generate — if something needs deleting, I'll delete it myself." Numbering
    # always continues past what is already on disk, so a re-roll ADDS
    # cand_<provider>_2.png beside _1 and the contact sheet shows them side by side.
    # Before this, --no-skip-existing restarted the index at 0 and silently
    # clobbered cand_1 — the one flag in the script that could destroy a candidate.
    # --skip-existing (default) still means "stop once N exist"; --no-skip-existing
    # now means "add N more", not "redo the first N".
    base = len(have)
    n_new = (candidates - base) if skip else candidates

    records: list[dict] = []
    for k in range(n_new):
        i = base + k
        tag = f"{card['file']} {provider} c{i + 1}"
        print(f"[{tag}] requesting {EDGE}x{EDGE} from {spec['model']} ...", flush=True)
        t0 = time.time()
        try:
            raw, err = POST[provider](prompt, key)
        except requests.RequestException as e:
            print(f"[{tag}] FAILED (network): {e}", flush=True)
            continue
        if raw is None:
            print(f"[{tag}] FAILED {err}", flush=True)
            continue

        dest = dest_dir / f"cand_{provider}_{i + 1}.png"
        dest.write_bytes(raw)
        dims = _conform(dest, tag)
        took = time.time() - t0
        print(f"[{tag}] -> {dest.relative_to(OUT)}  "
              f"({len(raw)/1e6:.1f} MB, {dims}, {took:.0f}s)", flush=True)
        records.append(
            {
                "file": dest.name,
                "provider": provider,
                "model": spec["model"],
                "requested": f"{EDGE}x{EDGE}",
                "delivered": dims,
                "seconds": round(took, 1),
                "generatedAt": time.strftime("%Y-%m-%dT%H:%M:%S"),
                "prompt": prompt,
            }
        )
    return records


def write_manifest(cards: list[dict], new_records: dict[str, list[dict]]) -> None:
    """Per IMAGE: provider + model + the exact prompt used, so any card can be
    re-rolled on the other provider reproducibly."""
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
                "candidateDir": str(card_dir(card).relative_to(REPO)).replace("\\", "/"),
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
    """One sheet per class: every candidate from every provider side by side,
    plus a 220px tile — the size §1 says the art has to survive. Judged small
    or not judged."""
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
        paths = sorted(card_dir(card).glob("cand_*.png"))
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
            d.text((x, top + TILE + 4), p.stem.replace("cand_", ""), fill=DIM, font=font(13))
        x = PAD + cols * (TILE + PAD)
        sheet.paste(first.resize((SMALL, SMALL), Image.LANCZOS), (x, top))
        d.text((x, top + SMALL + 4), f"@ {SMALL}px (board read)", fill=DIM, font=font(13))
        y += row_h

    # Sheet sits alongside the candidate folders it summarises, not at the root.
    dest = OUT / rows[0][0]["outGroup"] / f"SHEET_{cls}.png"
    dest.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(dest)
    print(f"{cls}: {len(rows)} card(s) -> {dest.relative_to(REPO)}  ({sheet.width}x{sheet.height})")


# ------------------------------------------------------------ spend guard

def estimate(provider: str, images: int) -> str:
    spec = PROVIDERS[provider]
    mins = images * spec["secs"] / 60
    usd = spec["usd_per_image"]
    money = (f"~${images * usd:.2f} (~${usd:.2f}/image, high quality)"
             if usd is not None else "cost not priced here (cheap first pass)")
    return (f"{images} image(s) on {provider} ({spec['model']}) "
            f"~{mins:.0f} min wall-clock, {money}")


def require_explicit_selection(provider: str, cards: list[dict], candidates: int,
                               selected: bool) -> bool:
    """The expensive provider is opt-in per card. A fat-fingered full-batch run
    on openai should be impossible, not merely unlikely."""
    if provider != "openai" or selected:
        return True
    print("REFUSED: --provider openai requires an explicit --card or --class.\n"
          f"  It is the fallback: {PROVIDERS['openai']['note']}.\n"
          f"  You asked for the whole batch: {estimate(provider, len(cards) * candidates)}.\n"
          "  Run the gemini pass first (the default), then name the cards that "
          "need the one-shot:\n"
          "    gen_card_art.py --provider openai --card grave_harrower_of_ash",
          file=sys.stderr)
    return False


# ------------------------------------------------------------------- main

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--provider", choices=sorted(PROVIDERS), default="gemini",
                    help="gemini = first pass (default); openai = expensive fallback")
    ap.add_argument("--dry-run", action="store_true",
                    help="print the fully composed prompt per card, generate nothing")
    ap.add_argument("--card", action="append", metavar="ID", help="single card id (repeatable)")
    ap.add_argument("--class", dest="cls", choices=sorted(CLASSES), help="one class only")
    ap.add_argument("--candidates", type=int, default=None, metavar="N",
                    help="candidates per card (default 2 gemini / 1 openai, clamp 1-4)")
    ap.add_argument("--skip-existing", dest="skip", action="store_true", default=True,
                    help="skip cards with enough candidates from this provider (default)")
    ap.add_argument("--no-skip-existing", dest="skip", action="store_false",
                    help="re-roll candidates even if they exist")
    ap.add_argument("--sheet-only", action="store_true",
                    help="rebuild contact sheets from what is already on disk")
    args = ap.parse_args()

    provider = args.provider
    spec = PROVIDERS[provider]
    candidates = spec["candidates"] if args.candidates is None else args.candidates

    if not DESIGN.is_dir():
        print(f"docs design dir not found: {DESIGN}  (set EMBERFALL_DOCS)", file=sys.stderr)
        return 2

    try:
        cards = load_cards()
    except (OSError, ValueError, json.JSONDecodeError) as e:
        print(f"card load failed: {e}", file=sys.stderr)
        return 2

    selected = bool(args.cls or args.card)
    if args.cls:
        cards = [c for c in cards if c["class"] == args.cls]
    if args.card:
        wanted = set(args.card)
        unknown = wanted - {c["id"] for c in cards}
        if unknown:
            print(f"unknown card id(s): {', '.join(sorted(unknown))}", file=sys.stderr)
            return 2
        cards = [c for c in cards if c["id"] in wanted]
    if not cards:
        print("no cards selected", file=sys.stderr)
        return 2

    if args.dry_run:
        for card in cards:
            print("=" * 78)
            print(f"{card['id']}  [{card['class']} / {card['rarity']} / {card['cost']} mana]")
            print(f"  provider {provider} ({spec['model']}) x{candidates} candidate(s)")
            print(f"  file     {card['file']}.png  ({EDGE}x{EDGE})")
            print(f"  cands    {card_dir(card).relative_to(REPO)}/cand_{provider}_N.png")
            print(f"  final    {final_path(card).relative_to(REPO)}")
            print("-" * 78)
            print(build_prompt(card, provider))
            print()
        print("=" * 78)
        print(f"{len(cards)} prompt(s) composed, in generation order "
              f"(legendary -> epic -> rare -> common). Nothing generated.")
        print(estimate(provider, len(cards) * candidates))
        if provider == "openai" and not selected:
            print("NOTE: a real run would be REFUSED — openai needs --card/--class.")
        return 0

    if args.sheet_only:
        for cls in sorted({c["class"] for c in cards}):
            contact_sheet([c for c in cards if c["class"] == cls], cls)
        return 0

    if not 1 <= candidates <= 4:
        print("--candidates must be 1-4 (hist-docs/source-snapshots/2026-07-31-pre-reorganization/design/art-brief-m1.md §7.2)", file=sys.stderr)
        return 2

    if not require_explicit_selection(provider, cards, candidates, selected):
        return 2

    key = os.environ.get(spec["env"])
    if not key:
        print(f"{spec['env']} is not set — required for --provider {provider} "
              f"({spec['model']}).\n"
              f"  Set it in the environment, e.g. PowerShell:\n"
              f"    [Environment]::SetEnvironmentVariable('{spec['env']}', '<key>', 'User')\n"
              f"  then start a NEW shell. --dry-run needs no key at all.",
              file=sys.stderr)
        return 2

    print(estimate(provider, len(cards) * candidates))
    OUT.mkdir(parents=True, exist_ok=True)
    new_records = {c["id"]: generate(c, provider, key, candidates, args.skip) for c in cards}
    write_manifest(cards, new_records)
    for cls in sorted({c["class"] for c in cards}):
        contact_sheet([c for c in cards if c["class"] == cls], cls)

    made = sum(len(v) for v in new_records.values())
    print(f"\n{made} image(s) generated -> {OUT}")
    print("Human picks one candidate per card, then promote it to "
          f"{UNITY_CARDS.relative_to(REPO)}/<setId>/<art_file>.png")
    return 0 if made or args.skip else 1


if __name__ == "__main__":
    raise SystemExit(main())
