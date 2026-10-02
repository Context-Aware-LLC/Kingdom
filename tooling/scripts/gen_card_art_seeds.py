"""Draft card-art SEEDS - the hand-authored direction that turns a placeholder brief into a
real one.

WHY THIS EXISTS. gen_card_prompt_briefs.py composes a full prompt for every card, but a card
with no seed gets "[perspective unset]", "[unset - ... replace with a real setting]" and a
subject line the module itself labels "[DRAFT - placeholder only, replace with a real subject
before generating]". Nothing downstream refuses to generate from that, so an unseeded batch
comes back weakly directed and samey. Seeds are the quality constraint, not the image model.

Measured 2026-08-06: 30 of 260 cards had a seed, and all 30 already had art - so 0 of the 83
cards still needing art were seeded.

TWO SOURCES, IN PRIORITY ORDER.

  1. SCRAPE. The frozen drafts corpus (see PROSE_CORPUS) carries a hand-authored prose prompt per card
     from the original art passes. That is real authored direction and it outranks anything
     invented here. It is prose, not fields, so it is RESTRUCTURED into the seed schema rather
     than parsed - the model is asked to split existing words into subject/perspective/
     background/notes, never to add new ones.

  2. DRAFT. A card with neither a seed nor a prose prompt is drafted from card data plus the
     class/world descriptors, few-shot against every seed already on hand. Scrape first and
     draft second in the same run: the scraped entries become exemplars for the drafted ones,
     which is why --scrape alone is a useful first pass.

WHAT IT WILL NOT DO. It never overwrites an existing seed - hand-authored direction is the
input, not the output. Re-running only fills gaps. Output is TEXT for review before a single
image is generated, which is the whole point: a bad seed costs a re-read, a bad batch costs a
generation run.

Keys come from the environment only: GEMINI_API_KEY, OPENAI_API_KEY.

  python tooling/scripts/gen_card_art_seeds.py --dry-run          # show the request, call nothing
  python tooling/scripts/gen_card_art_seeds.py --scrape           # stage 1 only
  python tooling/scripts/gen_card_art_seeds.py --scrape --draft   # both, scraped feed the drafts
  python tooling/scripts/gen_card_art_seeds.py --draft --set REKINDLING --limit 5
"""

import argparse
import collections
import importlib.util
import json
import os
import pathlib
import re
import sys
import time

import requests

HERE = pathlib.Path(__file__).resolve().parent
REPO = HERE.parents[1]


def _load(name: str, filename: str):
    spec = importlib.util.spec_from_file_location(name, HERE / filename)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


briefs = _load("gen_card_prompt_briefs", "gen_card_prompt_briefs.py")
SEEDS_DIR = briefs.SEEDS_DIR
CARD_DATA = REPO / "unity" / "Assets" / "CardData"
CARD_ART = REPO / "unity" / "Assets" / "Art" / "Cards"

SEED_FIELDS = ("subject", "perspective", "background", "notes")

# A seed that still carries a placeholder marker is worse than no seed, because it reads as
# authored direction to everything downstream. Reject on the markers the brief module emits.
PLACEHOLDER_MARKERS = ("[draft", "placeholder only", "[unset", "unset ]", "[perspective unset")

TEXT_PROVIDERS = {
    "gemini": {
        # NOT "gemini-3.1-flash" - that name 404s on generateContent. The image sibling
        # gen_card_art.py uses does not imply a text model of the same name; list with
        # GET /v1beta/models?key=... before assuming one exists.
        "model": "gemini-3.6-flash",
        "env": "GEMINI_API_KEY",
        "note": "cheapest capable text model; verified present in the v1beta model list",
    },
    "openai": {
        "model": "gpt-5.1",
        "env": "OPENAI_API_KEY",
        "note": "fallback; pass --model to pin a different one",
    },
}

SCHEMA_BLOCK = """Return ONE JSON object and nothing else, with exactly these four string keys:

  "subject"     - what is physically depicted. The creature, person, object or event, its
                  posture, materials, and the single readable focal point. One or two
                  sentences. Concrete nouns, not adjectives about mood.
  "perspective" - camera. Distance, angle, eye height, and what must stay inside the middle
                  70% so it survives the frame overlay and reads at 220 px. One sentence.
  "background"  - the setting and its palette, chosen to OPPOSE the subject in hue, value or
                  temperature so the silhouette separates. One sentence.
  "notes"       - constraints, continuity with other cards, or things to avoid. May be "".

No markdown, no code fence, no commentary. Never emit a bracketed placeholder."""


# --------------------------------------------------------------------- inputs

def load_rows():
    rows = []
    for fp in sorted(CARD_DATA.glob("*.cards.json")):
        rows.extend(json.loads(fp.read_text(encoding="utf-8")))
    return rows


def has_art(card):
    return (CARD_ART / card.get("setId", "EMBERFALL")
            / f"{card['artId'].replace('/', '_')}.png").exists()


def load_all_seeds():
    """set id -> {card id: seed}. Every set file on disk, not just one."""
    out = {}
    for fp in sorted(SEEDS_DIR.glob("*.json")):
        out[fp.stem] = json.loads(fp.read_text(encoding="utf-8"))
    return out


DRAFTS_ROOT = REPO / "art-pipeline" / "card-art"

_DRAFT_ID = re.compile(r"illustration for .+? \(([a-z0-9_]+)\)")
_DRAFT_PERSP = re.compile(r"^- Perspective:\s*(.+)$", re.M)
_DRAFT_BG = re.compile(r"^- Background palette and setting:\s*(.+)$", re.M)


def load_draft_seeds():
    """card id -> seed, scraped DETERMINISTICALLY from art-pipeline/card-art/<setId>/<artId>.md.

    These are briefs an art lead already filled in and generated from, so the direction is real
    and previously used - no model call, no tokens, and nothing invented. The three fields are
    already labelled in the file; only the subject needs slicing out of the SUBJECT section,
    whose leading SCALE/VISUAL POWER lines are computed boilerplate rather than direction.

    The prompt used to be `draft.txt` inside a per-card leaf directory. It is named after its
    card and sits beside the picture now (Sam, 2026-09-05) - see
    gen_card_prompt_briefs.delivery_block. The glob below matched the old name only, so this
    stage silently scraped ZERO after that move; the card id is read from the file's own text
    rather than its path, so nothing else here depended on the layout.

    art-pipeline/ is gitignored and disposable, so this is opportunistic: a missing or
    malformed draft is skipped, never fatal.
    """
    out, skipped = {}, 0
    if not DRAFTS_ROOT.exists():
        return out, 0
    for fp in sorted(DRAFTS_ROOT.rglob("*.md")):
        try:
            text = fp.read_text(encoding="utf-8", errors="replace")
        except OSError:
            skipped += 1
            continue
        m = _DRAFT_ID.search(text)
        p = _DRAFT_PERSP.search(text)
        b = _DRAFT_BG.search(text)
        if not (m and p and b):
            skipped += 1
            continue
        # SUBJECT runs to the next all-caps header. Drop the computed SCALE / VISUAL POWER
        # lines: they are derived from cost and rarity by scale_clause(), not authored.
        body = text.split("\nSUBJECT\n", 1)
        if len(body) < 2:
            skipped += 1
            continue
        section = re.split(r"\n(?=[A-Z][A-Z ]{3,}\n)", body[1])[0]
        lines = [ln.strip() for ln in section.splitlines()
                 if ln.strip() and not ln.startswith(("SCALE ", "VISUAL POWER"))]
        subject = " ".join(" ".join(lines).split())
        # A draft folds its per-card prohibitions into the subject prose ("No enormous rifle,
        # cannon, ..."). The hand-authored seeds keep those in `notes`, so split them back out:
        # trailing sentences that open with No/Avoid/Do not are constraints, not description.
        constraints = []
        sentences = re.split(r"(?<=\.)\s+", subject)
        while sentences and re.match(r"^(No|Avoid|Do not|Don't)\b", sentences[-1]):
            constraints.insert(0, sentences.pop())
        if sentences:
            subject = " ".join(sentences)
        # fp.stem is the card's artId now; it used to be fp.parent.name, the per-card leaf
        # directory. After the flatten that would have named the EXPANSION on every seed.
        provenance = f"Scraped from the draft brief this card was generated from ({fp.stem})."
        seed = {"subject": subject, "perspective": " ".join(p.group(1).split()),
                "background": " ".join(b.group(1).split()),
                "notes": " ".join(constraints + [provenance])}
        if any(any(mk in seed[f].lower() for mk in PLACEHOLDER_MARKERS) for f in SEED_FIELDS):
            skipped += 1
            continue
        if len(seed["subject"]) < 40:
            skipped += 1
            continue
        out[m.group(1)] = seed
    return out, skipped


# The prose corpus is FROZEN HISTORY, and this is the only thing that still reads it.
#
# These files were `docs/design/card-drafts-*.md` until the 2026-07-31 documentation
# reorganisation moved them under hist-docs/. Nothing repointed the reader, so the scrape
# stage silently harvested nothing from then until 2026-09-05 — it printed "missing - skipped"
# per file and returned an empty dict, which is indistinguishable from "already fully scraped".
# Measured when it was fixed: 162 card ids carry prose here, and 36 of them belong to rows that
# still have NO seed. That is hand-authored direction, and it outranks anything a model drafts.
#
# The parser used to be imported out of gen_card_art.py. It lives here now because this is its
# only consumer, and gen_card_art.py no longer composes from this corpus at all.
#
# RETIREMENT TRIGGER: when every id in this corpus has a seed under tooling/card-art-seeds/,
# the --scrape stage and this constant are spent — delete both rather than leaving a stage that
# reads frozen history forever. `--scrape` prints the remaining count on every run.
PROSE_CORPUS = (
    REPO / "docs" / "hist-docs" / "source-snapshots"
    / "2026-07-31-pre-reorganization" / "design"
)


def _art_section(md: str) -> str:
    """The closing 'Art prompts' section of a card-drafts .md."""
    m = re.search(r"^##\s+\d+\.\s*Art prompts\s*$", md, re.M)
    if not m:
        raise ValueError("no '## N. Art prompts' heading")
    tail = md[m.end():]
    nxt = re.search(r"^##\s", tail, re.M)
    return tail[: nxt.start()] if nxt else tail


def _card_prompts(section: str) -> dict[str, tuple[str, str]]:
    """id -> (head-of-first-line, prompt body) for each entry in the fence."""
    fences = re.findall(r"```[a-z]*\n(.*?)```", section, re.S)
    if not fences:
        raise ValueError("no fenced prompt block")
    out: dict[str, tuple[str, str]] = {}
    for chunk in re.split(r"\n\s*\n", "\n".join(fences)):
        chunk = chunk.strip()
        if not chunk:
            continue
        first, _, rest = chunk.partition("\n")
        m = re.match(r"^([a-z0-9_]+)\s*[—–-]\s*(.*)$", first)
        if not m:
            continue
        cid, head = m.group(1), m.group(2).strip()
        out[cid] = (head, " ".join(rest.split()))
    return out


def load_prose_prompts():
    """card handle -> the hand-authored prose prompt from the frozen drafts corpus.

    A parse failure on one file is reported and skipped rather than aborting the seed run:
    this corpus is history and will never be edited to satisfy a parser.
    """
    if not PROSE_CORPUS.is_dir():
        print(f"  ! prose corpus not found at {PROSE_CORPUS} - scrape stage unavailable",
              file=sys.stderr)
        return {}
    out = {}
    files = sorted(PROSE_CORPUS.glob("card-drafts-*.md"))
    for md in files:
        try:
            parsed = _card_prompts(_art_section(md.read_text(encoding="utf-8", errors="replace")))
        except ValueError as e:
            print(f"  ! {md.name}: {e} - skipped", file=sys.stderr)
            continue
        for cid, (head, body) in parsed.items():
            out[cid] = (head + " " + body).strip() if head else body
    print(f"  prose corpus: {len(files)} file(s), {len(out)} card id(s) with authored direction")
    return out


# ----------------------------------------------------------------- composition

def card_facts(card, class_descriptors, world_tone):
    cls = (card.get("class") or "NEUTRAL")
    palette, motifs, mood = class_descriptors.get(cls.lower(), ("", "", ""))
    bits = [
        f"name: {card['name']}",
        f"id: {card['id']}",
        f"type: {card.get('type', 'MINION')}",
        f"class: {cls}",
        f"set: {card.get('setId', 'EMBERFALL')}",
        f"rarity: {card.get('rarity', 'COMMON')}",
        f"cost: {card.get('cost', 0)}",
    ]
    if card.get("attack") is not None:
        bits.append(f"attack/health: {card.get('attack')}/{card.get('health')}")
    if card.get("tribe"):
        bits.append(f"tribe: {card['tribe']}")
    if card.get("keywords"):
        bits.append(f"keywords: {', '.join(card['keywords'])}")
    if card.get("text"):
        bits.append(f"card text: {card['text']}")
    bits.append(f"class palette / motifs / mood: {palette}; {motifs}; {mood}")
    bits.append(f"world tone: {world_tone.strip()[:400]}")
    return "\n".join(bits)


def exemplar_block(seeds_by_set, rows_by_id, want_class, n=6):
    """Prefer same-class exemplars, backfill with any. Same-class first is what keeps a
    Nightveil seed from reading like a Verdant one."""
    pool = []
    for set_id, entries in seeds_by_set.items():
        for cid, seed in entries.items():
            card = rows_by_id.get(cid)
            if not card or not isinstance(seed, dict):
                continue
            pool.append(((card.get("class") or "NEUTRAL"), card, seed))
    same = [p for p in pool if p[0] == want_class]
    other = [p for p in pool if p[0] != want_class]
    chosen = (same + other)[:n] if same else other[:n]
    out = []
    for cls, card, seed in chosen:
        out.append(f"--- {card['name']} ({cls})\n" + json.dumps(
            {k: seed.get(k, "") for k in SEED_FIELDS}, indent=2, ensure_ascii=False))
    return "\n\n".join(out)


def scrape_request(card, prose, exemplars):
    return f"""You are restructuring an EXISTING hand-authored art prompt into the project's
seed schema. This is a reformatting task, not a writing task.

RULES
- Use only what the prose already says. Do not invent a new subject, setting or camera.
- If the prose is silent on perspective or background, say what the prose implies as plainly
  as possible rather than inventing detail. Do not leave a field empty except "notes".
- Preserve specific nouns, materials and colours verbatim where you can.

{SCHEMA_BLOCK}

HOW EXISTING SEEDS ARE WRITTEN (match this register and length):

{exemplars}

CARD
{card['name']} ({card['id']}), {card.get('class') or 'NEUTRAL'}.

THE HAND-AUTHORED PROMPT TO RESTRUCTURE
{prose}
"""


def already_depicted(seeds_by_set, rows_by_id, want_class, budget=90):
    """A compact "these already exist" digest (Sam, 2026-08-06).

    The complaint that started this was that a fresh batch came back looking like cards
    generated days earlier. Exemplars alone make that WORSE - they teach the register, and the
    register is what repeats. So the same corpus is also fed back as a prohibition: one short
    line per already-seeded card, name plus the head of its subject, capped so it stays cheap.

    Same-class first, because that is where collisions actually happen - two Verdant minions
    both becoming "an antlered guardian in a mossy ruin" is the failure mode. Cross-class
    repetition is far less visible on the board.
    """
    lines = []
    for set_id, entries in seeds_by_set.items():
        for cid, seed in entries.items():
            card = rows_by_id.get(cid)
            if not card or not isinstance(seed, dict) or not seed.get("subject"):
                continue
            gist = " ".join(seed["subject"].split())[:budget].rstrip()
            lines.append(((card.get("class") or "NEUTRAL") == want_class,
                          f"- {card['name']}: {gist}..."))
    lines.sort(key=lambda t: not t[0])
    return "\n".join(t[1] for t in lines)


def draft_request(card, facts, exemplars, taken):
    return f"""You are writing art direction for one card in Kingdom of Bad Ideas, a painterly
digital fantasy trading card game set in a bright, warm storybook realm: an opulent capital
(Pendingham) ringed by threadbare, cheerful villages, played completely straight.

{SCHEMA_BLOCK}

ALREADY DEPICTED - DO NOT REPEAT ANY OF THESE
Every line is a card that already exists. Your subject must be visibly distinct from all of
them: a different creature or object, a different action, a different setting. Matching the
class palette is required; reusing a pose, silhouette or scene is not allowed.

{taken}

RULES
- One clear subject with a strong silhouette that reads at 220 px on a phone.
- The subject's focal point sits centered, inside the middle 70%; a frame overlays the edges.
- Background opposes the subject in hue, value or temperature.
- No text, letters, logos, banners, watermarks, frames or UI elements in the described image.
- Stay inside the class palette and motifs below. Do not borrow another class's iconography.
- Be specific. "A warrior in a ruin" is a failure; name the materials, the light, the moment.

HOW EXISTING SEEDS ARE WRITTEN (match this register and length):

{exemplars}

CARD
{facts}
"""


# -------------------------------------------------------------------- provider

def call_model(prompt, provider, model, key, timeout=120):
    if provider == "gemini":
        r = requests.post(
            f"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent",
            headers={"x-goog-api-key": key, "Content-Type": "application/json"},
            json={"contents": [{"parts": [{"text": prompt}]}],
                  "generationConfig": {"temperature": 0.9, "responseMimeType": "application/json"}},
            timeout=timeout,
        )
        if r.status_code != 200:
            return None, f"HTTP {r.status_code}: {r.text[:200]}"
        try:
            return r.json()["candidates"][0]["content"]["parts"][0]["text"], ""
        except (KeyError, IndexError) as e:
            return None, f"unexpected response shape: {type(e).__name__} {r.text[:200]}"
    r = requests.post(
        "https://api.openai.com/v1/chat/completions",
        headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json"},
        json={"model": model, "temperature": 0.9,
              "response_format": {"type": "json_object"},
              "messages": [{"role": "user", "content": prompt}]},
        timeout=timeout,
    )
    if r.status_code != 200:
        return None, f"HTTP {r.status_code}: {r.text[:200]}"
    try:
        return r.json()["choices"][0]["message"]["content"], ""
    except (KeyError, IndexError) as e:
        return None, f"unexpected response shape: {type(e).__name__} {r.text[:200]}"


def parse_seed(raw):
    """Returns (seed, error). Fails closed: a seed missing a field, or carrying a placeholder
    marker, is rejected rather than written - an unusable seed that LOOKS authored is the one
    failure mode this whole script exists to prevent."""
    if raw is None:
        return None, "no response"
    text = raw.strip()
    if text.startswith("```"):
        text = re.sub(r"^```[a-z]*\n|\n```$", "", text)
    try:
        obj = json.loads(text)
    except json.JSONDecodeError as e:
        return None, f"not JSON: {e}"
    if not isinstance(obj, dict):
        return None, f"expected an object, got {type(obj).__name__}"
    seed = {}
    for f in SEED_FIELDS:
        v = obj.get(f)
        if not isinstance(v, str):
            return None, f"field '{f}' missing or not a string"
        v = " ".join(v.split())
        if f != "notes" and len(v) < 20:
            return None, f"field '{f}' is too short to be real direction ({len(v)} chars)"
        low = v.lower()
        if any(m in low for m in PLACEHOLDER_MARKERS):
            return None, f"field '{f}' still carries a placeholder marker"
        seed[f] = v
    return seed, ""


# ------------------------------------------------------------------------ main

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--from-drafts", action="store_true",
                    help="stage 0: scrape art-pipeline/card-art/**/<artId>.md deterministically. NO API "
                         "CALL, no tokens - the fields are already labelled in the file")
    ap.add_argument("--scrape", action="store_true",
                    help="stage 1: restructure the frozen corpus's hand-authored prompts into seeds")
    ap.add_argument("--draft", action="store_true",
                    help="stage 2: draft seeds for cards with no prompt, few-shot off stage 1")
    ap.add_argument("--set", dest="set_id", help="limit to one setId")
    ap.add_argument("--only-missing-art", action="store_true",
                    help="only cards that have no art on disk yet")
    ap.add_argument("--limit", type=int, help="stop after N seeds (per stage)")
    ap.add_argument("--provider", choices=sorted(TEXT_PROVIDERS), default="gemini")
    ap.add_argument("--model", help="override the provider's default model")
    ap.add_argument("--dry-run", action="store_true",
                    help="print the composed request for the first card of each stage, call nothing")
    args = ap.parse_args()

    if not (args.from_drafts or args.scrape or args.draft):
        ap.error("pick at least one of --from-drafts / --scrape / --draft")

    needs_model = args.scrape or args.draft
    prov = TEXT_PROVIDERS[args.provider]
    model = args.model or prov["model"]
    key = os.environ.get(prov["env"], "")
    if needs_model and not key and not args.dry_run:
        print(f"{prov['env']} is not set", file=sys.stderr)
        return 2

    rows = load_rows()
    # KEYED BY THE NUMERIC CARD ID, everywhere in this file -- briefs.seed_key. It was the
    # handle rename then orphaned 18 hand-authored seeds silently: the cards read as unseeded
    # HANDLE, then the artId, and each rename stranded direction that still existed and would
    # have been re-drafted over. See briefs.seed_key for both rounds and the ruling that ended it.
    #
    # The two LEGACY inputs below still speak the handle and are not ours to rewrite -- the
    # frozen prose corpus and the scraped <artId>.md briefs -- so both are translated
    # through briefs.seed_key_index on the way in, and nothing downstream sees a name.
    rows_by_id = {briefs.seed_key(c): c for c in rows if c.get("artId")}
    to_key = briefs.seed_key_index(rows)
    seeds_by_set = load_all_seeds()
    seeded = {cid for entries in seeds_by_set.values() for cid in entries}
    class_descriptors = briefs.load_class_descriptors()
    prose = {to_art[k]: v for k, v in load_prose_prompts().items()
             if k in to_art} if args.scrape else {}

    targets = [c for c in rows if c.get("artId") and briefs.seed_key(c) not in seeded]
    if args.set_id:
        targets = [c for c in targets if c.get("setId", "EMBERFALL") == args.set_id]
    if args.only_missing_art:
        targets = [c for c in targets if not has_art(c)]

    world_cache = {}
    written = collections.Counter()
    failures = []

    # STAGE 0, before anything costs a token: every scraped brief already carries filled-in
    # direction, so those cards are seeded for free and then serve as exemplars for the rest.
    from_drafts = {}
    if args.from_drafts:
        scraped, skipped = load_draft_seeds()
        in_scope = {briefs.seed_key(c) for c in targets}
        # a brief names the card by a NAME -- handle or artId; translate before the scope test,
        # or every scraped draft for a renamed card is silently discarded as out of scope.
        scraped = {to_key[k]: v for k, v in scraped.items() if k in to_key}
        from_drafts = {cid: s for cid, s in scraped.items() if cid in in_scope}
        print(f"stage 0 FROM-DRAFTS: {len(scraped)} brief(s) parsed "
              f"({skipped} unusable), {len(from_drafts)} of them unseeded and in scope")
        for cid, seed in from_drafts.items():
            set_id = rows_by_id[cid].get("setId", "EMBERFALL")
            seeds_by_set.setdefault(set_id, {})[cid] = seed
            written[set_id] += 1
        seeded |= set(from_drafts)
        targets = [c for c in targets if briefs.seed_key(c) not in from_drafts]

    scrape_t = [c for c in targets if briefs.seed_key(c) in prose] if args.scrape else []
    draft_t = [c for c in targets if briefs.seed_key(c) not in prose] if args.draft else []

    print(f"cards: {len(rows)}   seeded now: {len(seeded)}   still unseeded in scope: {len(targets)}")
    print(f"  stage 1 SCRAPE (a hand-authored prompt exists): {len(scrape_t)}")
    print(f"  stage 2 DRAFT  (nothing to scrape):             {len(draft_t)}")
    if needs_model:
        print(f"  provider: {args.provider} / {model}")
    print()

    def run(stage, cards, build):
        nonlocal seeds_by_set
        n = 0
        for card in cards:
            if args.limit and n >= args.limit:
                print(f"  [{stage}] --limit {args.limit} reached, "
                      f"{len(cards) - n} card(s) NOT processed")
                break
            set_id = card.get("setId", "EMBERFALL")
            if set_id not in world_cache:
                world_cache[set_id] = briefs.load_world_tone(set_id)
            ex = exemplar_block(seeds_by_set, rows_by_id, card.get("class") or "NEUTRAL")
            req = build(card, world_cache[set_id], ex)
            if args.dry_run:
                print(f"===== [{stage}] composed request for {card['name']} =====")
                print(req)
                return
            raw, err = call_model(req, args.provider, model, key)
            seed, perr = parse_seed(raw) if not err else (None, err)
            if seed is None:
                failures.append((briefs.seed_key(card), card["name"], perr))
                print(f"  [{stage}] FAIL {card['name']}: {perr}", flush=True)
            else:
                # ART KEY. Writing a new seed under the handle is what stranded the last 18.
                seeds_by_set.setdefault(set_id, {})[briefs.seed_key(card)] = seed
                written[set_id] += 1
                print(f"  [{stage}] ok   {card['name']}: {seed['subject'][:74]}", flush=True)
            n += 1
            time.sleep(0.4)

    if scrape_t:
        run("scrape", scrape_t,
            lambda c, w, ex: scrape_request(c, prose[briefs.seed_key(c)], ex))
    if draft_t and (not args.dry_run or not scrape_t):
        run("draft", draft_t,
            lambda c, w, ex: draft_request(
                c, card_facts(c, class_descriptors, w), ex,
                already_depicted(seeds_by_set, rows_by_id, c.get("class") or "NEUTRAL")))

    if args.dry_run:
        print("\nDRY RUN - no model called, nothing written.")
        return 0

    for set_id, n in sorted(written.items()):
        fp = SEEDS_DIR / f"{set_id}.json"
        # Sorted NUMERICALLY. The keys are numbers in string clothing, and a lexical sort
        # files 1015 between 101 and 102 -- which makes every future diff of this file
        # unreadable for no reason anyone would remember.
        ordered = dict(sorted(seeds_by_set[set_id].items(), key=lambda kv: int(kv[0])))
        fp.write_text(json.dumps(ordered, indent=2, ensure_ascii=False) + "\n",
                      encoding="utf-8")
        print(f"\nwrote {fp.relative_to(REPO)}: +{n} seeds, {len(seeds_by_set[set_id])} total")

    print(f"\nseeds written: {sum(written.values())}   failures: {len(failures)}")
    for cid, name, why in failures:
        print(f"  {cid} ({name}): {why}")
    if written:
        print("\nNEXT: read the seeds before generating. They are text; a bad one costs a "
              "re-read, a bad batch costs a generation run.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
