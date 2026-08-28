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
# Committed, not gitignored: a flag raised in one session is acted on in another.
QUEUE_FILE = pathlib.Path(__file__).resolve().parent / "rewrite-queue.json"

VOICE_TAKES = ROOT / "art-pipeline" / "sfx" / "voice"
VOICE_FINAL = ROOT / "unity" / "Emberfall" / "Assets" / "Sound" / "Voice"
SFX_TAKES = ROOT / "art-pipeline" / "sfx"
SFX_FINAL = ROOT / "unity" / "Emberfall" / "Assets" / "Sound" / "SFX"

# Only these trees are ever read from or written to. Anything resolving outside is refused.
ROOTS = (VOICE_TAKES, VOICE_FINAL, SFX_TAKES, SFX_FINAL)

FOLDER = {
    "EMBERWARD": "Ember", "TIDECALLER": "Tide", "GRAVEKEEP": "Grave", "VERDANT": "Verdant",
    "CLOCKWRIGHT": "Clock", "NIGHTVEIL": "Night", "STONEWARDEN": "Warden", "TEMPEST": "Storm",
}
AUDIO_EXT = {".wav", ".mp3", ".ogg", ".opus"}

TAKE_RE = re.compile(r"_take\d+$")
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
                      "Rewrite button; clear an entry by unflagging it there, or by hand once the "
                      "line in voice-lines.json has been rewritten and regenerated.",
            "needs_rewrite": dict(sorted(queue.items())),
        }, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8")


def scan():
    """Every candidate clip, grouped for display. Rebuilt per request so the page is never stale."""
    text_by_stem, voice_by_class = load_lines()
    groups = {}

    def add(group_key, sub, path, canon, final_dir, text):
        g = groups.setdefault(group_key, {"voice": voice_by_class.get(group_key, ""), "subs": {}})
        line = g["subs"].setdefault(sub, {}).setdefault(
            canon, {"text": text, "takes": [], "final": None})
        final = final_dir / (canon + path.suffix)
        if final.exists():
            line["final"] = rel(final)
        line["takes"].append(rel(path))

    if VOICE_TAKES.is_dir():
        for p in sorted(VOICE_TAKES.rglob("*")):
            if p.suffix.lower() not in AUDIO_EXT or "_rejected" in p.parts:
                continue
            canon = canonical_stem(p.stem)
            cls, moment, text = text_by_stem.get(canon, ("UNMATCHED", "unknown", ""))
            add(cls, moment, p, canon, VOICE_FINAL / FOLDER.get(cls, "Unsorted"), text)

    if SFX_TAKES.is_dir():
        for p in sorted(SFX_TAKES.glob("*")):
            if p.suffix.lower() not in AUDIO_EXT:
                continue
            add("SFX", "candidates", p, canonical_stem(p.stem), SFX_FINAL, "")

    return groups


def destination(path: pathlib.Path) -> pathlib.Path:
    """Where a promoted clip lands, under its canonical stem."""
    canon = canonical_stem(path.stem)
    m = re.match(r"vo_([a-z]+)_", canon)
    if m:
        for cls, folder in FOLDER.items():
            if cls.lower() == m.group(1):
                return VOICE_FINAL / folder / (canon + path.suffix)
        return VOICE_FINAL / "Unsorted" / (canon + path.suffix)
    return SFX_FINAL / (canon + path.suffix)


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
X moves it to <code>_rejected/</code> &mdash; nothing is deleted.
Rewrite flags the LINE (all its takes) and pins it to the top &mdash; for words no read can save.</p>
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
    const btn = document.createElement("button"); btn.textContent = "unflag";
    btn.addEventListener("click", function () { flag(k); });
    row.append(stem, text, btn);
    return row;
  }));
}

async function flag(stem, cls, moment, text) {
  const r = await fetch("/flag", {
    method: "POST", headers: {"Content-Type": "application/json"},
    body: JSON.stringify({stem, on: !(stem in RW), cls, moment, text})
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
    """The player + Promote + X trio for one take. Shared, so a line rendered in the pinned box
    behaves identically to the same line rendered in its class -- act() finds .take and .line
    either way and needs to know nothing about where it was clicked."""
    return "".join(
        '<div class="take"><audio controls preload="none" src="/audio?p=%s"></audio>'
        '<button onclick=\'act("promote",%s,this)\'>Promote</button>'
        '<button class="x" onclick=\'act("reject",%s,this)\'>&times;</button></div>'
        % (urllib.parse.quote(t), attr(t), attr(t)) for t in paths)


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
            out.append(
                '<div class="line"><div class="text">%s</div>'
                '<div class="stem">%s%s</div>'
                '<div class="takes">%s<button class="flag" onclick=\'flag(%s)\'>Done</button>'
                '</div></div>'
                % (html.escape(entry.get("text", "")), html.escape(stem), in_assets,
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
                flag = ('<button class="flag%s" id="flag-%s" onclick=\'flag(%s,%s,%s,%s)\'>'
                        '%s</button>'
                        % (" on" if flagged else "", html.escape(canon), attr(canon),
                           attr(cls_name), attr(sub), attr(line["text"] or ""),
                           "Flagged &#8593;" if flagged else "Rewrite"))
                out.append(
                    '<div class="line"><div class="text">%s</div>'
                    '<div class="stem">%s%s</div><div class="takes">%s%s</div></div>'
                    % (html.escape(line["text"] or "&nbsp;"), html.escape(canon), done, takes,
                       flag))
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
                queue[stem] = {
                    "class": clean(payload.get("cls"), 40),
                    "moment": clean(payload.get("moment"), 40),
                    "text": clean(payload.get("text"), 300),
                    # Set to "rewritten" by whoever actually rewrites the line and regenerates
                    # its takes. That flips the entry from the flat list to the section with
                    # players in it, so a rewrite lands back in front of Sam instead of
                    # disappearing into a 240-clip page.
                    "status": "needs_rewrite",
                }
                msg = "Flagged %s for rewrite -- pinned to the top. No audio touched." % stem
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

        if parts.path == "/promote":
            dest = destination(src)
            dest.parent.mkdir(parents=True, exist_ok=True)
            existed = dest.exists()
            shutil.copy2(src, dest)
            self._json(200, {
                "ok": True, "dest": rel(dest),
                "message": "%s %s -> %s. Run Emberfall/Addressables/Setup Groups in the Editor "
                           "-- until then it has no address and fails silently." % (
                               "Replaced" if existed else "Promoted", src.name, rel(dest)),
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


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1")
    # 8781-8783 are cardweb's, and a second bind there silently loses to the running one on
    # Windows rather than raising -- the page comes back 200 and is somebody else's app.
    ap.add_argument("--port", type=int, default=8795)
    args = ap.parse_args()

    groups = scan()
    total = sum(len(l["takes"]) for g in groups.values()
                for sub in g["subs"].values() for l in sub.values())
    print("%d clip(s) across %d group(s)" % (total, len(groups)))
    print("http://%s:%d" % (args.host, args.port))
    srv = http.server.ThreadingHTTPServer((args.host, args.port), Handler)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        print("\nstopped")
    return 0


if __name__ == "__main__":
    sys.exit(main())
