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
 #toast { position: fixed; left: 0; right: 0; bottom: 0; padding: 10px 24px;
          background: Canvas; border-top: 1px solid; font-size: 13px; }
</style>
<h1>Emberfall sound preview</h1>
<p class="sub">Promote copies the clip into the Unity tree under its canonical stem.
X moves it to <code>_rejected/</code> &mdash; nothing is deleted.</p>
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
</script>
"""


def render(groups) -> bytes:
    out = [PAGE_HEAD]
    if not groups:
        out.append("<p>No candidate clips found under <code>art-pipeline/sfx/</code>.</p>")
    for cls in sorted(groups):
        g = groups[cls]
        n = sum(len(l["takes"]) for sub in g["subs"].values() for l in sub.values())
        out.append('<details class="cls" open><summary>%s <span class="voice">%s &middot; %d clips'
                   '</span></summary>' % (html.escape(cls), html.escape(g["voice"]), n))
        for sub in g["subs"]:
            out.append("<h3>%s</h3>" % html.escape(sub.replace("_", " ")))
            for canon, line in sorted(g["subs"][sub].items()):
                done = ('<span class="done">  &check; in Assets</span>' if line["final"] else "")
                takes = "".join(
                    '<div class="take"><audio controls preload="none" src="/audio?p=%s"></audio>'
                    '<button onclick=\'act("promote",%s,this)\'>Promote</button>'
                    '<button class="x" onclick=\'act("reject",%s,this)\'>&times;</button></div>'
                    % (urllib.parse.quote(t), json.dumps(t), json.dumps(t))
                    for t in line["takes"])
                out.append(
                    '<div class="line"><div class="text">%s</div>'
                    '<div class="stem">%s%s</div><div class="takes">%s</div></div>'
                    % (html.escape(line["text"] or "&nbsp;"), html.escape(canon), done, takes))
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
