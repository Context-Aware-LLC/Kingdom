"""Generate draft card-art prompt briefs from card data + class descriptors + hand-authored seeds.

WHY THIS EXISTS (Sam, 2026-08-01). Composing a prompt by hand means pulling stats/rarity/
keywords from card.json, cross-referencing the class palette table, cross-referencing the
world-tone doc, remembering the output-folder rule, and remembering the scale of the thing you
are describing should track the card's actual power level, not the model's default "epic boss
monster" instinct. This script collates all of that automatically, the way the Unity card
authoring tool collates existing data for you, and leaves only the genuinely creative fields
(subject, perspective, background, tone, notes) for a human to fill in via a seed file.

WHAT IS AUTO-GENERATED, and is NOT precious:
  - DELIVERY / COMPOSITION / STYLE / NEGATIVE PROMPT boilerplate, pulled from
    dev-docs/specs/card-generation-prompt.md so that doc stays the one source of truth.
    NOTE: COMPOSITION's first bullet is deliberately reworded from the doc's literal text.
    The doc's wording ("the frame may cover the outer 15 percent...") reads as an executable
    edit instruction to Codex's agentic image_gen tool and triggers an unrequested crop/edit
    call that then hangs on an approval gate (found 2026-08-01). This script emits the
    already-fixed descriptive phrasing instead. If the source doc's wording ever changes,
    reconcile this constant by hand — it is intentionally NOT a verbatim mirror.
  - Subject class + palette/motifs/mood, looked up from dev-docs/descriptors/classes.md's
    table by the card's `class`. Never hardcoded here — parsed at runtime so it cannot drift.
  - World tone, from dev-docs/descriptors/world.md (base + REKINDLING section when relevant).
  - Output path + canonical filename, from setId/artId. ONE rule, no per-class exceptions:
    art-pipeline/card-art/<setId>/<artId>/<artId>.png. The PNG stem repeats the enclosing
    leaf-directory name exactly. gen_card_art.py used to key candidates by a CLASS group
    instead, which put a card's draft and its candidates in different directories for 92 of
    260 cards; that split was retired 2026-08-06 and both are keyed by setId now.
  - SCALE and VISUAL POWER clauses, FOR MINIONS ONLY, derived from mana cost and body size (see
    `scale_clause`). These directly fix the failure mode where a cheap card renders as a titanic
    boss or battlefield-wide catastrophe: the model's default register is maximalist, so lower
    mana rungs explicitly demand tighter framing, smaller effects, fewer particles, and less
    environmental destruction. Rarity does NOT feed size — it is a drop rate, not a claim about
    how big the thing is — and can only ever raise a tier, never suppress one.
  - SPELLS get no size tier at all (Sam, 2026-08-06): a spell has no body to be large or small,
    and tiering it pushes toward generic magical effects. It is told instead to deliver what its
    NAME leads the viewer to picture, at the intensity the name implies rather than the mana.
  - WEAPONS and HERO POWERS likewise get no size tier: both render into a CIRCULAR SOCKET on the
    hero frame rather than a card frame (HeroSocketsView builds them as socket art), so they are
    told to read as one centred object legible as a silhouette at icon size, corners empty.

WHAT IS A SEED (human-authored, tracked, never auto-overwritten):
  tooling/card-art-seeds/<setId>.json, keyed by card id:
    { "subject": "...", "perspective": "...", "background": "...", "tone": "...", "notes": "..." }
  Deliberately NOT in art-pipeline/ (untracked, disposable per CLAUDE.md) and NOT inside
  unity/Emberfall/Assets/CardData/ (that file is gameplay data feeding the determinism hash;
  art-authoring metadata does not belong mixed into it).

A card with no seed entry still gets a full prompt: every field is filled with a generic,
clearly-marked placeholder built from name/tribe/keywords. That placeholder is
"plausible but general" by design, per Sam's own description of the tool -- a draft to react
to and rewrite, never a finished brief to submit as-is.

Usage:
  gen_card_prompt_briefs.py --set EMBERFALL --card ember_scalded_striker --card tide_venomfin
  gen_card_prompt_briefs.py --set EMBERFALL --class verdant
  gen_card_prompt_briefs.py --set EMBERFALL          # every collectible card in the set
"""

import argparse
import json
import os
import pathlib
import re
import sys

# Windows cp1252 consoles raise UnicodeEncodeError on em-dashes; degrade the display
# instead of crashing mid-run. Every file this script writes is explicit utf-8.
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(errors="replace")
    except (AttributeError, ValueError):
        pass

REPO = pathlib.Path(__file__).resolve().parents[2]
DOCS = pathlib.Path(
    os.environ.get(
        "EMBERFALL_DOCS",
        pathlib.Path.home() / "docs",
    )
)
CARD_DATA_DIR = REPO / "unity" / "Emberfall" / "Assets" / "CardData"
SEEDS_DIR = REPO / "tooling" / "card-art-seeds"
OUT_ROOT = REPO / "art-pipeline" / "card-art"

CLASSES_MD = DOCS / "dev-docs" / "descriptors" / "classes.md"
WORLD_MD = DOCS / "dev-docs" / "descriptors" / "world.md"

# ---------------------------------------------------------------- boilerplate

DELIVERY_TMPL = """DELIVERY
- 1024x1024 PNG in sRGB.
- Output Path: {out_dir}
- Canonical filename: {filename}.
- The PNG filename must exactly match its enclosing directory name, plus .png."""


def art_file(card):
    """THE definition of a card's art filename, for every script in this directory.

    It READS the row's artId; it does not rebuild "art_<cardId>". Those two agree only while
    every row happens to be authored as artId "art/<cardId>", and the moment one is not, a
    caller looks for art that exists under a name it never checks — reporting the card as
    having none, which puts it back in the generation queue.

    This lived in three places (here, card_art_editor.py, gen_card_art.py) and two of them
    were guessing. Import it; do not re-derive it.

    The Replace is a no-op today: the "art/" namespace was dropped 2026-08-06 (it had one
    value across every row, so it named nothing), leaving artId == filename. It stays because
    a namespaced id, if one is ever introduced, must still flatten - filenames never contain
    path separators.
    """
    return card["artId"].replace("/", "_")


def delivery_block(card):
    """Build the one canonical editor/generation destination for a card.

    Keep this shared between the CLI brief generator and browser editor so neither can
    accidentally omit the per-card leaf directory again.
    """
    stem = art_file(card)
    out_dir = OUT_ROOT / card["setId"] / stem
    return DELIVERY_TMPL.format(out_dir=f"{out_dir}{os.sep}", filename=f"{stem}.png")

# Reworded from dev-docs/specs/card-generation-prompt.md — see module docstring for why.
COMPOSITION = """COMPOSITION
Compose with generous margin — keep the subject's focal point centered, since a frame will overlay roughly the outer edge on all sides.
- Bleed art to every edge. No border and no vignette-to-black.
- No text, letters, logos, signatures, watermarks, frames, or UI elements.
- The image must read at 220 px: one clear subject, a strong silhouette, and strong value contrast.
- Painterly digital fantasy illustration. Not photorealistic, not a 3D render, and not anime or cel-shaded."""

STYLE = """STYLE
Stylized fantasy trading-card-game illustration, painterly digital art, dramatic rim lighting, single clear subject with strong silhouette, rich saturated colors, dynamic composition, centered focal point, background supports but never competes with subject, professional card game art quality, no text, no border, no frame, no watermark."""

NEGATIVE_PROMPT = """NEGATIVE PROMPT
text, letters, watermark, signature, frame, border, UI, photorealism, 3D render, anime, blurry, multiple competing subjects, cropped subject head"""

# A starting color/temperature note only, not a scene — mechanical hue opposition against each
# class's own subject palette, the same heuristic applied by hand for every prompt this session.
BACKGROUND_OPPOSITION_HINTS = {
    "emberward": "warm subject, so lean cool and dim to start (night sky, storm, cool stone)",
    "tidecaller": "cool subject, so lean warm and dim to start (amber candlelight, ember-glow, dusk)",
    "gravekeep": "cool subject, so lean warm and dim to start (amber candlelight, ember-glow, dusk)",
    "verdant": "warm subject, so lean cool and muted to start (pale grey, blue-toned twilight)",
    "clockwright": "mixed warm/cool subject palette already, so lean on VALUE contrast (very dark or very light) rather than a new hue",
    "nightveil": "cool/dark subject, so lean warm and dim to start (amber candlelight, ember-glow)",
    "stonewarden": "cool-neutral subject, so lean warm and dim to start (amber candlelight, ember-glow)",
    "tempest": "electric, high-saturation subject, so lean muted and grounded to start (dull earth tones, overcast)",
    "neutral": "warm-neutral subject, so lean cool and muted to start (pale grey, blue-toned twilight)",
}


# ------------------------------------------------------------ parsed sources

def load_class_descriptors():
    """class name (lowercased) -> (palette, motifs, mood), parsed from classes.md's table."""
    md = CLASSES_MD.read_text(encoding="utf-8")
    out = {}
    for line in md.splitlines():
        if not line.startswith("|") or line.startswith("|---") or "Subject palette" in line:
            continue
        cells = [c.strip() for c in line.strip("|").split("|")]
        if len(cells) != 4:
            continue
        cls, palette, motifs, mood = cells
        out[cls.strip().lower()] = (palette, motifs, mood)
    return out


def load_world_tone(set_id):
    md = WORLD_MD.read_text(encoding="utf-8")
    sections = re.split(r"^##\s+", md, flags=re.M)[1:]
    base, rekindling = "", ""
    for sec in sections:
        title, _, body = sec.partition("\n")
        title = title.strip().lower()
        body = body.strip()
        if title == "base world":
            base = body
        elif title == "rekindling expansion" and set_id == "REKINDLING":
            rekindling = body
    tone = (base + ("\n\n" + rekindling if rekindling else "")).strip()
    # world.md closes each section with a "**Provenance:** [doc](path) §3 ..." citation. That
    # is bookkeeping for a human reading the descriptor, and every consumer of this function
    # pastes the result into an IMAGE prompt whose own STYLE block says "no text, no letters".
    # Sending markdown link syntax and section marks to an image model is at best noise and at
    # worst something it tries to draw, so drop the citation and keep the tone prose.
    return _PROVENANCE.sub("", tone).strip()


_PROVENANCE = re.compile(r"^\s*\*{0,2}Provenance:?\*{0,2}.*$", re.M)


def load_seeds(set_id):
    fp = SEEDS_DIR / f"{set_id}.json"
    if not fp.exists():
        return {}
    return json.loads(fp.read_text(encoding="utf-8"))


def load_cards(set_id, class_filter=None, card_ids=None):
    cards = []
    for fp in sorted(CARD_DATA_DIR.glob("*.cards.json")):
        cards.extend(json.loads(fp.read_text(encoding="utf-8")))
    cards = [c for c in cards if c.get("setId") == set_id and c.get("collectible", True)]
    if card_ids:
        wanted = set(card_ids)
        cards = [c for c in cards if c["id"] in wanted]
    if class_filter:
        cards = [c for c in cards if c.get("class", "").upper() == class_filter.upper()]
    return cards


# --------------------------------------------------------------- scale clause

SCALE_TIERS = {
    0: "small and unglamorous — modest scale, nothing here should read as imposing or dramatic.",
    1: "solid but unremarkable in scale — competent, not extraordinary.",
    2: "sizable — a genuine mid-to-late-game presence, but not the biggest thing on the board.",
    3: "commanding — one of the biggest, most dramatic shapes in its class.",
}

VISUAL_POWER_TIERS = {
    0: "a small, local moment. Keep the subject modest in-frame and any magic no larger than the subject; use sparse particles and no towering or catastrophic staging.",
    1: "compact skirmish scale. Show one restrained action or effect with room to breathe; no battlefield-wide wave, giant-creature framing, or explosive sky.",
    2: "substantial but localized. It may dominate an immediate room, gate, or short stretch of street, but not a fortress, city, or landscape; keep secondary effects controlled.",
    3: "a major encounter. It may dominate one battlefield scene, but should not imply world-ending power unless the card text explicitly demands it.",
    4: "monumental. Fortress- or landscape-scale staging is allowed only when the card's identity and mechanics support it.",
}


SPELL_INTENT = (
    "READS FROM THE NAME (spell). A spell has no body, so it has no size — it is not staged by\n"
    "cost or rarity at all. The illustration's whole job is to deliver what \"{name}\" leads a\n"
    "player to picture before they read a word of the text. Chase the image the title puts in\n"
    "the viewer's head and commit to it. A specific, evocative reading of the name beats a\n"
    "generic magical effect at any mana cost; match the intensity the NAME implies, not the mana."
)

ICON_INTENT = (
    "READS AS AN ICON ({kind}) — do not stage this like a card illustration. It is rendered into\n"
    "a CIRCULAR SOCKET on the hero frame (HeroSocketsView builds it as socket art), not into a\n"
    "card frame. Compose ONE object, centred, whole, and legible as a silhouette at icon size.\n"
    "No scene, no environment, no narrative staging, and nothing that matters near the corners —\n"
    "the circle crops them. Size tiers do not apply: the subject is the object itself, and what\n"
    "\"{name}\" suggests it looks like is the only brief that matters."
)


def scale_clause(card):
    """COST AND BODY DECIDE SIZE, FOR MINIONS ONLY. RARITY DOES NOT (Sam, 2026-08-06).

    SPELLS AND WEAPONS GET NO SIZE TIER AT ALL. Tiering assumes the subject is a creature that
    can be large or small; a spell has no body, and a weapon is an object in a circular socket
    on the hero frame rather than a scene. Telling either one to look "commanding" or "modest"
    pushes toward generic staging and away from the thing that actually sells them — for a
    spell, what its NAME makes the viewer expect to see; for a weapon or hero power, reading
    cleanly as a single silhouette at icon size.

    This used to be rarity-first, with COMMON hard-capped at tier 1 no matter what it cost.
    That cap came from the Blazewing finding — a cheap card rendered as a titanic boss — but it
    fixed the wrong variable. Blazewing was CHEAP, not common; rarity is a drop-rate and an
    identity signal, not a statement about how big the thing is. The cap meant an 8-cost common
    was told to look "solid but unremarkable" while the VISUAL POWER line, which has always been
    cost-driven, told the same brief "monumental" — the two clauses contradicted each other
    inside one prompt.

    Cost is the spine now, so the Blazewing protection still holds: a 1-cost anything stays
    tier 0. Rarity can only ever RAISE, never suppress.
    """
    cost = card.get("cost", 0)
    rarity = card.get("rarity", "COMMON")
    card_type = card.get("type", "MINION")
    name = card.get("name", "this card")

    if card_type == "SPELL":
        return SPELL_INTENT.format(name=name)
    if card_type in ("WEAPON", "HERO_POWER"):
        kind = "weapon" if card_type == "WEAPON" else "hero power"
        return ICON_INTENT.format(kind=kind, name=name)

    attack = card.get("attack") or 0
    health = card.get("health") or 0
    body = attack + health

    if cost <= 1:
        tier = 0
    elif cost <= 3:
        tier = 1
    elif cost <= 5:
        tier = 2
    else:
        tier = 3

    # A body far larger than its rung reads big whatever it cost or however common it is.
    if body >= 12 or attack >= 7 or health >= 8:
        tier = min(3, tier + 1)

    # LEGENDARY is an identity asset and should never be drawn diminutively — but a 1-cost
    # legendary is still a 1-cost thing, so this raises by one rather than jumping to the top.
    if rarity == "LEGENDARY":
        tier = min(3, tier + 1)

    if cost <= 1:
        power_tier = 0
    elif cost <= 3:
        power_tier = 1
    elif cost <= 5:
        power_tier = 2
    elif cost <= 7:
        power_tier = 3
    else:
        power_tier = 4

    return (
        f"SCALE (cost {cost}, {rarity.title()}): {SCALE_TIERS[tier]}\n"
        f"VISUAL POWER (cost {cost}): {VISUAL_POWER_TIERS[power_tier]}"
    )


# --------------------------------------------------------------- placeholder

def placeholder_subject(card):
    bits = [card["name"]]
    if card.get("tribe") and card["tribe"] != "NONE":
        article = "an" if card["tribe"][0] in "AEIOU" else "a"
        bits.append(f"{article} {card['tribe'].lower()}")
    if card.get("keywords"):
        bits.append(f"keywords {', '.join(card['keywords'])}")
    return (
        "[DRAFT — placeholder only, replace with a real subject before generating] "
        + "; ".join(bits) + "."
    )


# ------------------------------------------------------------------- compose

def compose_brief(card, class_descriptors, world_tone, seed):
    cls = card.get("class", "NEUTRAL").lower()
    palette, motifs, mood = class_descriptors.get(cls, ("", "", ""))

    subject = (seed or {}).get("subject") or placeholder_subject(card)
    perspective = (seed or {}).get("perspective") or "[perspective unset]"
    bg_hint = BACKGROUND_OPPOSITION_HINTS.get(cls, "oppose the subject palette in hue or temperature")
    background = (seed or {}).get("background") or f"[unset — starting hint: {bg_hint}; replace with a real setting]"
    tone = (seed or {}).get("tone") or mood
    notes = (seed or {}).get("notes") or ""

    header = f"Generate one Emberfall card illustration for {card['name']} ({card['id']})."
    delivery = delivery_block(card)
    subj_opp = (
        "SUBJECT AND BACKGROUND OPPOSITION\n"
        f"- Subject class: {card.get('class', 'NEUTRAL').title()}.\n"
        f"- Subject palette, motifs, and mood: {palette}; {motifs}; {tone}.\n"
        f"- Perspective: {perspective}\n"
        f"- Background palette and setting: {background}\n"
        "- Make the subject and background oppose each other in hue, value, or temperature."
    )
    world = f"WORLD\n{world_tone}"
    subject_block = f"SUBJECT\n{scale_clause(card)}\n{subject}"
    if notes:
        subject_block += f"\n{notes}"

    return "\n\n".join(
        [header, delivery, COMPOSITION, subj_opp, world, STYLE, subject_block, NEGATIVE_PROMPT]
    )


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--set", required=True)
    ap.add_argument("--class", dest="class_filter", default=None)
    ap.add_argument("--card", dest="card_ids", action="append", default=None)
    ap.add_argument("--out", default=None, help="write to this file instead of stdout")
    args = ap.parse_args()

    class_descriptors = load_class_descriptors()
    world_tone = load_world_tone(args.set)
    seeds = load_seeds(args.set)
    cards = load_cards(args.set, args.class_filter, args.card_ids)

    if not cards:
        print("No matching cards found.", file=sys.stderr)
        sys.exit(1)

    briefs = [compose_brief(c, class_descriptors, world_tone, seeds.get(c["id"])) for c in cards]
    output = "\n\n---\n\n".join(briefs) + "\n"

    if args.out:
        pathlib.Path(args.out).write_text(output, encoding="utf-8")
        print(f"Wrote {len(cards)} briefs to {args.out}", file=sys.stderr)
    else:
        sys.stdout.write(output)


if __name__ == "__main__":
    main()
