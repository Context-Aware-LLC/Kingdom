"""Round 3 — keep the compositions, fix the rings.

Sam on round 2 (2026-07-23):
  "Keep them the way they are, put the rings back flat, just make them straight
   on. And make them significantly bigger than they are in the second run, and
   somewhat bigger than they are in the first run."
  "If we just fill up more of it with portrait, we don't have to worry quite as
   much about what's going on in the background."

So: same scenes, no tilt, and rings sized to the new Ø1150 slot — 53% of image
height, up from 42%. Tilt is dropped outright; round 2's angled rings turned the
aperture into a narrow ellipse and cost most of the portrait area.

Seeds are the two front runners: Versus2b_KeepRight and Versus2_CausewayAsym.
"""

import base64
import os
import pathlib
import sys
import time

import requests

ROOT = pathlib.Path(__file__).resolve().parents[2]
SRC = ROOT / "art-bakeoff" / "versus"
OUT = SRC / "round3"
SIZE = "3696x1584"
MODEL = "gpt-image-2"

# Ø1150 of 2160 canvas height = 53%. Centres at +/-850 of 5040 => 33.1% and 66.9%
# of width, at y 760/2160 = 35% height.
RINGS = """
THE TWO STONE RINGS ARE THE SUBJECT OF THIS IMAGE. Make them MUCH LARGER than in
the reference — each ring must be OVER HALF THE TOTAL IMAGE HEIGHT across,
dominating the composition. They are the biggest objects in the frame. If they
look modest, they are wrong.

Each ring faces STRAIGHT ON, flat and square to the viewer, perfectly circular —
NO tilt, NO angle, NO perspective turn, NO oval or elliptical shapes. Two flat
circular stone rings seen face-on.

Their carved stone bezel is SLENDER — a narrow rim — so the dark opening inside
is as wide as possible. The inside of each ring is FLAT, DARK and COMPLETELY
EMPTY: no scene, no face, no glow, nothing. A plain black void.

Position: one ring centred a third of the way across from the left edge, the
other a third from the right, both centred slightly ABOVE the vertical middle of
the image. They sit high, not low.
"""

BASE = """
Recreate the reference painting as an ultra-wide 21:9 cinematic matte painting,
dark fantasy, painterly — KEEPING its composition, its structures, its layout and
its mood almost exactly as they are. Do not redesign the scene. %s

%s

KEEP IT DARK. Night lit by embers. The distant horizon is a DIM smouldering ember
line, low and muted. No bright sunset, no glowing sky, no blown-out light. The
centre of the image stays dark — heavy cloud and haze, no light source behind it.

QUIET LOWER THIRD: the bottom third is low contrast — ash, haze, dark earth, a few
scattered embers. No hard edges or bright highlights there.

The left and right halves must NOT mirror each other, but must carry equal visual
weight. No people, no characters, no faces, no creatures. No text, letters,
numbers, runes, logos or interface elements. No elemental theming.
"""

VARIANTS = {
    "KeepRight": ("Versus2b_KeepRight.png", str(SRC / "composite"),
                  "Keep the tall ruined fortress on the RIGHT with its banners, the low broken "
                  "gatehouse arch on the LEFT, and the cracked causeway running away between them."),
    "CausewayAsym": ("Versus2_CausewayAsym.png", str(SRC / "round2"),
                     "Keep the tall gothic ruin on the LEFT, the lower sprawling ruin on the "
                     "RIGHT, the causeway down the centre and the scattered standing pillars."),
    "KeepRightWider": ("Versus2b_KeepRight.png", str(SRC / "composite"),
                       "Keep the fortress on the RIGHT and the broken arch on the LEFT, but pull "
                       "the camera back a little so there is more open sky above and the two "
                       "structures sit lower in the frame, leaving the upper middle clear."),
}


def generate(name: str, ref: str, folder: str, clause: str, key: str) -> bool:
    prompt = (BASE % (clause, RINGS)).strip()
    print(f"[{name}] ref={ref} -> {SIZE}", flush=True)
    t0 = time.time()

    path = pathlib.Path(folder) / ref
    with open(path, "rb") as fh:
        files = [("image[]", (ref, fh, "image/png"))]
        try:
            r = requests.post(
                "https://api.openai.com/v1/images/edits",
                headers={"Authorization": f"Bearer {key}"},
                data={"model": MODEL, "prompt": prompt, "size": SIZE,
                      "quality": "high", "n": "1"},
                files=files,
                timeout=900,
            )
        except requests.RequestException as e:
            print(f"[{name}] FAILED (network): {e}", flush=True)
            return False

    if r.status_code != 200:
        print(f"[{name}] FAILED {r.status_code}: {r.text[:500]}", flush=True)
        return False

    d = r.json()["data"][0]
    raw = base64.b64decode(d["b64_json"]) if "b64_json" in d else requests.get(d["url"], timeout=180).content
    dest = OUT / f"Versus3_{name}.png"
    dest.write_bytes(raw)
    print(f"[{name}] -> {dest.name}  ({len(raw)/1e6:.1f} MB, {time.time()-t0:.0f}s)", flush=True)
    return True


def main() -> int:
    key = os.environ.get("OPENAI_API_KEY")
    if not key:
        print("OPENAI_API_KEY not set", file=sys.stderr)
        return 2

    OUT.mkdir(parents=True, exist_ok=True)
    ok = 0
    for name, (ref, folder, clause) in VARIANTS.items():
        if generate(name, ref, folder, clause, key):
            ok += 1

    print(f"\n{ok}/{len(VARIANTS)} generated -> {OUT}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
