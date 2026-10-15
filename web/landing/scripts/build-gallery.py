"""Builds the public card teaser, the hero pages and the docs pages: public/cards.html,
public/docs/heroes/*.html, public/docs.html, public/docs/*.html, public/assets/cards/*.webp, and the hero roster inside
public/index.html.

Only handles listed in gallery-allowlist.json are published, in that order. Everything else in
unity/Assets/CardData stays private, because card text and stats still change before launch.
A handle that is missing from the card data, or has no art, fails the build rather than
publishing a half-card. Each hero in heroes.json gets a page showing its copy, its class's hero
power as the card data words it, and that class's allowlisted cards; a hero with no hero power or
no allowlisted card fails the build too. A keyword is defined once, on the glossary page, which is
docs/pub-docs/gameplay/keywords.md (Sam, 2026-10-10: the page is built from the public docs, not from
the game's copy); a hero page only names its keywords and links there, and a name the glossary does not
hold fails the build. Run from anywhere:
python web/landing/scripts/build-gallery.py
"""
import glob
import hashlib
import html
import json
import re
import sys
from pathlib import Path

import markdown
from PIL import Image

SITE = Path(__file__).resolve().parent.parent
UNITY = SITE.parent.parent / "unity" / "Assets"
OUT_IMG = SITE / "public" / "assets" / "cards"
OUT_HEROES = SITE / "public" / "docs" / "heroes"
OUT_POWERS = SITE / "public" / "assets" / "powers"
# Twice the size the page shows it at, for dense screens.
POWER_ICON = 192
GEM_ICON = 80
# Keywords whose glossary entry carries a picture, and the size it is encoded at (twice the drawn size).
KEYWORD_SHOTS = ("AEGIS", "BULWARK", "FROST", "SHROUD")
KEYWORD_SHOT_SIZE = (494, 610)
# Player docs written by hand in docs/pub-docs and published as they are: (source, address, menu
# label, menu group). A new page is a Markdown file there and a line here.
PUB_DOCS = SITE.parent.parent / "docs" / "pub-docs"
KEYWORDS_MD = "gameplay/keywords.md"
MD_PAGES = [
    ("gameplay/how-to-play.md", "/docs/how-to-play", "How to Play", "Gameplay"),
    ("gameplay/cards-and-decks.md", "/docs/cards-and-decks", "Cards and Decks", "Gameplay"),
    (KEYWORDS_MD, "/docs/keywords", "Keywords", "Gameplay"),
    ("gameplay/timers-reconnection-and-outcomes.md", "/docs/timers", "Timers and Reconnection", "Gameplay"),
    ("economy/economy.md", "/docs/economy", "Economy", "Economy"),
    ("economy/currency.md", "/docs/economy/currency", "Currency", "Economy"),
    ("economy/booster-packs.md", "/docs/economy/booster-packs", "Booster Packs", "Economy"),
]
# Where a link to a pub-docs file that is NOT published from Markdown lands instead: the classes
# page is the heroes section.
MD_LINK_OVERRIDES = {"gameplay/classes.md": "/docs/heroes"}
ECONOMY_HREF = "/docs/economy"
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
    """Keyword id -> printed name, read from the glossary table in the public docs: every row whose
    first cell is a bold term. "Backdraft (N)" is the keyword BACKDRAFT."""
    names = re.findall(r"^\|\s*\*\*(.+?)\*\*\s*\|", (PUB_DOCS / KEYWORDS_MD).read_text(encoding="utf-8"), re.M)
    if not names:
        sys.exit(f"no keyword rows found in {KEYWORDS_MD}")
    return {term_slug(n).upper().replace("-", "_"): n for n in names}


def term_slug(name):
    """The anchor of a glossary row: its term without any parenthetical, lower-case, hyphenated."""
    return re.sub(r"\s+", "-", re.sub(r"\(.*?\)", "", name).strip().lower())


def keyword_anchor(k):
    return k.lower().replace("_", "-")


def docs_toc(sections):
    """The page's own anchors, shown on the right. Empty for a page with none."""
    if not sections:
        return ""
    links = "\n".join(f'      <li><a href="{a}">{html.escape(s)}</a></li>' for s, a in sections)
    return f'  <nav class="docs-toc" aria-label="On this page">\n    <p>On this page</p>\n    <ul>\n{links}\n    </ul>\n  </nav>'


HEROES_HREF = "/docs/heroes"


def hero_href(h):
    return f'{HEROES_HREF}/{h["slug"]}'


def docs_nav(current, heroes):
    """The side menu every docs page carries. It lists the docs and nothing else (Sam, 2026-10-08).
    The hero pages are docs pages (Sam, 2026-10-10). "Heroes" links to their index, and the eight
    are listed under it, by class, only while the reader is on that index or on one of them."""
    def item(label, href):
        here = ' aria-current="page"' if href == current else ""
        return f'      <li><a href="{href}"{here}>{html.escape(label)}</a></li>'

    def group(heading, entries):
        rows = "\n".join(item(label, href) for label, href in entries)
        return f'    <p>{heading}</p>\n    <ul>\n{rows}\n    </ul>\n'

    def linked(label, href):
        here = ' aria-current="page"' if href == current else ""
        return f'<a href="{href}"{here}>{label}</a>'

    gameplay = [(label, href) for _, href, label, grp in MD_PAGES if grp == "Gameplay"]
    economy = [(label, href) for _, href, label, grp in MD_PAGES if grp == "Economy" and href != ECONOMY_HREF]
    roster = "\n".join(item(CLASS_LABEL[h["class"]], hero_href(h)) for h in heroes)
    sub = f'    <ul>\n{roster}\n    </ul>\n' if current.startswith(HEROES_HREF) else ""
    return ('  <nav class="docs-nav" aria-label="Docs">\n'
            + f'    <p>{linked("Docs", "/docs")}</p>\n'
            + group("Gameplay", gameplay)
            + f'    <p>{linked("Heroes", HEROES_HREF)}</p>\n{sub}'
            + group(linked("Economy", ECONOMY_HREF), economy)
            + '  </nav>')


def md_page(template, source, href, group, heroes):
    """One pub-docs Markdown file as a docs page: its title, its opening paragraph as the lead, the
    rest as the body, and its second-level headings as the page's own section list."""
    text = (PUB_DOCS / source).read_text(encoding="utf-8")
    m = re.match(r"\s*# (.+?)\n+(.*)", text, re.S)
    if not m:
        sys.exit(f"{source}: no title")
    title, rest = m.group(1).strip(), m.group(2)
    lead = ""
    if not rest.lstrip().startswith(("#", "|", "-")):
        lead, _, rest = rest.partition("\n\n")
    md = markdown.Markdown(extensions=["tables", "toc"])
    body = md.convert(rest)
    lead_html = markdown.markdown(lead) if lead else ""

    links = {s: h for s, h, _, _ in MD_PAGES}
    links.update(MD_LINK_OVERRIDES)

    def relink(match):
        target = (Path(source).parent / match.group(1)).as_posix()
        target = re.sub(r"(^|/)[^/]+/\.\./", r"\1", target)
        if target not in links:
            sys.exit(f"{source}: link to {match.group(1)}, which is not a published page")
        return f'href="{links[target]}"'

    body, lead_html = (re.sub(r'href="([^":]+\.md)"', relink, x) for x in (body, lead_html))
    # A table whose rows open on a bold term is a glossary: each row can be linked to, and is
    # listed among the page's sections ahead of its headings.
    terms = []

    def anchor_row(match):
        slug = term_slug(html.unescape(match.group(1)))
        terms.append((html.unescape(match.group(1)), "#" + slug))
        return f'<tr id="{slug}">\n<td><strong>{match.group(1)}</strong></td>'

    body = re.sub(r"<tr>\n<td><strong>(.+?)</strong></td>", anchor_row, body)
    sections = terms + [(t["name"], f'#{t["id"]}') for t in md.toc_tokens if t["level"] == 2]
    description = html.escape(re.sub(r"<[^>]+>", "", lead_html).strip() or title, quote=True)
    fields = {"TITLE": html.escape(title), "DESCRIPTION": description, "HREF": href, "EYEBROW": group,
              "LEAD": lead_html, "BODY": body, "DOCS_NAV": docs_nav(href, heroes), "DOCS_TOC": docs_toc(sections)}
    return re.sub(r"\{\{(\w+)\}\}", lambda x: fields[x.group(1)], template)


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
    return (f'        <figure class="card"><a href="{hero_href(h)}"><img src="/assets/hero-{h["slug"]}.webp" alt="{html.escape(h["alt"])}" '
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
    links = " &middot; ".join(f'<a href="/docs/keywords#{keyword_anchor(k)}">{html.escape(keywords[k])}</a>' for k in h["keywords"])
    fields = {
        "NAME": html.escape(h["name"]), "CLASS": CLASS_LABEL[h["class"]], "SLUG": h["slug"],
        "TAGLINE": html.escape(h["tagline"]), "ALT": html.escape(h["alt"]),
        "STYLE": html.escape(h["style"]), "POINTS": points, "KEYWORDS": links,
        "POWER_NAME": html.escape(powers[0]["name"].strip()), "POWER_TEXT": html.escape(powers[0]["text"]),
        "POWER_COST": str(powers[0]["cost"]),
        "CARDS": "\n".join(card_html(c) for c in own),
        "PREV_SLUG": prev["slug"], "PREV_NAME": html.escape(prev["name"]),
        "NEXT_SLUG": nxt["slug"], "NEXT_NAME": html.escape(nxt["name"]),
        "DOCS_NAV": docs_nav(hero_href(h), heroes),
        "DOCS_TOC": docs_toc([("Style of Battle", "#style"), (powers[0]["name"].strip(), "#power"),
                              (f'From the {CLASS_LABEL[h["class"]]} deck', "#cards")]),
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
    # The glossary shows a board minion wearing each status keyword. The pictures come out of the game:
    # Kingdom/Preview/Keyword States writes them to artifacts/keyword-preview, which
    # is not committed, so they are re-encoded only when a fresh render is there to encode.
    shots = SITE / "public" / "assets" / "keywords"
    shots.mkdir(parents=True, exist_ok=True)
    for k in KEYWORD_SHOTS:
        src = SITE.parent.parent / "artifacts" / "keyword-preview" / f"{k.lower()}.png"
        if src.exists():
            Image.open(src).convert("RGB").resize(KEYWORD_SHOT_SIZE, Image.LANCZOS).save(
                shots / f"{k.lower()}.webp", quality=85, method=6)
        elif not (shots / f"{k.lower()}.webp").exists():
            sys.exit(f"no picture for {k}: run the Keyword States preview in Unity")

    (SITE / "public" / "docs").mkdir(parents=True, exist_ok=True)
    # The eight hero cards are built once and used twice: on the home page and as the docs' heroes index.
    roster = "\n".join(roster_html(h) for h in heroes)
    for template, out, href, sections in (("docs.template.html", "docs.html", "/docs", []),
                                          ("heroes.template.html", "docs/heroes.html", HEROES_HREF, [])):
        page = (SITE / "scripts" / template).read_text(encoding="utf-8")
        page = (page.replace("{{DOCS_NAV}}", docs_nav(href, heroes)).replace("{{DOCS_TOC}}", docs_toc(sections))
                .replace("{{ROSTER}}", roster))
        (SITE / "public" / out).write_text(page, encoding="utf-8", newline="\n")
    # The rarity gems the docs show beside each rarity's name. Twice the drawn size, alpha kept.
    gems = SITE / "public" / "assets" / "gems"
    gems.mkdir(parents=True, exist_ok=True)
    for rarity in ("common", "rare", "epic", "legendary"):
        src = UNITY / "Art" / "UI" / "Gems" / f"gem_{rarity}.png"
        if not src.exists():
            sys.exit(f"no gem art: {src}")
        Image.open(src).convert("RGBA").resize((GEM_ICON, GEM_ICON), Image.LANCZOS).save(
            gems / f"gem-{rarity}.webp", quality=90, method=6)
    page_template = (SITE / "scripts" / "page.template.html").read_text(encoding="utf-8")
    for source, href, _, grp in MD_PAGES:
        out = SITE / "public" / (href.lstrip("/") + ".html")
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(md_page(page_template, source, href, grp, heroes), encoding="utf-8", newline="\n")
    print(f"published {len(MD_PAGES)} pages from docs/pub-docs")
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
    block = "<!-- roster:start -->\n" + roster + "\n        <!-- roster:end -->"
    text, n = re.subn(r"<!-- roster:start -->.*?<!-- roster:end -->", lambda m: block, text, flags=re.S)
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
