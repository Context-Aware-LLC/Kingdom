"""Round 2b — the composite Sam called: a keep where one arch stands.

"If they could take one of the keeps from the Twin Keeps picture and put it where
the arch is, on one side of Broken Causeway, we'd have a winner." (Sam, 2026-07-23)

Broken Causeway supplies the causeway, the flanking pillars and the carved ring
style. Twin Keeps supplies a fortress, which replaces the gatehouse on ONE side.
That asymmetry is the point: keep on one side, arch on the other, so the mirror
breaks by construction rather than by asking for it.

Also per Sam: tone the horizon down. Round 1 blew out the centre with a sunset.
"""

import base64
import os
import pathlib
import sys
import time

import requests

ROOT = pathlib.Path(__file__).resolve().parents[2]
SRC = ROOT / "art-pipeline" / "ui" / "templates" / "versus"
OUT = SRC / "composite"
SIZE = "3696x1584"
MODEL = "gpt-image-2"

REFS = ["Versus_BrokenCauseway.png", "Versus_TwinKeeps.png"]

BASE = """
Combine the two reference paintings into one new ultra-wide 21:9 cinematic matte
painting, dark fantasy, painterly.

FROM THE FIRST REFERENCE keep: the broken stone causeway running away from the
viewer into deep distance down the middle, the row of shattered pillars flanking
it, and the style of the carved stone portal rings.

FROM THE SECOND REFERENCE take: one of the great ruined keeps — the tall
fortress crowning a rock, with its tattered banners.

THE COMPOSITION IS DELIBERATELY UNEVEN. %s That asymmetry is the whole point:
the two sides must NOT mirror each other. They carry equal visual weight, but
they are different structures of different height, mass and silhouette.

KEEP IT DARK. This is a night scene lit by embers. The distant horizon is a DIM,
deep, smouldering ember line — low, muted, barely there. NO bright sunset, NO
glowing sky, NO blown-out light anywhere. The centre of the image, around the
vertical middle, stays dark: heavy cloud and haze, no light source behind it. Far
more of this painting is in shadow than in light.

THE RINGS: two large stone portal rings, one left of centre and one right of
centre, each about a quarter of the image height across, sitting above the
causeway. Their carved stone bezel is SLENDER — a narrow rim, not a thick wall —
so the dark opening inside is as wide as possible. Each ring is turned slightly
on its vertical axis so it angles inward toward the centre rather than facing
square-on, and the two turn in opposite directions. The inside of each ring is
FLAT, DARK and COMPLETELY EMPTY — no scene, no face, no glow, nothing.

QUIET LOWER THIRD: the bottom third of the image is low contrast — ash, haze,
dark earth, a few scattered embers. No hard edges or bright highlights there.

No people, no characters, no faces, no creatures. No text, letters, numbers,
runes, logos or interface elements. No elemental theming: no ocean, no forest, no
fire deity. Ashy warm-biased near-black ground, scattered ember glow, cold teal
storm sky kept dim.
"""

VARIANTS = {
    "KeepLeft": "The LEFT side is the tall ruined keep on its rock, banners flying. "
                "The RIGHT side is the low broken gatehouse arch from the first reference, "
                "smaller and wider, sprawling along the ground.",
    "KeepRight": "The RIGHT side is the tall ruined keep on its rock, banners flying. "
                 "The LEFT side is the low broken gatehouse arch from the first reference, "
                 "smaller and wider, sprawling along the ground.",
    "KeepLeftDeeper": "The LEFT side is the tall ruined keep on its rock, banners flying, close "
                      "to the viewer and looming. The RIGHT side is the broken gatehouse arch, set "
                      "much further back down the causeway and half-lost in haze, so the two sides "
                      "sit at clearly different distances as well as different shapes.",
}


def generate(name: str, clause: str, key: str) -> bool:
    prompt = (BASE % clause).strip()
    print(f"[{name}] refs={REFS} -> {SIZE}", flush=True)
    t0 = time.time()

    handles = []
    files = []
    try:
        for ref in REFS:
            fh = open(SRC / ref, "rb")
            handles.append(fh)
            files.append(("image[]", (ref, fh, "image/png")))
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
    finally:
        for fh in handles:
            fh.close()

    if r.status_code != 200:
        print(f"[{name}] FAILED {r.status_code}: {r.text[:500]}", flush=True)
        return False

    d = r.json()["data"][0]
    raw = base64.b64decode(d["b64_json"]) if "b64_json" in d else requests.get(d["url"], timeout=180).content
    dest = OUT / f"Versus2b_{name}.png"
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
    for name, clause in VARIANTS.items():
        if generate(name, clause, key):
            ok += 1

    print(f"\n{ok}/{len(VARIANTS)} generated -> {OUT}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
