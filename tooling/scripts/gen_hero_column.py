"""Generate the hero column frame — ONE plate holding BOTH the weapon and the hero power.

Sam's call (2026-07-25): the weapon and the hero power are not two separate discs,
they are one image. The plate drops into the existing two-disc column budget from
ProceduralBoardSetup (x -6.08..-4.98, 0.9 wide; WeaponY 3.10 over HeroPowerY 4.00,
1.8 tall) — so it is generated at aspect 0.5 and nothing else on the bottom row moves.

Same pipeline as gen_card_frames.py / gen_board_plates.py: direct OpenAI API
(gpt-image-2), pure-black regions cut to transparency afterwards.

BLACK MEANS CUT. Two things are painted pure black and both become transparent:
the two round sockets (the weapon illustration and the hero-power icon are drawn
BEHIND the plate and show through) and everything outside the plate's silhouette.
That is what puts the stat bosses half over the weapon art and half over the
plate — the boss is part of the frame, so it occludes the art it overlaps.

Variants cross material (IronEmber / CarvedStone) with whether the hero-power
socket carries a cost boss. Sam's 2026-07-23 board ruling says icon-only hero
power, but the mana cost is genuinely at-a-glance information, so both readings
go to the bake-off rather than being decided here.

Usage: gen_hero_column.py [variant ...]
"""

import base64
import itertools
import os
import pathlib
import sys
import time

import requests

OUT = pathlib.Path(__file__).resolve().parents[2] / "art-pipeline" / "ui" / "board" / "hp-frames"
SIZE = "1024x2048"          # aspect 0.5 — the 0.9 x 1.8 world-unit column
FALLBACK_SIZE = "1024x1536"  # if the API rejects 0.5; crop/letterbox after
MODEL = "gpt-image-2"

# Socket + boss geometry is a CONTRACT — WeaponDiscView fits the art and the
# digit labels to these fractions, so candidates must be comparable.
COMMON = """
A fantasy game-UI FRAME plate, tall and narrow (twice as tall as it is wide),
painterly dark-fantasy. This is an EMPTY frame: no text, no letters, no numbers,
no creature, no illustration, no icons. Ornamental chrome only.

The plate is a single forged vertical bracket holding TWO round openings stacked
one above the other, like two rings welded into one strut. It reads as ONE
object, not two separate discs — the metal is continuous between the rings.

COMPOSITION IS A CONTRACT — game code draws content into these regions:
- UPPER SOCKET: a perfect circle centred at 50% width, 27% height (from the
  top), about 76% of the plate's width across. Fill it with SOLID PURE BLACK
  (#000000), perfectly flat. A raised ornamental rim rings it.
- LOWER SOCKET: an identical circle centred at 50% width, 73% height, same
  diameter, same rim, also SOLID PURE BLACK.
- ATTACK BOSS: a round raised medallion about 30% of the plate's width across,
  centred at 23% width / 40% height — so it sits ON the upper socket's rim at
  its lower-left, half over the socket opening and half over the plate metal.
  It OVERLAPS and occludes the rim. Plain, dark, EMPTY centre.
- DURABILITY BOSS: the same medallion mirrored, centred at 77% width / 40%
  height, on the upper socket's lower-right rim. Plain, dark, EMPTY centre.
  Both bosses are the same size and style; big numerals are drawn onto them by
  the game, so their centres must be flat, dark and free of ornament.
- EVERYTHING OUTSIDE the plate's silhouette is SOLID PURE BLACK (#000000),
  perfectly flat, right to the image edges — it is cut to transparency. Do not
  paint a background, a scene, a glow, a shadow or a vignette outside the plate.

STYLE: weighty and physical, like a forged object you could hold. Crisp edges
suitable for 2048px game UI. Ornament lives on the strut and the rims, never
inside the sockets or on the boss centres. No text anywhere, no watermark.
"""

COST_BOSS = """
- COST BOSS: one more round medallion, the same size and style as the other two,
  centred at 23% width / 86% height — on the LOWER socket's rim at its
  lower-left, half over the socket and half over the plate. Plain, dark, EMPTY
  centre. The lower socket's lower-RIGHT has no boss; leave that rim clean.
"""

MATERIALS = {
    "IronEmber": """
Dark forged iron, hammered texture on the strut. Thin ember-orange molten seams
glow faintly in the recesses of the ornament. The socket rims are riveted iron
rings; the bosses are iron discs with a raised rivet edge. Palette: near-black
iron, dim ember orange, warm grey.
""",
    "CarvedStone": """
Carved grey stone, like the ruined castle blocks of a battlefield — weathered
granite with chiselled edge bevels, small chips and cracks. The socket rims are
carved stone rings with a faint ember glow deep in their recess; the bosses are
chiselled stone discs. Palette: cool granite grey, sandy tan, dim ember accent.
""",
}

COSTS = {"nocost": "", "cost": COST_BOSS}


def build_prompt(material: str, cost_key: str) -> str:
    return (COMMON + COSTS[cost_key] + "\nMATERIAL AND PALETTE:\n" + MATERIALS[material]).strip()


def request_image(prompt: str, size: str, key: str):
    return requests.post(
        "https://api.openai.com/v1/images/generations",
        headers={"Authorization": f"Bearer {key}"},
        json={"model": MODEL, "prompt": prompt, "size": size, "quality": "high", "n": 1},
        timeout=600,
    )


def generate(name: str, prompt: str, key: str) -> bool:
    print(f"[{name}] requesting {SIZE} ...", flush=True)
    t0 = time.time()
    size = SIZE
    try:
        r = request_image(prompt, size, key)
        # 0.5 is an unusual aspect; fall back rather than lose the run.
        if r.status_code != 200 and "size" in r.text.lower():
            size = FALLBACK_SIZE
            print(f"[{name}] {SIZE} rejected, retrying at {size} ...", flush=True)
            r = request_image(prompt, size, key)
    except requests.RequestException as e:
        print(f"[{name}] FAILED (network): {e}", flush=True)
        return False

    if r.status_code != 200:
        print(f"[{name}] FAILED {r.status_code}: {r.text[:400]}", flush=True)
        return False

    data = r.json()["data"][0]
    raw = base64.b64decode(data["b64_json"]) if "b64_json" in data else requests.get(data["url"], timeout=120).content
    dest = OUT / f"HeroColumn_{name}.png"
    dest.write_bytes(raw)
    print(f"[{name}] -> {dest.name}  ({len(raw)/1e6:.1f} MB, {size}, {time.time()-t0:.0f}s)", flush=True)
    return True


def main() -> int:
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        print("OPENAI_API_KEY not set", file=sys.stderr)
        return 2

    all_variants = {
        f"{m}_{c}": build_prompt(m, c)
        for m, c in itertools.product(MATERIALS, COSTS)
    }

    names = sys.argv[1:] or list(all_variants)
    for n in names:
        if n not in all_variants:
            print(f"unknown variant '{n}' (have: {', '.join(all_variants)})", file=sys.stderr)
            return 2

    OUT.mkdir(parents=True, exist_ok=True)
    ok = 0
    for name in names:
        if generate(name, all_variants[name], key):
            ok += 1

    print(f"\n{ok}/{len(names)} generated -> {OUT}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
