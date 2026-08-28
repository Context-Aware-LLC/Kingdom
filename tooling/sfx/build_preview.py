#!/usr/bin/env python3
"""Build a local preview page for a folder of voice takes.

The ElevenLabs web player stalls on long take lists, and Audacity is a waveform
editor rather than a browser -- neither lets you A/B two takes of one line quickly.
This writes a preview.html INTO the take folder, referencing the wavs by relative
path, so opening it plays them straight off disk with no server and no upload.

    python tooling/sfx/build_preview.py                      # every class with takes
    python tooling/sfx/build_preview.py --only GRAVEKEEP

Then open art-pipeline/sfx/voice/<Class>/preview.html.

Picking a take marks it and appends it to a list at the bottom of the page. Copy
that list back and the picks can be finalised into Assets under their canonical
stems in one pass.

Firefox plays file:// audio without complaint. Chrome is stricter about local
files; if a clip will not play there, use Firefox rather than debugging it.
"""

import argparse
import html
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
LINES = pathlib.Path(__file__).resolve().parent / "voice-lines.json"
TAKE_ROOT = ROOT / "art-pipeline" / "sfx" / "voice"

FOLDER = {
    "EMBERWARD": "Ember", "TIDECALLER": "Tide", "GRAVEKEEP": "Grave", "VERDANT": "Verdant",
    "CLOCKWRIGHT": "Clock", "NIGHTVEIL": "Night", "STONEWARDEN": "Warden", "TEMPEST": "Storm",
}

PAGE = """<!doctype html>
<meta charset="utf-8">
<title>%(cls)s takes</title>
<style>
 :root { color-scheme: light dark; }
 body { font: 15px/1.5 system-ui, sans-serif; margin: 0; padding: 24px;
        max-width: 1000px; background: Canvas; color: CanvasText; }
 h1 { font-size: 20px; margin: 0 0 2px; }
 .sub { opacity: .7; margin: 0 0 24px; font-size: 13px; }
 h2 { font-size: 13px; text-transform: uppercase; letter-spacing: .08em;
      opacity: .6; margin: 28px 0 8px; border-bottom: 1px solid; padding-bottom: 4px; }
 .line { margin: 0 0 18px; }
 .text { font-size: 16px; margin-bottom: 6px; }
 .stem { font: 12px ui-monospace, monospace; opacity: .55; }
 .takes { display: flex; gap: 10px; flex-wrap: wrap; align-items: center; }
 .take { display: flex; align-items: center; gap: 6px; border: 1px solid;
         border-radius: 8px; padding: 6px 8px; }
 .take.picked { outline: 2px solid; background: color-mix(in srgb, CanvasText 10%%, Canvas); }
 audio { height: 32px; }
 button { font: inherit; padding: 3px 10px; border-radius: 6px; cursor: pointer; }
 #picks { position: sticky; bottom: 0; margin-top: 32px; padding: 12px;
          border: 1px solid; border-radius: 8px; background: Canvas; }
 pre { font: 12px ui-monospace, monospace; white-space: pre-wrap; margin: 8px 0 0; }
</style>
<h1>%(cls)s &mdash; %(voice)s</h1>
<p class="sub">%(count)d takes. Click <b>Pick</b> on the one to keep; the list at the
bottom collects them.</p>
%(body)s
<div id="picks"><b>Picked</b> <span id="n">0</span> of %(lines)d lines
 <button onclick="copyPicks()">Copy list</button>
 <button onclick="clearPicks()">Clear</button>
 <pre id="out"></pre></div>
<script>
const KEY = "picks:%(cls)s";
let picks = {};
try { picks = JSON.parse(localStorage.getItem(KEY) || "{}"); } catch (e) { picks = {}; }

function render() {
  document.querySelectorAll(".take").forEach(el => {
    el.classList.toggle("picked", picks[el.dataset.stem] === el.dataset.file);
  });
  const chosen = Object.values(picks).sort();
  document.getElementById("n").textContent = Object.keys(picks).length;
  document.getElementById("out").textContent = chosen.join("\\n");
}
function pick(stem, file) {
  if (picks[stem] === file) delete picks[stem]; else picks[stem] = file;
  try { localStorage.setItem(KEY, JSON.stringify(picks)); } catch (e) {}
  render();
}
function copyPicks() {
  const t = document.getElementById("out").textContent;
  navigator.clipboard.writeText(t).catch(() => {
    const r = document.createRange(); r.selectNode(document.getElementById("out"));
    getSelection().removeAllRanges(); getSelection().addRange(r);
  });
}
function clearPicks() {
  picks = {};
  try { localStorage.removeItem(KEY); } catch (e) {}
  render();
}
render();
</script>
"""


def build(cls, entry, folder):
    takes = sorted(folder.glob("vo_*_take*.wav"))
    if not takes:
        return None
    by_stem = {}
    for p in takes:
        stem = re.sub(r"_take\d+$", "", p.stem)
        by_stem.setdefault(stem, []).append(p)

    out, nlines = [], 0
    for moment, lines in entry["lines"].items():
        rows = []
        for i, text in enumerate(lines, 1):
            stem = "vo_%s_%s_%d" % (cls.lower(), moment, i)
            if stem not in by_stem:
                continue
            nlines += 1
            buttons = []
            for p in sorted(by_stem[stem]):
                buttons.append(
                    '<div class="take" data-stem="%s" data-file="%s">'
                    '<audio controls preload="none" src="%s"></audio>'
                    '<button onclick="pick(\'%s\',\'%s\')">Pick</button></div>'
                    % (stem, p.name, p.name, stem, p.name)
                )
            rows.append(
                '<div class="line"><div class="text">%s</div>'
                '<div class="stem">%s</div><div class="takes">%s</div></div>'
                % (html.escape(text), stem, "".join(buttons))
            )
        if rows:
            out.append("<h2>%s</h2>%s" % (moment.replace("_", " "), "".join(rows)))

    return PAGE % {
        "cls": cls, "voice": html.escape(entry["voice_name"]),
        "count": len(takes), "lines": nlines, "body": "".join(out),
    }


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lines", type=pathlib.Path, default=LINES)
    ap.add_argument("--takes", type=pathlib.Path, default=TAKE_ROOT)
    ap.add_argument("--only")
    args = ap.parse_args()

    data = json.loads(args.lines.read_text(encoding="utf-8"))
    built = 0
    for cls, entry in data["voices"].items():
        if args.only and cls != args.only:
            continue
        folder = args.takes / FOLDER[cls]
        if not folder.is_dir():
            continue
        page = build(cls, entry, folder)
        if page is None:
            continue
        dest = folder / "preview.html"
        dest.write_text(page, encoding="utf-8")
        print("  %-12s %s" % (cls, dest))
        built += 1
    if not built:
        print("no take folders found under %s" % args.takes, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
