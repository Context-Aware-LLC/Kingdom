"""Generate card-back candidates for the two M2 classes.

Sam's call (2026-07-25): seed the generator from the class palettes that no art
has leaned on yet — Gravekeep and Verdant Warden (dev-docs/descriptors/classes.md:7) —
and run TWO candidates per palette.

The two candidates per class differ on STRUCTURE, not just subject, so the
bake-off answers a real question:

  EMBLEM  — one large central class icon on a flat field, heavy border.
            Reads instantly; risks looking sparse when a full hand is fanned.
  ALLOVER — no focal point; a woven repeating field that reads as texture at
            hand size and resolves into detail on a full-screen deck pile.
            Risks reading as noise if the weave is too fine.

SCALE IS THE WHOLE PROBLEM. A card back is only ever seen as the opponent's
hand and the deck pile: the card quad is 1.17 x 1.65 world units, so at ortho
size 4.6 (~117 px/world unit) it lands at roughly 137 x 193 px on a 1080p
screen. That is SMALLER than the hero-power frame that forced the no-filigree
rule (gen_hero_frames.py). Judge every candidate at true pixel size, never off
the full-size sheet. Few large shapes, strong value contrast, nothing thin.

Geometry: full bleed, no alpha, no sockets. Generated at 1456x2048 — the same
0.71 ratio as the card front (gen_card_frames.py), multiples of 16, and under
the Art/UI 2048 import cap.

Usage: gen_card_backs.py [variant ...]
"""

import base64
import os
import pathlib
import sys
import time

import requests

OUT = pathlib.Path(__file__).resolve().parents[2] / "art-pipeline" / "card-art" / "cardbacks"
SIZE = "1456x2048"
MODEL = "gpt-image-2"

COMMON = """
The BACK of a fantasy trading card, portrait orientation, painterly dark-fantasy
game art. Every card in the deck shows this same face, so the design is PURELY
DECORATIVE and PERFECTLY SYMMETRICAL about its vertical centre line.

ABSOLUTELY NO TEXT. No letters, no numerals, no runes that resemble writing, no
logo, no wordmark, no signature, no watermark. Nothing that could be mistaken
for language anywhere in the image.

FULL BLEED. The design runs to all four edges of the image with a deliberate
ornamental border framing it. No white margin, no drop shadow, no mock-up of a
card lying on a surface, no perspective, no rounded corners drawn into the art,
no card edges depicted. This IS the card face, seen flat and straight on.

KEEP IT BOLD. This is displayed very small — about 140 pixels wide on screen.
Use FEW LARGE SHAPES with STRONG light-to-dark contrast. NO fine tracery, NO
hairline scrollwork, NO tiny repeated rivets, NO delicate filigree. Anything
thinner than a heavy stroke dissolves into grey noise at display size. Detail
that cannot survive being shrunk to a thumbnail is worse than no detail.

The world is "Emberfall": a land after a sky-fire cataclysm, where embers still
fall like snow. The mood is old, weathered and hand-made — a forged and painted
object, not a digital graphic.
"""

STRUCTURES = {
    "Emblem": """
STRUCTURE — CENTRAL EMBLEM. One single large emblem, centred, occupying roughly
the middle 55% of the card's height and 65% of its width. It is the only focal
point. Around it the field is calm and nearly flat, dark and softly textured
like worn leather or aged parchment, with no competing detail whatsoever. A
thick, heavy ornamental border rings the whole card, plain and weighty. The
emblem reads as one clear silhouette from across a room.
""",
    "Allover": """
STRUCTURE — ALLOVER PATTERN. NO central emblem and no focal point at all. The
entire card face is one continuous interlocking pattern, repeating and woven
edge to edge, evenly weighted across the whole surface like a tooled leather
book cover or a forged iron grille. The repeating motif is LARGE — only about
four to six repeats across the width of the card — so it stays legible when the
card is shrunk to a thumbnail. A thick ornamental border rings the whole card
and contains the pattern.
""",
    # v2, after seeing "Allover" in a live match (Sam, 2026-07-25): alone it was
    # the better object, but five of them overlapping in the opponent's hand
    # merged into one busy ribbon. The ironwork was not the problem — the glow
    # was, spread over the whole card in a dozen hot spots so nothing rested.
    # This keeps the lattice and cuts the repeat count roughly in half.
    "AlloverBig": """
STRUCTURE — ALLOVER PATTERN, LARGE AND CALM. NO central emblem and no focal
point. One continuous interlocking pattern woven edge to edge, contained by a
thick ornamental border.

THE MOTIF IS VERY LARGE: only TWO OR THREE repeats across the width of the card
and three or four down its height. Each single unit of the pattern is therefore
big and simple — read it as a few massive forged elements, not a fine mesh.

REST IS REQUIRED. Between the pattern's elements leave broad areas of deep,
almost-black shadow doing nothing at all. Roughly half the card should be quiet
dark metal. The pattern is heavy and sparse, not dense and even.
""",
}

CLASSES = {
    "Gravekeep": """
CLASS — GRAVEKEEP, the wardens of the dead of the cataclysm. Solemn, patient,
quietly inevitable. Motifs to draw from: a heavy iron funeral bell, wrapped
chains, a great sealed gate, pale moths, soul-lanterns. Palette: bone white,
cold iron grey, funerary bronze, and a sickly green-gold grave-light glowing
from within the darkest recesses. The green-gold light is the only saturated
colour; everything else is bone and iron.
""",
    "Verdant": """
CLASS — VERDANT WARDEN, the world growing back over the cataclysm's scar. Slow,
vast, inexorably alive. Motifs to draw from: a great world-tree, antlered
branches, living roots splitting carved stone, seeds and saplings, thorn and
moss. Palette: deep forest green, artichoke green, cedar brown, and warm golden
honey sap-light glowing from within. The honey light is the only bright note;
everything else is deep green and brown.
""",
}

SUBJECTS = {
    ("Gravekeep", "Emblem"): """
THE EMBLEM: a single heavy iron funeral bell hanging dead centre, seen straight
on, wrapped once around with a thick chain, green-gold grave-light spilling out
of its hollow mouth from below. Bold, blocky, unmistakable in silhouette.
""",
    ("Gravekeep", "Allover"): """
THE PATTERN: heavy interlocking chain links and blunt iron gate-bars woven into
a repeating lattice, with pale moths resting in the openings between them.
Green-gold grave-light seeps through the gaps in the lattice from behind.
""",
    ("Verdant", "Emblem"): """
THE EMBLEM: a single great seed or acorn hanging dead centre, cracked open, one
bold shoot rising from it, the whole thing cradled in thick woven roots that
have split a slab of carved stone. Golden honey sap-light glows from the crack.
""",
    ("Verdant", "Allover"): """
THE PATTERN: thick roots and antlered branches interlacing into a repeating
woven knot-work, broad and heavy like carved cedar, with big simple leaves
filling the openings. Golden honey sap-light glows from deep inside the weave.
""",
    ("Gravekeep", "AlloverBig"): """
THE PATTERN: massive interlocking chain links and heavy iron gate-bars, only two
or three across the width of the card, forming a bold sparse lattice. A single
pale moth rests in each large opening.

THE LIGHT IS THE POINT. Grave-light appears in only THREE OR FOUR small places
on the whole card — narrow slivers seeping from behind the ironwork — and
nowhere else. Everywhere else is cold dark iron and deep shadow. Do NOT scatter
glow evenly across the surface; do NOT light every opening. The card should read
as a dark iron gate at night with a little sick green light leaking through it.
""",
    ("Verdant", "AlloverBig"): """
THE PATTERN: enormous roots and antlered branches interlacing into a bold sparse
knot-work, only two or three across the width of the card, with a few big simple
leaves. Honey sap-light appears in only three or four narrow places; the rest is
deep green shadow and dark cedar.
""",
}

VARIANTS = {f"{c}_{s}": (c, s) for c in CLASSES for s in STRUCTURES}


def build_prompt(cls: str, struct: str) -> str:
    return (
        COMMON
        + CLASSES[cls]
        + STRUCTURES[struct]
        + SUBJECTS[(cls, struct)]
    ).strip()


def generate(name: str, key: str) -> bool:
    cls, struct = VARIANTS[name]
    prompt = build_prompt(cls, struct)
    print(f"[{name}] requesting {SIZE} ...", flush=True)
    t0 = time.time()
    try:
        r = requests.post(
            "https://api.openai.com/v1/images/generations",
            headers={"Authorization": f"Bearer {key}"},
            json={"model": MODEL, "prompt": prompt, "size": SIZE, "quality": "high", "n": 1},
            timeout=600,
        )
    except requests.RequestException as e:
        print(f"[{name}] FAILED (network): {e}", flush=True)
        return False

    if r.status_code != 200:
        print(f"[{name}] FAILED {r.status_code}: {r.text[:400]}", flush=True)
        return False

    data = r.json()["data"][0]
    raw = base64.b64decode(data["b64_json"]) if "b64_json" in data else requests.get(data["url"], timeout=120).content
    dest = OUT / f"CardBack_{name}.png"
    dest.write_bytes(raw)
    print(f"[{name}] -> {dest.name}  ({len(raw)/1e6:.1f} MB, {time.time()-t0:.0f}s)", flush=True)
    return True


def main() -> int:
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        print("OPENAI_API_KEY not set", file=sys.stderr)
        return 2

    names = sys.argv[1:] or list(VARIANTS)
    for n in names:
        if n not in VARIANTS:
            print(f"unknown variant '{n}' (have: {', '.join(VARIANTS)})", file=sys.stderr)
            return 2

    OUT.mkdir(parents=True, exist_ok=True)
    ok = sum(1 for n in names if generate(n, key))
    print(f"\n{ok}/{len(names)} generated -> {OUT}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
