#!/usr/bin/env python3
"""Render a readable map of the codebase from the code-review-graph database.

WHY THIS IS A TREEMAP AND NOT A NODE-LINK GRAPH
-----------------------------------------------
The graph's C# edges do not carry architecture. Node keys are path-scoped
(`...\\CardDefinition.cs::TargetSpec.IsKnown`) while CALLS targets are stored as bare
names (`FromSeconds`, `Delay`, `nameof`), so a call almost never resolves to a node
across a file, let alone across an assembly. Measured on 2026-08-20:

    edges whose target is not a node in the graph : 16755 / 25042  (67%)
    edges joining Emberfall.Net.Server -> Core    : 0
    distinct cross-project pairs (CALLS/INHERITS) : 1   (and it is a Python one)
    distinct cross-community pairs, all kinds     : 9

So `visualize --mode community` draws 562 bubbles that are nearly edgeless, and any
filtered node-link view is disconnected confetti. Everything derived from those edges
is unreliable too - `caller_count` says 2327 symbols have no callers, `test_coverage`
says 16 of 3924 are tested, and 92% of `risk_score` is the 0.3 default. Do not use
`dead-code` against this graph.

What the graph records reliably is WHERE code lives and HOW BIG it is. That is what
this draws: a zoomable treemap, area -> project -> file -> symbol, sized by lines.

    python tooling/scripts/graph_view.py
    python tooling/scripts/graph_view.py --area engine
    python tooling/scripts/graph_view.py --min-loc 25 --no-symbols

RETIREMENT TRIGGER: if the C# call resolver is ever fixed so cross-assembly edges
resolve, this file's premise dies - re-check the four numbers above and a node-link
view becomes the better default. The same applies to the "Ask the graph before you
grep" section in CLAUDE.md, which promises callers/callees.
"""

from __future__ import annotations

import argparse
import io
import json
import os
import sqlite3
import sys

AREAS = [
    ("engine",     "#e8833a", ("src/emberfall.core",)),
    ("server",     "#d1495b", ("src/emberfall.net.server",)),
    ("net",        "#c77dff", ("src/emberfall.net.",)),
    ("tools",      "#7a8b99", ("src/emberfall.tools.", "src/emberfall.training.")),
    ("unity-ui",   "#3fa7d6", ("unity/emberfall/assets/scripts/presentation",
                               "unity/emberfall/assets/scripts/ui")),
    ("unity-edit", "#4c956c", ("unity/emberfall/assets/scripts/editor",)),
    ("unity",      "#2d6a4f", ("unity/",)),
    ("tooling",    "#b5a642", ("tooling/", "tools/")),
    ("tests",      "#8d6e63", ("tests/",)),
]
FALLBACK = ("other", "#6b7280")


def norm(path: str) -> str:
    return (path or "").replace("\\", "/").split("tcg-hearthlike/")[-1]


def classify(rel: str):
    low = rel.lower()
    for name, colour, prefixes in AREAS:
        if low.startswith(prefixes):
            return name, colour
    return FALLBACK


def project_of(rel: str) -> str:
    parts = rel.split("/")
    if parts[0] == "src" and len(parts) > 1:
        return parts[1]
    if parts[0] == "unity" and len(parts) > 5:
        return "/".join(parts[4:6])
    if parts[0] in ("tooling", "tools", "tests") and len(parts) > 1:
        return "/".join(parts[:2])
    return parts[0]


def build(db: str, area_filter, min_loc: int, with_symbols: bool):
    con = sqlite3.connect(db)
    rows = con.execute(
        "select file_path,name,kind,line_start,line_end,language from nodes "
        "where kind in ('Function','Class','Test') and file_path is not null "
        "and line_start is not null and line_end is not null"
    ).fetchall()
    con.close()

    tree, totals = {}, {"symbols": 0, "loc": 0, "files": set()}
    for fp, name, kind, ls, le, lang in rows:
        rel = norm(fp)
        area, colour = classify(rel)
        if area_filter and area != area_filter:
            continue
        loc = max(1, (le or ls) - ls + 1)
        if loc < min_loc:
            continue
        proj = project_of(rel)
        fname = rel.rsplit("/", 1)[-1]

        a = tree.setdefault(area, {"name": area, "colour": colour, "children": {}})
        p = a["children"].setdefault(proj, {"name": proj, "children": {}})
        f = p["children"].setdefault(fname, {"name": fname, "path": rel, "children": {}})
        # The graph holds a few genuinely duplicate (file, name, line_start) rows - four of
        # them in RuleDefinition.cs - so disambiguate or the tree silently loses them while
        # the header count still includes them.
        key = f"{name}#{ls}"
        while key in f["children"]:
            key += "'"
        f["children"][key] = {"name": name, "kind": kind, "loc": loc, "line": ls, "lang": lang}

        totals["symbols"] += 1
        totals["loc"] += loc
        totals["files"].add(rel)

    def to_list(node, depth):
        kids = node.get("children")
        if not kids:
            return {"name": node["name"], "value": node["loc"],
                    "kind": node.get("kind"), "line": node.get("line")}
        out = {"name": node["name"]}
        if "colour" in node:
            out["colour"] = node["colour"]
        if "path" in node:
            out["path"] = node["path"]
        if depth == 3 and not with_symbols:
            out["value"] = sum(k["loc"] for k in kids.values())
            return out
        out["children"] = [to_list(v, depth + 1) for v in kids.values()]
        return out

    root = {"name": "emberfall", "children": [to_list(v, 1) for v in tree.values()]}
    return root, {"symbols": totals["symbols"], "loc": totals["loc"], "files": len(totals["files"])}


TEMPLATE = """<title>Emberfall — code map</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body { margin:0; background:#101319; color:#e6e8ec; overflow:hidden;
         font:13px/1.45 ui-sans-serif,system-ui,'Segoe UI',sans-serif; }
  header { padding:11px 18px 9px; border-bottom:1px solid #232936; display:flex;
           align-items:baseline; gap:14px; }
  header b { font-size:15px; letter-spacing:.01em; }
  header .stats { color:#8b94a3; font-size:12px; margin-left:auto; }
  #crumb { color:#8b94a3; font-size:12px; }
  #crumb span { cursor:pointer; }
  #crumb span:hover { color:#e8833a; text-decoration:underline; }
  #crumb i { color:#4a5464; font-style:normal; margin:0 6px; }
  .cell rect { stroke:#101319; stroke-width:1px; }
  .cell text { pointer-events:none; fill:#0d0f14; font-weight:600; }
  .cell .sub { font-weight:400; opacity:.62; }
  .cell:hover rect { stroke:#e6e8ec; stroke-width:1.5px; }
  #tip { position:fixed; pointer-events:none; z-index:9; padding:7px 10px; display:none;
         background:#1b1f28f2; border:1px solid #2e3542; border-radius:7px; font-size:12px;
         max-width:340px; }
  #tip b { color:#e8833a; }
  #tip div { color:#8b94a3; margin-top:2px; word-break:break-all; }
</style>
<header>
  <b>Emberfall — code map</b>
  <span id="crumb"></span>
  <span class="stats" id="stats"></span>
</header>
<svg id="c"></svg>
<div id="tip"></div>
<script src="https://d3js.org/d3.v7.min.js"
        integrity="sha384-CjloA8y00+1SDAUkjs099PVfnY2KmDC2BZnws9kh8D/lX1s46w6EPhpXdqMfjK6i"
        crossorigin="anonymous"></script>
<script>
const DATA = __DATA__, META = __META__;
document.getElementById('stats').textContent =
  `${META.symbols.toLocaleString()} symbols · ${META.loc.toLocaleString()} lines · ${META.files} files — sized by lines`;

const HEAD = 42;
let W = innerWidth, H = innerHeight - HEAD;
const svg = d3.select('#c').attr('width', W).attr('height', H).style('display','block');
const tip = document.getElementById('tip');

const root0 = d3.hierarchy(DATA).sum(d => d.value || 0).sort((a, b) => b.value - a.value);
let current = root0;

function colourOf(d) {
  let n = d;
  while (n && !n.data.colour) n = n.parent;
  return n ? n.data.colour : '#6b7280';
}
function shade(hex, k) {
  const c = d3.color(hex); return k >= 0 ? c.brighter(k) : c.darker(-k);
}

function render(node) {
  current = node;
  const r = d3.hierarchy(node.data).sum(d => d.value || 0).sort((a, b) => b.value - a.value);
  d3.treemap().size([W, H]).paddingInner(1).paddingTop(d => d.depth === 0 ? 0 : 15).round(true)(r);

  svg.selectAll('*').remove();
  const leaves = r.descendants().filter(d => d.depth > 0);

  const cell = svg.selectAll('g').data(leaves).join('g')
    .attr('class', 'cell').attr('transform', d => `translate(${d.x0},${d.y0})`);

  cell.append('rect')
    .attr('width', d => Math.max(0, d.x1 - d.x0))
    .attr('height', d => Math.max(0, d.y1 - d.y0))
    .attr('fill', d => {
      const base = colourOf(node) || colourOf(d);
      return d.children ? shade(base, -1.4 + d.depth * .12) : shade(base, .25 - d.depth * .1);
    })
    .attr('fill-opacity', d => d.children ? .5 : .92);

  cell.filter(d => (d.x1 - d.x0) > 46 && (d.y1 - d.y0) > 13)
    .append('text').attr('x', 4).attr('y', 11)
    .attr('font-size', d => d.children ? 10.5 : 10)
    .text(d => d.data.name)
    .append('tspan').attr('class', 'sub')
    .text(d => (d.x1 - d.x0) > 132 ? `  ${d.value.toLocaleString()}` : '');

  cell.on('click', (e, d) => {
      e.stopPropagation();
      const target = findIn(node, d);
      if (target && target.children && target.children.length) { render(target); crumbs(); }
    })
    .on('mousemove', (e, d) => {
      tip.style.display = 'block';
      tip.style.left = Math.min(e.clientX + 14, innerWidth - 350) + 'px';
      tip.style.top = (e.clientY + 16) + 'px';
      tip.innerHTML = `<b>${d.data.name}</b> — ${d.value.toLocaleString()} lines` +
        (d.data.kind ? ` · ${d.data.kind}${d.data.line ? ' @ ' + d.data.line : ''}` : '') +
        (d.data.path ? `<div>${d.data.path}</div>` : '');
    })
    .on('mouseleave', () => { tip.style.display = 'none'; });
}

// d3.hierarchy copies data, so map a clicked layout node back to the real tree node.
function findIn(parent, d) {
  const path = [];
  let n = d; while (n && n.depth > 0) { path.unshift(n.data.name); n = n.parent; }
  let cur = parent;
  for (const name of path) {
    if (!cur.children) return null;
    cur = cur.children.find(c => c.data.name === name);
    if (!cur) return null;
  }
  return cur;
}

function crumbs() {
  const chain = []; let n = current;
  while (n) { chain.unshift(n); n = n.parent; }
  document.getElementById('crumb').innerHTML =
    chain.map((n, i) => `<span data-i="${i}">${n.data.name}</span>`).join('<i>›</i>');
  document.querySelectorAll('#crumb span').forEach(el => {
    el.onclick = () => { render(chain[+el.dataset.i]); crumbs(); };
  });
}

addEventListener('resize', () => {
  W = innerWidth; H = innerHeight - HEAD;
  svg.attr('width', W).attr('height', H); render(current);
});
render(root0); crumbs();
</script>
"""


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--db", default=".code-review-graph/graph.db")
    ap.add_argument("--area", default=None, help="keep one area: " + ", ".join(a[0] for a in AREAS))
    ap.add_argument("--min-loc", type=int, default=1, help="drop symbols shorter than this")
    ap.add_argument("--no-symbols", action="store_true", help="stop at file level")
    ap.add_argument("--out", default=".code-review-graph/code-map.html")
    args = ap.parse_args()

    if not os.path.exists(args.db):
        print(f"no graph at {args.db} - run `code-review-graph build` first", file=sys.stderr)
        return 1

    root, meta = build(args.db, args.area, args.min_loc, not args.no_symbols)
    if not root["children"]:
        print("nothing survived the filter", file=sys.stderr)
        return 1

    page = TEMPLATE.replace("__DATA__", json.dumps(root)).replace("__META__", json.dumps(meta))
    os.makedirs(os.path.dirname(args.out) or ".", exist_ok=True)
    with io.open(args.out, "w", encoding="utf-8", newline="\n") as f:
        f.write(page)

    print(f"areas     : {len(root['children'])}")
    print(f"symbols   : {meta['symbols']}")
    print(f"files     : {meta['files']}")
    print(f"lines     : {meta['loc']}")
    print(f"written   : {args.out} ({os.path.getsize(args.out) / 1024:.0f} KB)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
