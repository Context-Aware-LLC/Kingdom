"""Generate board-background template candidates.

Same pipeline as gen_versus_plates.py: direct OpenAI API (gpt-image-2), NOT the
Unity wrapper (it silently drops width/height and caps at 1024 square).

The board camera is orthographic size 4.6 -> 9.2 world units tall, up to +-10.2
wide at 20:9 (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md 1.1). Native generation at 3696x1664 (~2.22:1,
multiple of 16, longest edge <= 3840). Narrower aspects crop the sides, so all
required decoration lives inside the 16:9 span and the outer flanks are bonus.

Brief (Sam, 2026-07-24): near-flat neutral centre - gray primary, sandy/tan
stipple accent. Perimeter: ruined-castle blocks, smoldering boulders, burnt
tree stumps. Very few graphical elements.

Usage: gen_board_plates.py [variant ...] [WxH]
  No args: all variants at the default bakeoff size (3696x1664, ~20:9).
  The chosen template regenerates at 3696x2080 (16:9) so the decorated ring
  survives the 16:9 crop; build_board_master.py then adds the ultrawide bleed.
"""

import base64
import os
import pathlib
import sys
import time

import requests

OUT = pathlib.Path(__file__).resolve().parents[2] / "art-pipeline" / "ui" / "board" / "background"
DEFAULT_SIZE = "3696x1664"
MODEL = "gpt-image-2"

# The centre is a play surface: cards, minions and text render on top of it.
COMMON = """
Top-down overhead view, looking straight down at a flat battlefield ground
surface. This is a game-board background texture, painterly dark fantasy,
matte and understated. No people, no creatures, no faces. Absolutely no text,
letters, numbers, runes, logos or interface elements. No perspective depth --
a flat plane seen from directly above.

COMPOSITION IS A CONTRACT -- game cards and UI are drawn over this image:
- The middle 70% of the image, horizontally and vertically, is an almost-flat,
  even, LOW-CONTRAST field: matte neutral gray as the primary color with a
  fine sandy-tan stipple and dust speckle as the accent. Subtle tonal
  variation only -- no hard shapes, no bright highlights, no dark pits, no
  cracks that draw the eye. It must read as calm empty ground.
- Decoration lives ONLY around the outer edges and corners of the image, and
  it is SPARSE: a handful of simple elements, not a busy border.
- Edge decoration is low-contrast and desaturated so it never competes with
  the cards drawn above it. Faint warm ember glow is allowed inside the
  smoldering elements, dim and small.

PALETTE: neutral ash gray dominant, warm sandy tan accent in the stipple,
charcoal black in the burnt elements, small dim orange ember accents. No
green, no blue, no purple.
"""

VARIANTS = {
    "QuarryFloor": """
The ground is smooth packed stone-dust, like a quarry floor swept flat. Around
the perimeter: a few large squared castle blocks, tumbled and cracked, lying
half-sunk at the corners and along the left and right edges. Two or three
blocks per side at most. A faint drift of sand collects against them.
""",
    "AshCourtyard": """
The ground is a ruined courtyard: the ghost of huge worn flagstones, so faded
and dust-covered they are barely visible. Along the edges: short broken runs
of collapsed castle wall, two or three courses of stone high, crumbled to
rubble at their ends. One charred tree stump near one corner. Thin ash dust
over everything.
""",
    "ScorchedField": """
The ground is dry scorched earth, gray with sandy patches, swept nearly flat.
Around the edges: a few smoldering rounded boulders with dim ember cracks, and
two or three burnt tree stumps, charcoal black and broken short. Small drifts
of ash. The elements sit alone with wide empty space between them.
""",
    "EmberRing": """
The ground is bare tamped gray earth with a sandy stipple. The perimeter
suggests an old fortification ring long destroyed: at each corner a single
massive ruined block or shattered column base, charred at the base, with a
faint bed of dim embers glowing in the rubble beneath it. Between the corners
the edges stay almost empty.
""",
}


def generate(name: str, body: str, key: str, size: str) -> bool:
    prompt = (COMMON + "\nSCENE:\n" + body).strip()
    print(f"[{name}] requesting {size} ...", flush=True)
    t0 = time.time()
    try:
        r = requests.post(
            "https://api.openai.com/v1/images/generations",
            headers={"Authorization": f"Bearer {key}"},
            json={
                "model": MODEL,
                "prompt": prompt,
                "size": size,
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
    dest = OUT / (f"Board_{name}.png" if size == DEFAULT_SIZE else f"Board_{name}_{size}.png")
    dest.write_bytes(raw)
    print(f"[{name}] -> {dest.name}  ({len(raw)/1e6:.1f} MB, {time.time()-t0:.0f}s)", flush=True)
    return True


def main() -> int:
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        print("OPENAI_API_KEY not set", file=sys.stderr)
        return 2

    size = DEFAULT_SIZE
    names = []
    for arg in sys.argv[1:]:
        if "x" in arg and arg.replace("x", "").isdigit():
            size = arg
        elif arg in VARIANTS:
            names.append(arg)
        else:
            print(f"unknown variant '{arg}' (have: {', '.join(VARIANTS)})", file=sys.stderr)
            return 2
    if not names:
        names = list(VARIANTS)

    OUT.mkdir(parents=True, exist_ok=True)
    ok = 0
    for name in names:
        if generate(name, VARIANTS[name], key, size):
            ok += 1

    print(f"\n{ok}/{len(names)} generated -> {OUT}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
