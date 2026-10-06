"""Per-card numbers for one class from a matrix run's telemetry. Read-only.

    python card_report.py <telemetry.jsonl> <CLASS> [<card-data-dir>]

For every card the class had in a deck: how often it was drawn, how often a drawn copy was played,
the turn it was first played, and the class's win rate in the games where it was drawn against the
class's win rate overall. Mirror matches are left out of the win rates: both seats are the class,
so they are 50% by construction.
"""
import collections
import glob
import json
import os
import sys

telemetry, klass = sys.argv[1], sys.argv[2]
card_dir = sys.argv[3] if len(sys.argv) > 3 else r"C:/Users/User/Documents/tcg-hearthlike/unity/Assets/CardData"

cards = {}
for path in glob.glob(os.path.join(card_dir, "*.cards.json")):
    for row in json.load(open(path, encoding="utf-8")):
        cards[row["id"]] = row

mirror = set()
games = wins = 0
seen = collections.defaultdict(lambda: collections.Counter())
turns = collections.defaultdict(list)
rows = []
with open(telemetry, encoding="utf-8") as handle:
    for line in handle:
        row = json.loads(line)
        if row["type"] == "match_complete":
            classes = [p["classId"] for p in row["players"]]
            if classes[0] == classes[1]:
                mirror.add(row["matchId"])
                continue
            for player in row["players"]:
                if player["classId"] == klass:
                    games += 1
                    wins += player["result"] == "win"
        elif row["type"] == "card_seen" and row["classId"] == klass:
            rows.append(row)

for row in rows:
    if row["matchId"] in mirror or not row.get("inDeck"):
        continue
    stat = seen[row["cardDefId"]]
    stat["in_deck"] += 1
    if row.get("drawnThisMatch"):
        stat["drawn"] += 1
        stat["drawn_won"] += bool(row.get("matchWon"))
        if row.get("firstPlayedTurn") is not None:
            stat["played"] += 1
            turns[row["cardDefId"]].append(row["firstPlayedTurn"])

base = wins / games if games else 0.0
print(f"{klass}: {wins}/{games} = {base:.1%} outside mirrors  ({os.path.dirname(telemetry)})")
print(f"{'id':>5} {'card':<28} {'cost':>4} {'body':>5} {'drawn':>6} {'played':>7} {'turn':>5} {'win drawn':>10} {'vs class':>9}")
for card_id, stat in sorted(seen.items(), key=lambda kv: kv[1]["drawn_won"] / max(1, kv[1]["drawn"])):
    card = cards.get(card_id, {})
    body = f"{card.get('attack')}/{card.get('health')}" if card.get("type") == "MINION" else card.get("type", "?")[:5]
    drawn = stat["drawn"]
    rate = stat["drawn_won"] / drawn if drawn else 0.0
    played = stat["played"] / drawn if drawn else 0.0
    turn = sum(turns[card_id]) / len(turns[card_id]) if turns[card_id] else 0.0
    print(f"{card_id:>5} {card.get('name', '?')[:28]:<28} {card.get('cost', '?'):>4} {body:>5} {drawn:>6} "
          f"{played:>7.0%} {turn:>5.1f} {rate:>10.1%} {rate - base:>+9.1%}")
