#!/usr/bin/env python3
"""Aggregate the Kingdom card corpus into a single data file for the admin panel.

Reads every *.cards.json under unity/Assets/CardData/ and emits
tooling/card-admin/data.json: a normalized per-card array plus precomputed
corpus-wide and per-class rollups (keyword frequency, targeting-selector
frequency, cost distribution, type split, and minion attack/health
distribution).

Regenerate any time the card data changes:  python tooling/card-admin/build_data.py

Follows the pattern of tooling/scripts/card_inventory.py: the printed stdout is
the deliverable summary; data.json is the machine-readable output the panel reads.

IT ALSO BRINGS THE LIVE SERVER IN LINE. The corpus is baked into the server image
(`Dockerfile.server` COPYs the CardData dir), frozen at `fly deploy` -- so editing a card
here changes the FACE the client draws while the server keeps resolving the old rule, with
no error on either side. That is exactly what happened to card 1067 on 2026-08-28: it
printed "Summon a 1/1 Gear Wisp" and summoned nothing, against a corpus four days and 34
rules stale. So a plain run compares the working tree's corpus digest against the digest
the live server reports on /healthz and, when they differ, deploys. `--no-deploy` reports
the comparison and stops; `--check` never touches the network at all.
"""
import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import urllib.error
import urllib.request
from collections import Counter, defaultdict
from pathlib import Path

# Repo root is two levels up from this file (tooling/card-admin/build_data.py).
REPO_ROOT = Path(__file__).resolve().parents[2]
# EVERY set file, not a hand-listed subset. CORE split out 2026-08-23 and was never added here,
# so the panel reported 284 cards against a 293-row corpus -- a rollup that is quietly wrong is
# worse than one that is missing, because nothing about it looks incomplete. Globbing means the
# next set is picked up without anyone remembering this line.
#
# RETIREMENT TRIGGER: if a *.cards.json ever lands here that is NOT a shipped set (a fixture, a
# scratch file), this glob starts counting it and must go back to an explicit list -- the same
# list CardJsonRowIO.PathForSet holds.
CARD_FILES = sorted((REPO_ROOT / "unity/Assets/CardData").glob("*.cards.json"))
OUT_PATH = Path(__file__).resolve().parent / "data.json"
# Browsers block fetch('data.json') under file://, so the panel loads this thin
# wrapper instead (window.CARD_DATA = <same object>). data.json stays canonical.
OUT_JS_PATH = Path(__file__).resolve().parent / "data.js"

CARD_TYPES = ["MINION", "SPELL", "WEAPON", "HERO_POWER"]

# The live match server. `fly.toml` keeps one machine running, but a deploy or a Fly host event can
# still leave it suspended, and with auto_start a plain GET is what WAKES it -- which is why the health check is an HTTP request and
# not `fly ssh console`, whose first attempt against a suspended machine simply fails.
HEALTH_URL = "https://emberfall.fly.dev/healthz"
FLY_APP = "emberfall"
CARD_DIR = REPO_ROOT / "unity/Assets/CardData"
DIGEST_LENGTH = 16
EMPTY_DIGEST = "0" * DIGEST_LENGTH


def load_cards():
    """Return a flat list of normalized card records across all set files."""
    cards = []
    for path in CARD_FILES:
        with open(path, "r", encoding="utf-8") as f:
            entries = json.load(f)
        for c in entries:
            cards.append(normalize(c, path.name))
    return cards


def selectors_of(card):
    """Every targeting selector on a card (rules[].action.target.selector)."""
    sels = []
    for rule in card.get("rules", []) or []:
        action = rule.get("action")
        if not isinstance(action, dict):
            continue
        target = action.get("target")
        if isinstance(target, dict) and "selector" in target:
            sels.append(target["selector"])
    return sels


def action_tags_of(card):
    """Category tags for the panel's 'Action types' rollup, one per rules[].action.

    DAMAGE splits by how `target.selection` shapes it (see card-schema.md's `selection`
    section): "1" is a chosen single target, "random: N" is engine-rolled, and any other
    integer is N == the selector's derived maximum -- i.e. it hits everything, an AoE.
    """
    tags = []
    for rule in card.get("rules", []) or []:
        action = rule.get("action")
        if not isinstance(action, dict):
            continue
        t = action.get("type")
        if t == "SILENCE":
            tags.append("SILENCE")
        elif t == "DESTROY":
            tags.append("REMOVAL")
        elif t == "RETURN_TO_HAND":
            tags.append("BOUNCE")
        elif t == "DAMAGE":
            selection = ((action.get("target") or {}).get("selection")) or ""
            if selection == "1":
                tags.append("DAMAGE_SINGLE")
            elif selection.startswith("random:"):
                tags.append("DAMAGE_RANDOM")
            else:
                tags.append("DAMAGE_AOE")
    return tags


def normalize(card, source_file):
    """Flatten one raw card into the shape the panel consumes."""
    return {
        "id": card.get("id"),
        "name": card.get("name"),
        "type": card.get("type"),
        "cost": card.get("cost"),
        "attack": card.get("attack"),
        "health": card.get("health"),
        "durability": card.get("durability"),
        "class": card.get("class"),
        "rarity": card.get("rarity"),
        "keywords": list(card.get("keywords", []) or []),
        "selectors": selectors_of(card),
        "actionTags": action_tags_of(card),
        "tribe": card.get("tribe"),
        "setId": card.get("setId"),
        "collectible": card.get("collectible"),
        "artId": card.get("artId"),
        "sourceFile": source_file,
    }


def rollup(cards):
    """Compute an aggregate bundle over an arbitrary list of cards."""
    keywords = Counter()
    selectors = Counter()
    actions = Counter()
    cost = Counter()
    types = Counter()
    rarities = Counter()
    tribes = Counter()
    attack = Counter()
    health = Counter()

    for c in cards:
        for kw in c["keywords"]:
            keywords[kw] += 1
        for sel in c["selectors"]:
            selectors[sel] += 1
        for act in c["actionTags"]:
            actions[act] += 1
        if c["cost"] is not None:
            cost[c["cost"]] += 1
        if c["type"]:
            types[c["type"]] += 1
        if c["rarity"]:
            rarities[c["rarity"]] += 1
        if c["tribe"]:
            tribes[c["tribe"]] += 1
        if c["type"] == "MINION":
            if c["attack"] is not None:
                attack[c["attack"]] += 1
            if c["health"] is not None:
                health[c["health"]] += 1

    minion_count = types.get("MINION", 0)
    return {
        "total": len(cards),
        "typeSplit": {t: types.get(t, 0) for t in CARD_TYPES},
        "keywordFrequency": ordered_counter(keywords),
        "selectorFrequency": ordered_counter(selectors),
        "actionFrequency": ordered_counter(actions),
        "costDistribution": int_keyed(cost),
        "rarityBreakdown": ordered_counter(rarities),
        "tribeBreakdown": ordered_counter(tribes),
        "minionCount": minion_count,
        "attackDistribution": int_keyed(attack),
        "healthDistribution": int_keyed(health),
    }


def ordered_counter(counter):
    """Counter -> dict sorted by count desc, then name, for stable output."""
    return {k: v for k, v in sorted(counter.items(), key=lambda kv: (-kv[1], str(kv[0])))}


def int_keyed(counter):
    """Counter with int keys -> dict with string keys sorted numerically."""
    return {str(k): counter[k] for k in sorted(counter.keys())}


def corpus_digest():
    """Digest the working tree's card files, byte for byte.

    MIRRORS CorpusDigest.Compute IN src/Kingdom.Net.Server/Data/CorpusDigest.cs and the two
    must agree exactly -- this is the value compared against what the live server reports. Per
    file, in relative-path order: "<relative path>\\n<sha256 of its bytes>\\n" fed into one
    running sha256, truncated to 16 hex characters.

    Bytes, never parsed rows: a digest over deserialized cards would need two serializers held
    in step, which is the drift generator this exists to remove. Hashing the path as well means
    a set file that is RENAMED registers as a different corpus even if no card text moved.
    """
    files = sorted(
        (str(p.relative_to(CARD_DIR)).replace("\\", "/"), p)
        for p in CARD_DIR.rglob("*.cards.json")
    )
    if not files:
        return EMPTY_DIGEST

    running = hashlib.sha256()
    for rel, path in files:
        running.update(rel.encode("utf-8"))
        running.update(b"\n")
        running.update(hashlib.sha256(path.read_bytes()).hexdigest().encode("utf-8"))
        running.update(b"\n")
    return running.hexdigest()[:DIGEST_LENGTH]


def live_health(timeout=45):
    """GET /healthz. Returns the parsed body, or None with a reason printed.

    Never raises. A missing network or a sleeping machine must not fail the local aggregation --
    the artifacts are already written by the time this runs, and reporting "could not reach the
    server" is a true and useful answer where a traceback is neither.
    """
    try:
        with urllib.request.urlopen(HEALTH_URL, timeout=timeout) as response:
            return json.loads(response.read().decode("utf-8"))
    except (urllib.error.URLError, OSError, ValueError, json.JSONDecodeError) as exc:
        print(f"  could not reach {HEALTH_URL}: {exc}")
        return None


def fly_binary():
    """flyctl is installed under both names depending on how it was installed."""
    return shutil.which("fly") or shutil.which("flyctl")


def working_tree_note():
    """One line naming what a deploy would actually ship.

    `fly deploy` uploads the WORKING TREE, not HEAD -- so an uncommitted engine change under
    src/ rides along with the card data. That is usually what is wanted and occasionally a
    surprise, and the surprise is only avoidable if it is said out loud before the upload.
    """
    try:
        head = subprocess.run(
            ["git", "rev-parse", "--short", "HEAD"], cwd=REPO_ROOT,
            capture_output=True, text=True, check=True).stdout.strip()
        dirty = subprocess.run(
            ["git", "status", "--porcelain", "--", "src", "unity/Assets/CardData",
             "tooling/data/bot-roster.json"],
            cwd=REPO_ROOT, capture_output=True, text=True, check=True).stdout.strip()
    except (OSError, subprocess.CalledProcessError):
        return "deploying the working tree (git unavailable, contents unverified)"
    if not dirty:
        return f"deploying the working tree at {head}, clean across the three baked paths"
    changed = len(dirty.splitlines())
    return (f"deploying the working tree at {head} PLUS {changed} uncommitted "
            f"change(s) under src/, CardData/ or bot-roster.json")


def sync_server(local, deploy):
    """Compare the live corpus against the working tree and, when asked, close the gap.

    Returns an exit code. Deliberately NOT fatal when the server is merely unreachable: the
    aggregation succeeded and that is what the caller asked for. It IS fatal when a deploy was
    attempted and did not land, because then the run reported an action it did not perform.
    """
    banner = "SERVER CORPUS SYNC" if deploy else "SERVER CORPUS SYNC (report only)"
    print("=" * 72)
    print(banner)
    print("=" * 72)
    print(f"Local corpus digest:  {local}")

    health = live_health()
    if health is None:
        print("Live corpus digest:   UNKNOWN")
        print("SERVER_CORPUS=unreachable")
        print("=" * 72)
        return 0

    live = health.get("corpus")
    print(f"Live corpus digest:   {live or 'ABSENT'}"
          + ("" if live else "  (server predates the /healthz corpus field)"))
    print(f"Live cards loaded:    {health.get('cards', 'unknown')}")

    if live == local:
        print("SERVER_CORPUS=in-sync")
        print("=" * 72)
        return 0

    print("Live server is BEHIND the working tree -- cards will resolve by their deployed rules.")
    if not deploy:
        print("SERVER_CORPUS=stale")
        print("Run without --no-deploy to bring it in line.")
        print("=" * 72)
        return 0

    fly = fly_binary()
    if fly is None:
        print("SERVER_CORPUS=stale")
        print("flyctl not on PATH -- cannot deploy. Install it, or run `fly deploy` yourself.")
        print("=" * 72)
        return 1

    print(working_tree_note())
    print(f"$ {Path(fly).name} deploy --app {FLY_APP}")
    print("-" * 72)
    # Output is STREAMED, not captured: a deploy runs for minutes and a silent terminal is
    # indistinguishable from a hung one.
    result = subprocess.run([fly, "deploy", "--app", FLY_APP], cwd=REPO_ROOT)
    print("-" * 72)
    if result.returncode != 0:
        print(f"SERVER_CORPUS=deploy-failed (exit {result.returncode})")
        print("=" * 72)
        return result.returncode

    # Re-read rather than trusting the deploy's own exit code. `fly deploy` returning 0 means
    # the release was created; it does not prove the machine that answers /healthz is serving
    # it, and "deployed" is precisely the claim that was wrong on 2026-08-28.
    after = live_health()
    live_after = (after or {}).get("corpus")
    print(f"Live corpus digest after deploy: {live_after or 'UNKNOWN'}")
    if live_after == local:
        print(f"Live cards loaded:{after.get('cards', 'unknown')}")
        print("SERVER_CORPUS=deployed")
        print("=" * 72)
        return 0
    print("SERVER_CORPUS=deploy-unconfirmed")
    print("The deploy succeeded but /healthz still does not match. Check `fly logs -a "
          f"{FLY_APP}` -- a machine may still be rolling.")
    print("=" * 72)
    return 1


def rendered(data):
    """The exact text of both outputs, so --check never has to write to compare."""
    payload = json.dumps(data, indent=2, ensure_ascii=False) + "\n"
    mirror = (
        "// Generated by build_data.py — do not edit by hand. Mirror of data.json.\n"
        "window.CARD_DATA = " + json.dumps(data, ensure_ascii=False) + ";\n"
    )
    return {OUT_PATH: payload, OUT_JS_PATH: mirror}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--check", action="store_true",
        help="report staleness and exit 1 if stale; write nothing, touch no network",
    )
    parser.add_argument(
        "--no-deploy", action="store_true",
        help="report the live server's corpus but never deploy",
    )
    args = parser.parse_args()

    cards = load_cards()
    digest = corpus_digest()
    classes = sorted({c["class"] for c in cards if c["class"]})

    by_class = defaultdict(list)
    for c in cards:
        by_class[c["class"]].append(c)

    data = {
        "generatedFrom": [str(p.relative_to(REPO_ROOT)).replace("\\", "/") for p in CARD_FILES],
        "totalCards": len(cards),
        # Which corpus this rollup describes, in the same terms /healthz reports — so a panel
        # open next to a live game can be told apart from one describing an older corpus.
        "corpusDigest": digest,
        "classes": classes,
        "cardTypes": CARD_TYPES,
        "corpus": rollup(cards),
        "byClass": {cls: rollup(by_class[cls]) for cls in classes},
        "cards": cards,
    }

    # Both outputs are written through Python's default newline translation, so read them back
    # the same way — universal newlines restore "\n" and the comparison stays platform-neutral.
    outputs = rendered(data)
    stale = []
    for path, text in outputs.items():
        if not path.exists():
            stale.append(path)
            continue
        with open(path, "r", encoding="utf-8") as f:
            if f.read() != text:
                stale.append(path)

    if not args.check:
        for path, text in outputs.items():
            with open(path, "w", encoding="utf-8") as f:
                f.write(text)

    # Printed summary is the run's deliverable — quote it, don't paraphrase it.
    print("=" * 72)
    print("CARD CORPUS AGGREGATION")
    print("=" * 72)
    print(f"Sources: {', '.join(p.name for p in CARD_FILES)}")
    print(f"Total cards: {len(cards)}")
    print(f"Corpus digest: {digest}")
    print(f"Classes ({len(classes)}): {', '.join(classes)}")
    print()
    print("Type split (corpus):")
    for t in CARD_TYPES:
        print(f"  {t}: {data['corpus']['typeSplit'][t]}")
    print()
    print("Per-class card counts:")
    for cls in classes:
        print(f"  {cls}: {data['byClass'][cls]['total']}")
    print()
    for path, text in outputs.items():
        name = path.relative_to(REPO_ROOT)
        if args.check:
            print(f"{'STALE' if path in stale else 'OK   '} {name} ({len(text)} chars expected)")
        else:
            print(f"Wrote {name} ({path.stat().st_size} bytes)")
    # The counter is the staleness measured BEFORE any write, so name it for what the run did:
    # in write mode it is how many outputs the run actually had to regenerate.
    print(f"CARD_ADMIN_{'STALE_OUTPUTS' if args.check else 'REGENERATED'}={len(stale)}")
    print("=" * 72)

    # --check is the CI/pre-commit shape: pure, offline, and about THIS repo's artifacts only.
    # Reaching the network there would make a gate's verdict depend on whether fly.io answered.
    if args.check:
        return 1 if stale else 0

    print()
    return sync_server(digest, deploy=not args.no_deploy)


if __name__ == "__main__":
    sys.exit(main())
