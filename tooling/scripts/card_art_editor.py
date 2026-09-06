"""Local card-art brief editor — a browser UI for the thing Sam actually asked for (2026-08-01):
a card list, a marker for which cards have no art yet, and a click-through screen where the
boilerplate is already collated and there is just an obvious blank spot to type subject and
background. No auto-generated subject text, no structured fields beyond that — deliberately.

Reuses gen_card_prompt_briefs.py for everything that script already gets right: boilerplate
text, class palette lookup, world tone, the scale clause, and the background-opposition hint.
This file adds only the browser UI and per-card draft persistence on top.

Prompts are saved as art-pipeline/card-art/<setId>/<artId>.md, directly beside that card's
picture at <artId>.png — same directory, same stem, so the two sort together and a set folder
lists as the art it contains. Untracked, same as the rest of art-pipeline/ (2026-08-02: an
earlier version of this file kept drafts under tooling/card-art-seeds/drafts/ for safety; the
DRAFTS subdirectory was never tracked, which is what made that location no safer than this one.
The seed corpus itself IS tracked and always has been — `git log -- tooling/card-art-seeds/` —
so do not read the note above as a claim about the seeds; corrected 2026-08-27).

<artId> is READ from the row, not rebuilt as "art_<cardId>" — see art_file().

"Has art" means promoted art OR a candidate waiting to be picked — a card with candidates on
disk does not need a brief written again.

Usage:
  python tooling/scripts/card_art_editor.py --port 8765
  Then open http://localhost:8765/ in a browser.
"""

import argparse
import json
import pathlib
import sys
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from gen_card_prompt_briefs import (  # noqa: E402
    BACKGROUND_OPPOSITION_HINTS,
    CARD_DATA_DIR,
    COMPOSITION,
    NEGATIVE_PROMPT,
    STYLE,
    art_file,
    delivery_block,
    load_cards,
    load_class_descriptors,
    load_world_tone,
    scale_clause,
)

REPO = pathlib.Path(__file__).resolve().parents[2]
# Prompts AND pictures, same directory, same stem: art-pipeline/card-art/<setId>/<artId>.{md,png}.
# One constant, because they are one directory - that is the point.
CANDIDATES_DIR = REPO / "art-pipeline" / "card-art"
PROMOTED_ART_DIR = REPO / "unity" / "Emberfall" / "Assets" / "Art" / "Cards"
HTML_PATH = pathlib.Path(__file__).resolve().parent / "card_art_editor.html"

# mtime-keyed caches — avoid re-reading/re-parsing on every request; invalidated automatically
# if the underlying file(s) change on disk.
_html_cache = {"mtime": None, "text": None}
_sets_cache = {"mtimes": None, "sets": None}


def read_html_cached():
    mtime = HTML_PATH.stat().st_mtime
    if _html_cache["mtime"] != mtime:
        _html_cache["text"] = HTML_PATH.read_text(encoding="utf-8")
        _html_cache["mtime"] = mtime
    return _html_cache["text"]


def list_sets_cached():
    files = sorted(CARD_DATA_DIR.glob("*.cards.json"))
    mtimes = tuple((str(fp), fp.stat().st_mtime) for fp in files)
    if _sets_cache["mtimes"] != mtimes:
        _sets_cache["sets"] = sorted({
            c.get("setId")
            for fp in files
            for c in json.loads(fp.read_text(encoding="utf-8"))
            if c.get("setId")
        })
        _sets_cache["mtimes"] = mtimes
    return _sets_cache["sets"]

def draft_path(card):
    """The prompt sits beside the picture it produced, named after the same card.

    One directory per expansion, no per-card leaf (Sam, 2026-09-05) — see
    gen_card_prompt_briefs.delivery_block for why the leaf directory is gone.
    """
    return CANDIDATES_DIR / card["setId"] / f"{art_file(card)}.md"


def has_art(card):
    """Promoted art, or any candidate waiting to be picked. Returns a real bool — both
    branches must, or every card reads as "needs art" and gets re-briefed."""
    stem = art_file(card)
    if (PROMOTED_ART_DIR / card["setId"] / f"{stem}.png").exists():
        return True
    setdir = CANDIDATES_DIR / card["setId"]
    # `<artId>.png` is the pick; `<artId>__<variant>.png` is a candidate beside it.
    return (setdir / f"{stem}.png").exists() or any(setdir.glob(f"{stem}__*.png"))


def compose_blank(card, class_descriptors, world_tone):
    """Same shape as gen_card_prompt_briefs.compose_brief, but with no auto-written subject
    text — just an obvious marker to type over, per Sam's explicit ask."""
    cls = card.get("class", "NEUTRAL").lower()
    palette, motifs, mood = class_descriptors.get(cls, ("", "", ""))
    bg_hint = BACKGROUND_OPPOSITION_HINTS.get(cls, "oppose the subject palette in hue or temperature")

    header = f"Generate one Emberfall card illustration for {card['name']} ({card['id']})."
    delivery = delivery_block(card)
    subj_opp = (
        "SUBJECT AND BACKGROUND OPPOSITION\n"
        f"- Subject class: {card.get('class', 'NEUTRAL').title()}.\n"
        f"- Subject palette, motifs, and mood: {palette}; {motifs}; {mood}.\n"
        "- Perspective: \n"
        f"- Background palette and setting: [[ starting hint: {bg_hint} ]]\n"
        "- Make the subject and background oppose each other in hue, value, or temperature."
    )
    world = f"WORLD\n{world_tone}"
    subject_block = f"SUBJECT\n{scale_clause(card)}\n"

    return "\n\n".join(
        [header, delivery, COMPOSITION, subj_opp, world, STYLE, subject_block, NEGATIVE_PROMPT]
    )


def card_summary(card):
    return {
        "id": card["id"],
        "name": card["name"],
        "type": card.get("type"),
        "cost": card.get("cost"),
        "attack": card.get("attack"),
        "health": card.get("health"),
        "class": card.get("class"),
        "rarity": card.get("rarity"),
        "hasArt": has_art(card),
        "hasDraft": draft_path(card).exists(),
    }
# A rarity-only scale() lived here and is deliberately gone (2026-08-06). It was never called
# — compose_blank() has always used the shared scale_clause() — and it could not have worked:
# it indexed ["common","uncommon","rare","legendary"] with the row's rarity, which is uppercase
# and includes EPIC, so every lookup raised ValueError. It also encoded exactly the rule Sam
# asked to remove: size read off rarity alone, so an expensive common was drawn small.
#
# Sizing lives in gen_card_prompt_briefs.scale_clause(), keyed on cost and body with rarity
# only ever raising. One definition, both front-ends. Do not re-add a local copy.


class Handler(BaseHTTPRequestHandler):
    def _json(self, obj, status=200):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _text(self, text, status=200, content_type="text/plain; charset=utf-8"):
        body = text.encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        qs = urllib.parse.parse_qs(parsed.query)

        if parsed.path == "/":
            self._text(read_html_cached(), content_type="text/html; charset=utf-8")
            return

        if parsed.path == "/api/sets":
            self._json(list_sets_cached())
            return

        if parsed.path == "/api/cards":
            set_id = qs.get("set", [None])[0]
            if not set_id:
                self._json({"error": "missing ?set="}, 400)
                return
            cards = load_cards(set_id)
            summaries = [card_summary(c) for c in cards]
            # Only show cards that need a brief written: no promoted art and no draft yet.
            summaries = [s for s in summaries if not s["hasArt"] and not s["hasDraft"]]
            self._json(summaries)
            return

        if parsed.path == "/api/card":
            set_id = qs.get("set", [None])[0]
            card_id = qs.get("id", [None])[0]
            if not set_id or not card_id:
                self._json({"error": "missing ?set=&id="}, 400)
                return
            cards = load_cards(set_id, card_ids=[card_id])
            if not cards:
                self._json({"error": f"no such card {card_id!r} in set {set_id!r}"}, 404)
                return
            card = cards[0]
            dp = draft_path(card)
            if dp.exists():
                text = dp.read_text(encoding="utf-8")
            else:
                class_descriptors = load_class_descriptors()
                world_tone = load_world_tone(set_id)
                text = compose_blank(card, class_descriptors, world_tone)
            self._json({"card": card, "text": text, "hasDraft": dp.exists()})
            return

        self._json({"error": "not found"}, 404)

    def do_POST(self):
        parsed = urllib.parse.urlparse(self.path)
        qs = urllib.parse.parse_qs(parsed.query)

        if parsed.path == "/api/card":
            set_id = qs.get("set", [None])[0]
            card_id = qs.get("id", [None])[0]
            if not set_id or not card_id:
                self._json({"error": "missing ?set=&id="}, 400)
                return
            length = int(self.headers.get("Content-Length", 0))
            body = self.rfile.read(length).decode("utf-8")

            # Saved right next to where this card's generated candidates will land — no
            # separate "authoritative" copy anymore; art-pipeline/ is untracked in full,
            # same as everything else the generation pipeline produces. Plain overwrite
            # on every save, no versioning.
            #
            # The row is loaded rather than the path being built from card_id, because the
            # filename comes from the row's artId. See art_file().
            cards = load_cards(set_id, card_ids=[card_id])
            if not cards:
                self._json({"error": f"no such card {card_id!r} in set {set_id!r}"}, 404)
                return
            dp = draft_path(cards[0])
            dp.parent.mkdir(parents=True, exist_ok=True)
            dp.write_text(body, encoding="utf-8")

            self._json({"ok": True, "path": str(dp)})
            return

        self._json({"error": "not found"}, 404)

    def log_message(self, fmt, *args):
        pass  # keep the terminal quiet; errors still raise normally


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8765)
    args = ap.parse_args()
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print(f"Card art editor running at http://127.0.0.1:{args.port}/  (Ctrl+C to stop)")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
