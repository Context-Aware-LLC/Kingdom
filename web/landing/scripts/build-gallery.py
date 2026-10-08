"""Builds the public card teaser, the hero pages and the keyword glossary: public/cards.html,
public/heroes/*.html, public/docs.html, public/docs/keywords.html, public/assets/cards/*.webp, and the hero roster inside
public/index.html.

Only handles listed in gallery-allowlist.json are published, in that order. Everything else in
unity/Assets/CardData stays private, because card text and stats still change before launch.
A handle that is missing from the card data, or has no art, fails the build rather than
publishing a half-card. Each hero in heroes.json gets a page showing its copy, its class's hero
power as the card data words it, and that class's allowlisted cards; a hero with no hero power or
no allowlisted card fails the build too. A keyword is defined once, on the glossary page, in the
words the game itself uses (KeywordCopy.Glossary); a hero page only names its keywords and links
there, and a name the glossary does not hold fails the build. Run from anywhere:
python web/landing/scripts/build-gallery.py
"""
import glob
import hashlib
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
OUT_POWERS = SITE / "public" / "assets" / "powers"
# Twice the size the page shows it at, for dense screens.
POWER_ICON = 192
KEYWORD_COPY = UNITY / "Scripts" / "Presentation" / "KeywordCopy.cs"
KEYWORD_SEMANTICS = SITE.parent.parent / "src" / "Emberfall.Core.Cards" / "KeywordSemantics.cs"
# REINFORCE's glossary line is a placeholder no player meets; KeywordCopy says so at the line.
UNPUBLISHED_KEYWORDS = {"REINFORCE"}
# Toll is a trigger label (CardTextGenerator.TriggerLabel), not a Keyword, so the game holds no
# glossary line for it. It is the one definition written here.
TRIGGER_TERMS = {"TOLL": "Happens when this minion dies."}
# The docs section's own pages, in side-menu order. A new one needs a template and a line here.
DOCS_PAGES = [("Start here", "/docs"), ("Keywords", "/docs/keywords")]
THUMB = 480

CLASS_LABEL = {"EMBERWARD": "Emberward", "TIDECALLER": "Tidecaller", "GRAVEKEEP": "Gravekeep", "VERDANT": "Verdant",
               "CLOCKWRIGHT": "Clockwright", "NIGHTVEIL": "Nightveil", "STONEWARDEN": "Stonewarden", "TEMPEST": "Tempest"}


def load_cards():
    cards = {}
    for f in glob.glob(str(UNITY / "CardData" / "*.cards.json")):
        for c in json.load(open(f, encoding="utf-8")):
            cards[c["handle"]] = c
    return cards


def load_keywords():
    """Keyword id -> (printed name, definition), read from the game's own copy."""
    # The game prints a keyword as its humanised id unless KeywordSemantics.AuthoredName overrides
    # it. Nothing does today; if that changes, this build must read the override rather than guess.
    semantics = KEYWORD_SEMANTICS.read_text(encoding="utf-8")
    if not re.search(r"AuthoredName\(Keyword keyword\)(?:\s*//[^\n]*)*\s*=> null;", semantics):
        sys.exit("KeywordSemantics.AuthoredName now names a keyword: teach load_keywords to read it")
    found = re.findall(r'Keyword\.(\w+) =>\s*"((?:[^"\\]|\\.)*)"', KEYWORD_COPY.read_text(encoding="utf-8"))
    if not found:
        sys.exit(f"no glossary lines found in {KEYWORD_COPY}")
    terms = {k: text for k, text in found if k not in UNPUBLISHED_KEYWORDS}
    terms.update(TRIGGER_TERMS)
    return {k: (" ".join(w.capitalize() for w in k.split("_")), text) for k, text in terms.items()}


def keyword_anchor(k):
    return k.lower().replace("_", "-")


def docs_toc(sections):
    """The page's own anchors, shown on the right. Empty for a page with none."""
    if not sections:
        return ""
    links = "\n".join(f'      <li><a href="{a}">{html.escape(s)}</a></li>' for s, a in sections)
    return f'  <nav class="docs-toc" aria-label="On this page">\n    <p>On this page</p>\n    <ul>\n{links}\n    </ul>\n  </nav>'


def docs_nav(current):
    """The side menu every docs page carries. It lists the docs and nothing else (Sam, 2026-10-08)."""
    def item(label, href):
        here = ' aria-current="page"' if href == current else ""
        return f'      <li><a href="{href}"{here}>{html.escape(label)}</a></li>'

    items = "\n".join(item(label, href) for label, href in DOCS_PAGES)
    return f'  <nav class="docs-nav" aria-label="Docs">\n    <p>Docs</p>\n    <ul>\n{items}\n    </ul>\n  </nav>'


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
    return (f'        <figure class="card"><a href="/heroes/{h["slug"]}"><img src="/assets/hero-{h["slug"]}.webp" alt="{html.escape(h["alt"])}" '
            f'width="560" height="560" loading="lazy"><figcaption><strong>{html.escape(h["name"])}</strong><em>{CLASS_LABEL[h["class"]]}</em>'
            f'<span>{html.escape(h["tagline"])}</span></figcaption></a></figure>')


def hero_page(template, heroes, i, cards, published, keywords):
    h = heroes[i]
    powers = [c for c in cards.values() if c["class"] == h["class"] and c["type"] == "HERO_POWER"]
    if len(powers) != 1:
        sys.exit(f'{h["slug"]}: expected one hero power, found {len(powers)}')
    icon = UNITY / "Art" / "Cards" / powers[0]["setId"] / f'{powers[0]["artId"]}.png'
    if not icon.exists():
        sys.exit(f'{h["slug"]}: no hero power art: {icon}')
    Image.open(icon).convert("RGB").resize((POWER_ICON, POWER_ICON), Image.LANCZOS).save(OUT_POWERS / f'{h["slug"]}.webp', quality=82, method=6)
    own = [c for c in published if c["class"] == h["class"]]
    if not own:
        sys.exit(f'{h["slug"]}: no allowlisted cards')
    prev, nxt = heroes[i - 1], heroes[(i + 1) % len(heroes)]
    unknown = [k for k in h["keywords"] if k not in keywords]
    if unknown:
        sys.exit(f'{h["slug"]}: keywords not in the glossary: {", ".join(unknown)}')
    points = "\n".join(f"        <li>{html.escape(p)}</li>" for p in h["points"])
    links = " &middot; ".join(f'<a href="/docs/keywords#{keyword_anchor(k)}">{html.escape(keywords[k][0])}</a>' for k in h["keywords"])
    fields = {
        "NAME": html.escape(h["name"]), "CLASS": CLASS_LABEL[h["class"]], "SLUG": h["slug"],
        "TAGLINE": html.escape(h["tagline"]), "ALT": html.escape(h["alt"]),
        "STYLE": html.escape(h["style"]), "POINTS": points, "KEYWORDS": links,
        "POWER_NAME": html.escape(powers[0]["name"].strip()), "POWER_TEXT": html.escape(powers[0]["text"]),
        "POWER_COST": str(powers[0]["cost"]),
        "CARDS": "\n".join(card_html(c) for c in own),
        "PREV_SLUG": prev["slug"], "PREV_NAME": html.escape(prev["name"]),
        "NEXT_SLUG": nxt["slug"], "NEXT_NAME": html.escape(nxt["name"]),
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
    keywords = load_keywords()
    ordered = sorted(keywords.items(), key=lambda kv: kv[1][0])
    entries = "\n".join(f'        <dt id="{keyword_anchor(k)}">{html.escape(name)}</dt>\n        <dd>{html.escape(text)}</dd>'
                        for k, (name, text) in ordered)
    anchors = [(name, f"#{keyword_anchor(k)}") for k, (name, _) in ordered]
    (SITE / "public" / "docs").mkdir(parents=True, exist_ok=True)
    for template, out, href, sections in (("docs.template.html", "docs.html", "/docs", []),
                                          ("keywords.template.html", "docs/keywords.html", "/docs/keywords", anchors)):
        page = (SITE / "scripts" / template).read_text(encoding="utf-8")
        page = (page.replace("{{DOCS_NAV}}", docs_nav(href)).replace("{{DOCS_TOC}}", docs_toc(sections))
                .replace("{{KEYWORDS}}", entries))
        (SITE / "public" / out).write_text(page, encoding="utf-8", newline="\n")
    print(f"published {len(keywords)} keywords")
    template = (SITE / "scripts" / "hero.template.html").read_text(encoding="utf-8")
    OUT_HEROES.mkdir(parents=True, exist_ok=True)
    OUT_POWERS.mkdir(parents=True, exist_ok=True)
    print(f"published {len(heroes)} hero pages")
    for i, h in enumerate(heroes):
        page, n = hero_page(template, heroes, i, cards, published, keywords)
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

    # /assets/* is cached for a week, so a changed stylesheet reaches nobody who already has the
    # old one unless its address changes too. Every page links it by content hash.
    version = hashlib.sha256((SITE / "public" / "assets" / "site.css").read_bytes()).hexdigest()[:10]
    for page in (SITE / "public").rglob("*.html"):
        text = page.read_text(encoding="utf-8")
        stamped = re.sub(r'/assets/site\.css(\?v=[\w-]+)?"', f'/assets/site.css?v={version}"', text)
        if stamped != text:
            page.write_text(stamped, encoding="utf-8", newline="\n")
    print(f"stylesheet version {version}")


if __name__ == "__main__":
    main()
