"""Cut the three coloured digit sockets out of the neutral one.

WHY THIS EXISTS. Cost, attack and health each sit in their own colour of disc (Sam, 2026-10-03:
cost blue, attack yellow, health red). The disc in socket_stat.png is near-black, and a
SpriteRenderer tint MULTIPLIES, so no runtime colour can lift it -- black times anything is
black. The colour has to be in the texture.

WHAT IT DOES. Reads socket_stat.png, repaints only the flat interior (the digit-safe disc inside
the rope band) and writes one PNG per stat beside it. The band, the alpha and the canvas are
copied untouched, so all four sprites stay interchangeable at FrameRarityCatalog.StatSocketPx.
socket_stat.png itself is never written: it is the preserved neutral source. The shaped minion
and weapon stat sprites are separate assets; this script only maintains the circular variants.

Re-run after changing a colour below or after any re-export of socket_stat.png. Outputs are
overwritten in place, which keeps each .meta guid and the Addressables entry pointing at it.

    python tooling/scripts/tint_stat_sockets.py
"""
from pathlib import Path

import numpy as np
from PIL import Image

REPO = Path(__file__).resolve().parents[2]
FRAMES = REPO / "unity/Assets/Art/UI/Frames"
SRC = FRAMES / "socket_stat.png"

# Mid-tone, not neon: the digits are white with a black rim and have to stay the brightest
# thing on the disc.
TINTS = {
    "socket_cost":   (36, 92, 214),
    "socket_attack": (226, 168, 28),
    "socket_health": (190, 34, 38),
}

# Radii in px from the disc centre, off the radial profile in docs/dev-docs/specs/
# card-frame-geo.md: the interior is flat to r 103 and the band's inner edge is at r 108.
FILL_R = 102.0      # fully repainted inside this
FEATHER_R = 106.0   # back to the source art by here
EDGE_SHADE = 0.55   # how far the disc darkens toward the band, so it reads as a well


def tint(src, rgb):
    h, w = src.shape[:2]
    yy, xx = np.mgrid[0:h, 0:w]
    r = np.hypot(xx - (w - 1) / 2.0, yy - (h - 1) / 2.0)

    # Keep the source's faint mottling so the disc is painted rather than flat.
    luma = src[..., :3].mean(axis=2)
    grain = luma / luma[r < FILL_R].mean()

    shade = 1.0 - (1.0 - EDGE_SHADE) * np.clip(r / FILL_R, 0.0, 1.0) ** 2
    fill = np.asarray(rgb, dtype=float) * (shade * grain)[..., None]

    mix = np.clip((FEATHER_R - r) / (FEATHER_R - FILL_R), 0.0, 1.0)[..., None]
    out = src.copy()
    out[..., :3] = np.clip(fill * mix + src[..., :3] * (1.0 - mix), 0, 255)
    return out


def main():
    src = np.asarray(Image.open(SRC).convert("RGBA")).astype(float)
    for name, rgb in TINTS.items():
        path = FRAMES / f"{name}.png"
        Image.fromarray(tint(src, rgb).astype(np.uint8), "RGBA").save(path)
        print(f"wrote {path.relative_to(REPO)}  tint {rgb}")


if __name__ == "__main__":
    main()
