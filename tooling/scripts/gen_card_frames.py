"""Generate card-front frame (skin) candidates — the master minion frame.

Same pipeline as gen_board_plates.py: direct OpenAI API (gpt-image-2). The
winning candidate becomes the master composition: its art window is alpha-cut
and its socket positions are measured off the pixels, then BoardController's
label/art positions are fitted to the art (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/frame-rarity-system.md).
Rarity/type variants derive from the approved master by recoloring, keeping
every socket aligned across the 12-frame matrix (spec 3, data-driven assembly).

Card quad is 1.17 x 1.65 world units; generated at 1456x2048 (same 0.71 ratio,
multiples of 16). Art/UI imports cap at 2048 so this is full resolution.

Usage: gen_card_frames.py [variant ...]
"""

import base64
import os
import pathlib
import sys
import time

import requests

OUT = pathlib.Path(__file__).resolve().parents[2] / "art-pipeline" / "card-art" / "frames"
SIZE = "1456x2048"
MODEL = "gpt-image-2"

# Socket layout is a CONTRACT — code fits labels to these regions after the
# bake-off, but generation keeps them consistent so candidates are comparable.
COMMON = """
A fantasy trading-card FRONT FRAME template, portrait orientation, painterly
dark-fantasy game UI. This is an empty card frame with NO content: no text, no
letters, no numbers, no creature, no illustration. Ornamental chrome only.

COMPOSITION IS A CONTRACT — game code draws content into these regions:
- ART WINDOW: a large empty rectangle with slightly rounded corners spanning
  from 6% to 94% of the width and from 8% to 55% of the height (from the top).
  Fill it with SOLID PURE BLACK (#000000), perfectly flat — it will be cut to
  transparency. A thin ornamental border rings it.
- NAME BAND: a horizontal plaque across the top, from the very top edge to 8%
  height, full width between the corner bosses. Flat, quiet, darker than the
  frame body so light text reads on it.
- COST BOSS: a round boss (a raised circular medallion) centred on the TOP-LEFT
  corner of the card, about 17% of the card width across. Flat dark centre.
- ATTACK BOSS: the same round boss centred on the BOTTOM-LEFT corner.
- HEALTH BOSS: the same round boss centred on the BOTTOM-RIGHT corner.
  All three bosses have plain, dark, empty centres — big numerals are drawn
  onto them by the game. They are the same size and style.
- BODY BAND: the area between the art window and the bottom edge, inset from
  the side rails — a quiet parchment-toned panel for rules text, from 58% to
  88% of the height. Low contrast, no ornament inside it.
- The bottom-centre of the body band's lower edge has a small empty oval
  socket, about 10% of the card width, for a rarity gem.

STYLE: weighty and physical, like a forged object you could hold. Crisp edges
suitable for 2048px game UI. Ornament lives on the rails and corners, never
inside the content regions. No text anywhere, no watermark.
"""

VARIANTS = {
    "IronEmber": """
Dark forged iron frame, hammered texture on the rails. Thin ember-orange
molten seams glow faintly in the recesses of the ornament. Corner bosses are
riveted iron rings. The name band is soot-dark steel. The body band is warm
charcoal parchment. Palette: near-black iron, dim ember orange, warm grey.
""",
    "AgedGilt": """
Aged bronze-gold frame, worn gilding over dark wood rails. Corner bosses are
tarnished bronze medallions with a rope-braid rim. The name band is dark
leather. The body band is aged parchment, tea-stained. Palette: antique gold,
deep umber, parchment cream.
""",
    "CarvedStone": """
Carved grey stone frame, like the ruined castle blocks of a battlefield -
weathered granite rails with chiselled edge bevels, small chips and cracks.
Corner bosses are carved stone rings with a faint ember glow deep in their
recess. The name band is a darker stone lintel. The body band is pale sandy
stone. Palette: cool granite grey, sandy tan, dim ember accent.
""",
    "DarkGilt": """
Near-black lacquered frame with restrained gold filigree tracing the rails,
elegant and minimal. Corner bosses are black discs rimmed with a fine gold
ring. The name band is matte black. The body band is deep charcoal with a
faint gold-dusted texture. Palette: black, muted gold, charcoal.
""",
}


def generate(name: str, body: str, key: str) -> bool:
    prompt = (COMMON + "\nMATERIAL AND PALETTE:\n" + body).strip()
    print(f"[{name}] requesting {SIZE} ...", flush=True)
    t0 = time.time()
    try:
        r = requests.post(
            "https://api.openai.com/v1/images/generations",
            headers={"Authorization": f"Bearer {key}"},
            json={
                "model": MODEL,
                "prompt": prompt,
                "size": SIZE,
                "quality": "high",
                "n": 1,
            },
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
    dest = OUT / f"Frame_{name}.png"
    dest.write_bytes(raw)
    print(f"[{name}] -> {dest.name}  ({len(raw)/1e6:.1f} MB, {time.time()-t0:.0f}s)", flush=True)
    return True


def main() -> int:
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        print("OPENAI_API_KEY not set", file=sys.stderr)
        return 2

    names = [a for a in sys.argv[1:]] or list(VARIANTS)
    for n in names:
        if n not in VARIANTS:
            print(f"unknown variant '{n}' (have: {', '.join(VARIANTS)})", file=sys.stderr)
            return 2

    OUT.mkdir(parents=True, exist_ok=True)
    ok = 0
    for name in names:
        if generate(name, VARIANTS[name], key):
            ok += 1

    print(f"\n{ok}/{len(names)} generated -> {OUT}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
