"""Builds the public card teaser: public/cards.html and public/assets/cards/*.webp.

Only handles listed in gallery-allowlist.json are published, in that order. Everything else in
unity/Assets/CardData stays private, because card text and stats still change before launch.
A handle that is missing from the card data, or has no art, fails the build rather than
publishing a half-card. Run from anywhere:  python web/landing/scripts/build-gallery.py
"""
import glob
import html
import json
import sys
from pathlib import Path

from PIL import Image

SITE = Path(__file__).resolve().parent.parent
UNITY = SITE.parent.parent / "unity" / "Assets"
OUT_IMG = SITE / "public" / "assets" / "cards"
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
    stats = f'<span class="chip cost" title="Cost">{c["cost"]}</span>'
    if c["type"] == "MINION":
        stats += f'<span class="chip atk" title="Attack">{c["attack"]}</span><span class="chip hp" title="Health">{c["health"]}</span>'
    return f'''      <figure class="card r-{c["rarity"].lower()}">
        <img src="/assets/cards/{c["handle"]}.webp" alt="{name}, a {rarity.lower()} {html.escape(cls)} card" width="{THUMB}" height="{THUMB}" loading="lazy">
        <figcaption>
          <div class="stats">{stats}</div>
          <strong>{name}</strong>
          <em>{html.escape(cls)} &middot; {rarity}</em>
          <span>{html.escape(c["text"])}</span>
        </figcaption>
      </figure>'''


def main():
    cards = load_cards()
    handles = json.load(open(SITE / "gallery-allowlist.json", encoding="utf-8"))
    OUT_IMG.mkdir(parents=True, exist_ok=True)
    for old in OUT_IMG.glob("*.webp"):
        old.unlink()
    body = []
    for h in handles:
        c = cards.get(h)
        if c is None:
            sys.exit(f"allowlisted handle not in card data: {h}")
        src = UNITY / "Art" / "Cards" / c["setId"] / f'{c["artId"]}.png'
        if not src.exists():
            sys.exit(f"no art for {h}: {src}")
        Image.open(src).convert("RGB").resize((THUMB, THUMB), Image.LANCZOS).save(OUT_IMG / f"{h}.webp", quality=82, method=6)
        body.append(card_html(c))
    page = (SITE / "scripts" / "cards.template.html").read_text(encoding="utf-8").replace("{{CARDS}}", "\n".join(body))
    (SITE / "public" / "cards.html").write_text(page, encoding="utf-8", newline="\n")
    print(f"published {len(handles)} cards")
    for h in handles:
        print(" ", h)


if __name__ == "__main__":
    main()
