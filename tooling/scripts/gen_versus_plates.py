"""Generate versus-screen landscape candidates.

Per techart/versus-screen.md §7: direct OpenAI API (gpt-image-2), NOT the Unity
wrapper — the wrapper silently drops width/height and caps at 1024 square.

Native 21:9 in one pass at 3696x1584 (multiple of 16, longest edge <= 3840,
aspect <= 3:1). Upscale + Lanczos down to the 5040x2160 master happens later.

Reference: unity/Emberfall/Assets/Art/Backgrounds/MainMenu_Backdrop.png — take
its architecture and division, not its art.
"""

import base64
import os
import pathlib
import sys
import time

import requests

OUT = pathlib.Path(__file__).resolve().parents[2] / "art-bakeoff" / "versus"
SIZE = "3696x1584"
MODEL = "gpt-image-2"

# Zone constraints every candidate must satisfy — versus-screen.md §4.
# Fractions are of the full canvas: frames at 32%/68% width, 39% height;
# orb dead centre; text bands 62-79% height; status strip 81-91%.
COMMON = """
ULTRA-WIDE 21:9 cinematic matte painting, dark fantasy, painterly and
atmospheric. No people, no characters, no faces, no creatures anywhere.
Absolutely no text, letters, numbers, runes, logos or interface elements.

COMPOSITION IS A CONTRACT — this image is a template that game UI is drawn over:
- Two large EMPTY circular stone portals face each other, one centred at 32% of
  the width and one at 68%, both centred at 39% of the height from the top.
  Each is a carved ring of weathered stone about a quarter of the image height
  across, and the inside of each ring is FLAT, DARK and COMPLETELY EMPTY — a
  plain void. Nothing inside them. They are frames waiting to be filled.
- The exact centre of the image is a calm, dark, empty pocket of air — no
  structure, no bright detail — about a fifth of the image height across.
- The lower third, from 62% to 91% of the height, is quiet and low-contrast:
  ground haze, ash, dark earth. No hard shapes or bright highlights there.
- Left and right halves are BALANCED. Neither side dominates or looks like the
  winner.

NEUTRALITY: this must suit any two opponents. No elemental theming — no ocean or
water god, no forest or nature deity, no single dominant fire god. Ember light is
ambient warmth in the ground, not a faction.

PALETTE: ashy warm-biased near black ground, orange ember glow low and scattered,
cold teal storm light in the sky, low warm horizon band far away in the centre.
"""

VARIANTS = {
    "TwinKeeps": """
Two colossal ruined fortresses face each other from opposing ridgelines across a
wide dead valley. Tattered war banners on bent iron poles lean in the foreground
on both sides. Scattered embers glow in the churned earth. A teal storm sky
breaks open high above; far below and between them, a pale warm horizon.
""",
    "BrokenCauseway": """
A shattered stone causeway runs away from the viewer into deep distance down the
centre of a drowned ash plain. A ruined gatehouse stands on each side of it,
mirrored, their arches collapsed. Broken pillars line the causeway. Deep
atmospheric perspective, heavy haze, embers drifting. Cold storm sky above, warm
horizon glow where the causeway vanishes.
""",
    "AshBasin": """
A vast shallow crater basin of scorched earth seen from its rim. Two enormous
ruined bastions stand on the far rim, left and right, silhouetted. The foreground
is a dark field of ash and broken siege timbers. Smoke rises in slow columns.
Cold light rakes across the basin from a torn sky; the basin floor glows faintly
with buried embers.
""",
    "StormSpires": """
Two immense broken spires of black stone rise on either side of an empty
windswept moor, silhouetted against an enormous turbulent teal and charcoal sky
that fills most of the frame. Minimal ground detail — just dark heath and a few
embers. Vast negative space. Stark, quiet, monumental, high contrast between the
dark spires and the luminous storm behind them.
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
    dest = OUT / f"Versus_{name}.png"
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
    for name, body in VARIANTS.items():
        if generate(name, body, key):
            ok += 1

    print(f"\n{ok}/{len(VARIANTS)} generated -> {OUT}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
