"""Generate launcher-icon candidates for Emberfall.

Sam's ask (2026-07-27): a launcher icon that reads as "a game" at a glance —
not specifically "a trading card game" — pulling from the palette already
established on the main menu backdrop and loading screen: near-black void,
ember orange/gold fire, cold teal/frost light, aged iron/bronze. Sam's follow-
up call, same session: use Gemini, not gpt-image-2 — this is a cheap icon
sketch pass, not house-look card art, so skip the "fancy" model.

SCALE IS THE WHOLE PROBLEM, same rule as every other UI asset in this repo
(gen_hero_frames.py, gen_card_backs.py): a launcher icon is judged at home-
screen size, commonly 48-192px, occasionally larger in a store listing. Few
large shapes, strong value contrast, nothing thin survives. No text, no
wordmark — platforms already print the app name next to the icon.

Generated square at EDGE x EDGE (gen_card_art.py's Gemini client + 1:1 aspect,
imported rather than reimplemented — same JPEG->PNG transcode and square
conform logic as the card-art pipeline). Full bleed, subject fills the frame —
the OS applies its own mask (rounded square / circle / squircle), so do not
bake in a border or a circular vignette.

Per repo convention (CLAUDE.md, 2026-07-26): this writes to art-pipeline/, never
into the Unity project. Wiring a chosen candidate into PlayerSettings icons is
a separate, later step.

Usage: gen_launcher_icon.py [variant ...]
"""

import pathlib
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

from gen_card_art import EDGE, REPO, _conform, _post_gemini  # noqa: E402

OUT = REPO / "art-pipeline" / "ui" / "brand" / "icons"

COMMON = """
A launcher/app icon for a dark-fantasy digital game called "Emberfall", square
format, subject filling the entire frame edge to edge. FULL BLEED — no border,
no frame, no vignette, no rounded-corner mockup, no phone/device mockup, no
drop shadow, no background gradient sitting behind a small centered logo.

ABSOLUTELY NO TEXT. No letters, no numerals, no runes that resemble writing,
no wordmark, no logo type, no watermark, no signature.

NOT A CARD. Do not depict a playing card, a card frame, a hand of cards, or
any card-game furniture. The icon should read as "a game" in general, not as
"a trading card game" specifically.

KEEP IT BOLD. This will be viewed as small as 48 pixels square, most commonly
under 200px. Use ONE clear silhouette and FEW large shapes with strong
light-to-dark contrast. NO fine tracery, NO hairline linework, NO tiny repeated
detail, NO delicate filigree — anything thinner than a heavy stroke turns to
grey noise at icon size. It must still read instantly as a single clear shape
when shrunk to a thumbnail.

PALETTE — pull from the game's established look: a near-black void base;
molten ember orange and gold as the dominant warm light; a cold pale teal/blue
as a secondary accent light (frost, or a storm-lit sky); aged dark iron and
bronze for any hard material. Do not introduce a palette family the game
hasn't already established (no random flat pastels, no bright saturated
primary red/purple/pink).

The world: "Emberfall" — a land after a sky-fire cataclysm, where embers still
fall like snow. Mood is old, weathered, hand-forged and painterly — not a flat
vector logo, not a photo, not 3D-render-clean. A dark, painted, atmospheric
object.
"""

VARIANTS = {
    "EmberOrb": """
SUBJECT — A CRACKED EMBER ORB. A single dark, weathered sphere or orb, nearly
black, seen head-on and centered, filling almost the whole frame. A thick
crack splits across it, molten ember-orange light glowing out from inside the
crack. A thin ring or arc of cold pale teal light wraps partway around the
opposite side of the orb, like a rime of frost catching starlight. The dark
sphere itself is the dominant shape; the two lights are small, bright accents,
not equal partners. Reads instantly as one bold circular silhouette.
""",
    "SunSigil": """
SUBJECT — A FORGED SUN-SIGIL. One bold, heavy sigil in aged bronze and dark
iron, built from a few thick radiating spokes around a small central disc —
like a crude ancient sun symbol hammered out of metal, not a delicate
snowflake. It sits centered on a deep near-black background lit from directly
behind it with a molten ember-orange glow, so the sigil itself reads as a dark
bronze silhouette rimmed in fire light. A faint cold teal haze sits far in the
background, well behind the sigil. The sigil must be simple enough that its
outer silhouette alone is recognizable at 48px — no more than a handful of
spokes, no fine engraving on the metal.
""",
    "CrackedCore": """
SUBJECT — A SHATTERED EMBER CORE. A single large chunk of broken dark stone or
obsidian, roughly rounded, split clean open down the middle like a cracked
geode, with blinding molten ember-orange light bursting out of the crack
between the two halves. The stone exterior is matte near-black; only the
crack itself is bright. A subtle cold teal glow rims the stone's outer edge
where it catches distant light. One dominant shape, one bright focal crack —
nothing else competing for attention.
""",
    "EmberFlame": """
SUBJECT — A SINGLE RISING EMBER FLAME. One large, bold, simplified flame
shape rising from a small dark base, rendered as molten ember-orange and gold,
darkening toward black at its own core so the flame reads as a thick solid
silhouette rather than a wispy sketch. A thin curl of cold pale teal smoke or
mist trails off one side of the flame, the only cool note in an otherwise
warm, dark composition. The flame fills most of the frame vertically, simple
enough to read as one shape at a glance.
""",
}


def build_prompt(variant: str) -> str:
    return (COMMON + VARIANTS[variant]).strip()


def generate(name: str, key: str) -> bool:
    prompt = build_prompt(name)
    print(f"[{name}] requesting {EDGE}x{EDGE} from gemini ...", flush=True)
    t0 = time.time()
    raw, err = _post_gemini(prompt, key)
    if raw is None:
        print(f"[{name}] FAILED {err}", flush=True)
        return False
    dest = OUT / f"LauncherIcon_{name}_gemini.png"
    dest.write_bytes(raw)
    dims = _conform(dest, name)
    print(f"[{name}] -> {dest.name}  ({len(raw)/1e6:.1f} MB, {dims}, {time.time()-t0:.0f}s)",
          flush=True)
    return True


def main() -> int:
    import os

    key = os.environ.get("GEMINI_API_KEY")
    if not key:
        print("GEMINI_API_KEY is not set in THIS process. A User env var set "
              "after the shell started is not visible here; pull it in the "
              "same command:\n"
              "  $env:GEMINI_API_KEY = [Environment]::GetEnvironmentVariable("
              "'GEMINI_API_KEY','User'); python ...", file=sys.stderr)
        return 2

    names = sys.argv[1:] or list(VARIANTS)
    for n in names:
        if n not in VARIANTS:
            print(f"unknown variant '{n}' (have: {', '.join(VARIANTS)})", file=sys.stderr)
            return 2

    OUT.mkdir(parents=True, exist_ok=True)
    ok = sum(1 for n in names if generate(n, key))
    print(f"\n{ok}/{len(names)} generated -> {OUT}")
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main())
