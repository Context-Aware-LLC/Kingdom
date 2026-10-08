"""Builds the public card teaser and the hero pages: public/cards.html, public/heroes/*.html,
public/assets/cards/*.webp, and the hero roster inside public/index.html.

Only handles listed in gallery-allowlist.json are published, in that order. Everything else in
unity/Assets/CardData stays private, because card text and stats still change before launch.
A handle that is missing from the card data, or has no art, fails the build rather than
publishing a half-card. Each hero in heroes.json gets a page showing its copy, its class's hero
power as the card data words it, and that class's allowlisted cards; a hero with no hero power or
no allowlisted card fails the build too. Run from anywhere:
python web/landing/scripts/build-gallery.py
"""
import glob
import html
import json
import re
import sys
from pathlib import Path

from PIL import Image

SITE = Path(__file__).resolve().parent.parent
UNITY = SITE.parent.parent / "unity" / "Assets"
OUT_IMG = SITE / "public" / "assets" / "cards"
OUT_HEROES = SITE / "public" / "heroes"
THUMB = 480

CLASS_LABEL = {"EMBERWARD": "Emberward", "TIDECALLER": "Tidecaller", "GRAVEKEEP": "Gravekeep", "VERDANT": "Verdant",
               "CLOCKWRIGHT": "Clockwright", "NIGHTVEIL": "Nightveil", "STONEWARDEN": "Stonewarden", "TEMPEST": "Tempest"}


def load_cards():
    cards = {}
    for f in glob.glob(str(UNITY / "CardData" / "*.cards.json")):
        for c in json.load(open(f, encoding="utf-8")):
            cards[c["handle"]] = c
    return cards


def card_html(c):
    name = html.escape(c["name"].strip())
    cls = CLASS_LABEL.get(c["class"], c["class"].title())
    rarity = c["rarity"].title()
    article = "an" if rarity[0] in "AEIOU" else "a"
    return f'''      <figure class="card r-{c["rarity"].lower()}">
        <img src="/assets/cards/{c["handle"]}.webp" alt="{name}, {article} {rarity.lower()} {html.escape(cls)} card" width="{THUMB}" height="{THUMB}" loading="lazy">
        <figcaption>
          <strong>{name}</strong>
          <em>{html.escape(cls)} &middot; {rarity}</em>
          <span>{html.escape(c["text"])}</span>
        </figcaption>
      </figure>'''


def roster_html(h):
    name = CLASS_LABEL[h["class"]]
    return (f'        <figure class="card"><a href="/heroes/{h["slug"]}"><img src="/assets/hero-{h["slug"]}.webp" alt="{html.escape(h["alt"])}" '
            f'width="560" height="560" loading="lazy"><figcaption><strong>{name}</strong><span>{html.escape(h["tagline"])}</span></figcaption></a></figure>')


def hero_page(template, heroes, i, cards, published):
    h = heroes[i]
    name = CLASS_LABEL[h["class"]]
    powers = [c for c in cards.values() if c["class"] == h["class"] and c["type"] == "HERO_POWER"]
    if len(powers) != 1:
        sys.exit(f'{h["slug"]}: expected one hero power, found {len(powers)}')
    own = [c for c in published if c["class"] == h["class"]]
    if not own:
        sys.exit(f'{h["slug"]}: no allowlisted cards')
    prev, nxt = heroes[i - 1], heroes[(i + 1) % len(heroes)]
    traits = "\n".join(f"        <dt>{html.escape(t)}</dt>\n        <dd>{html.escape(d)}</dd>" for t, d in h["traits"])
    fields = {
        "NAME": name, "SLUG": h["slug"], "TAGLINE": html.escape(h["tagline"]), "ALT": html.escape(h["alt"]),
        "PLAY": html.escape(h["play"]), "TRAITS": traits,
        "POWER_NAME": html.escape(powers[0]["name"].strip()), "POWER_TEXT": html.escape(powers[0]["text"]),
        "POWER_COST": str(powers[0]["cost"]),
        "CARDS": "\n".join(card_html(c) for c in own),
        "PREV_SLUG": prev["slug"], "PREV_NAME": CLASS_LABEL[prev["class"]],
        "NEXT_SLUG": nxt["slug"], "NEXT_NAME": CLASS_LABEL[nxt["class"]],
    }
    return re.sub(r"\{\{(\w+)\}\}", lambda m: fields[m.group(1)], template), len(own)


def main():
    cards = load_cards()
    handles = json.load(open(SITE / "gallery-allowlist.json", encoding="utf-8"))
    OUT_IMG.mkdir(parents=True, exist_ok=True)
    for old in OUT_IMG.glob("*.webp"):
        old.unlink()
    published = []
    for h in handles:
        c = cards.get(h)
        if c is None:
            sys.exit(f"allowlisted handle not in card data: {h}")
        src = UNITY / "Art" / "Cards" / c["setId"] / f'{c["artId"]}.png'
        if not src.exists():
            sys.exit(f"no art for {h}: {src}")
        Image.open(src).convert("RGB").resize((THUMB, THUMB), Image.LANCZOS).save(OUT_IMG / f"{h}.webp", quality=82, method=6)
        published.append(c)
    page = (SITE / "scripts" / "cards.template.html").read_text(encoding="utf-8").replace("{{CARDS}}", "\n".join(card_html(c) for c in published))
    (SITE / "public" / "cards.html").write_text(page, encoding="utf-8", newline="\n")
    print(f"published {len(handles)} cards")
    for h in handles:
        print(" ", h)

    heroes = json.load(open(SITE / "heroes.json", encoding="utf-8"))
    template = (SITE / "scripts" / "hero.template.html").read_text(encoding="utf-8")
    OUT_HEROES.mkdir(parents=True, exist_ok=True)
    print(f"published {len(heroes)} hero pages")
    for i, h in enumerate(heroes):
        page, n = hero_page(template, heroes, i, cards, published)
        (OUT_HEROES / f'{h["slug"]}.html').write_text(page, encoding="utf-8", newline="\n")
        print(f'  {h["slug"]}: {n} cards')

    # The home page is hand-written apart from the roster, which links to the pages above.
    index = SITE / "public" / "index.html"
    text = index.read_text(encoding="utf-8")
    roster = "<!-- roster:start -->\n" + "\n".join(roster_html(h) for h in heroes) + "\n        <!-- roster:end -->"
    text, n = re.subn(r"<!-- roster:start -->.*?<!-- roster:end -->", lambda m: roster, text, flags=re.S)
    if n != 1:
        sys.exit("index.html: roster markers not found")
    index.write_text(text, encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
