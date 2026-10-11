/* Kingdom card authoring — web front end.
 *
 * The client never holds a card id, a handle or an artId. It holds a `token` the server minted
 * and hands that token back when staging an edit. There is nowhere in this file that could send
 * one of those three keys, because it never receives one.
 */

const $ = (s, r = document) => r.querySelector(s);
const state = {
  cards: [], options: {}, staged: new Map(),   // cardToken -> changed fields (display only)
  comments: new Map(),                         // cardToken -> [comment]
  current: null, draft: null, digest: '',
};

/* ---------- fetch helpers ---------- */
async function api(path, opts) {
  const r = await fetch(path, opts);
  const body = await r.json().catch(() => ({ error: `HTTP ${r.status}` }));
  if (!r.ok) throw new Error(body.error || `HTTP ${r.status}`);
  return body;
}

let toastTimer;
function toast(msg, kind = '') {
  const t = $('#toast');
  t.textContent = msg;
  t.className = 'toast ' + kind;
  t.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { t.hidden = true; }, 3200);
}

/* ---------- load ---------- */
async function load() {
  const data = await api('/api/cards');
  state.cards = data.cards;
  state.options = data.options;
  state.digest = data.corpusDigest;
  $('#corpus-meta').textContent = `${data.cards.length} rows · corpus ${data.corpusDigest}`;

  fillSelect($('#f-set'), state.options.setId);
  fillSelect($('#f-class'), state.options.class);
  fillSelect($('#f-type'), state.options.type);
  fillSelect($('#f-rarity'), state.options.rarity);

  await refreshStaging();
  await refreshComments();
  render();
}

function fillSelect(el, values) {
  for (const v of values || []) {
    const o = document.createElement('option');
    o.value = v; o.textContent = titled(v);
    el.appendChild(o);
  }
}

const titled = (s) => String(s).replace(/_/g, ' ').toLowerCase()
  .replace(/\b\w/g, (c) => c.toUpperCase());

async function refreshStaging() {
  // The server resolves its own card ids to this run's tokens, so the client never has to
  // guess which row an edit belongs to -- matching on a name would break on the first pair of
  // cards that share one.
  const doc = await api('/api/staging');
  state.staged.clear();
  for (const e of doc.edits) {
    if (e.token) state.staged.set(e.token, e.changed);
  }
  const n = doc.edits.length;
  const pill = $('#staged-count');
  pill.textContent = n;
  pill.classList.toggle('hot', n > 0);
}

async function refreshComments() {
  // Like staging, the server resolves its own card ids to this run's tokens -- the sidecar is
  // keyed on the permanent numeric id, which the browser is never given.
  const doc = await api('/api/comments');
  state.comments.clear();
  for (const [token, list] of Object.entries(doc.comments || {})) {
    state.comments.set(token, list);
  }
}

const openComments = (token) =>
  (state.comments.get(token) || []).filter((c) => !c.resolved).length;

/* ---------- grid ---------- */
function visible() {
  const q = $('#q').value.trim().toLowerCase();
  const fs = $('#f-set').value, fc = $('#f-class').value;
  const ft = $('#f-type').value, fr = $('#f-rarity').value;
  const onlyStaged = $('#f-staged').checked;
  const onlyCommented = $('#f-commented').checked;
  return state.cards.filter((c) => {
    if (fs && c.setId !== fs) return false;
    if (fc && c.class !== fc) return false;
    if (ft && c.type !== ft) return false;
    if (fr && c.rarity !== fr) return false;
    if (onlyStaged && !state.staged.has(c.token)) return false;
    if (onlyCommented && openComments(c.token) === 0) return false;
    if (q) {
      const hay = `${c.name} ${c.text || ''} ${c.flavorText || ''}`.toLowerCase();
      if (!hay.includes(q)) return false;
    }
    return true;
  });
}

function render() {
  const grid = $('#grid');
  const list = visible();
  $('#count').textContent = `${list.length} shown`;
  grid.textContent = '';

  for (const c of list) {
    const el = document.createElement('button');
    el.className = 'card'
      + (state.current === c.token ? ' sel' : '')
      + (state.staged.has(c.token) ? ' staged' : '');
    el.type = 'button';

    if (c.hasArt) {
      const img = document.createElement('img');
      img.className = 'card-art'; img.loading = 'lazy';
      img.src = `/thumb/${c.token}.webp`; img.alt = '';
      el.appendChild(img);
    } else {
      const ph = document.createElement('div');
      ph.className = 'card-art none'; ph.textContent = '◇';
      el.appendChild(ph);
    }

    const body = document.createElement('div');
    body.className = 'card-body';

    const nm = document.createElement('div');
    nm.className = 'card-name'; nm.textContent = c.name;
    body.appendChild(nm);

    const sub = document.createElement('div');
    sub.className = 'card-sub';
    sub.textContent = statLine(c);
    body.appendChild(sub);

    const tags = document.createElement('div');
    tags.className = 'card-tags';
    for (const [t, cls] of [[c.class, ''], [c.rarity, 'r-' + c.rarity]]) {
      const s = document.createElement('span');
      s.className = 'tag ' + cls; s.textContent = titled(t);
      tags.appendChild(s);
    }
    body.appendChild(tags);
    el.appendChild(body);

    // Only OPEN comments earn a marker. A resolved one is a record, and a badge that never
    // clears makes the badge mean "this card has ever been discussed", which is not actionable.
    const open = openComments(c.token);
    if (open) {
      const badge = document.createElement('span');
      badge.className = 'card-comments';
      badge.textContent = open;
      badge.title = `${open} open comment${open === 1 ? '' : 's'}`;
      el.appendChild(badge);
    }

    el.addEventListener('click', () => openEditor(c.token));
    grid.appendChild(el);
  }
}

function statLine(c) {
  const bits = [`${c.cost}◆`, titled(c.type)];
  if (c.attack != null || c.health != null) bits.push(`${c.attack ?? '–'}/${c.health ?? '–'}`);
  if (c.durability != null) bits.push(`${c.attack ?? '–'}/${c.durability}⛨`);
  return bits.join('  ·  ');
}

/* ---------- editor ---------- */
function openEditor(token) {
  const card = state.cards.find((c) => c.token === token);
  if (!card) return;
  state.current = token;
  state.draft = structuredClone(card);

  $('#ed-title').textContent = card.name;
  const art = $('#ed-art');
  if (card.hasArt) { art.src = `/thumb/${token}.webp`; art.hidden = false; }
  else { art.removeAttribute('src'); art.hidden = true; }

  const rules = card.rules || [];
  $('#ed-rules').textContent = JSON.stringify(rules, null, 1);
  $('#ed-rules-wrap').hidden = rules.length === 0;

  buildForm(card);
  renderComments(token);
  $('#ed-discard').hidden = !state.staged.has(token);
  $('#editor').hidden = false;
  $('#ed-status').textContent = '';
  render();
}

function buildForm(card) {
  const f = $('#ed-form');
  f.textContent = '';

  field(f, 'name', 'Name', text(card.name || ''));
  const grid3 = document.createElement('div');
  grid3.className = 'row2';
  f.appendChild(grid3);
  field(grid3, 'cost', 'Cost', num(card.cost));
  field(grid3, 'attack', 'Attack', num(card.attack));
  field(grid3, card.durability != null ? 'durability' : 'health',
    card.durability != null ? 'Durability' : 'Health',
    num(card.durability != null ? card.durability : card.health));

  field(f, 'type', 'Type', select(state.options.type, card.type));
  field(f, 'class', 'Class', select(state.options.class, card.class));
  field(f, 'rarity', 'Rarity', select(state.options.rarity, card.rarity));
  field(f, 'setId', 'Set', select(state.options.setId, card.setId));
  field(f, 'lifecycle', 'Lifecycle', select(state.options.lifecycle, card.lifecycle || 'PUBLISHED'));
  if (card.tribe != null) field(f, 'tribe', 'Tribe', select(state.options.tribe, card.tribe));

  // Keywords
  const kwWrap = document.createElement('div');
  kwWrap.className = 'field'; kwWrap.dataset.key = 'keywords';
  const kwLab = document.createElement('label');
  kwLab.textContent = 'Keywords'; kwWrap.appendChild(kwLab);
  const kw = document.createElement('div'); kw.className = 'kw';
  for (const k of state.options.keywords || []) {
    const lab = document.createElement('label');
    const cb = document.createElement('input');
    cb.type = 'checkbox'; cb.value = k;
    cb.checked = (card.keywords || []).includes(k);
    cb.addEventListener('change', () => {
      const on = [...kw.querySelectorAll('input:checked')].map((i) => i.value);
      setDraft('keywords', on);
    });
    lab.appendChild(cb);
    lab.appendChild(document.createTextNode(titled(k)));
    kw.appendChild(lab);
  }
  kwWrap.appendChild(kw); f.appendChild(kwWrap);

  field(f, 'text', 'Rules text', area(card.text || ''));
  field(f, 'flavorText', 'Flavour', area(card.flavorText || ''));
  field(f, 'tooltip', 'Tooltip', text(card.tooltip || ''));

  const colWrap = document.createElement('div');
  colWrap.className = 'field'; colWrap.dataset.key = 'collectible';
  const cl = document.createElement('label'); cl.className = 'check';
  const cb = document.createElement('input');
  cb.type = 'checkbox'; cb.checked = !!card.collectible;
  cb.addEventListener('change', () => setDraft('collectible', cb.checked));
  cl.appendChild(cb); cl.appendChild(document.createTextNode(' Collectible'));
  colWrap.appendChild(cl); f.appendChild(colWrap);

  markChanged();
}

function field(parent, key, label, input) {
  const w = document.createElement('div');
  w.className = 'field'; w.dataset.key = key;
  const l = document.createElement('label'); l.textContent = label;
  w.appendChild(l); w.appendChild(input);
  input.addEventListener('input', () => {
    let v = input.value;
    if (input.type === 'number') v = v === '' ? null : Number(v);
    setDraft(key, v);
  });
  parent.appendChild(w);
  return w;
}

const text = (v) => { const i = document.createElement('input'); i.type = 'text'; i.value = v; return i; };
const num = (v) => { const i = document.createElement('input'); i.type = 'number'; i.value = v ?? ''; return i; };
const area = (v) => { const i = document.createElement('textarea'); i.value = v; return i; };
function select(values, cur) {
  const s = document.createElement('select');
  for (const v of values || []) {
    const o = document.createElement('option');
    o.value = v; o.textContent = titled(v); o.selected = v === cur;
    s.appendChild(o);
  }
  s.addEventListener('change', () => setDraft(s.closest('.field').dataset.key, s.value));
  return s;
}

function setDraft(key, value) {
  state.draft[key] = value;
  markChanged();
}

function changedFields() {
  const orig = state.cards.find((c) => c.token === state.current);
  const out = {};
  if (!orig) return out;
  for (const k of Object.keys(state.draft)) {
    if (k === 'token' || k === 'hasArt' || k === 'rules' || k === 'soundEffects') continue;
    const a = orig[k], b = state.draft[k];
    if (JSON.stringify(a ?? null) !== JSON.stringify(b ?? null)) out[k] = b;
  }
  return out;
}

function markChanged() {
  const ch = changedFields();
  for (const w of $('#ed-form').querySelectorAll('.field')) {
    w.classList.toggle('changed', Object.prototype.hasOwnProperty.call(ch, w.dataset.key));
  }
  const n = Object.keys(ch).length;
  $('#ed-stage').disabled = n === 0;
  $('#ed-status').textContent = n ? `${n} field${n === 1 ? '' : 's'} changed` : 'no changes';
}

/* ---------- comments ----------
 * Every value here goes in through textContent, never innerHTML. Comment text is the one thing
 * on this page a human typed freely, and the Unity authoring window renders the same bytes.
 */
function renderComments(token) {
  const list = state.comments.get(token) || [];
  const wrap = $('#ed-comments');
  wrap.textContent = '';

  const open = list.filter((c) => !c.resolved).length;
  const pill = $('#ed-comment-count');
  pill.textContent = open;
  pill.classList.toggle('hot', open > 0);

  if (!list.length) {
    const p = document.createElement('p');
    p.className = 'dim';
    p.textContent = 'No comments on this card.';
    wrap.appendChild(p);
    return;
  }

  for (const c of list) {
    const d = document.createElement('div');
    d.className = 'comment' + (c.resolved ? ' resolved' : '');

    const head = document.createElement('div');
    head.className = 'comment-head';
    const who = document.createElement('b');
    who.textContent = c.author || 'unknown';
    const when = document.createElement('span');
    when.className = 'dim';
    when.textContent = (c.at || '').replace('T', ' ').slice(0, 16);
    head.appendChild(who);
    head.appendChild(when);

    const actions = document.createElement('span');
    actions.className = 'comment-actions';
    const res = document.createElement('button');
    res.className = 'ghost tiny';
    res.textContent = c.resolved ? 'Reopen' : 'Resolve';
    res.addEventListener('click', () => commentAction('resolve', c, !c.resolved));
    const del = document.createElement('button');
    del.className = 'ghost tiny';
    del.textContent = 'Delete';
    del.addEventListener('click', () => commentAction('delete', c));
    actions.appendChild(res);
    actions.appendChild(del);
    head.appendChild(actions);
    d.appendChild(head);

    const body = document.createElement('p');
    body.className = 'comment-text';
    body.textContent = c.text;
    d.appendChild(body);
    wrap.appendChild(d);
  }
}

async function commentAction(op, comment, resolved) {
  try {
    await api(`/api/comments/${op}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token: state.current, commentId: comment.id, resolved }),
    });
    await refreshComments();
    renderComments(state.current);
    render();
  } catch (e) {
    toast(e.message, 'bad');
  }
}

$('#ed-comment-add').addEventListener('click', async () => {
  const box = $('#ed-comment-text');
  const text = box.value.trim();
  if (!text) return;
  try {
    await api('/api/comments', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      // No author field. The server takes it from the credential -- a byline off the request
      // body is one anybody can forge, and the byline is the whole point of the field.
      body: JSON.stringify({ token: state.current, text }),
    });
    box.value = '';
    await refreshComments();
    renderComments(state.current);
    render();
    toast('Comment added', 'good');
  } catch (e) {
    toast(e.message, 'bad');
  }
});

/* ---------- tabs ---------- */
let adminLoaded = false;
function showTab(which) {
  const cards = which === 'cards';
  $('#pane-cards').hidden = !cards;
  $('#pane-corpus').hidden = cards;
  for (const [id, on] of [['#tab-cards', cards], ['#tab-corpus', !cards]]) {
    $(id).classList.toggle('on', on);
    $(id).setAttribute('aria-selected', String(on));
  }
  // The drawer belongs to the grid; leaving it up over the panel would let a Stage click land
  // on a card the author can no longer see.
  if (!cards && !$('#editor').hidden) $('#ed-close').click();
  if (!cards && !adminLoaded) {
    // Src is set on first reveal, not in the markup: /admin/data.js rolls up the whole corpus
    // and there is no reason to pay for it on a page load that never leaves the grid.
    $('#admin-frame').src = '/admin/';
    adminLoaded = true;
  }
}
$('#tab-cards').addEventListener('click', () => showTab('cards'));
$('#tab-corpus').addEventListener('click', () => showTab('corpus'));

/* ---------- actions ---------- */
$('#ed-close').addEventListener('click', () => {
  $('#editor').hidden = true; state.current = null; render();
});
$('#ed-revert').addEventListener('click', () => openEditor(state.current));

$('#ed-discard').addEventListener('click', async () => {
  try {
    await api('/api/discard', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token: state.current }),
    });
    toast('Staged edit discarded', 'good');
    await refreshStaging();
    openEditor(state.current);
  } catch (e) {
    toast(e.message, 'bad');
  }
});

$('#ed-stage').addEventListener('click', async () => {
  const fields = changedFields();
  try {
    const r = await api('/api/stage', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ token: state.current, fields }),
    });
    toast(r.staged ? `Staged ${r.changedFields.join(', ')}` : 'No change to stage', 'good');
    await refreshStaging();
    $('#ed-discard').hidden = !state.staged.has(state.current);
    render();
  } catch (e) {
    toast(e.message, 'bad');
  }
});

$('#btn-staging').addEventListener('click', async () => {
  const doc = await api('/api/staging');
  const body = $('#modal-body');
  $('#modal-title').textContent = `Staged edits (${doc.edits.length})`;
  body.textContent = '';
  if (!doc.edits.length) {
    const p = document.createElement('p');
    p.className = 'dim';
    p.textContent = 'Nothing staged. Edits appear here until reconcile_card_staging.py merges them.';
    body.appendChild(p);
  }
  for (const e of doc.edits) {
    const d = document.createElement('div');
    d.className = 'stage-item';
    const h = document.createElement('h4');
    h.textContent = `${e.name}  `;
    const s = document.createElement('span');
    s.className = 'dim'; s.textContent = e.setId;
    h.appendChild(s); d.appendChild(h);
    for (const [k, v] of Object.entries(e.changed)) {
      const line = document.createElement('div');
      line.className = 'd';
      const b = document.createElement('b'); b.textContent = k;
      line.appendChild(b);
      line.appendChild(document.createTextNode(` → ${JSON.stringify(v)}`));
      d.appendChild(line);
    }
    body.appendChild(d);
  }
  $('#modal').hidden = false;
});

$('#btn-validate').addEventListener('click', async () => {
  $('#modal-title').textContent = 'Validating on-disk corpus…';
  $('#modal-body').textContent = '';
  $('#modal').hidden = false;
  try {
    const r = await api('/api/validate');
    $('#modal-title').textContent = r.ok ? 'Corpus valid' : 'Corpus has errors';
    const pre = document.createElement('pre');
    pre.textContent = r.output || '(no output)';
    $('#modal-body').textContent = '';
    $('#modal-body').appendChild(pre);
  } catch (e) {
    $('#modal-body').textContent = e.message;
  }
});

$('#modal-close').addEventListener('click', () => { $('#modal').hidden = true; });
$('#modal').addEventListener('click', (e) => {
  if (e.target === $('#modal')) $('#modal').hidden = true;
});

for (const id of ['#q', '#f-set', '#f-class', '#f-type', '#f-rarity', '#f-staged',
  '#f-commented']) {
  $(id).addEventListener('input', render);
}
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    if (!$('#modal').hidden) $('#modal').hidden = true;
    else if (!$('#editor').hidden) $('#ed-close').click();
  }
});

load().catch((e) => toast('Load failed: ' + e.message, 'bad'));
