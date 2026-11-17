#!/usr/bin/env python3
"""Report, class by class, what the cards tell a deck builder to collect.

A SIGNPOST is a card whose own text names its partners: a tribe, a keyword, a damaged
minion, a hero that was hurt this turn, a price paid in minions, or anything that keeps
firing after it lands. Win rate and rarity cannot see one -- a class can be strong and
still give a player nothing to build around -- so this reads the rules the resolver reads
and prints, per class, the cards that carry such a hook and the cards that carry nothing.

Three questions, in the order the output answers them:

* **What can the engine do that no card asks of it?** Triggers and actions the parser
  accepts and no pool card uses. Each is a build-around nobody has printed yet.
* **Per class: where are the hooks, and where is the filler?** Hooks, unusual effects,
  rule-less bodies, the keyword and tribe spread, and the effects a class repeats.
* **Across classes: what is the same card twice?** Shared statlines, shared text, name
  words, tokens nobody summons.

The pool is what a player can put in a deck: collectible, PUBLISHED, not a hero power
(CardDefinition.PoolEligible). The engine vocabulary is read out of the C# source rather
than listed here, so a new trigger or action shows up as unused the day it lands.

ADVISORY -- always exits 0. Identity is a design finding for Sam, not breakage.

    python tooling/scripts/audit_class_identity.py                # everything
    python tooling/scripts/audit_class_identity.py --class TEMPEST
    python tooling/scripts/audit_class_identity.py --markdown     # fenced, for a doc
"""
import argparse
import collections
import glob
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
CARD_DIR = ROOT / "unity" / "Assets" / "CardData"
ENUMS = ROOT / "src" / "Kingdom.Core.Contracts" / "Enums.cs"
CARD_JSON = ROOT / "src" / "Kingdom.Core.Cards" / "CardJson.cs"

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

CLASSES = ["EMBERWARD", "TIDECALLER", "VERDANT", "STONEWARDEN", "TEMPEST", "NIGHTVEIL",
           "GRAVEKEEP", "CLOCKWRIGHT", "NEUTRAL"]
TARGET = {"COMMON": 11, "RARE": 11, "EPIC": 6, "LEGENDARY": 2}  # per class; NEUTRAL is unconstrained
ONE_SHOT_TRIGGERS = ("ON_PLAY", "ON_CAST")
# A rule under any other activation keeps mattering after the card lands. ON_DEATH and
# ON_ATTACK are left out: a Toll fires once and an Attack: effect asks for no partner.
PERSISTENT = ("AURA", "ON_TURN_START", "ON_TURN_END", "ON_FRIENDLY_SUMMON", "ON_SPELL_CAST",
              "ON_DAMAGED", "ON_HEAL")
PLAIN_CONDITIONS = ("ALWAYS", "COST_PAID", "TURN_OWNER")
UNUSUAL_AT_MOST = 3  # an action type on this many pool cards or fewer counts as unusual


def load_rows():
    rows = []
    for f in sorted(glob.glob(str(CARD_DIR / "*.cards.json"))):
        rows += json.loads(pathlib.Path(f).read_text(encoding="utf-8"))
    return rows


def published(r):
    return r.get("lifecycle", "PUBLISHED") == "PUBLISHED"


def in_pool(r):
    return bool(r.get("collectible")) and published(r) and r["type"] != "HERO_POWER"


def trigger(rule):
    act = rule["activation"]
    return act.get("trigger") or act["type"]


def count_of(selection):
    """How many characters a selection reaches: '7', 'random: 3' and 'auto: 7' all end in it."""
    m = re.search(r"(\d+)\s*$", str(selection))
    return int(m.group(1)) if m else 1


def is_multi(action):
    t = action.get("target") or {}
    return bool(t.get("selector")) and count_of(t.get("selection", "1")) > 1


def hooks(rule):
    """Why this rule names a partner. Empty for a plain one-shot."""
    out = []
    action = rule["action"]
    target = action.get("target") or {}
    flt = target.get("filter") or {}
    if "tribe" in flt:
        out.append(("not " if flt.get("negateTribe") else "") + flt["tribe"].lower())
    if "keyword" in flt:
        out.append(("not " if flt.get("negateKeyword") else "") + flt["keyword"].lower())
    if flt.get("damaged"):
        out.append("damaged")
    cond = rule["condition"]
    if cond["type"] not in PLAIN_CONDITIONS:
        out.append(cond["type"].lower() + (":" + cond["tribe"].lower() if "tribe" in cond else ""))
    if "instead" in action:
        out.append(action["instead"]["condition"]["type"].lower())
    if target.get("cost"):
        out.append("pays a minion")
    if trigger(rule) in PERSISTENT:
        out.append(trigger(rule).lower())
    return out


def fmt(counter):
    return " ".join("%s=%d" % kv for kv in sorted(counter.items(), key=lambda kv: (-kv[1], kv[0]))) or "-"


def engine_vocabulary():
    """Triggers and action types the engine accepts, read from source. None if unreadable."""
    try:
        enums = ENUMS.read_text(encoding="utf-8")
        body = re.search(r"public enum Trigger\s*\{(.*?)\n    \}", enums, re.S).group(1)
        body = re.sub(r"//[^\n]*", "", body)
        triggers = re.findall(r"\b([A-Z][A-Z_]+)\b", body)
        src = CARD_JSON.read_text(encoding="utf-8")
        fn = src[src.index("RuleAction ActionFromElement"):]
        fn = fn[:fn.index("default:")]
        actions = re.findall(r'case "([A-Z_]+)":', fn)
        return triggers, actions
    except (OSError, AttributeError, ValueError):
        return None


def section(title):
    print()
    print("== " + title)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--class", dest="only", help="print one class block and skip the rest")
    ap.add_argument("--markdown", action="store_true", help="wrap the output in a fence")
    args = ap.parse_args()

    rows = load_rows()
    by_id = {r["id"]: r for r in rows}
    pool = [r for r in rows if in_pool(r)]
    minions = [r for r in pool if r["type"] == "MINION"]
    action_use = collections.Counter()
    for r in pool:
        action_use.update({rule["action"]["type"] for rule in r["rules"]})
    unusual = {a for a, n in action_use.items() if n <= UNUSUAL_AT_MOST}

    if args.markdown:
        print("```")
    print("POOL=%d cards (collectible, published, hero powers excluded)" % len(pool))

    if not args.only:
        section("WHAT THE ENGINE ACCEPTS AND NO POOL CARD USES")
        trig_use = collections.Counter(trigger(rule) for r in pool for rule in r["rules"])
        vocab = engine_vocabulary()
        if vocab is None:
            print(" could not read the Trigger enum or CardJson.cs; skipped")
        else:
            triggers, actions = vocab
            print(" triggers: %s" % (", ".join(t for t in triggers if not trig_use[t]) or "none"))
            print(" actions : %s" % (", ".join(a for a in actions if not action_use[a]) or "none"))
        print(" rules by trigger: %s  (total %d)" % (fmt(trig_use), sum(trig_use.values())))
        once = sum(trig_use[t] for t in ONE_SHOT_TRIGGERS)
        print(" fire once, on play or cast: %d of %d" % (once, sum(trig_use.values())))
        keep = [r for r in pool if any(trigger(rule) in PERSISTENT for rule in r["rules"])]
        print(" cards with a rule that keeps firing: %d of %d: %s" % (
            len(keep), len(pool),
            ", ".join("%s (%s %s)" % (r["name"], r["class"].title(), r["rarity"].lower()) for r in keep)))
        bare = [r for r in pool if not r["rules"]]
        print(" cards with no rule at all: %d of %d; epic or legendary among them: %d" % (
            len(bare), len(pool), sum(1 for r in bare if r["rarity"] in ("EPIC", "LEGENDARY"))))

    for cls in CLASSES:
        if args.only and cls != args.only.upper():
            continue
        group = [r for r in pool if r["class"] == cls]
        section("%s  n=%d" % (cls, len(group)))
        power = [r for r in rows if r["class"] == cls and r["type"] == "HERO_POWER"]
        for p in power:
            print(" hero power : %s - %s" % (p["name"], p.get("text", "")))
        rar = collections.Counter(r["rarity"] for r in group)
        if cls != "NEUTRAL":
            print(" rarity     : %s   against 11/11/6/2: %s" % (
                "/".join(str(rar[k]) for k in TARGET),
                " ".join("%s%+d" % (k[0], rar[k] - TARGET[k]) for k in TARGET)))
        else:
            print(" rarity     : %s" % "/".join(str(rar[k]) for k in TARGET))
        print(" keywords   : %s" % fmt(collections.Counter(k for r in group for k in r["keywords"])))
        print(" tribes     : %s" % fmt(collections.Counter(
            r.get("tribe", "none") for r in group if r["type"] == "MINION")))
        print(" actions    : %s" % fmt(collections.Counter(
            a for r in group for a in {rule["action"]["type"] for rule in r["rules"]})))
        hooked = []
        for r in group:
            why = []
            for rule in r["rules"]:
                for h in hooks(rule):
                    if h not in why:
                        why.append(h)
            if why:
                hooked.append("%s [%s]" % (r["name"], ", ".join(why)))
        print(" HOOKS %2d   : %s" % (len(hooked), "; ".join(hooked) or "none"))
        odd = ["%s [%s]" % (r["name"], ", ".join(sorted(
            {rule["action"]["type"].lower() for rule in r["rules"]} & {u.lower() for u in unusual})))
               for r in group if {rule["action"]["type"] for rule in r["rules"]} & unusual]
        print(" UNUSUAL %2d : %s" % (len(odd), "; ".join(odd) or "none"))
        bare = [r for r in group if not r["rules"]]
        top = [r["name"] for r in bare if r["rarity"] in ("EPIC", "LEGENDARY")]
        print(" no rule    : %d of %d; epic or legendary: %s" % (len(bare), len(group), ", ".join(top) or "-"))
        sweep = [r["name"] for r in group if any(
            rule["action"]["type"] in ("DAMAGE", "DROWN") and is_multi(rule["action"])
            and "FRIENDLY" not in rule["action"]["target"]["selector"] for rule in r["rules"])]
        print(" hits several enemies (damage or Drown): %d: %s" % (len(sweep), ", ".join(sweep) or "-"))
        pump = [r["name"] for r in group if any(
            rule["action"]["type"] == "MODIFY_STATS" and is_multi(rule["action"])
            and "FRIENDLY" in rule["action"]["target"]["selector"] for rule in r["rules"])]
        print(" buffs several friendly minions: %d: %s" % (len(pump), ", ".join(pump) or "-"))
        made = collections.defaultdict(list)
        for r in group:
            for rule in r["rules"]:
                if rule["action"]["type"] == "SUMMON":
                    token = by_id.get(rule["action"].get("cardId"))
                    made[token["name"] if token else "?"].append(r["name"])
        print(" token makers: %s" % ("; ".join(
            "%s x%d (%s)" % (t, len(v), ", ".join(v)) for t, v in sorted(made.items())) or "-"))
        shape = collections.defaultdict(list)
        for r in group:
            if r["type"] == "MINION":
                shape[(r["cost"], r["attack"], r["health"])].append(r["name"])
        for (cost, atk, hp), names in sorted(shape.items()):
            if len(names) >= 3:
                print(" same body   : %dc %d/%d x%d: %s" % (cost, atk, hp, len(names), ", ".join(names)))

    if not args.only:
        section("KEYWORD x CLASS (pool cards printed with the keyword)")
        kw = collections.defaultdict(collections.Counter)
        for r in pool:
            for k in r["keywords"]:
                kw[k][r["class"]] += 1
        for k in sorted(kw, key=lambda k: -sum(kw[k].values())):
            print(" %-13s %2d  in %d of 9: %s" % (k, sum(kw[k].values()), len(kw[k]), " ".join(
                "%s=%d" % (c[:5], kw[k][c]) for c in CLASSES if kw[k][c])))
        with_kw = sum(1 for r in minions if "BULWARK" in r["keywords"])
        print(" Bulwark minions: %d of %d pool minions" % (with_kw, len(minions)))

        section("TRIBE x CLASS (pool minions), and how many pool cards name the tribe")
        tr = collections.defaultdict(collections.Counter)
        for r in minions:
            tr[r.get("tribe", "none")][r["class"]] += 1
        named = collections.Counter()
        for r in pool:
            seen = set()
            for rule in r["rules"]:
                t = ((rule["action"].get("target") or {}).get("filter") or {}).get("tribe") \
                    or rule["condition"].get("tribe")
                if t:
                    seen.add(t)
            named.update(seen)
        for t in sorted(tr, key=lambda t: -sum(tr[t].values())):
            print(" %-10s %2d  named by %d: %s" % (t, sum(tr[t].values()), named[t], " ".join(
                "%s=%d" % (c[:5], tr[t][c]) for c in CLASSES if tr[t][c])))

        section("THE SAME CARD TWICE")
        shape = collections.defaultdict(list)
        for r in minions:
            if not r["rules"]:
                shape[(r["cost"], r["attack"], r["health"], tuple(sorted(r["keywords"])))].append(
                    "%s (%s)" % (r["name"], r["class"].title()))
        total = 0
        for (cost, atk, hp, kws), names in sorted(shape.items()):
            if len(names) > 1:
                total += len(names)
                print(" %dc %d/%d %-13s %s" % (cost, atk, hp, ",".join(kws).lower() or "vanilla", "; ".join(names)))
        print(" rule-less minions sharing cost, stats and keywords with another: %d" % total)
        text = collections.defaultdict(list)
        for r in pool:
            if r["rules"]:
                text[(r["type"] == "SPELL", r.get("text", ""))].append(
                    "%s (%s %dc)" % (r["name"], r["class"].title(), r["cost"]))
        for (spell, words), names in sorted(text.items()):
            if len(names) > 1 and len({n.split("(")[1].split()[0] for n in names}) > 1:
                print(' %s "%s": %s' % ("spell " if spell else "minion", words, "; ".join(names)))

        section("NAME WORDS ON FOUR OR MORE ROWS (tokens and drafts included)")
        words = collections.Counter()
        for r in rows:
            words.update({w for w in re.findall(r"[A-Za-z']+", r["name"]) if len(w) > 3})
        print(" " + ", ".join("%s=%d" % kv for kv in words.most_common() if kv[1] >= 4))

        section("TOKENS AND WHO MAKES THEM; LOOSE ENDS")
        makers = collections.defaultdict(list)
        for r in rows:
            for rule in r["rules"]:
                cid = rule["action"].get("cardId")
                if cid:
                    makers[cid].append(r["name"] + ("" if published(r) else " [draft]"))
        for r in rows:
            if not r["collectible"] and r["type"] != "HERO_POWER":
                print(" %-19s %-11s %-9s <- %s" % (
                    r["name"], r["class"].title(), r.get("lifecycle", "PUBLISHED").lower(),
                    ", ".join(makers.get(r["id"], [])) or "no card"))
        loose = ["%s (%d)" % (r["name"], r["id"]) for r in minions if not r.get("tribe")]
        print(" pool minions with no tribe: %s" % (", ".join(loose) or "none"))

    print()
    print("CLASS_IDENTITY_AUDIT=DONE  POOL=%d" % len(pool))
    if args.markdown:
        print("```")
    return 0


if __name__ == "__main__":
    sys.exit(main())
