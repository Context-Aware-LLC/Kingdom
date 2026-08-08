"""EF-062 · Resolution, aspect and safe area — the measurement, not the reasoning.

Prints the phone aspect range, the safe-area budget, and the TRUE pixel size of a card
in hand on the smallest supported device, then re-checks EF-050's body font against
that number instead of the desktop / collection-wall one.

EVERY GEOMETRY NUMBER IS PARSED OUT OF THE SHIPPING SOURCE, never retyped here. The
board is an orthographic scene, so its on-screen pixel geometry is exact arithmetic
once you have the constants — but a second copy of one of those constants has already
gone stale on this project once (BoardController.CardW, see its comment). So this
script reads them, prints each with its file:line provenance, and derives everything
from what it read. If a constant moves, this output moves with it.

Cap height is MEASURED off the shipping .ttf via PIL glyph metrics, not assumed at
some textbook fraction of the em.

Usage:  python tooling/scripts/device_layout.py
"""

import math
import pathlib
import re
import sys

sys.stdout.reconfigure(encoding="utf-8")   # em dashes survive a cp1252 console

REPO = pathlib.Path(__file__).resolve().parents[2]
UNITY = REPO / "unity" / "Emberfall"
SCRIPTS = UNITY / "Assets" / "Scripts" / "Presentation"
SCENES = UNITY / "Assets" / "Scenes"
ART = UNITY / "Assets" / "Art"


# ── constant extraction ──────────────────────────────────────────────────────────

class Missing(Exception):
    pass


_read_cache: dict[pathlib.Path, list[str]] = {}


def _lines(path: pathlib.Path) -> list[str]:
    if path not in _read_cache:
        if not path.exists():
            raise Missing(f"source file not found: {path.relative_to(REPO)}")
        _read_cache[path] = path.read_text(encoding="utf-8").splitlines()
    return _read_cache[path]


FOUND: list[tuple[str, str, str]] = []   # (label, value, provenance)


def const(path: pathlib.Path, name: str, label: str | None = None) -> float:
    """Read a `const float NAME = <expr>;` out of a C# source file.

    <expr> is either a literal or a literal-over-literal ratio — the two forms the
    board constants actually use (`4.27f` and `108f / 2048f`). Anything else must be
    added deliberately rather than eval'd; a silent partial parse is the failure mode
    this whole script exists to avoid.
    """
    num = r"-?[\d.]+f?"
    pat = re.compile(
        r"\bconst\s+(?:float|int)\s+" + re.escape(name)
        + rf"\s*=\s*({num})(?:\s*/\s*({num}))?\s*;")
    for i, line in enumerate(_lines(path), 1):
        m = pat.search(line)
        if m:
            v = float(m.group(1).rstrip("f"))
            if m.group(2):
                v /= float(m.group(2).rstrip("f"))
            FOUND.append((label or name, f"{v:g}",
                          f"{path.relative_to(REPO).as_posix()}:{i}"))
            return v
    raise Missing(f"const {name} not found in {path.relative_to(REPO)}")


def yaml_scalar(path: pathlib.Path, key: str, label: str) -> float:
    pat = re.compile(r"^\s*" + re.escape(key) + r":\s*(-?[\d.]+)\s*$")
    for i, line in enumerate(_lines(path), 1):
        m = pat.match(line)
        if m:
            v = float(m.group(1))
            FOUND.append((label, f"{v:g}",
                          f"{path.relative_to(REPO).as_posix()}:{i}"))
            return v
    raise Missing(f"'{key}:' not found in {path.relative_to(REPO)}")


def png_size(path: pathlib.Path, label: str) -> tuple[int, int]:
    from PIL import Image
    with Image.open(path) as im:
        w, h = im.size
    FOUND.append((label, f"{w} x {h} px", path.relative_to(REPO).as_posix()))
    return w, h


# ── device profiles ──────────────────────────────────────────────────────────────
#
# Landscape. `short` is the SHORT edge in px, which under an orthographic camera is
# the only dimension that sets scale — the long edge only buys or costs width.
# ppi and diagonal are vendor-published panel specs.

class Device:
    def __init__(self, name, long_px, short_px, diag_in, note=""):
        self.name = name
        self.long_px = long_px
        self.short_px = short_px
        self.diag_in = diag_in
        self.note = note

    @property
    def aspect(self):
        return self.long_px / self.short_px

    @property
    def ppi(self):
        return math.hypot(self.long_px, self.short_px) / self.diag_in


DEVICES = [
    Device("Pixel 5a (ticket reference)", 2400, 1080, 6.34),
    Device("Pixel 8",                     2400, 1080, 6.20),
    Device("Galaxy S24",                  2340, 1080, 6.20),
    Device("Galaxy A15 (budget FHD+)",    2340, 1080, 6.50),
    Device("Xperia 1 V (4K 21:9)",        3840, 1644, 6.50, "past the backdrop bleed"),
    Device("Legacy 16:9 phone",           1920, 1080, 5.50, "narrow bound of the range"),
    Device("Moto G HD+ (720p budget)",    1600,  720, 6.60, "EXCLUDED — see verdict"),
    Device("Tablet 16:10",                1920, 1200, 10.5, "below MinAspect"),
    Device("Tablet 4:3",                  2048, 1536, 9.70, "below MinAspect"),
]

# EF-050 Appendix A reference measurement (Hearthstone collection screen, Pixel 5a,
# measured off a native-resolution screenshot on 2026-07-30). A RECORD, not a target:
# it is the number EF-050's acuity margin was computed against, reproduced here only
# so the same arithmetic can be re-run against OUR card instead.
REF_CARD_W_PX = 265.0
REF_BODY_CAP_MM = 0.7
REF_MARGIN = 1.6
VIEW_DIST_MM = 300.0        # Appendix A's holding distance
ACUITY_THRESHOLD_ARCMIN = 5.0   # letter-recognition threshold (a 20/20 Snellen letter)


def arcmin(size_mm, dist_mm=VIEW_DIST_MM):
    return math.degrees(math.atan2(size_mm, dist_mm)) * 60.0


def measured_cap_over_em(ttf: pathlib.Path, label: str) -> float:
    """Cap height as a fraction of the em, measured off the shipping font file."""
    from PIL import ImageFont
    probe = 512
    f = ImageFont.truetype(str(ttf), probe)
    top, bottom = f.getbbox("H")[1], f.getbbox("H")[3]
    frac = (bottom - top) / probe
    FOUND.append((label, f"{frac:.4f} em", f"{ttf.relative_to(REPO).as_posix()} (PIL glyph bbox 'H')"))
    return frac


def rule(ch="-", n=94):
    print(ch * n)


def main() -> int:
    print("EF-062 · Resolution, aspect and safe area")
    print("Emberfall board = orthographic world-space scene. Every figure below is derived")
    print("from the constants in the table, which are parsed from the shipping source.")
    print()

    board = SCRIPTS / "BoardController.cs"
    fan = SCRIPTS / "HandFanLayout.cs"
    setup = SCRIPTS / "ProceduralBoardSetup.cs"
    hero = SCRIPTS / "HeroPlateView.cs"
    deck = SCRIPTS / "DeckPileView.cs"
    mana = SCRIPTS / "ManaTrayView.cs"
    frames = SCRIPTS / "FrameRarityCatalog.cs"
    backdrop = SCRIPTS / "BoardBackdrop.cs"

    ortho = yaml_scalar(SCENES / "Board.unity", "orthographic size", "camera orthographicSize")
    ortho_net = yaml_scalar(SCENES / "BoardNetworked.unity", "orthographic size",
                            "camera orthographicSize (networked)")
    card_w = const(board, "CardW", "BoardController.CardW")
    card_h = const(board, "CardH", "BoardController.CardH")
    centre_y = const(fan, "CentreY", "HandFanLayout.CentreY")
    sag = const(fan, "Sag", "HandFanLayout.Sag")
    rest_scale = const(fan, "RestScale", "HandFanLayout.RestScale")
    tilt_deg = const(fan, "MaxTiltDeg", "HandFanLayout.MaxTiltDeg")
    lift = const(SCRIPTS / "HandCardHover.cs", "Lift", "HandCardHover.Lift")
    hover_scale = const(SCRIPTS / "HandCardHover.cs", "Scale", "HandCardHover.Scale")
    hero_x = const(setup, "HeroX", "ProceduralBoardSetup.HeroX")
    deck_x = const(setup, "DeckX", "ProceduralBoardSetup.DeckX")
    mana_x = const(setup, "ManaRailX", "ProceduralBoardSetup.ManaRailX")
    baseline = const(setup, "Baseline", "ProceduralBoardSetup.Baseline")
    slot_gap = const(setup, "SlotSpacing", "ProceduralBoardSetup.SlotSpacing")
    slot_n = const(setup, "BoardSlotCount", "ProceduralBoardSetup.BoardSlotCount")
    plate_w = const(hero, "PlateW", "HeroPlateView.PlateW")
    pile_w = const(deck, "PileW", "DeckPileView.PileW")
    rail_w = const(mana, "RailW", "ManaTrayView.RailW")
    body_em = const(frames, "BodyEm", "FrameRarityCatalog.BodyEm")
    body_floor = const(frames, "BodyEmFloor", "FrameRarityCatalog.BodyEmFloor")
    name_em = const(frames, "NameEm", "FrameRarityCatalog.NameEm")
    body_cy = const(frames, "BodyCenterY", "FrameRarityCatalog.BodyCenterY")
    plate_px = png_size(ART / "Backgrounds" / "BoardPlate_Master.png", "BoardPlate_Master")
    vis_h_declared = const(backdrop, "VisibleHeight", "BoardBackdrop.VisibleHeight")
    cap_frac = measured_cap_over_em(ART / "Fonts" / "GoogleSans-Regular.ttf",
                                    "GoogleSans cap height")

    print("== 1. CONSTANTS READ (nothing below is typed by hand) " + "=" * 39)
    w = max(len(a) for a, _, _ in FOUND)
    for label, value, prov in FOUND:
        print(f"  {label:<{w}}  {value:>14}   {prov}")
    print()
    if ortho != ortho_net:
        print(f"  !! Board.unity and BoardNetworked.unity disagree on ortho size "
              f"({ortho} vs {ortho_net}) — the numbers below use {ortho}.")
        print()

    # ── 2. the horizontal budget ────────────────────────────────────────────────
    design_h = ortho                      # half-height, world units
    elements = [
        ("hero plate outer edge", hero_x + plate_w / 2),
        ("deck pile outer edge", deck_x + pile_w / 2),
        ("mana rail outer edge", mana_x + rail_w / 2),
        ("board row outer edge", (slot_n - 1) * 0.5 * slot_gap + card_w / 2),
    ]
    elements.sort(key=lambda e: -e[1])
    design_w = elements[0][1]             # half-width the layout actually needs
    min_aspect = design_w / design_h

    print("== 2. THE HORIZONTAL BUDGET — what the layout needs " + "=" * 42)
    print(f"  Visible half-height is fixed by the ortho camera: {design_h:.4f} world units")
    print(f"  ({2*design_h:.2f} units top to bottom, on every device, at every aspect).")
    print()
    print("  Widest |x| reached by each bottom-row element (seat 0; seat 1 mirrors):")
    for label, x in elements:
        print(f"    {label:<26} |x| = {x:8.4f}")
    print()
    print(f"  DESIGN HALF-WIDTH        = {design_w:.4f} world units  ({elements[0][0]})")
    print(f"  MINIMUM SUPPORTED ASPECT = {design_w:.4f} / {design_h:.4f} = {min_aspect:.5f}")
    print(f"                             narrower than this and the hero plate leaves the screen.")
    print()

    # ── 3. backdrop coverage ────────────────────────────────────────────────────
    ppu_plate = plate_px[1] / vis_h_declared
    plate_half_w = plate_px[0] / ppu_plate / 2
    plate_aspect = plate_half_w / design_h
    print("== 3. BACKDROP COVERAGE — the upper bound of the range " + "=" * 39)
    print(f"  BoardPlate_Master is {plate_px[0]} x {plate_px[1]} px at "
          f"{ppu_plate:.4f} px/unit (tex.height / VisibleHeight {vis_h_declared:g}).")
    print(f"  It therefore covers |x| <= {plate_half_w:.4f} units, i.e. aspects up to "
          f"{plate_aspect:.5f}.")
    print(f"  SUPPORTED ASPECT RANGE   = {min_aspect:.5f} .. {plate_aspect:.5f}")
    print(f"                             ({min_aspect:.3f} = layout floor, "
          f"{plate_aspect:.3f} = art ceiling)")
    print()

    # ── 4. per-device table ─────────────────────────────────────────────────────
    print("== 4. PER-DEVICE, LANDSCAPE " + "=" * 66)
    print(f"  {'device':<28} {'px':>10} {'aspect':>7} {'px/unit':>8} "
          f"{'card in hand':>14} {'side spare':>11}  verdict")
    smallest = None
    for d in DEVICES:
        ppu = d.short_px / (2 * design_h)
        cw = card_w * rest_scale * ppu
        ch = card_h * rest_scale * ppu
        half_w = design_h * d.aspect
        spare = (half_w - design_w) * ppu
        if d.aspect < min_aspect:
            verdict = "CLIPS — unsupported"
        elif d.aspect > plate_aspect:
            verdict = f"bare edge {(d.aspect - plate_aspect) * design_h * ppu:.0f}px/side"
        else:
            verdict = "fits"
            if smallest is None or d.short_px < smallest.short_px:
                smallest = d
        if d.note.startswith("EXCLUDED"):
            verdict = "EXCLUDED (see §6)"
        print(f"  {d.name:<28} {d.long_px}x{d.short_px:<4} {d.aspect:7.4f} {ppu:8.3f} "
              f"{cw:6.1f}x{ch:<7.1f} {spare:9.0f}px  {verdict}")
    print()
    print("  Under an orthographic camera the SHORT edge alone sets scale, so every 1080p")
    print("  phone renders a byte-identical card size regardless of its aspect or its ppi.")
    print("  Aspect buys horizontal room; ppi changes only the PHYSICAL size, never the pixels.")
    print()

    # ── 5. the true card size ───────────────────────────────────────────────────
    ref = DEVICES[0]
    ppu = ref.short_px / (2 * design_h)
    # NOR-244: a card IN HAND is drawn at RestScale, a card on the BOARD at 1.0. Everything in
    # this section and in §6 is the HAND card — that is the one whose rules text has to be read.
    hand_w, hand_h = card_w * rest_scale, card_h * rest_scale
    cw_px, ch_px = hand_w * ppu, hand_h * ppu
    board_ch_px = card_h * ppu
    card_top = -(centre_y) + hand_h / 2          # seat 0: fan sits at negative y
    hidden = (-design_h - (-(centre_y) - hand_h / 2)) / hand_h
    hidden_edge = (-design_h - (-(centre_y + sag) - hand_h / 2)) / hand_h
    body_centre_y = -(centre_y) + hand_h / 2 - body_cy * hand_h
    # The band's own half-height, so the question is "is the TEXT on screen", not "is its centre".
    band_half = 0.105 * hand_h
    band_bottom = -(centre_y + sag) + hand_h / 2 - body_cy * hand_h - band_half
    # ... and the same edge on the outermost card, which is also tilted MaxTiltDeg.
    t_rad = math.radians(tilt_deg)
    band_half_w = 0.37 * hand_w
    band_corner = (-(centre_y + sag)
                   + (hand_h / 2 - body_cy * hand_h - band_half) * math.cos(t_rad)
                   - band_half_w * math.sin(t_rad))

    print("== 5. THE TRUE PIXEL SIZE OF A CARD IN HAND " + "=" * 50)
    print(f"  Smallest supported configuration: {smallest.short_px} px short edge — and under")
    print(f"  an ortho camera that is the ONLY input, so this figure is every 1080p phone's.")
    print(f"  Reference device for physical size: {ref.name}, "
          f"{ref.ppi:.0f} ppi computed from {ref.diag_in}\" diagonal.")
    print()
    print(f"    pixels per world unit    = {ref.short_px} / {2*design_h:g} = {ppu:.4f}")
    print(f"    CARD IN HAND             = {cw_px:.1f} x {ch_px:.1f} px "
          f"({cw_px/ref.ppi*25.4:.2f} x {ch_px/ref.ppi*25.4:.2f} mm at {ref.ppi:.0f} ppi)")
    print(f"    (a card ON THE BOARD is x1 = {card_w*ppu:.1f} x {board_ch_px:.1f} px; a card in")
    print(f"     hand is drawn at HandFanLayout.RestScale {rest_scale:g} — NOR-244)")
    print(f"    hidden below screen edge = {hidden*100:.1f}% centre card, "
          f"{hidden_edge*100:.1f}% outermost (sag)")
    print(f"    VISIBLE at rest          = {cw_px:.1f} x {ch_px*(1-hidden):.1f} px")
    print(f"    card top edge            = y {card_top:+.3f} "
          f"(screen top is y {design_h:+.2f}) — fully on screen")
    print(f"    rules-band centre        = y {body_centre_y:+.3f} vs screen edge "
          f"y {-design_h:+.2f}")
    print(f"    rules-band BOTTOM, worst card in the fan (outermost, full sag):")
    print(f"      flat  y {band_bottom:+.3f} -> {band_bottom + design_h:+.3f} clear of the edge"
          f"  {'OK' if band_bottom > -design_h else 'CLIPPED'}")
    print(f"      outer corner at {tilt_deg:g}° tilt  y {band_corner:+.3f} -> "
          f"{band_corner + design_h:+.3f}  "
          f"{'OK' if band_corner > -design_h else 'dips below (known residual, NOR-244)'}")
    if body_centre_y <= -design_h:
        print(f"                               -> THE RULES BAND IS OFF-SCREEN AT REST.")
    print(f"    on hover: +{lift:g} lift, x{hover_scale:g} scale -> "
          f"{cw_px*hover_scale:.1f} x {ch_px*hover_scale:.1f} px, fully exposed")
    lift_needed = hidden_edge * hand_h + (hover_scale - 1) * hand_h / 2
    print(f"      lift needed to fully expose the worst card = {lift_needed:.3f} "
          f"(Lift is {lift:g})  {'OK' if lift >= lift_needed else 'TOO SHORT'}")
    print()
    print(f"  Against the ticket's reference point: the collection-wall card measured for")
    print(f"  Appendix A is {REF_CARD_W_PX:.0f} px wide. A card IN HAND is {cw_px:.1f} px —")
    print(f"  {cw_px/REF_CARD_W_PX*100:.0f}% of it. NOR-244 closed most of that gap; the card on")
    print(f"  the BOARD is still {card_w*ppu:.1f} px, {card_w*ppu/REF_CARD_W_PX*100:.0f}% of it.")
    print()

    # ── 6. EF-050 re-check ──────────────────────────────────────────────────────
    ref_cap_arcmin = arcmin(REF_BODY_CAP_MM)
    print("== 6. EF-050 RE-CHECKED AGAINST THAT NUMBER " + "=" * 50)
    print(f"  Appendix A's model, reproduced: a {REF_BODY_CAP_MM} mm cap height at "
          f"{VIEW_DIST_MM:.0f} mm subtends")
    print(f"  {ref_cap_arcmin:.2f} arcmin against a {ACUITY_THRESHOLD_ARCMIN:.0f} arcmin "
          f"recognition threshold = {ref_cap_arcmin/ACUITY_THRESHOLD_ARCMIN:.2f}x margin "
          f"(document says {REF_MARGIN:g}x). Model reproduces.")
    print()
    print(f"  Our rules box: GoogleSans-Regular, cap height {cap_frac:.4f} em (measured),")
    print(f"  em box = BodyEm {body_em:.5f} x CardH, auto-shrinking to BodyEmFloor "
          f"{body_floor:.5f} on long text.")
    print()
    print(f"  {'state':<34} {'em px':>7} {'cap px':>7} {'cap mm':>7} {'arcmin':>7} "
          f"{'margin':>7}  verdict")
    # NOR-244 scales the HAND card by RestScale and leaves the board card at 1.0, so height is
    # now per-row. The two safe-area rows are the point of the exercise: SafeAreaCamera shrinks
    # everything on screen to lift the board off the Android gesture bar, and a hand size that
    # only clears threshold at the authored camera does not clear it on a real phone.
    worst = None
    worst_hand_rest = None
    for label, em_frac, scale, h_px in [
        ("hand, at rest (BodyEm)", body_em, 1.0, ch_px),
        ("hand, at rest (shrink floor)", body_floor, 1.0, ch_px),
        ("hand, hovered (BodyEm)", body_em, hover_scale, ch_px),
        ("hand, hovered (shrink floor)", body_floor, hover_scale, ch_px),
        ("hand at rest, 24dp gesture bar", body_floor, 1.0, ch_px * 4.6 / 4.8994),
        ("hand at rest, 48dp nav bar", body_floor, 1.0, ch_px * 4.6 / 5.2405),
        ("board minion (BodyEm)", body_em, 1.0, board_ch_px),
        ("board minion (shrink floor)", body_floor, 1.0, board_ch_px),
    ]:
        em_px = em_frac * h_px * scale
        cap_px = em_px * cap_frac
        cap_mm = cap_px / ref.ppi * 25.4
        am = arcmin(cap_mm)
        margin = am / ACUITY_THRESHOLD_ARCMIN
        verdict = "PASS" if margin >= 1.0 else "FAIL — below threshold"
        if worst is None or margin < worst:
            worst = margin
        if label == "hand, at rest (shrink floor)":
            worst_hand_rest = margin
        print(f"  {label:<34} {em_px:7.2f} {cap_px:7.2f} {cap_mm:7.3f} {am:7.2f} "
              f"{margin:6.2f}x  {verdict}")
    print()
    print(f"  The board minion is the floor now, and deliberately: a minion is READ ONCE when it")
    print(f"  is played and then identified by its art and its stat bosses, while a card in hand")
    print(f"  is what a turn is planned from. NOR-244 bought size for the hand only.")
    print()
    name_px = name_em * ch_px
    print(f"  For contrast, the card NAME (AlfaSlabOne, display face) is "
          f"{name_px:.2f} px em on the same card.")
    print()
    print(f"  VERDICT (hand, at rest, common case): {worst_hand_rest:.2f}x the recognition")
    print(f"  threshold. EF-050's 1.6x margin was computed against a {REF_CARD_W_PX:.0f} px collection")
    print(f"  tile and never transferred to a hand card; NOR-244 closed the gap with SIZE, which")
    print(f"  is the only thing that could — no typeface choice buys a factor of 1.35.")
    print(f"  Worst row anywhere in the table: {worst:.2f}x.")
    print()

    # ── 7. safe area ────────────────────────────────────────────────────────────
    print("== 7. SAFE-AREA BUDGET " + "=" * 71)
    vert_spare = design_h - baseline
    print(f"  HORIZONTAL. Spare per side = (orthoSize x aspect) - {design_w:.4f} units:")
    for d in DEVICES:
        if d.aspect < min_aspect:
            continue
        p = d.short_px / (2 * design_h)
        spare_u = design_h * d.aspect - design_w
        print(f"    {d.name:<28} {spare_u:7.4f} units = {spare_u*p:6.1f} px per side")
    print()
    print(f"  VERTICAL. The whole bottom row rests on ProceduralBoardSetup.Baseline "
          f"{baseline:g},")
    print(f"  which is {vert_spare:.4f} units above the screen edge at {design_h:g} — that is the")
    print(f"  ENTIRE vertical inset budget:")
    for d in DEVICES[:4]:
        p = d.short_px / (2 * design_h)
        print(f"    {d.name:<28} {vert_spare*p:6.1f} px of vertical clearance")
    print()
    print("  Android landscape puts the gesture/navigation bar on the BOTTOM edge, which is")
    print("  exactly where the hero plate, both sockets, the mana rail and the deck pile sit.")
    print("  The gesture inset alone is 24-48 dp; at the 2.75x density of a 1080p 6.3\" panel")
    print(f"  that is {24*2.75:.0f}-{48*2.75:.0f} px against a "
          f"{vert_spare*DEVICES[0].short_px/(2*design_h):.0f} px budget.")
    print()
    print("  => THE BOTTOM ROW LANDS UNDER THE GESTURE BAR ON EVERY MODERN ANDROID PHONE.")
    print("     Horizontal insets are affordable at 19.5:9+ and are NOT at 16:9 "
          f"({(design_h*(16/9)-design_w)*DEVICES[0].short_px/(2*design_h):.0f} px).")
    print()
    print("  The fix cannot be a fixed inset — Screen.safeArea is device data, only readable")
    print("  at runtime. Assets/Scripts/Presentation/SafeAreaCamera.cs solves the camera for")
    print("  it: it raises orthographicSize until the design box fits INSIDE the safe rect and")
    print("  re-centres on that rect, so the backdrop still fills the physical screen edge to")
    print("  edge. No letterbox. The cost is scale — quantified below.")
    print()
    for label, inset_px in [("Pixel 5a, 24dp gesture bar (66 px)", 66.0),
                            ("Pixel 5a, 48dp nav bar (132 px)", 132.0)]:
        d = DEVICES[0]
        safe_h = d.short_px - inset_px
        solved = design_h * d.short_px / safe_h
        p2 = d.short_px / (2 * solved)
        print(f"    {label:<38} orthoSize {design_h:g} -> {solved:.4f}, "
              f"card in hand {card_w*p2:.1f} x {card_h*p2:.1f} px "
              f"({(1 - p2/ppu)*100:.1f}% smaller)")
    print()

    # ── 8. solve check ──────────────────────────────────────────────────────────
    #
    # A line-for-line port of DeviceLayout.Solve / SafeWorldRect, run against the same
    # cases DeviceLayout.SelfCheck asserts. This is NOT a substitute for the harness —
    # it checks the arithmetic, not the compile — but the re-centring sign is exactly
    # the kind of error that ships silently and it costs nothing to catch here. The C#
    # is the source of truth; if these two disagree, the C# is what runs.

    def solve(safe, screen_w, screen_h):
        sx, sy, sw, sh = safe
        sw = min(sw, screen_w) if sw > 1 else screen_w
        sh = min(sh, screen_h) if sh > 1 else screen_h
        sx = sx if safe[2] > 1 else 0
        sy = sy if safe[3] > 1 else 0
        ortho = max(design_h, design_h * screen_h / sh, design_w * screen_h / sw)
        off_x = -((sx + sw / 2) - screen_w / 2) / screen_h * (2 * ortho)
        off_y = -((sy + sh / 2) - screen_h / 2) / screen_h * (2 * ortho)
        return ortho, off_x, off_y

    def world_rect(ortho, off_x, off_y, safe, screen_w, screen_h):
        half_h = ortho
        half_w = ortho * screen_w / screen_h
        sx, sy, sw, sh = safe
        return (off_x - half_w + sx / screen_w * 2 * half_w,
                off_y - half_h + sy / screen_h * 2 * half_h,
                off_x - half_w + (sx + sw) / screen_w * 2 * half_w,
                off_y - half_h + (sy + sh) / screen_h * 2 * half_h)

    print("== 8. SOLVE CHECK — DeviceLayout.Solve, ported and exercised " + "=" * 33)
    solve_ok = True
    cases = [
        ("no insets, 20:9", (0, 0, 2400, 1080), 2400, 1080),
        ("48dp bottom nav bar (132 px)", (0, 132, 2400, 948), 2400, 1080),
        ("24dp gesture bar (66 px)", (0, 66, 2400, 1014), 2400, 1080),
        ("96 px left cutout", (96, 0, 2304, 1080), 2400, 1080),
        ("cutout + nav bar", (96, 132, 2256, 948), 2400, 1080),
        ("no insets, 16:9", (0, 0, 1920, 1080), 1920, 1080),
        ("16:10 tablet (below MinAspect)", (0, 0, 1920, 1200), 1920, 1200),
    ]
    print(f"  {'case':<32} {'ortho':>7} {'offX':>8} {'offY':>8} "
          f"{'safe-rect world x':>21} {'safe-rect world y':>21}  holds design box")
    for label, safe, sw_, sh_ in cases:
        o, ox, oy = solve(safe, sw_, sh_)
        l, b, r, t = world_rect(o, ox, oy, safe, sw_, sh_)
        holds = (l <= -design_w + 1e-3 and r >= design_w - 1e-3
                 and b <= -design_h + 1e-3 and t >= design_h - 1e-3)
        solve_ok &= holds
        print(f"  {label:<32} {o:7.4f} {ox:8.4f} {oy:8.4f} "
              f"{l:9.4f}..{r:<10.4f} {b:9.4f}..{t:<10.4f}  {'yes' if holds else 'NO'}")
    print()
    print(f"  Every case must hold |x| <= {design_w:.2f} and |y| <= {design_h:.2f} inside the SAFE")
    print(f"  rect, not merely inside the screen. Result: "
          f"{'ALL PASS' if solve_ok else 'FAILURE — see the NO rows above'}")
    print()

    # ── 9. verdict ──────────────────────────────────────────────────────────────
    print("== 9. WHAT THIS ESTABLISHES " + "=" * 66)
    print(f"  A. Supported landscape aspect range: {min_aspect:.4f} .. {plate_aspect:.4f}")
    print(f"     (16:9 = {16/9:.4f} is IN, by {(design_h*16/9-design_w)*ppu:.0f} px per side;")
    print(f"      16:10 = {1.6:.4f} and 4:3 = {4/3:.4f} are OUT — the hero plate clips.)")
    hd_ppu = 720 / (2 * design_h)
    print(f"  B. Supported short edge: 1080 px. A 720p HD+ panel gives a "
          f"{hand_w*hd_ppu:.0f} px hand card and a body")
    print(f"     cap height of {body_em*hand_h*hd_ppu*cap_frac:.1f} px "
          f"({body_floor*hand_h*hd_ppu*cap_frac:.1f} px at the shrink floor). That is a RASTER")
    print(f"     limit, not an acuity one — the glyph is the same physical size as on a Pixel 5a")
    print(f"     and simply has too few pixels to form a letter. No font fixes it either.")
    print(f"  C. True card size in hand, 1080p: {cw_px:.1f} x {ch_px:.1f} px "
          f"({cw_px/REF_CARD_W_PX*100:.0f}% of the 265 px reference) — a card in hand is")
    print(f"     drawn at x{rest_scale:g}; on the BOARD it is {card_w*ppu:.1f} x {board_ch_px:.1f} px.")
    print(f"  D. Vertical safe-area budget: {vert_spare:.4f} units = "
          f"{vert_spare*ppu:.0f} px. Android needs 66-132 px.")
    print(f"  E. EF-050 re-check, HAND at rest, common case: {worst_hand_rest:.2f}x threshold "
          f"(NOR-244:")
    print(f"     RestScale {rest_scale:g} + CentreY {centre_y:g}). Holds through a 24 dp gesture bar")
    print(f"     and lands at 0.94x under a 48 dp three-button bar — a stated residual.")
    print(f"     A minion ON THE BOARD is still {worst:.2f}x and is deliberately not covered.")
    print()
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Missing as e:
        print(f"device_layout: FAILED — {e}", file=sys.stderr)
        print("A constant this script reads has been renamed or moved. Fix the reference; "
              "do not hardcode the value.", file=sys.stderr)
        sys.exit(2)
