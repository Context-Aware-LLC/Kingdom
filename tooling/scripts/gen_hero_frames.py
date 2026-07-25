"""Generate the hero-power + weapon frame PAIR as one landscape piece, to be split.

Sam's spec (2026-07-25, revising gen_hero_column.py):
  - The hero power owns its own frame; the weapon frame DOCKS onto it when a
    weapon is played, so the weapon piece must be able to appear and vanish
    without the hero-power piece looking wrong.
  - Each frame is 3/4 of the hero plate: 1.8 * 0.75 = 1.35 world units, square.
  - "The image will be close to that" — the socket nearly fills the frame. Thin
    rim, almost no chrome.
  - Attack and durability ride the LEFT and RIGHT of the weapon socket, half in
    and half out of the rim (Sam, revising the top/bottom placement of the first
    landscape pass).
  - Generate as ONE piece and split it ourselves (split_hero_frames.py).

TWO HARD CONSTRAINTS learned from the first pass:

1. NO BRIDGING ORNAMENT. The first attempt was a single strut with two rings
   welded into it. Split down the middle, the hero-power half kept a severed stub
   where the strut used to be — fine when the weapon is docked, broken when it
   is not. So the two rings are independent and self-contained; they touch at the
   seam and nothing crosses it.

2. NO FILIGREE. Sam, same day: "that level of ornate detail isn't going to
   translate, almost ever, for us." At ortho size 4.6 a 1.35-unit frame is ~158 px
   on a 1080p screen. Ornament below a few px of stroke width dissolves into
   noise. So: heavy rims, few large shapes, strong value contrast, nothing small.

Geometry is a CONTRACT (fractions of the full 2:1 image, top-left origin):
  left socket   centre (0.25, 0.50)  diameter 0.36 W  = hero power
  right socket  centre (0.75, 0.50)  diameter 0.36 W  = weapon
  attack boss   centre (0.57, 0.50)  diameter 0.11 W  — on the weapon rim, left
  durability    centre (0.93, 0.50)  diameter 0.11 W  — on the weapon rim, right
The weapon socket's radius is 0.18 W, so its rim tangents sit at x 0.57 and 0.93 —
exactly where the bosses are centred, putting each half on the art and half on
the metal.

BOSS SIZE IS LOAD-BEARING. At 0.13 W the inboard boss would reach x 0.505, ten
pixels off the x 0.50 split line — one bad generation away from being shaved in
half by the cut. 0.11 W buys ~30 px of clearance at both the seam and the right
edge. Do not grow it back without moving the sockets inward first.

Usage: gen_hero_frames.py [variant ...]
"""

import base64
import os
import pathlib
import sys
import time

import requests

OUT = pathlib.Path(__file__).resolve().parents[2] / "art-bakeoff" / "heroframes"
SIZE = "2048x1024"
MODEL = "gpt-image-2"

COMMON = """
A fantasy game-UI FRAME asset, landscape, exactly twice as wide as it is tall.
Painterly dark-fantasy. This is an EMPTY frame pair: no text, no letters, no
numbers, no creature, no illustration, no icons. Chrome only.

It shows TWO SEPARATE ROUND FRAMES sitting side by side, like two heavy forged
rings laid on a table, just touching where they meet in the middle. CRITICAL:
each ring is COMPLETE AND CLOSED on its own, a full unbroken circle of metal.
NOTHING connects, bridges, welds or overlaps between the two rings — no strut, no
bar, no shared ornament, no chain. They only touch. The image must be able to be
cut straight down the vertical centre line leaving two finished, whole rings.

KEEP IT BOLD AND SIMPLE. This is rendered small on screen. Use FEW LARGE SHAPES,
a THICK heavy rim, and STRONG light-to-dark contrast. NO filigree, NO fine
tracery, NO small ornament, NO tiny rivets, NO thin scrollwork, NO spikes or
points. Fine detail is worse than no detail here.

COMPOSITION IS A CONTRACT — game code draws content into these regions:
- LEFT SOCKET: a perfect circle centred at 25% width, 50% height, its diameter
  36% of the image WIDTH (which is 72% of the image height). Fill it with SOLID
  PURE BLACK (#000000), perfectly flat. The rim around it is thick and plain.
- RIGHT SOCKET: an identical circle centred at 75% width, 50% height. Same
  diameter, same thick plain rim, also SOLID PURE BLACK.
- LEFT BOSS: a round raised medallion, diameter 11% of the image width, centred at
  57% width / 50% height — so it sits ON the right ring's rim at its leftmost
  point (9 o'clock), half over the socket opening and half over the rim metal. It
  OVERLAPS and occludes the rim. Plain, dark, EMPTY, flat centre.
- RIGHT BOSS: the same medallion centred at 93% width / 50% height, on the right
  ring's rim at its rightmost point (3 o'clock). Plain, dark, EMPTY, flat centre.
  The two bosses face each other across the ring, level with its centre. Neither
  one touches the left ring or runs off the image edge.
  Big numerals are drawn onto both bosses by the game, so their centres must be
  flat, dark and completely free of ornament. The LEFT ring has NO bosses at all;
  its rim is clean all the way round.
- EVERYTHING ELSE is SOLID PURE BLACK (#000000), perfectly flat, right to the
  image edges — it is cut to transparency. No background, no scene, no glow, no
  shadow, no vignette anywhere outside the two rings.

STYLE: weighty and physical, like a forged object you could hold. Crisp edges.
No text anywhere, no watermark.
"""

MATERIALS = {
    "IronEmber": """
Dark forged iron, hammered. A few broad ember-orange molten seams glow in the
rim — thick and bold, not hairlines. The bosses are plain dark iron discs with a
simple raised edge. Palette: near-black iron, strong ember orange, warm grey.
""",
    "CarvedStone": """
Carved pale granite, weathered, with big chiselled bevels and a couple of large
chips. A faint broad ember glow deep in the rim's inner recess. The bosses are
plain chiselled stone discs. Palette: pale granite grey, sandy tan, dim ember.
""",
}


def generate(name: str, key: str) -> bool:
    prompt = (COMMON + "\nMATERIAL AND PALETTE:\n" + MATERIALS[name]).strip()
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
    dest = OUT / f"HeroFrames_{name}.png"
    dest.write_bytes(raw)
    print(f"[{name}] -> {dest.name}  ({len(raw)/1e6:.1f} MB, {time.time()-t0:.0f}s)", flush=True)
    return True


def main() -> int:
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        print("OPENAI_API_KEY not set", file=sys.stderr)
        return 2

    names = sys.argv[1:] or list(MATERIALS)
    for n in names:
        if n not in MATERIALS:
            print(f"unknown variant '{n}' (have: {', '.join(MATERIALS)})", file=sys.stderr)
            return 2

    OUT.mkdir(parents=True, exist_ok=True)
    ok = sum(1 for n in names if generate(n, key))
    print(f"\n{ok}/{len(names)} generated -> {OUT}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
