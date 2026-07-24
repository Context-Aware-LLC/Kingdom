"""Build the round-2 versus-plate board (self-contained HTML)."""
import base64
import pathlib

SRC = pathlib.Path(__file__).resolve().parents[2] / "art-bakeoff" / "versus"


def uri(rel):
    return "data:image/jpeg;base64," + base64.b64encode((SRC / rel).read_bytes()).decode()


CARDS = [
    ("composite/Versus2b_KeepRight", "Keep Right", "front runner", "good",
     "Your composite, keep on the right. The most complete plate we have — the fortress looms "
     "with its banners, the broken arch sits low and wide on the left, and the causeway threads "
     "between them. Horizon is down to a single ember line at the vanishing point.",
     [("good", "Asymmetric by construction — keep vs arch, not two of the same thing"),
      ("good", "Darkest centre of the set; nothing competes with the orb"),
      ("good", "Horizon toned right down, as asked"),
      ("warn", "Rings sit low (~64% height vs 39% spec) and the right ring is inboard")]),
    ("round2/Versus2_CausewayAsym", "Causeway Asym", "front runner", "good",
     "Round 2 driven from Broken Causeway alone. Tall gothic ruin left, lower sprawl right, "
     "causeway and pillars intact. The largest, best-carved rings of any candidate.",
     [("good", "Biggest ring openings — closest to filling the Ø900 slot"),
      ("good", "Warm glow pushed hard to the left edge; centre is pure dark cloud"),
      ("good", "Lower third is very quiet"),
      ("warn", "Rings at ~27%/73% width, ~52% height — outboard and low")]),
    ("composite/Versus2b_KeepLeft", "Keep Left", "strong", "good",
     "Same composite, mirrored intent — keep on the left, arch on the right. Reads well but the "
     "weight sits heavily left; the right half is emptier than it should be.",
     [("good", "Clean asymmetry, good depth down the causeway"),
      ("warn", "Visual weight favours the left — not the equal balance we want"),
      ("warn", "Rings smallest of the round-2 set and sit low")]),
    ("round2/Versus2_KeepsAsym", "Keeps Asym", "strong", "good",
     "Twin Keeps rebuilt asymmetrically: a tall spire-fortress on the left, a broad low bastion "
     "on the right, with the banners varied between sides.",
     [("good", "Two genuinely different fortresses at comparable weight"),
      ("good", "Dark centre; warm glow far left"),
      ("warn", "No causeway — loses the centre corridor you liked"),
      ("warn", "Rings low, and the right one crowds its bastion")]),
    ("composite/Versus2b_KeepLeftDeeper", "Keep Left, Deeper", "variant", "warn",
     "The depth experiment: keep close and looming, arch set far back down the causeway so the "
     "two sides differ in distance as well as shape.",
     [("good", "Strongest depth cue of the set"),
      ("warn", "The distance trick costs balance — the far side reads as unimportant"),
      ("warn", "Rings drift furthest from spec")]),
    ("round2/Versus2_CausewayRuins", "Causeway Ruins", "variant", "warn",
     "The most aggressive restructure — cliff wall and leaning tower one side, low broken arches "
     "receding the other.",
     [("good", "Least mirrored of everything generated"),
      ("warn", "Busiest lower band of round 2"),
      ("warn", "Reads more chaotic than monumental")]),
]

rows = []
for path, title, chip, tone, lede, pts in CARDS:
    lis = "\n".join('          <li class="%s"><span></span>%s</li>' % (c, t) for c, t in pts)
    rows.append(
        '      <article class="cand">\n'
        '        <header class="cand-head">\n          <h3>%s</h3>\n'
        '          <span class="chip %s">%s</span>\n        </header>\n'
        '        <div class="shot">\n'
        '          <img class="raw" src="%s" alt="%s">\n'
        '          <img class="grid" src="%s" alt="%s with zone grid">\n'
        '        </div>\n        <p class="lede">%s</p>\n'
        '        <ul class="points">\n%s\n        </ul>\n      </article>'
        % (title, tone, chip, uri(path + "_web.jpg"), title,
           uri(path + "_grid_web.jpg"), title, lede, lis))

HTML = """<title>Emberfall — Versus Plates, Round 2</title>
<style>
  :root { --ground:#14100E; --ground-2:#1c1712; --ink:#F3ECE2; --ink-dim:#b6a996;
    --hair:#3a3129; --ember:#E8632A; --frost:#4FA8C7; --card:#1a1511; --card-hair:#40352b;
    --good:#78dc8c; --bad:#e2603f; --warn:#e0b44a; }
  @media (prefers-color-scheme: light) { :root { --ground:#efe7db; --ground-2:#e6dccd;
    --ink:#201914; --ink-dim:#6a5c4c; --hair:#cdbfad; --card:#f6efe4; --card-hair:#d8cab4; } }
  :root[data-theme="dark"] { --ground:#14100E; --ground-2:#1c1712; --ink:#F3ECE2;
    --ink-dim:#b6a996; --hair:#3a3129; --card:#1a1511; --card-hair:#40352b; }
  :root[data-theme="light"] { --ground:#efe7db; --ground-2:#e6dccd; --ink:#201914;
    --ink-dim:#6a5c4c; --hair:#cdbfad; --card:#f6efe4; --card-hair:#d8cab4; }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--ground); color:var(--ink);
    font-family:system-ui,-apple-system,"Segoe UI",Roboto,sans-serif; line-height:1.5;
    -webkit-font-smoothing:antialiased; }
  .wrap { max-width:1180px; margin:0 auto; padding:40px 24px 80px; }
  .eyebrow { font-size:12px; letter-spacing:.22em; text-transform:uppercase;
    color:var(--ink-dim); margin:0 0 10px; }
  h1 { font-size:clamp(25px,3.8vw,38px); margin:0 0 8px; font-weight:800;
    letter-spacing:-.01em; text-wrap:balance; }
  .sub { color:var(--ink-dim); max-width:70ch; margin:0; font-size:15px; }
  code { font-family:ui-monospace,Menlo,Consolas,monospace; font-size:.92em;
    background:var(--ground-2); padding:1px 5px; border-radius:4px; }
  .bar { display:flex; flex-wrap:wrap; gap:12px; align-items:center; margin:26px 0 8px;
    padding:14px 16px; border:1px solid var(--card-hair); border-radius:10px; background:var(--card); }
  .bar p { margin:0; font-size:13.5px; color:var(--ink-dim); max-width:70ch; }
  .toggle { display:inline-flex; align-items:center; gap:9px; margin-left:auto;
    font-size:13.5px; font-weight:600; cursor:pointer; user-select:none; }
  .toggle input { position:absolute; opacity:0; width:0; height:0; }
  .track { width:42px; height:23px; border-radius:99px; background:var(--hair);
    position:relative; transition:background .16s; flex:none; }
  .track::after { content:""; position:absolute; top:3px; left:3px; width:17px; height:17px;
    border-radius:50%; background:var(--ink); transition:transform .16s; }
  .toggle input:checked + .track { background:var(--ember); }
  .toggle input:checked + .track::after { transform:translateX(19px); }
  .toggle input:focus-visible + .track { outline:2px solid var(--frost); outline-offset:3px; }
  .cands { display:flex; flex-direction:column; gap:34px; margin-top:26px; }
  .cand { border:1px solid var(--card-hair); border-radius:12px; background:var(--card);
    padding:16px 16px 20px; }
  .cand-head { display:flex; align-items:baseline; gap:12px; flex-wrap:wrap; margin:0 0 12px; }
  .cand-head h3 { margin:0; font-size:19px; font-weight:800; letter-spacing:-.01em; }
  .chip { font-size:11px; font-weight:700; letter-spacing:.1em; text-transform:uppercase;
    padding:4px 9px; border-radius:99px; border:1px solid currentColor; }
  .chip.good { color:var(--good); } .chip.warn { color:var(--warn); } .chip.bad { color:var(--bad); }
  .shot { position:relative; border-radius:7px; overflow:hidden; background:#000;
    aspect-ratio:3696/1584; }
  .shot img { position:absolute; inset:0; width:100%; height:100%; display:block; }
  .shot .grid { opacity:0; transition:opacity .18s; }
  body.show-grid .shot .grid { opacity:1; }
  .lede { margin:14px 2px 0; font-size:14.5px; max-width:74ch; }
  .points { list-style:none; margin:12px 0 0; padding:0; display:grid; gap:7px;
    grid-template-columns:1fr; }
  @media (min-width:800px) { .points { grid-template-columns:1fr 1fr; } }
  .points li { display:flex; gap:9px; align-items:flex-start; font-size:13.5px; color:var(--ink-dim); }
  .points li span { width:7px; height:7px; border-radius:50%; margin-top:7px; flex:none; }
  .points li.good span { background:var(--good); }
  .points li.warn span { background:var(--warn); }
  .points li.bad span { background:var(--bad); }
  section.verdict { margin-top:44px; }
  h2 { font-size:13px; letter-spacing:.18em; text-transform:uppercase; color:var(--ink-dim);
    margin:0 0 14px; font-weight:700; }
  .notes { display:grid; gap:12px; grid-template-columns:repeat(auto-fit,minmax(250px,1fr)); }
  .note { border:1px solid var(--card-hair); border-radius:8px; padding:15px 17px; background:var(--card); }
  .note h4 { margin:0 0 6px; font-size:13.5px; }
  .note p { margin:0; font-size:13px; color:var(--ink-dim); }
  .note.a { border-left:3px solid var(--bad); }
  .note.b { border-left:3px solid var(--warn); }
  .note.c { border-left:3px solid var(--good); }
  @media (prefers-reduced-motion:reduce) { * { transition:none !important; } }
</style>

<div class="wrap">
  <p class="eyebrow">Emberfall · Versus Screen · Round 2 · 2026-07-23</p>
  <h1>The mirror is gone</h1>
  <p class="sub">Six plates driven from round-1 candidates through <code>gpt-image-2</code>'s edit
  endpoint — three rebuilt for asymmetry, three composites that drop a Twin Keeps fortress where one
  of Broken Causeway's arches stood. Every one fixes the mirroring, and the horizon is down to an
  ember line.</p>

  <div class="bar">
    <p>Toggle the zone grid to check placement. <strong>Gold circles</strong> are the medallion
    rects, <strong>violet</strong> the orb, <strong>white</strong> the text slots,
    <strong>green</strong> the 4:3 safe band.</p>
    <label class="toggle"><input type="checkbox" id="g"><span class="track"></span><span>Zone grid</span></label>
  </div>

  <div class="cands">
""" + "\n".join(rows) + """
  </div>

  <section class="verdict">
    <h2>Where this leaves us</h2>
    <div class="notes">
      <div class="note c">
        <h4>The composite was the right call</h4>
        <p>Building the asymmetry into the subject — a keep facing an arch — held far better than
        asking the model not to mirror. Two different <em>things</em> can't reflect each other.</p>
      </div>
      <div class="note a">
        <h4>Tilted rings fight a circular slot</h4>
        <p>The angled rings look good, but a turned ring gives an <em>elliptical</em> opening while
        the slot is a Ø900 circle holding a circular-masked portrait. Either keep the tilt slight,
        make the slot an ellipse, or tilt the portrait quad in Unity to match.</p>
      </div>
      <div class="note b">
        <h4>Placement still drifts low</h4>
        <p>Every plate puts the rings below the 39% spec line, most around 52–64%. Since the frames
        are painted, the cheap fix is to measure the chosen plate's rings and adopt those numbers as
        the rects rather than fight the model for pixel placement.</p>
      </div>
      <div class="note b">
        <h4>Balance is still the open question</h4>
        <p>Asymmetry is solved; equal <em>weight</em> is not. Keep Left and Keep Left Deeper both
        tilt the composition toward the fortress side. Keep Right and Causeway Asym hold it best.</p>
      </div>
    </div>
  </section>
</div>

<script>
  var cb = document.getElementById('g');
  cb.addEventListener('change', function () {
    document.body.classList.toggle('show-grid', cb.checked);
  });
</script>
"""

out = SRC / "bakeoff2.html"
out.write_text(HTML, encoding="utf-8")
print("wrote", out, len(HTML) // 1024, "KB")
