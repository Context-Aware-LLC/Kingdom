"""Round 2 — reference-driven versus plates.

Round 1 (gen_versus_plates.py) produced four candidates. Sam's read:
  - MIRRORING IS DISQUALIFYING. All four came back as near-perfect reflections.
  - Broken Causeway: the causeway, the centre pillars and the ring STYLE are all
    right. Rings are too thick — want a bigger opening for the portrait, or a
    thinner bezel around it.
  - Storm Spires: the rings are tilted on the x-axis, angled rather than face-on.
    Keep that.
  - Twin Keeps + Ash Basin: preserved as-is under keep/.
  - Claude's finding: three of four put a bright horizon behind the orb well. The
    orb glows and pulses; it needs a DARK backing, not merely a calm one.

So round 2 feeds round-1 plates back through /v1/images/edits as visual
references and asks for the fixes. Same 3696x1584 target.
"""

import base64
import os
import pathlib
import sys
import time

import requests

ROOT = pathlib.Path(__file__).resolve().parents[2]
SRC = ROOT / "art-bakeoff" / "versus"
OUT = SRC / "round2"
SIZE = "3696x1584"
MODEL = "gpt-image-2"

# Every round-2 plate must fix these. Round 1 failed on all three.
FIXES = """
BREAK THE MIRROR. The left and right halves must NOT be reflections of each
other. Give each side a different silhouette, different ruin, different scale and
a different number of shapes — while keeping the two halves in EQUAL visual
weight so neither player's side looks favoured. Balanced, never mirrored. This is
the most important instruction here.

THE CENTRE MUST BE DARK. The exact middle of the image, from 45% to 55% of the
width around the vertical centre line, is a DARK empty pocket of air — deep
shadow, dark cloud or dark haze. Do NOT place a bright sunset, a glowing horizon
or any light source behind the centre. Any warm horizon glow must be pushed low
and off to one side.

THE RINGS: two large stone portal rings, one left of centre, one right of centre,
each about a quarter of the image height across. The stone bezel is SLENDER —
a narrow carved rim, not a thick wall — so the dark empty opening inside is as
large as possible. Each ring is turned slightly on its vertical axis so it faces
inward at an angle rather than square-on to the viewer, and the two are turned in
OPPOSITE directions, each angled toward the centre. The inside of each ring is
FLAT, DARK and COMPLETELY EMPTY — no scene, no face, nothing.

QUIET LOWER THIRD: from 62% to 91% of the height, low contrast — ash, haze, dark
earth. No hard edges or bright highlights there.

No people, no characters, no faces, no creatures. No text, letters, numbers,
runes, logos or interface elements. No elemental theming: no ocean, no forest, no
single fire deity. Ultra-wide 21:9 cinematic matte painting, dark fantasy,
painterly. Ashy warm-biased near-black ground, scattered ember glow, cold teal
storm sky.
"""

VARIANTS = {
    # The composition Sam liked most in content: causeway + centre pillars.
    "CausewayAsym": (
        ["Versus_BrokenCauseway.png"],
        """
Keep this painting's broken stone causeway running away from the viewer into deep
distance, and keep the row of shattered pillars flanking it — those are the best
part of the reference. Keep the ring style: ornate carved stone, but SLENDER.

Now rebuild everything else asymmetrically. On one side put a tall collapsed
gatehouse; on the other, a lower sprawling ruin of a different shape and mass.
Different heights, different silhouettes, equal weight. Push the warm horizon glow
low and away from the vertical centre so the middle of the sky stays dark.
""",
    ),
    # Same source, more aggressive restructure.
    "CausewayRuins": (
        ["Versus_BrokenCauseway.png", "Versus_StormSpires.png"],
        """
Take the causeway and flanking pillars from the first reference, and the angled
tilt of the stone rings from the second.

Build a deliberately uneven scene: one side is a huge fractured cliff wall with a
single leaning tower, the other is an open field of low broken arches receding
into haze. The two sides must not echo each other's shapes at all. Keep them in
equal visual weight. The sky directly above the causeway's vanishing point is
dark, heavy cloud — no glowing horizon in the centre.
""",
    ),
    # Twin Keeps' sense of place, fixed.
    "KeepsAsym": (
        ["Versus_TwinKeeps.png"],
        """
Keep this painting's mood, palette, banners and its sense of two great powers
facing each other across a valley.

Rebuild the two strongholds so they are clearly DIFFERENT buildings: one a tall
narrow spire-fortress crowning a rock, the other a broad low bastion sprawling
along a ridge — different heights, different silhouettes, different amounts of
detail, but equal visual weight. Vary the banners between the sides too.

Move the warm horizon glow low and to one side. The centre of the sky must be
dark cloud so nothing bright sits in the middle of the frame.
""",
    ),
}


def generate(name: str, refs, body: str, key: str) -> bool:
    prompt = (FIXES + "\nSCENE:\n" + body).strip()
    print(f"[{name}] refs={refs} -> {SIZE}", flush=True)
    t0 = time.time()

    files = []
    handles = []
    try:
        for ref in refs:
            fh = open(SRC / ref, "rb")
            handles.append(fh)
            files.append(("image[]", (ref, fh, "image/png")))

        data = {"model": MODEL, "prompt": prompt, "size": SIZE, "quality": "high", "n": "1"}
        try:
            r = requests.post(
                "https://api.openai.com/v1/images/edits",
                headers={"Authorization": f"Bearer {key}"},
                data=data,
                files=files,
                timeout=900,
            )
        except requests.RequestException as e:
            print(f"[{name}] FAILED (network): {e}", flush=True)
            return False
    finally:
        for fh in handles:
            fh.close()

    if r.status_code != 200:
        print(f"[{name}] FAILED {r.status_code}: {r.text[:500]}", flush=True)
        return False

    d = r.json()["data"][0]
    raw = base64.b64decode(d["b64_json"]) if "b64_json" in d else requests.get(d["url"], timeout=180).content
    dest = OUT / f"Versus2_{name}.png"
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
    for name, (refs, body) in VARIANTS.items():
        if generate(name, refs, body, key):
            ok += 1

    print(f"\n{ok}/{len(VARIANTS)} generated -> {OUT}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
