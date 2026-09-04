"""What the LIVE board camera actually shows, per screen aspect and safe-area inset.

WHY THIS EXISTS, AND WHY device_layout.py IS NOT IT. The board camera was orthographic until
`f121a591` (2026-08-28, "fit tabletop camera to safe areas") replaced it with the perspective
tabletop rig in `BoardCameraRig`. Every layout argument in this repo — `DeviceLayout.MinAspect`,
`DeviceLayout.Solve`, issue #235's clipping table, `device_layout.py` in full — is arithmetic for
the camera that was retired that day, and it survives because nothing failed when it stopped being
true. It answers a plausible question with a wrong number, which is the worst failure mode
available.

THE CORRECTION IN ONE LINE: `DesignHalfWidth`/`DesignHalfHeight` are a FLOOR, not a ceiling.
`BoardCameraRig.ApplySafeArea` fits the eight corners of that box into the safe viewport by
RETREATING the camera. It is a guarantee about what must be visible, not a limit on what may
exist — and past it, a wide screen simply shows more board.

Confirming the perspective branch is the live one, two ways:
  1. `BoardCameraRig.Configure` sets `orthographic = false`, and `ProceduralBoardSetup
     .AttachSafeAreaCamera` calls it BEFORE `AddComponent<SafeAreaCamera>()`, so the component's
     Awake captures `_authoredOrthographic = false`.
  2. `SafeAreaCamera` is authored in ZERO scenes — grep its .cs.meta guid across Assets/**.unity —
     so there is no path by which it is added before Configure runs. Its `if (_authoredOrthographic)`
     branch, and everything of `DeviceLayout.Solve` it consumes, is unreachable.

Every constant below is parsed out of the shipping source, never retyped, for the reason
device_layout.py's header already gives: a second copy of a constant has gone stale here before.

Usage:  python tooling/scripts/board_camera_fit.py

RETIREMENT TRIGGER: if the board camera is ever made orthographic again, or `ApplySafeArea` stops
being the fit, this script and `device_layout.py` swap places and both headers change together.
"""

import math
import pathlib
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")

REPO = pathlib.Path(__file__).resolve().parents[2]
P = REPO / "unity" / "Emberfall" / "Assets" / "Scripts" / "Presentation"
RIG = "BoardCameraRig.cs"


def const(fname, name):
    num = r"-?[\d.]+f?"
    pat = re.compile(r"\bconst\s+(?:float|int)\s+" + re.escape(name)
                     + rf"\s*=\s*({num})(?:\s*\*\s*({num}))?\s*;")
    for i, line in enumerate((P / fname).read_text(encoding="utf-8").splitlines(), 1):
        m = pat.search(line)
        if m:
            v = float(m.group(1).rstrip("f"))
            return (v * float(m.group(2).rstrip("f")) if m.group(2) else v), f"{fname}:{i}"
    raise SystemExit(f"board_camera_fit: const {name} not found in {fname}")


def vec3(fname, name):
    pat = re.compile(re.escape(name)
                     + r"\s*=\s*new\(?\s*(-?[\d.]+)f?\s*,\s*(-?[\d.]+)f?\s*,\s*(-?[\d.]+)f?")
    text = (P / fname).read_text(encoding="utf-8")
    m = pat.search(text)
    if not m:
        raise SystemExit(f"board_camera_fit: {name} not found in {fname}")
    line = text[:m.start()].count("\n") + 1
    return tuple(float(g) for g in m.groups()), f"{fname}:{line}"


FOV, FOV_SRC = const(RIG, "FieldOfView")
NEAR, NEAR_SRC = const(RIG, "NearClip")
RETREAT0, RETREAT0_SRC = const(RIG, "CompositionRetreat")
FRONT_Z, FRONT_SRC = const(RIG, "DesignFrontZ")
BACK_Z, _ = const(RIG, "DesignBackZ")
POS, POS_SRC = vec3(RIG, "AuthoredPosition")
TGT, TGT_SRC = vec3(RIG, "LookTarget")
HW, HW_SRC = const("DeviceLayout.cs", "DesignHalfWidth")
HH, HH_SRC = const("DeviceLayout.cs", "DesignHalfHeight")
BASELINE, BASE_SRC = const("ProceduralBoardSetup.cs", "Baseline")

MAX_RETREAT, EPS = 128.0, 1e-5

# Unity LookRotation(fwd, up): right = norm(cross(up, fwd)); trueUp = cross(fwd, right).
_d = tuple(TGT[i] - POS[i] for i in range(3))
_n = math.sqrt(sum(c * c for c in _d))
FWD = tuple(c / _n for c in _d)
_r = (FWD[2], 0.0, -FWD[0])
_rn = math.sqrt(sum(c * c for c in _r))
RIGHT = tuple(c / _rn for c in _r)
UP = (FWD[1] * RIGHT[2] - FWD[2] * RIGHT[1],
      FWD[2] * RIGHT[0] - FWD[0] * RIGHT[2],
      FWD[0] * RIGHT[1] - FWD[1] * RIGHT[0])


def dot(a, b):
    return sum(p * q for p, q in zip(a, b))


def corners():
    """BoardCameraRig.DesignCornersInCameraSpace — the box in AUTHORED camera space."""
    out = []
    for z in (FRONT_Z, BACK_Z):
        for y in (-HH, HH):
            for x in (-HW, HW):
                v = (x - POS[0], y - POS[1], z - POS[2])
                out.append((dot(RIGHT, v), dot(UP, v), dot(FWD, v)))
    return out


def try_offsets(pts, retreat, tan_h, tan_v, left=-1.0, right=1.0, bottom=-1.0, top=1.0):
    """BoardCameraRig.TryOffsetIntervals, verbatim in its logic."""
    mnx, mxx, mny, mxy = -math.inf, math.inf, -math.inf, math.inf
    for (px, py, pz) in pts:
        depth = pz + retreat
        if depth <= NEAR:
            return None
        mnx = max(mnx, px - right * depth * tan_h)
        mxx = min(mxx, px - left * depth * tan_h)
        mny = max(mny, py - top * depth * tan_v)
        mxy = min(mxy, py - bottom * depth * tan_v)
    return (mnx, mxx, mny, mxy) if (mnx <= mxx + EPS and mny <= mxy + EPS) else None


def solve(aspect, bottom_px=0, screen_h=1080):
    """BoardCameraRig.ApplySafeArea: minimum retreat, then clamp the offsets to the authored
    centre. Returns (retreat, offsetY, cameraPosition, tanHorizontal)."""
    tan_v = math.tan(math.radians(FOV) * 0.5)
    tan_h = tan_v * aspect
    pts = corners()
    bottom = 2.0 * (bottom_px / screen_h) - 1.0

    def feasible(r):
        return try_offsets(pts, r, tan_h, tan_v, -1.0, 1.0, bottom, 1.0)

    retreat = RETREAT0
    if feasible(retreat) is None:
        lo, hi = retreat, max(retreat + 1.0, 1.0)
        while hi < MAX_RETREAT and feasible(hi) is None:
            hi *= 2
        hi = min(hi, MAX_RETREAT)
        for _ in range(36):
            mid = (lo + hi) * 0.5
            if feasible(mid) is not None:
                hi = mid
            else:
                lo = mid
        retreat = hi
    iv = feasible(retreat)
    if iv is None:
        return None
    mnx, mxx, mny, mxy = iv
    ox = min(max(0.0, mnx), mxx)
    oy = min(max(0.0, mny), mxy)
    cam = tuple(POS[i] + ox * RIGHT[i] + oy * UP[i] - retreat * FWD[i] for i in range(3))
    return retreat, oy, cam, tan_h


def visible_half_width(cam, tan_h, y, z=0.0):
    """The right screen edge satisfies px = pz * tanH, and pz does not depend on x, so this is
    exact rather than iterative. Answers: how far out in x can something sit at world (y, z)
    and still be on screen?"""
    v = (0.0 - cam[0], y - cam[1], z - cam[2])
    return dot(FWD, v) * tan_h


ASPECTS = [("4:3   tablet", 4 / 3), ("16:10", 1.6), ("16:9  desktop", 16 / 9),
           ("18:9", 2.0), ("19.5:9 iPhone", 19.5 / 9), ("20:9  2400x1080", 20 / 9),
           ("21:9  ultrawide", 21 / 9)]


def main():
    print("board_camera_fit — the PERSPECTIVE rig, read from source\n")
    for label, src in (("FieldOfView", FOV_SRC), ("CompositionRetreat", RETREAT0_SRC),
                       ("AuthoredPosition", POS_SRC), ("LookTarget", TGT_SRC),
                       ("DesignFrontZ", FRONT_SRC), ("DesignHalfWidth", HW_SRC),
                       ("DesignHalfHeight", HH_SRC), ("Baseline", BASE_SRC)):
        print(f"  {label:<20} {src}")
    print(f"\n  fov {FOV}  retreat floor {RETREAT0}  box +/-{HW} x +/-{HH}, z {FRONT_Z}..{BACK_Z}")
    print(f"  forward {tuple(round(c, 5) for c in FWD)}  up {tuple(round(c, 5) for c in UP)}\n")

    print("VISIBLE HALF-WIDTH, no insets. The bottom row (hero, sockets, deck) sits at")
    print(f"|y| {BASELINE} and is NEARER the camera than the centreline, so it sees LESS width.\n")
    print(f"{'aspect':<18}{'retreat':>9}{'|x| @ baseline':>16}{'|x| @ y=0':>12}{'vs box':>9}")
    for name, a in ASPECTS:
        s = solve(a)
        if s is None:
            print(f"{name:<18}   INFEASIBLE")
            continue
        retreat, oy, cam, tan_h = s
        near = visible_half_width(cam, tan_h, -BASELINE)
        mid = visible_half_width(cam, tan_h, 0.0)
        print(f"{name:<18}{retreat:>9.3f}{near:>16.3f}{mid:>12.3f}{near - HW:>+9.3f}")

    print("\nWHERE THE FIT ACTUALLY BINDS — retreat leaves its floor only below here:")
    a = 2.30
    while a > 0.95:
        if solve(a)[0] > RETREAT0 + 1e-4:
            print(f"  aspect {a:.3f}. Everything wider fits at the composition floor, so the")
            print("  design box is NOT the horizontal constraint on any real device.")
            break
        a -= 0.005
    for name, a in (("1:1", 1.0), ("5:4", 1.25)):
        print(f"  sanity: aspect {name} retreat {solve(a)[0]:.4f} (the solver does respond)")

    print("\nSAFE-AREA INSETS cost horizontal room only if they force retreat. On a 1080p panel")
    print("at 2.75x, a 48dp gesture bar is 132 px:")
    print(f"\n{'aspect':<18}{'inset px':>9}{'retreat':>9}{'offsetY':>9}{'|x| @ baseline':>16}")
    for name, a in (("16:9  desktop", 16 / 9), ("20:9  2400x1080", 20 / 9)):
        for inset in (0, 132, 200):
            retreat, oy, cam, tan_h = solve(a, inset)
            print(f"{name:<18}{inset:>9}{retreat:>9.3f}{oy:>9.3f}"
                  f"{visible_half_width(cam, tan_h, -BASELINE):>16.3f}")
    print("\nInsets are absorbed by SLIDING the camera (offsetY), not by retreating, so they")
    print("cost no horizontal room at all until they are large enough to force a retreat.")
    print("\nNOT VERIFIED AGAINST PIXELS. This replicates the code exactly and the solver responds")
    print("where it must, but only a Game view at a real resolution proves the code matches the")
    print("screen. Corroboration: SafeAreaCamera's doc calls 18 px 'the ENTIRE vertical inset")
    print("budget', and this solve leaves ~16 px of NDC margin at the box's bottom under 48dp.")


if __name__ == "__main__":
    main()
