# -*- coding: utf-8 -*-
"""What master loses when the effect-model fork's card data lands on top of it.

    python tooling/scripts/reconcile_card_data.py <fork-repo-path> > docs/dev-docs/card-data-reconciliation.md

Sam's plan (2026-08-23) is to overwrite both card files wholesale with the fork's,
because the fork's rows have ALREADY been through the schema transform, and then
re-apply master's balance work on top. This script produces the list of what
"re-apply on top" means, so the answer comes off a command rather than a memory.

MATCHING IS THE WHOLE PROBLEM. No single key works:

  * id stem   -- the class token differs on BOTH axes, because the fork predates
                 master's <classToken>_<name> rename (7a7dc1b2):
                 master ward_aldric  <->  fork rekind_stone_aldric
  * name      -- master renamed cards outright: 'General Kira, Flame Reunified'
                 became 'General Kira'.

So: alias first, then id stem, then display name. An alias is an OPERATOR RULING,
not a guess -- 'Tide Harbinger' and 'Prophet of the Flood' share no id, no stem
and no word, and only Sam knew they are the same card. Add a row when he says so,
and cite him in the comment.

WHAT THIS SCRIPT DELIBERATELY DOES NOT REPORT: the id churn. The overwrite reverts
every id to the fork's older spelling, and that would normally be a blocking step,
because renaming a card id breaks saved decks with no error. It is not one here --
Sam is replacing string ids with numeric ones immediately after this merge
(2026-08-23), so the strings stop being identity at all. Do not add an id column
back without checking whether that landed.

DELETE THIS SCRIPT WITH THE WORKSHEET once the merge is applied. It answers one
question about one moment and gets less true every day after it.
"""
import json
import sys
import io
import os

# fork id -> master id, for pairs that stem and name matching cannot reach.
ALIASES = {
    # Sam, 2026-08-23: "That's the same card as Harbinger of the Flood."
    # Renamed AND rebalanced on master: cost 1 -> 4, 1/2 -> 4/2.
    "rekind_tide_harbinger": "tide_prophet_of_the_flood",
}

CLASS_TOKENS = {"clock", "ember", "grave", "neutral", "night", "stone", "tempest",
                "tide", "verdant", "storm", "ward", "token", "core", "relic",
                "frost", "wave"}

# Plural selectors the fork collapsed into a singular selector plus an explicit
# `selection` count off ZoneCaps. Normalised away before comparing rules, so a
# difference this script reports is a real one and not transform noise.
PLURAL = {"ENEMY_MINIONS": "ANY_ENEMY_MINION", "FRIENDLY_MINIONS": "ANY_FRIENDLY_MINION",
          "ENEMY_CHARACTERS": "ANY_ENEMY", "ALL_MINIONS": "ANY_MINION"}

SCALARS = ("cost", "attack", "health", "rarity", "class", "type", "collectible", "setId")


def rows(path):
    data = json.load(io.open(path, encoding="utf-8"))
    return data["cards"] if isinstance(data, dict) and "cards" in data else data


def stem(card_id):
    parts = card_id.split("_")
    while parts and (parts[0] in CLASS_TOKENS or parts[0] == "rekind"):
        parts = parts[1:]
    return "_".join(parts)


def signature(card):
    """Rule fingerprint with the known schema transforms normalised away."""
    out = []
    for rule in card.get("rules", []):
        action = dict(rule.get("action", {}))
        target = dict(action.pop("target", {}) or {})
        kind = action.pop("type", "?")
        if kind == "REINFORCE":                      # the fork's uniform rewrite
            kind = "MODIFY_STATS"
            action["attack"] = 1
            action["health"] = 1
        selector = PLURAL.get(target.get("selector"), target.get("selector"))
        scalars = tuple(sorted((k, v) for k, v in action.items()
                               if k in ("amount", "attack", "health", "keyword")))
        out.append((kind, scalars, selector,
                    rule.get("activation", {}).get("trigger"),
                    rule.get("lifetime", {}).get("type")))
    return out


def describe(card):
    if not card.get("rules"):
        return "(vanilla — no rules)"
    parts = []
    for rule in card["rules"]:
        action = rule.get("action", {})
        target = action.get("target") or {}
        extras = "".join(" %s=%s" % (k, action[k])
                         for k in ("amount", "attack", "health", "keyword") if k in action)
        parts.append("%s %s -> %s%s" % (rule.get("activation", {}).get("trigger"),
                                        action.get("type"), target.get("selector"), extras))
    return " | ".join(parts)


def pair_up(master_rows, fork_rows):
    """Alias, then id stem, then display name. Returns (pairs, master_only, fork_only)."""
    remaining_master = {r["id"]: r for r in master_rows}
    pairs = []
    remaining_fork = {}

    for row in fork_rows:
        alias = ALIASES.get(row["id"])
        if alias and alias in remaining_master:
            pairs.append((remaining_master.pop(alias), row))
        else:
            remaining_fork[row["id"]] = row

    by_stem = {}
    for row in remaining_master.values():
        by_stem.setdefault(stem(row["id"]), row)
    for fork_id in list(remaining_fork):
        match = by_stem.get(stem(fork_id))
        if match is not None and match["id"] in remaining_master:
            pairs.append((remaining_master.pop(match["id"]), remaining_fork.pop(fork_id)))

    by_name = {row["name"]: row for row in remaining_master.values()}
    for fork_id in list(remaining_fork):
        match = by_name.get(remaining_fork[fork_id]["name"])
        if match is not None and match["id"] in remaining_master:
            pairs.append((remaining_master.pop(match["id"]), remaining_fork.pop(fork_id)))

    by_display = lambda row: row["name"]
    return (pairs,
            sorted(remaining_master.values(), key=by_display),
            sorted(remaining_fork.values(), key=by_display))


def stat_line(row):
    return "**%s** `%s` — %s %s, cost %s, %s/%s, %s" % (
        row["name"], row["id"], row["type"], row.get("class"), row.get("cost"),
        row.get("attack"), row.get("health"), row.get("rarity"))


def main():
    if len(sys.argv) < 2:
        sys.exit("usage: reconcile_card_data.py <fork-repo-path>")
    fork_root = sys.argv[1]
    repo = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
    out = io.open(sys.stdout.fileno(), "w", encoding="utf-8", newline="\n", closefd=False)

    def w(*args):
        out.write(" ".join(str(a) for a in args) + "\n")

    w("# Card-data reconciliation — what MASTER loses when the fork's card data lands\n")
    w("**Generated by `tooling/scripts/reconcile_card_data.py`.** Re-run it as rulings land — do not")
    w("hand-edit the tables.\n")
    w("**The plan (Sam, 2026-08-23):** overwrite `Emberfall.cards.json` and `Rekindling.cards.json`")
    w("wholesale with the fork's. Its rows have already been through the schema transform — plural")
    w("selectors collapsed to a singular selector plus an explicit `selection` count off `ZoneCaps`,")
    w("`REINFORCE` rewritten as `MODIFY_STATS +1/+1`, `chosenBy`/`count`/`required` replaced by")
    w("`selection` and `cost`, and an authored `text` field carrying the player-facing sentence.")
    w("Re-deriving that here would be inventing it twice. Then re-apply, from this page, everything")
    w("the overwrite destroys.\n")
    w("**A worksheet with a short life. Delete it, and the script, once applied.**\n")
    w("**The id churn is deliberately not tracked here.** The overwrite reverts every card id to the")
    w("fork's older spelling, which would normally block — renaming a card id breaks saved decks with")
    w("no error. It does not block here: Sam is replacing string ids with numeric ones immediately")
    w("after this merge, so the strings stop being identity. Check that landed before trusting this")
    w("paragraph.\n")
    w("---\n")

    for card_set in ("Emberfall", "Rekindling"):
        relative = "unity/Assets/CardData/%s.cards.json" % card_set
        mine = rows(os.path.join(repo, relative))
        theirs = rows(os.path.join(fork_root, relative))
        pairs, only_master, only_fork = pair_up(mine, theirs)

        w("## %s — master %d, fork %d, matched %d\n"
          % (card_set, len(mine), len(theirs), len(pairs)))

        w("### Cards only on MASTER — re-author into the new schema by hand  (%d)\n" % len(only_master))
        if not only_master:
            w("_none_\n")
        for row in only_master:
            w("- " + stat_line(row))
            w("  - `%s`" % describe(row))

        w("\n### Cards only on the FORK — decide whether they arrive  (%d)\n" % len(only_fork))
        if not only_fork:
            w("_none_\n")
        for row in only_fork:
            w("- " + stat_line(row))
            w("  - `%s`" % describe(row))

        renames = sorted((f["name"], m["name"]) for m, f in pairs if m["name"] != f["name"])
        w("\n### Display-name renames to re-apply  (%d)\n" % len(renames))
        if not renames:
            w("_none_\n")
        for old, new in renames:
            w("- `%s`  →  **`%s`**" % (old, new))

        diffs = sorted((m["name"], k, f.get(k), m.get(k))
                       for m, f in pairs for k in SCALARS if m.get(k) != f.get(k))
        w("\n### Stat / meta values to re-apply  (%d)\n" % len(diffs))
        w("| card | field | fork (incoming) | MASTER (restore) |")
        w("| --- | --- | --- | --- |")
        for name, field, incoming, restore in diffs:
            w("| %s | `%s` | %s | **%s** |" % (name, field, incoming, restore))

        differing = [(m, f) for m, f in pairs if signature(m) != signature(f)]
        w("\n### Rule-level differences — decide one per card  (%d)\n" % len(differing))
        w("Compared AFTER normalising the known schema transforms, so these are real divergences.\n")
        if not differing:
            w("_none_\n")
        for master_row, fork_row in differing:
            w("**%s**\n" % master_row["name"])
            w("- MASTER: `%s`" % describe(master_row))
            w("- fork:   `%s`" % describe(fork_row))
            if fork_row.get("text"):
                w("- fork text: `%s`" % fork_row["text"])
            w("")
        w("---\n")
    out.flush()


if __name__ == "__main__":
    main()
