"""Sound preview and promotion — audition every generated clip, promote or reject in one click.

    python tooling/sfx/soundweb.py                  # http://127.0.0.1:8795
    python tooling/sfx/soundweb.py --port 8799

WHY THIS IS A SERVER AND NOT A STATIC PAGE
    A file:// page can play audio but cannot copy or move a file, and promotion is the whole
    point. Everything here is local-only and bound to 127.0.0.1 by default.

WHAT IT DOES
    Scans the candidate trees under art-pipeline/ (gitignored) and lists every clip, grouped
    by class and moment, with the authored line text beside it. Two actions per clip:

      PROMOTE  copies it into the Unity tree under its CANONICAL stem -- the _takeN suffix is
               stripped. That suffix matters: the Addressables Voice group addresses by FILE
               NAME, so a take promoted with its suffix intact would claim its own address and
               ship beside the keeper.

      REJECT   MOVES it to a _rejected/ folder beside it. It does not delete. Regenerating a
               take costs credits and a rejected take is often the better one heard again
               later, so the X is reversible by design -- drag it back out.

      REWRITE  Flags the LINE, not a take: every take of it is wrong because the words are
               wrong. Writes tooling/sfx/rewrite-queue.json and pins the line to a section at
               the TOP of the page. Reject is for a bad read of a good line; rewrite is for a
               line no read can save. Flagging is a toggle and touches no audio.

               A flagged line has TWO states and the box has a section for each. While it is
               "needs_rewrite" there is nothing to hear, so it shows as a name and the old
               words. Once someone rewrites it and sets its status to "rewritten", it moves to
               the second section WITH ITS NEW TAKES AND A PROMOTE BUTTON -- because a rewrite
               that merely leaves the queue has to be hunted for again in a 240-clip page,
               which is the exact cost the box exists to remove. Done dismisses it.

WHY THE QUEUE IS A FILE AND NOT A PAGE STATE
    A first pass through 240 clips is long, and the flags have to survive the page being
    closed, the server being restarted, and the session that acts on them being a different
    one from the session that made them. The file is committed for the same reason: the
    rewrite list is work, not scratch.

CLEARING THE BOARD BETWEEN CHUNKS
    python tooling/sfx/soundweb.py --archive-decided

    A chunk's takes stay on the page forever after they are promoted, so by the third chunk the
    new clips are below 300 decided ones and the page stops being usable for its one job. This
    MOVES every take whose canonical stem already has a promoted file in the Unity tree into
    art-pipeline/sfx/_archive/, preserving its relative path. It is a move, never a delete --
    a keeper is often re-auditioned against a later take, and regenerating costs credits.

    A take whose stem is NOT promoted is never touched, so an unfinished chunk cannot be swept
    away by running this at the wrong moment. --dry-run prints what it would move.

WHAT IT DELIBERATELY DOES NOT DO
    Delete anything, write a card-data file, or touch Addressables. Promotion puts a file on
    disk; the Editor still has to run Emberfall/Addressables/Setup Groups + Migrate Card Data
    before that file has an address, and until it does the clip resolves to nothing and fails
    SILENTLY -- the page says so on every promote, because nothing else will. (The .meta Unity
    writes by itself on the next Editor focus; it only needs committing, and git status shows it.)

PATHS ARE VALIDATED, NOT TRUSTED
    Every path from the client is resolved and checked to be inside a known root before it is
    opened, copied or moved. A local server is still a server.
"""

from __future__ import annotations

import argparse
import html
import http.server
import json
import mimetypes
import pathlib
import re
import shutil
import sys
import urllib.parse

ROOT = pathlib.Path(__file__).resolve().parents[2]
LINES_FILE = pathlib.Path(__file__).resolve().parent / "voice-lines.json"
CARD_LINES_FILE = pathlib.Path(__file__).resolve().parent / "card-voice-casting.json"
# Committed, not gitignored: a flag raised in one session is acted on in another.
QUEUE_FILE = pathlib.Path(__file__).resolve().parent / "rewrite-queue.json"

VOICE_TAKES = ROOT / "art-pipeline" / "sfx" / "voice"
VOICE_FINAL = ROOT / "unity" / "Emberfall" / "Assets" / "Sound" / "Voice"
SFX_TAKES = ROOT / "art-pipeline" / "sfx"
SFX_FINAL = ROOT / "unity" / "Emberfall" / "Assets" / "Sound" / "SFX"

# Decided takes are moved here, not deleted. Sited beside the candidate trees rather than
# inside them: VOICE_TAKES is walked with rglob, so an archive under voice/ would need the
# skip below to work; the SFX scan is a non-recursive glob, so this level is invisible to it
# either way. Both are belt and braces on purpose -- a swept take reappearing on the board
# reads as "promote did nothing", which is the most expensive wrong conclusion here.
ARCHIVE = ROOT / "art-pipeline" / "sfx" / "_archive"

# Mirrors AddressableSoundEffectPlayer.MaxVariants. The player probes <stem>_2 .. _N for a real
# Addressables entry, so promoting past N puts a file on disk that nothing will ever play -- the
# exact silent failure this tool exists to prevent. The two numbers move together.
MAX_VARIANTS = 6
SKIP_DIRS = ("_rejected", "_archive")

# Only these trees are ever read from or written to. Anything resolving outside is refused.
ROOTS = (VOICE_TAKES, VOICE_FINAL, SFX_TAKES, SFX_FINAL)

FOLDER = {
    "EMBERWARD": "Ember", "TIDECALLER": "Tide", "GRAVEKEEP": "Grave", "VERDANT": "Verdant",
    "CLOCKWRIGHT": "Clock", "NIGHTVEIL": "Night", "STONEWARDEN": "Warden", "TEMPEST": "Storm",
}
AUDIO_EXT = {".wav", ".mp3", ".ogg", ".opus"}

# Liberal on purpose: dispatch_vo writes "<stem>_take2" and dispatch_sfx writes
# "<stem>__take2", and a stripper that knows only one leaves the other's separator
# behind. That failure is SILENT and total -- "tempest_spell_cast_" promotes happily,
# reads as a normal filename in every listing, and resolves for nobody. Cost the whole
# phase-1 SFX set on 2026-08-28. Strip both rather than making the two dispatchers agree:
# their key formats are load-bearing elsewhere (existing_keys splits on "__take").
TAKE_RE = re.compile(r"__?take\d+$")
# A canonical stem and nothing else. This is the only validation between a client
# string and a key written into rewrite-queue.json.
STEM_RE = re.compile(r"^[a-z0-9][a-z0-9_]{0,79}$")


def canonical_stem(stem: str) -> str:
    """vo_gravekeep_win_1_take2 -> vo_gravekeep_win_1. The suffix must not reach Assets."""
    return TAKE_RE.sub("", stem)


def inside_roots(p: pathlib.Path) -> bool:
    p = p.resolve()
    for r in ROOTS:
        try:
            p.relative_to(r.resolve())
            return True
        except ValueError:
            continue
    return False


def rel(p: pathlib.Path) -> str:
    return p.resolve().relative_to(ROOT).as_posix()


def load_lines():
    """stem -> (class, moment, text). Missing file is not fatal: clips still list, untitled."""
    try:
        data = json.loads(LINES_FILE.read_text(encoding="utf-8"))
    except Exception:
        return {}, {}
    text_by_stem, voice_by_class = {}, {}
    for cls, entry in data.get("voices", {}).items():
        voice_by_class[cls] = entry.get("voice_name", "")
        for moment, lines in entry.get("lines", {}).items():
            for i, text in enumerate(lines, 1):
                text_by_stem["vo_%s_%s_%d" % (cls.lower(), moment, i)] = (cls, moment, text)
    return text_by_stem, voice_by_class


def load_card_lines():
    """canonical stem -> (handle, moment, text); handle -> voice_name. Sibling to load_lines(),
    for card-voice-casting.json's per-MINION voices rather than voice-lines.json's per-CLASS
    ones -- kept as a separate lookup rather than merged into load_lines() because the two
    files key differently (handle vs. class) and a card's group in the page is per-card, not
    per-class. Missing file is not fatal, same reasoning as load_lines()."""
    try:
        data = json.loads(CARD_LINES_FILE.read_text(encoding="utf-8"))
    except Exception:
        return {}, {}
    text_by_stem, voice_by_handle = {}, {}
    for handle, entry in data.get("cards", {}).items():
        voice_by_handle[handle] = entry.get("voice_name") or ""
        for moment, lines in entry.get("lines", {}).items():
            for i, text in enumerate(lines, 1):
                text_by_stem["vo_card_%s_%s_%d" % (handle, moment, i)] = (handle, moment, text)
    return text_by_stem, voice_by_handle


def load_queue() -> dict:
    """stem -> {class, moment, text}. A missing or corrupt file is an EMPTY queue, never fatal:
    losing the flags is annoying, taking the whole preview page down over them is worse."""
    try:
        data = json.loads(QUEUE_FILE.read_text(encoding="utf-8"))
        return data.get("needs_rewrite", {}) if isinstance(data, dict) else {}
    except Exception:
        return {}


def save_queue(queue: dict) -> None:
    QUEUE_FILE.write_text(
        json.dumps({
            "_about": "Lines flagged in soundweb as needing a REWRITE -- the words are wrong, so "
                      "every take of them is wrong. Keyed by canonical stem. Written by the "
                      "Rewrite button's panel, which carries an optional 'note' -- Sam's suggested "
                      "replacement, entered before the flag is written at all. Clear an entry by "
                      "unflagging it there, or by hand once the line in voice-lines.json (or "
                      "card-voice-casting.json) has been rewritten and regenerated.",
            "needs_rewrite": dict(sorted(queue.items())),
        }, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8")


def scan():
    """Every candidate clip, grouped for display. Rebuilt per request so the page is never stale."""
    text_by_stem, voice_by_class = load_lines()
    card_text_by_stem, voice_by_handle = load_card_lines()
    voice_by_group = dict(voice_by_class)
    voice_by_group.update({"card:" + h: v for h, v in voice_by_handle.items()})
    groups = {}

    def add(group_key, sub, path, canon, final_dir, text):
        g = groups.setdefault(group_key, {"voice": voice_by_group.get(group_key, ""), "subs": {}})
        line = g["subs"].setdefault(sub, {}).setdefault(
            canon, {"text": text, "takes": [], "final": None})
        final = final_dir / (canon + path.suffix)
        if final.exists():
            line["final"] = rel(final)
        line["takes"].append(rel(path))

    if VOICE_TAKES.is_dir():
        for p in sorted(VOICE_TAKES.rglob("*")):
            if p.suffix.lower() not in AUDIO_EXT or any(d in p.parts for d in SKIP_DIRS):
                continue
            canon = canonical_stem(p.stem)
            # Card stems (vo_card_<handle>_...) are checked first and routed to their own
            # per-card group/folder; everything else falls through to the class lookup exactly
            # as before.
            if canon in card_text_by_stem:
                handle, moment, text = card_text_by_stem[canon]
                add("card:" + handle, moment, p, canon, VOICE_FINAL / "Cards" / handle, text)
                continue
            cls, moment, text = text_by_stem.get(canon, ("UNMATCHED", "unknown", ""))
            add(cls, moment, p, canon, VOICE_FINAL / FOLDER.get(cls, "Unsorted"), text)

    if SFX_TAKES.is_dir():
        for p in sorted(SFX_TAKES.glob("*")):
            if p.suffix.lower() not in AUDIO_EXT:
                continue
            add("SFX", "candidates", p, canonical_stem(p.stem), SFX_FINAL, "")

    return groups


def destination(path: pathlib.Path, variant: bool = False) -> pathlib.Path:
    """Where a promoted clip lands, under its canonical stem.

    With variant=True it lands in the next FREE numbered slot instead -- <stem>_2, _3, ... -- so a
    second good take is kept rather than overwriting the first. AddressableSoundEffectPlayer probes
    those suffixes and picks among whatever exists, which is why nothing but the filename declares
    a variant: there is no list to keep in step, and a promoted clip is a playing clip.

    Numbering starts at _2 because the BASE stem is variant one. A shared sound is heard several
    times a turn (undead_summon alone covers 22 cards), and one clip stops sounding like the game
    and starts sounding like a loop."""
    canon = canonical_stem(path.stem)
    card_text_by_stem, _ = load_card_lines()
    if canon in card_text_by_stem:
        base = VOICE_FINAL / "Cards" / card_text_by_stem[canon][0]
    else:
        m = re.match(r"vo_([a-z]+)_", canon)
        if m:
            folder = next((f for cls, f in FOLDER.items() if cls.lower() == m.group(1)), "Unsorted")
            base = VOICE_FINAL / folder
        else:
            base = SFX_FINAL
    if not variant:
        return base / (canon + path.suffix)
    for i in range(2, MAX_VARIANTS + 1):
        slot = base / ("%s_%d%s" % (canon, i, path.suffix))
        if not slot.exists():
            return slot
    return base / ("%s_%d%s" % (canon, MAX_VARIANTS, path.suffix))


PAGE_HEAD = """<!doctype html>
<meta charset="utf-8"><title>Emberfall sound preview</title>
<style>
 :root { color-scheme: light dark; }
 body { font: 15px/1.5 system-ui, sans-serif; margin: 0; padding: 20px 24px 80px;
        background: Canvas; color: CanvasText; }
 h1 { font-size: 19px; margin: 0 0 4px; }
 .sub { opacity: .65; font-size: 13px; margin: 0 0 18px; }
 .cls { margin: 26px 0 0; }
 .cls > summary { font-size: 15px; font-weight: 600; cursor: pointer; padding: 6px 0;
                  border-bottom: 1px solid; }
 .voice { font-weight: 400; opacity: .6; font-size: 13px; }
 h3 { font-size: 12px; text-transform: uppercase; letter-spacing: .08em; opacity: .55;
      margin: 18px 0 6px; }
 .line { margin: 0 0 14px; padding-left: 2px; }
 .text { font-size: 15px; }
 .stem { font: 11.5px ui-monospace, monospace; opacity: .5; }
 .done { color: seagreen; font-size: 12px; font-weight: 600; }
 .takes { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 4px; }
 .take { display: flex; align-items: center; gap: 5px; border: 1px solid;
         border-radius: 8px; padding: 5px 7px; }
 .take.gone { opacity: .35; text-decoration: line-through; }
 audio { height: 30px; }
 button { font: inherit; font-size: 13px; padding: 2px 9px; border-radius: 6px; cursor: pointer; }
 .x { padding: 2px 7px; }
 .var { padding: 2px 7px; }
 .rw-panel { display: none; margin: 6px 0 12px; }
 .rw-panel.open { display: block; }
 .rw-panel textarea { width: 100%; max-width: 480px; box-sizing: border-box; font: inherit;
                       font-size: 13px; border-radius: 8px; border: 1px solid;
                       background: Canvas; color: CanvasText; padding: 6px 8px; resize: vertical; }
 .rw-panel-actions { display: flex; gap: 8px; margin-top: 6px; }
 .rw-panel-actions .ghost { opacity: .7; }
 .note { font-size: 12.5px; opacity: .7; font-style: italic; margin: 2px 0 0; }
 .rw { border: 1px solid; border-radius: 10px; padding: 10px 14px; margin: 0 0 8px;
       background: color-mix(in srgb, Canvas 92%, orange); }
 .rw > summary { font-size: 14px; font-weight: 600; cursor: pointer; }
 .rw.done-box { background: color-mix(in srgb, Canvas 92%, seagreen); }
 .rw.done-box .line { margin: 8px 0 2px; }
 .rw-row { display: flex; align-items: baseline; gap: 10px; padding: 5px 0 4px;
           border-top: 1px solid color-mix(in srgb, CanvasText 20%, transparent); }
 .rw-row:first-child { border-top: 0; }
 .rw-row .text { flex: 1; }
 .rw-empty { opacity: .6; font-size: 13px; padding: 5px 0 0; }
 .flag { padding: 2px 8px; }
 .flag.on { font-weight: 700; border-width: 2px; }
 #toast { position: fixed; left: 0; right: 0; bottom: 0; padding: 10px 24px;
          background: Canvas; border-top: 1px solid; font-size: 13px; }
</style>
<h1>Emberfall sound preview</h1>
<p class="sub">Promote copies the clip into the Unity tree under its canonical stem.
<b>+alt</b> keeps a take <i>as well as</i> the promoted one &mdash; it lands in the next free
<code>_2</code>/<code>_3</code> slot and the game picks among them at random, so a sound shared by
20 cards stops sounding like a loop.
X moves it to <code>_rejected/</code> &mdash; nothing is deleted.
Rewrite opens a note for what the line should say instead, then flags the LINE (all its takes)
and pins it to the top &mdash; for words no read can save.</p>
"""

PAGE_TAIL = """
<div id="toast">Ready.</div>
<script>
async function act(kind, path, el) {
  const r = await fetch("/" + kind, {
    method: "POST", headers: {"Content-Type": "application/json"},
    body: JSON.stringify({path})
  });
  const d = await r.json();
  document.getElementById("toast").textContent = d.message;
  if (d.ok && kind === "reject") el.closest(".take").classList.add("gone");
  if (d.ok && kind === "promote") {
    const line = el.closest(".line");
    let tag = line.querySelector(".done");
    if (!tag) { tag = document.createElement("span"); tag.className = "done";
                line.querySelector(".stem").after(tag); }
    tag.textContent = "  \\u2713 promoted \\u2192 " + d.dest;
  }
}

// The pinned list is rebuilt from RW on every toggle rather than patched in place: one
// rendering path means the section cannot drift from the file the server just wrote.
function drawRewrites() {
  const box = document.getElementById("rw-list");
  const stems = Object.keys(RW).sort();
  document.getElementById("rw-count").textContent = stems.length;
  // Built with DOM APIs, never innerHTML. The stem and the line text come out of a file on
  // disk that anything able to POST to this port can write, and a rewrite queue is exactly
  // the long-lived store that turns a stray angle bracket into stored script. textContent
  // cannot be talked into markup; string concatenation always can.
  if (!stems.length) {
    const empty = document.createElement("div");
    empty.className = "rw-empty";
    empty.textContent = "Nothing flagged. Rewrite is for a line whose WORDS are wrong; "
                      + "X is for a bad read of a good line.";
    box.replaceChildren(empty);
    return;
  }
  box.replaceChildren(...stems.map(function (k) {
    const e = RW[k] || {};
    const row = document.createElement("div"); row.className = "rw-row";
    const stem = document.createElement("span"); stem.className = "stem"; stem.textContent = k;
    const text = document.createElement("span"); text.className = "text";
    text.textContent = e.text || "";
    const note = document.createElement("span"); note.className = "note";
    note.textContent = e.note ? ("→ " + e.note) : "";
    const btn = document.createElement("button"); btn.textContent = "unflag";
    btn.addEventListener("click", function () { flag(k); });
    row.append(stem, text, note, btn);
    return row;
  }));
}

async function flag(stem, cls, moment, text, note) {
  const r = await fetch("/flag", {
    method: "POST", headers: {"Content-Type": "application/json"},
    body: JSON.stringify({stem, on: !(stem in RW), cls, moment, text, note})
  });
  const d = await r.json();
  document.getElementById("toast").textContent = d.message;
  if (!d.ok) return;
  RW = d.queue;
  drawRewrites();
  const b = document.getElementById("flag-" + stem);
  if (b) {
    const isOn = stem in RW;
    b.classList.toggle("on", isOn);
    b.textContent = isOn ? "Flagged ↑" : "Rewrite";
  }
  const panel = document.getElementById("panel-" + stem);
  if (panel) panel.classList.remove("open");
}

// Rewrite no longer flags on the click that opens it -- it opens this panel FIRST, so there is
// somewhere to type the suggestion before anything is written to the queue file.
function togglePanel(stem) {
  const panel = document.getElementById("panel-" + stem);
  if (panel) panel.classList.toggle("open");
}

function submitRewrite(stem, cls, moment, text, btn) {
  const panel = btn.closest(".rw-panel");
  const note = panel.querySelector("textarea").value;
  flag(stem, cls, moment, text, note);
}
drawRewrites();
</script>
"""


def clean(value, limit: int) -> str:
    """Free text on its way into the committed queue file. Control characters go (they make a
    committed json unreadable in a diff and can hide the rest of a line), and the length is
    capped so a flag stays a label rather than a place to park a payload."""
    text = value if isinstance(value, str) else ""
    text = "".join(c for c in text if c == " " or not (c.isspace() or ord(c) < 32))
    return text[:limit]


def attr(value) -> str:
    """JSON for an HTML attribute. Both halves are load-bearing: json.dumps makes it a JS
    literal, html.escape makes it survive being inside a single-quoted onclick."""
    return html.escape(json.dumps(value), quote=True)


def take_html(paths) -> str:
    """The player + Promote + alt + X row for one take. Shared, so a line rendered in the pinned box
    behaves identically to the same line rendered in its class -- act() finds .take and .line
    either way and needs to know nothing about where it was clicked."""
    return "".join(
        '<div class="take"><audio controls preload="none" src="/audio?p=%s"></audio>'
        '<button onclick=\'act("promote",%s,this)\'>Promote</button>'
        '<button class="var" title="Keep this one TOO -- next free _2/_3 slot" '
        'onclick=\'act("variant",%s,this)\'>+alt</button>'
        '<button class="x" onclick=\'act("reject",%s,this)\'>&times;</button></div>'
        % (urllib.parse.quote(t), attr(t), attr(t), attr(t)) for t in paths)


def find_line(groups, stem):
    """The scanned line for a canonical stem, or None. Linear, because the page is rebuilt per
    request and 240 clips is not a number worth indexing for."""
    for g in groups.values():
        for sub in g["subs"].values():
            if stem in sub:
                return sub[stem]
    return None


def rewrite_box(queue: dict, groups: dict) -> str:
    """The pinned sections. Two of them, because a flagged line has two states and only one of
    them is a list:

      NEEDS REWRITE  a name and the old words. Nothing to listen to -- the words are the
                     problem -- so the client renders it from RW and that is enough.

      REWRITTEN      the new words, WITH ITS TAKES AND A PROMOTE BUTTON. Rendered server-side
                     for exactly one reason: a rewritten line that leaves this box has to be
                     hunted for again in a 240-clip page, which is the whole thing the box
                     exists to prevent. It stays here until it is promoted or dismissed.
    """
    pending = {k: v for k, v in queue.items() if v.get("status") != "rewritten"}
    redone = [(k, v) for k, v in sorted(queue.items()) if v.get("status") == "rewritten"]

    out = ['<details class="rw" open><summary>Needs rewrite '
           '(<span id="rw-count">%d</span>)</summary><div id="rw-list"></div></details>'
           % len(pending)]

    if redone:
        out.append('<details class="rw done-box" open><summary>Rewritten &mdash; listen (%d)'
                   '</summary>' % len(redone))
        for stem, entry in redone:
            line = find_line(groups, stem) or {"takes": [], "final": None}
            in_assets = ('<span class="done">  &check; in Assets</span>'
                         if line.get("final") else "")
            note_html = ('<div class="note">was: %s</div>' % html.escape(entry.get("note", ""))
                         if entry.get("note") else "")
            out.append(
                '<div class="line"><div class="text">%s</div>%s'
                '<div class="stem">%s%s</div>'
                '<div class="takes">%s<button class="flag" onclick=\'flag(%s)\'>Done</button>'
                '</div></div>'
                % (html.escape(entry.get("text", "")), note_html, html.escape(stem), in_assets,
                   take_html(line["takes"]), attr(stem)))
        out.append("</details>")

    return ("".join(out)
            + '<script>var RW = %s;</script>'
            # "</" is neutered because a JSON string is not HTML: an entry containing the
            # literal text "</script>" would otherwise close this block early. "\\/" is a valid
            # JSON escape for "/", so the value JS sees is unchanged; only the HTML parser
            # stops seeing a closing tag. A "<" can only appear inside a string here -- JSON
            # structure has none -- so the replace cannot corrupt the document.
            % json.dumps(pending).replace("</", "<\\/"))


def render(groups) -> bytes:
    queue = load_queue()
    out = [PAGE_HEAD, rewrite_box(queue, groups)]
    if not groups:
        out.append("<p>No candidate clips found under <code>art-pipeline/sfx/</code>.</p>")
    for cls in sorted(groups):
        cls_name = cls
        g = groups[cls]
        n = sum(len(l["takes"]) for sub in g["subs"].values() for l in sub.values())
        out.append('<details class="cls" open><summary>%s <span class="voice">%s &middot; %d clips'
                   '</span></summary>' % (html.escape(cls), html.escape(g["voice"]), n))
        for sub in g["subs"]:
            out.append("<h3>%s</h3>" % html.escape(sub.replace("_", " ")))
            for canon, line in sorted(g["subs"][sub].items()):
                done = ('<span class="done">  &check; in Assets</span>' if line["final"] else "")
                takes = take_html(line["takes"])
                flagged = canon in queue
                # json.dumps alone is NOT safe inside an HTML attribute: a line carrying an
                # apostrophe closes the onclick early and everything after it parses as markup.
                # html.escape(quote=True) is what makes the JSON safe to sit there.
                if flagged:
                    # Already flagged: this click UNFLAGS, same as the "unflag"/"Done" buttons
                    # elsewhere on the page. Only the FIRST click, on an unflagged line, opens
                    # the note panel instead of writing anything.
                    flag = ('<button class="flag on" id="flag-%s" onclick=\'flag(%s)\'>'
                            'Flagged &#8593;</button>'
                            % (html.escape(canon), attr(canon)))
                    panel = ""
                else:
                    flag = ('<button class="flag" id="flag-%s" onclick=\'togglePanel(%s)\'>'
                            'Rewrite</button>' % (html.escape(canon), attr(canon)))
                    panel = (
                        '<div class="rw-panel" id="panel-%s">'
                        '<textarea rows="2" placeholder="What should this line say instead? '
                        '(optional -- blank just flags the read)"></textarea>'
                        '<div class="rw-panel-actions">'
                        '<button onclick=\'submitRewrite(%s,%s,%s,%s,this)\'>Flag for rewrite</button>'
                        '<button class="ghost" onclick=\'togglePanel(%s)\'>Cancel</button>'
                        '</div></div>'
                        % (html.escape(canon), attr(canon), attr(cls_name), attr(sub),
                           attr(line["text"] or ""), attr(canon)))
                out.append(
                    '<div class="line"><div class="text">%s</div>'
                    '<div class="stem">%s%s</div><div class="takes">%s%s</div>%s</div>'
                    % (html.escape(line["text"] or "&nbsp;"), html.escape(canon), done, takes,
                       flag, panel))
        out.append("</details>")
    out.append(PAGE_TAIL)
    return "".join(out).encode("utf-8")


class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "emberfall-soundweb"

    def log_message(self, fmt, *a):
        pass

    def _json(self, code, obj):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _resolve(self, raw):
        """A client-supplied path, or None if it escapes the known roots."""
        if not raw:
            return None
        p = (ROOT / raw).resolve()
        if not inside_roots(p) or not p.is_file():
            return None
        return p

    def do_GET(self):
        parts = urllib.parse.urlparse(self.path)
        if parts.path == "/":
            body = render(scan())
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if parts.path == "/audio":
            q = urllib.parse.parse_qs(parts.query).get("p", [""])[0]
            p = self._resolve(q)
            if p is None:
                self.send_error(404)
                return
            data = p.read_bytes()
            self.send_response(200)
            self.send_header("Content-Type", mimetypes.guess_type(p.name)[0] or "audio/wav")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
            return
        self.send_error(404)

    def do_POST(self):
        parts = urllib.parse.urlparse(self.path)
        try:
            n = int(self.headers.get("Content-Length") or 0)
            payload = json.loads(self.rfile.read(n) or b"{}")
        except Exception:
            self._json(400, {"ok": False, "message": "bad request body"})
            return

        # /flag acts on a LINE (a canonical stem), not on a file, so it never goes through the
        # path resolver below -- there is no path to resolve. The stem is still validated: it is
        # written into a json file, and an unchecked one would be a write primitive.
        if parts.path == "/flag":
            stem = str(payload.get("stem") or "")
            if not STEM_RE.match(stem):
                self._json(400, {"ok": False, "message": "not a valid clip stem, refused"})
                return
            queue = load_queue()
            if payload.get("on"):
                note = clean(payload.get("note"), 500)
                queue[stem] = {
                    "class": clean(payload.get("cls"), 40),
                    "moment": clean(payload.get("moment"), 40),
                    "text": clean(payload.get("text"), 300),
                    # What Sam typed into the Rewrite panel -- the suggested replacement, not the
                    # old line (that's "text"). Optional: a bad read of otherwise-fine words can
                    # still be flagged with nothing here, same as before this panel existed.
                    "note": note,
                    # Set to "rewritten" by whoever actually rewrites the line and regenerates
                    # its takes. That flips the entry from the flat list to the section with
                    # players in it, so a rewrite lands back in front of Sam instead of
                    # disappearing into a 240-clip page.
                    "status": "needs_rewrite",
                }
                msg = ("Flagged %s with your note -- pinned to the top. No audio touched." % stem
                       if note else
                       "Flagged %s for rewrite -- pinned to the top. No audio touched." % stem)
            else:
                queue.pop(stem, None)
                msg = "Unflagged %s." % stem
            save_queue(queue)
            self._json(200, {"ok": True, "queue": queue, "message": msg})
            return

        src = self._resolve(payload.get("path"))
        if src is None:
            self._json(400, {"ok": False, "message": "path is outside the known roots, refused"})
            return

        if parts.path in ("/promote", "/variant"):
            variant = parts.path == "/variant"
            dest = destination(src, variant=variant)
            dest.parent.mkdir(parents=True, exist_ok=True)
            existed = dest.exists()
            shutil.copy2(src, dest)
            if variant and existed:
                # Every slot up to MAX_VARIANTS was taken, so destination() handed back the last
                # one and this overwrote it. Say so: silently replacing a keeper is the one
                # outcome nobody clicking "+alt" is expecting.
                verb = "All %d slots full, REPLACED the last --" % MAX_VARIANTS
            elif variant:
                verb = "Kept as an alternate:"
            else:
                verb = "Replaced" if existed else "Promoted"
            self._json(200, {
                "ok": True, "dest": rel(dest),
                "message": "%s %s -> %s. Run Emberfall/Addressables/Setup Groups in the Editor "
                           "-- until then it has no address and fails silently." % (
                               verb, src.name, rel(dest)),
            })
            return

        if parts.path == "/reject":
            trash = src.parent / "_rejected"
            trash.mkdir(parents=True, exist_ok=True)
            dest = trash / src.name
            i = 1
            while dest.exists():
                dest = trash / ("%s.%d%s" % (src.stem, i, src.suffix))
                i += 1
            shutil.move(str(src), str(dest))
            self._json(200, {"ok": True, "dest": rel(dest),
                             "message": "Moved to %s (not deleted)." % rel(dest)})
            return

        self.send_error(404)


def promoted_stems() -> set:
    """Canonical stems that already have a file in the Unity tree. The board's definition of
    'decided' -- and it reads the destination rather than a ledger, so a clip promoted by hand,
    by an earlier session, or replaced later all count the same."""
    stems = set()
    for final in (VOICE_FINAL, SFX_FINAL):
        if final.is_dir():
            for p in final.rglob("*"):
                if p.suffix.lower() in AUDIO_EXT:
                    stems.add(canonical_stem(p.stem))
    return stems


def archive_decided(dry_run: bool = False) -> int:
    """Move every take whose stem is already promoted out of the candidate trees."""
    decided = promoted_stems()
    moves, skipped = [], 0

    def consider(path):
        nonlocal skipped
        if canonical_stem(path.stem) not in decided:
            skipped += 1
            return
        moves.append((path, ARCHIVE / path.resolve().relative_to(SFX_TAKES.resolve())))

    if VOICE_TAKES.is_dir():
        for p in sorted(VOICE_TAKES.rglob("*")):
            if p.suffix.lower() in AUDIO_EXT and not any(d in p.parts for d in SKIP_DIRS):
                consider(p)
    if SFX_TAKES.is_dir():
        for p in sorted(SFX_TAKES.glob("*")):
            if p.suffix.lower() in AUDIO_EXT:
                consider(p)

    for src, dest in moves:
        print(("would move " if dry_run else "moved ") + rel(src))
        if not dry_run:
            dest.parent.mkdir(parents=True, exist_ok=True)
            if dest.exists():
                dest.unlink()      # same stem, same archive slot: the newer take wins
            shutil.move(str(src), str(dest))

    print("%s %d decided take(s) -> %s" % (
        "would archive" if dry_run else "archived", len(moves), rel(ARCHIVE) if moves else "-"))
    print("%d take(s) left on the board (stem not promoted yet)" % skipped)
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1")
    # 8781-8783 are cardweb's, and a second bind there silently loses to the running one on
    # Windows rather than raising -- the page comes back 200 and is somebody else's app.
    ap.add_argument("--port", type=int, default=8795)
    ap.add_argument("--archive-decided", action="store_true",
                    help="move already-promoted takes into _archive/ and exit")
    ap.add_argument("--dry-run", action="store_true",
                    help="with --archive-decided, print the moves without making them")
    args = ap.parse_args()

    if args.archive_decided:
        return archive_decided(dry_run=args.dry_run)

    # allow_reuse_address is TRUE by default, and on Windows that does not mean "reuse a dead
    # socket" -- it means a second bind to a LIVE one succeeds and loses silently. The page then
    # comes back 200 from the process that got there first, which may be an instance left running
    # by an earlier session with older code in it. That is not theoretical: a stale instance
    # carrying the pre-758cd6a9 take-stripper promoted four summon clips as "<stem>_" on
    # 2026-08-28, hours after the fix landed, while the terminal here reported a healthy server.
    # Refuse the bind instead, and say which process is holding it.
    http.server.ThreadingHTTPServer.allow_reuse_address = False
    try:
        srv = http.server.ThreadingHTTPServer((args.host, args.port), Handler)
    except OSError:
        print("port %d is already serving. Almost certainly an OLDER soundweb still"
              " running -- the page would answer from THAT process, running whatever code it"
              " started with, and this terminal would look healthy. Stop it first, then"
              " restart:" % args.port, file=sys.stderr)
        print("  powershell -NoProfile -Command \"Get-CimInstance Win32_Process | Where-Object"
              " { $_.CommandLine -like '*soundweb*' } | Stop-Process -Force\"", file=sys.stderr)
        return 1
    groups = scan()
    total = sum(len(l["takes"]) for g in groups.values()
                for sub in g["subs"].values() for l in sub.values())
    print("%d clip(s) across %d group(s)" % (total, len(groups)))
    print("http://%s:%d" % (args.host, args.port))

    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        print("\nstopped")
    return 0


if __name__ == "__main__":
    sys.exit(main())
