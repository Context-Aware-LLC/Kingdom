"""Build the versus-plate bake-off comparison board (self-contained HTML)."""
import json

E = json.load(open('_embed.json'))

CARDS = [
    ("TwinKeeps", "Twin Keeps", "strongest overall",
     "Closest to the menu backdrop and the best <em>place</em> of the four — two fortresses "
     "actually reading as opposing powers, banners leaning in, real depth. Portals landed close "
     "to spec.",
     [("good", "Lower third is dark and quiet — text will sit cleanly"),
      ("good", "Portals within ~4% of the spec rects"),
      ("bad", "Bright horizon sits directly behind the orb well — a glowing orb needs a dark "
              "backing, and here it would wash out"),
      ("warn", "Reads near-mirrored; a little asymmetry in the ruins would sell it better")]),
    ("BrokenCauseway", "Broken Causeway", "best frames",
     "By far the best carved rings — ornate, weighty, genuinely architectural. The causeway "
     "gives the strongest depth of the set.",
     [("good", "Portal rings are the most convincing frames of the four"),
      ("bad", "Causeway runs straight through the status strip — busiest lower band of the set"),
      ("bad", "Bright horizon behind the orb, same problem as Twin Keeps"),
      ("warn", "Almost perfectly mirrored — the most artificial-looking of the four")]),
    ("AshBasin", "Ash Basin", "cleanest for UI",
     "The most <em>usable</em> plate. Quietest lower third by a wide margin, and the only one "
     "where the orb well sits against dark cloud instead of a bright horizon.",
     [("good", "Orb well backs onto dark sky — the only candidate that gets this right"),
      ("good", "Lower third is genuinely calm; every text band is clean"),
      ("good", "Portals well placed and clearly readable as empty"),
      ("warn", "Least interesting scene — emptier and flatter than Twin Keeps")]),
    ("StormSpires", "Storm Spires", "portals missed",
     "Best sky of the set and a striking silhouette, but the layout failed: the portals drifted "
     "far outboard and low, landing under the text bands.",
     [("good", "Most atmospheric sky; strong monumental silhouettes"),
      ("bad", "Portals badly out of position — outboard and low, colliding with the nameplate "
              "and class bands"),
      ("bad", "Ring interiors fall outside the 4:3 safe band on both sides"),
      ("warn", "Ground is nearly featureless")]),
]

rows = []
for key, title, chip, lede, points in CARDS:
    lis = "\n".join(f'          <li class="{c}"><span></span>{t}</li>' for c, t in points)
    rows.append(
        '      <article class="cand" id="%s">\n'
        '        <header class="cand-head">\n'
        '          <h3>%s</h3>\n'
        '          <span class="chip chip-%s">%s</span>\n'
        '        </header>\n'
        '        <div class="shot">\n'
        '          <img class="raw" src="%s" alt="%s candidate plate">\n'
        '          <img class="grid" src="%s" alt="%s with the zone grid overlaid">\n'
        '        </div>\n'
        '        <p class="lede">%s</p>\n'
        '        <ul class="points">\n%s\n        </ul>\n'
        '      </article>'
        % (key, title, key, chip, E[key + '_raw'], title, E[key + '_grid'], title, lede, lis))

STYLE = """<title>Emberfall — Versus Plate Bake-off</title>
<style>
  :root {
    --ground:#14100E; --ground-2:#1c1712; --ink:#F3ECE2; --ink-dim:#b6a996;
    --hair:#3a3129; --ember:#E8632A; --frost:#4FA8C7; --card:#1a1511; --card-hair:#40352b;
    --good:#78dc8c; --bad:#e2603f; --warn:#e0b44a;
  }
  @media (prefers-color-scheme: light) {
    :root { --ground:#efe7db; --ground-2:#e6dccd; --ink:#201914; --ink-dim:#6a5c4c;
      --hair:#cdbfad; --card:#f6efe4; --card-hair:#d8cab4; }
  }
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
  .sub { color:var(--ink-dim); max-width:68ch; margin:0; font-size:15px; }
  code { font-family:ui-monospace,Menlo,Consolas,monospace; font-size:.92em;
    background:var(--ground-2); padding:1px 5px; border-radius:4px; }

  .bar { display:flex; flex-wrap:wrap; gap:12px; align-items:center;
    margin:26px 0 8px; padding:14px 16px; border:1px solid var(--card-hair);
    border-radius:10px; background:var(--card); }
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
  .chip-AshBasin, .chip-TwinKeeps { color:var(--good); }
  .chip-BrokenCauseway { color:var(--warn); }
  .chip-StormSpires { color:var(--bad); }

  .shot { position:relative; border-radius:7px; overflow:hidden; background:#000;
    aspect-ratio:3696/1584; }
  .shot img { position:absolute; inset:0; width:100%; height:100%; display:block; }
  .shot .grid { opacity:0; transition:opacity .18s; }
  body.show-grid .shot .grid { opacity:1; }

  .lede { margin:14px 2px 0; font-size:14.5px; max-width:74ch; }
  .points { list-style:none; margin:12px 0 0; padding:0; display:grid; gap:7px;
    grid-template-columns:1fr; }
  @media (min-width:800px) { .points { grid-template-columns:1fr 1fr; } }
  .points li { display:flex; gap:9px; align-items:flex-start; font-size:13.5px;
    color:var(--ink-dim); }
  .points li span { width:7px; height:7px; border-radius:50%; margin-top:7px; flex:none; }
  .points li.good span { background:var(--good); }
  .points li.bad span { background:var(--bad); }
  .points li.warn span { background:var(--warn); }

  section.verdict { margin-top:44px; }
  h2 { font-size:13px; letter-spacing:.18em; text-transform:uppercase; color:var(--ink-dim);
    margin:0 0 14px; font-weight:700; }
  .notes { display:grid; gap:12px; grid-template-columns:repeat(auto-fit,minmax(250px,1fr)); }
  .note { border:1px solid var(--card-hair); border-radius:8px; padding:15px 17px;
    background:var(--card); }
  .note h4 { margin:0 0 6px; font-size:13.5px; }
  .note p { margin:0; font-size:13px; color:var(--ink-dim); }
  .note.a { border-left:3px solid var(--bad); }
  .note.b { border-left:3px solid var(--warn); }
  .note.c { border-left:3px solid var(--good); }
  @media (prefers-reduced-motion:reduce) { * { transition:none !important; } }
</style>
"""

BODY_TOP = """
<div class="wrap">
  <p class="eyebrow">Emberfall · Versus Screen · Candidate plates · 2026-07-23</p>
  <h1>Four takes on the versus landscape</h1>
  <p class="sub">All four generated at 3696&times;1584 with <code>gpt-image-2</code>, built from the
  main menu backdrop's architecture rather than its art. Same brief each time: two empty stone
  portals, a calm centre for the orb, a quiet lower third for text, balanced halves, and no
  elemental theming.</p>

  <div class="bar">
    <p>Toggle the zone grid to check placement against the contract. <strong>Gold circles</strong>
    are the medallion rects, <strong>violet</strong> the orb, <strong>white</strong> the text slots,
    <strong>green</strong> the 4:3 safe band.</p>
    <label class="toggle">
      <input type="checkbox" id="g">
      <span class="track"></span>
      <span>Zone grid</span>
    </label>
  </div>

  <div class="cands">
"""

BODY_END = """
  </div>

  <section class="verdict">
    <h2>What the set tells us</h2>
    <div class="notes">
      <div class="note a">
        <h4>The orb backing is the real problem</h4>
        <p>Three of four put a bright warm horizon exactly where the orb sits. The orb is a glowing
        object with its own pulse &mdash; on a bright band it disappears. Only Ash Basin backs it
        onto dark sky. The brief said &ldquo;calm&rdquo; when it needed to say <em>calm and
        dark</em>.</p>
      </div>
      <div class="note b">
        <h4>Placement drifts outboard and low</h4>
        <p>Every candidate pushed the portals further apart and lower than asked. That is a
        consistent bias rather than random error, so it is correctable &mdash; either by naming the
        drift in the prompt, or by measuring the winner's rings and adopting those as the rects.</p>
      </div>
      <div class="note b">
        <h4>Too symmetrical</h4>
        <p>The brief asked for balanced halves and got near-mirror images. Balance was the goal;
        literal reflection reads as cheap. Worth asking for differing silhouettes at matched visual
        weight.</p>
      </div>
      <div class="note c">
        <h4>The portals worked</h4>
        <p>The main risk going in was whether the model would paint empty frames at all. It did, in
        all four, and read &ldquo;empty&rdquo; correctly every time &mdash; no faces, no scenes
        inside. Painting the frames in is viable.</p>
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

html = STYLE + BODY_TOP + "\n".join(rows) + BODY_END
open('bakeoff.html', 'w', encoding='utf-8').write(html)
print('wrote bakeoff.html', len(html) // 1024, 'KB')
