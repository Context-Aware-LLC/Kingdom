# -*- coding: utf-8 -*-
"""Derive the live UI Toolkit panel scale from an Android screenshot.

    adb exec-out screencap -p > shot.png
    python tooling/scripts/measure_panel_scale.py shot.png

Answers "what scale is the panel ACTUALLY running at on this device", which is
not a thing the project can tell you from its own files: it depends on
PanelSettings' scale mode, its reference dpi, and what Unity's Screen.dpi
reports -- and Screen.dpi does NOT have to match the density Android
advertises. On a Pixel 7 emulator advertising 420 dpi it reports ~418, close but not
equal. Predicting it instead of measuring is how you get this wrong.

.ms-gear is authored 44x44 and is the cleanest ruler on screen: a solid
--surface-2 rounded square on a dark backdrop, with a known authored size.

MEASURE ITS BORDER BOX, NOT ITS FILL. UI Toolkit sizes border-box, so the
authored 44 INCLUDES .ms-gear's 3px border on each side, and the --surface-2 run
this script matches is the 38-unit fill inside it. Reading the fill and dividing
by 44 under-reports the scale by about 14%: on 2026-08-22 that turned a real
2.61x into a reported 4.14x at referenceDpi 96, and made Screen.dpi look like
397 when it is ~418. Add both borders back before dividing.
"""
import sys
from PIL import Image

path = sys.argv[1]
im = Image.open(path).convert('RGB')
W, H = im.size
px = im.load()

# --surface-2 is rgb(30, 37, 48); the gear sits on a much darker backdrop.
def is_gear(c):
    r, g, b = c
    return abs(r - 30) <= 10 and abs(g - 37) <= 10 and abs(b - 48) <= 12


# Search the top-middle band where the button appeared.
minx, maxx, miny, maxy = W, 0, H, 0
hits = 0
for y in range(0, int(H * 0.45), 2):
    for x in range(int(W * 0.45), int(W * 0.85), 2):
        if is_gear(px[x, y]):
            hits += 1
            minx, maxx = min(minx, x), max(maxx, x)
            miny, maxy = min(miny, y), max(maxy, y)

print('image           %dx%d' % (W, H))
if hits < 50:
    print('gear            not found (%d matching px)' % hits)
    sys.exit(0)
w, h = maxx - minx, maxy - miny
print('gear bbox       x %d..%d  y %d..%d' % (minx, maxx, miny, maxy))
print('gear rendered   %d x %d px' % (w, h))
print('authored        44 x 44 px')
print('scale factor    %.2f x  (%.2f x)' % (w / 44.0, h / 44.0))
print('at 420 dpi      %.1f mm across' % (w / 420.0 * 25.4))
