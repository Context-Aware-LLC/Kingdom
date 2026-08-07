"""Generate collection-screen (S6/S9) plate candidates.

Per hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/collection-plate.md §6: direct OpenAI API (gpt-image-2), NOT the Unity
wrapper — the wrapper silently drops width/height and caps at 1024 square.

Native 21:9 in one pass at 3696x1584 (multiple of 16, longest edge <= 3840,
aspect <= 3:1). Upscale + Lanczos down to the 5040x2160 master happens later.

This plate is unlike the other three: it has NO focal region and NO registered
feature. The canvas crops horizontally while the UI is derived from the viewport,
so no column of the canvas has a fixed relationship to anything on screen — the
sidebar band lands on canvas x 672-1242 at 16:9 and x 1152-1692 at 4:3. Only the
vertical axis is stable. Hence: horizontal bands, featureless horizontal axis.

The binder itself (spine, page bed, page edges) is UI Toolkit chrome, NOT art.
Do not let a candidate paint a book.
"""

import base64
import os
import pathlib
import sys
import time

import requests

OUT = pathlib.Path(__file__).resolve().parents[2] / "art-pipeline" /"ui" / "collection" / "background"
SIZE = "3696x1584"
MODEL = "gpt-image-2"

# Constraints every candidate must satisfy — hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/collection-plate.md §4.3.
COMMON = """
ULTRA-WIDE 21:9 painterly matte painting, dark fantasy interior, atmospheric and
quiet. No people, no characters, no faces, no creatures anywhere. No books, no
tomes, no ledgers, no open binders, no cards, no scrolls. Absolutely no text,
letters, numerals, runes, heraldry, banners, logos or interface elements.

THIS IS A BACKGROUND FIELD, NOT A PICTURE — game UI covers most of it:
- There is NO subject and NO focal point. Nothing for the eye to land on.
- COMPOSE IN HORIZONTAL BANDS ONLY: a floor or ground plane across the bottom, a
  quiet middle field, and an upper region that carries the light. Every band runs
  edge to edge, left to right, unbroken.
- The LEFT-TO-RIGHT axis must be FEATURELESS. No feature, mass, structure or
  bright accent that sits at one side rather than the other. If you covered the
  left half or the right half, nothing important would be lost.
- The middle of the frame vertically — from 13% to 97% of the height — is CALM:
  no hard edges, no silhouettes, no bright highlights, no strong contrast. Detail
  there lives in texture, dust and haze only. Compressed, dark value range.
- Colour is painted edge to edge, full bleed. No vignette, no dark corners, no
  border, no framing device.

DEPTH IS THE WHOLE JOB. With no subject and no focal point, the only thing that
separates this from a flat gradient is real atmospheric depth: layered haze,
distance falloff, light that comes from somewhere and falls off across the room.

PALETTE: ashy warm-biased near-black ground, low scattered orange ember glow near
the floor, cool teal-blue light high above, faint warm parchment reflections. Dark
overall — this sits behind bright card faces.
"""

VARIANTS = {
    "Archive": """
The interior of a vast vaulted stone archive, seen down its length. Empty carved
stone shelving bays recede into deep haze on both sides at exactly the same rate,
so neither side leads. Low warm lamplight pools along the stone floor; cold pale
daylight falls from a high clerestory far above and never reaches the floor. Dust
hangs in the air in horizontal layers.
""",
    "Hearth": """
A great hall at night, long and low, seen straight down its axis. A banked fire
somewhere out of frame throws a warm glow across a dark stone floor. Heavy timber
beams cross overhead in horizontal courses, lost in shadow. Cold moonlight enters
from high windows above and stops well short of the ground. Smoke drifts in flat
horizontal strata.
""",
    "Vault": """
A deep stone reliquary vault. The walls are courses of empty carved niches running
in unbroken horizontal rows into haze on both sides. A cold shaft of pale light
falls from an unseen opening high overhead; low ember braziers glow faintly at
floor level. Wet dark stone, mineral bloom, still air.
""",
    "Terrace": """
An open stone terrace at dusk, seen from under a deep colonnade so the sky is a
wide horizontal band across the upper frame. Below it a flat sea of low cloud and
ash stretches to a level horizon. The stone floor in the foreground is dark and
worn. Embers drift up slowly from below the terrace edge. Cool teal sky, a thin
warm horizon line, everything else near-black.
""",
}


def generate(name: str, body: str, key: str) -> bool:
    prompt = (COMMON + "\nSCENE:\n" + body).strip()
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

    # Never overwrite a previous candidate — land beside it.
    n = 1
    while (dest := OUT / f"Collection_{name}_{n}.png").exists():
        n += 1
    dest.write_bytes(raw)
    print(f"[{name}] -> {dest.name}  ({len(raw)/1e6:.1f} MB, {time.time()-t0:.0f}s)", flush=True)
    return True


def main() -> int:
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        print("OPENAI_API_KEY not set", file=sys.stderr)
        return 2

    wanted = sys.argv[1:] or list(VARIANTS)
    unknown = [w for w in wanted if w not in VARIANTS]
    if unknown:
        print(f"unknown variant(s): {', '.join(unknown)}", file=sys.stderr)
        print(f"available: {', '.join(VARIANTS)}", file=sys.stderr)
        return 2

    OUT.mkdir(parents=True, exist_ok=True)
    ok = 0
    for name in wanted:
        if generate(name, VARIANTS[name], key):
            ok += 1

    print(f"\n{ok}/{len(wanted)} generated -> {OUT}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
