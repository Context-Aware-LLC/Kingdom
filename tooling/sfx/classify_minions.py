#!/usr/bin/env python3
"""Assign every MINION row to a summon-sound archetype, and print the tally.

The archetype is what the card ART shows -- a body, not a job title -- because that is
what a player expects to hear. So creature kind is ranked ABOVE role: "Charnel Priest"
is a skeleton in robes and rattles like bone rather than intoning like a caster, and
"Thornfang Matron" is a beast rather than a serpent.

Rules are ordered and the first match wins, so a NEW RULE MUST BE PLACED BY HAND rather
than appended -- appended below the generic rules it would match nothing. The final rule
is a deliberate catch-all: an armoured humanoid is the default body.

This is a script and not a committed table because a table of 190 assignments drifts the
moment a card is added or renamed. Re-run it rather than trusting a copy.

    python tooling/sfx/classify_minions.py [out.json]

THE RULES ARE NOT IN THIS FILE ANY MORE.

They live in the game, in unity/.../Presentation/MinionSummonArchetypes.cs, and are parsed out
of it here -- so this tally cannot disagree with what a player actually hears. A stem that reads
back EMPTY is an archetype with no clip (soldier, titan, treant as of 2026-08-28); it is counted
and listed as "(silent)" rather than dropped, because "no card matched" and "matched an archetype
nobody recorded" are different problems and only one of them is a rules bug.

tooling/sfx/sound-library.md holds the prompt and the search term for each stem.
"""
import json,glob,collections,re,sys
rows=[]
for f in glob.glob('unity/Assets/CardData/*.cards.json'):
    d=json.load(open(f,encoding='utf-8'))
    rows += d if isinstance(d,list) else d.get('cards',d)
mins=[r for r in rows if r.get('type')=='MINION']

CS = 'unity/Assets/Scripts/Presentation/MinionSummonArchetypes.cs'
# ("stem", @"pattern"),  -- one rule per line, in order. A format change in that file shows up
# here as an empty rule list, which is loud, rather than as a quietly different tally.
RULES = [(m.group(1), m.group(2)) for m in
         re.finditer(r'\(\s*"([a-z_]*)"\s*,\s*@"(.*?)"\s*\)\s*,',
                     open(CS, encoding='utf-8').read())]
if not RULES:
    sys.exit("no rules parsed from %s -- has the table format changed?" % CS)

assign={};rule_of={};counts=collections.Counter();
for r in mins:
    n=(r.get('name','')+" "+r.get('handle','')).lower()
    for i,(stem,pat) in enumerate(RULES):
        if re.search(pat,n):
            # Counted by RULE INDEX, not by stem: three rules resolve to the empty stem and
            # keying on it merges them into one bucket of 34 printed three times, which reads
            # as a bug in the tally rather than as three separate silent archetypes.
            assign[r['id']]=stem;rule_of[r['id']]=i;counts[i]+=1;break
print("MINIONS %d  archetypes %d  total assigned %d  audible %d" % (
    len(mins),len([i for i,_ in enumerate(RULES) if counts[i]]),sum(counts.values()),
    sum(c for i,c in counts.items() if RULES[i][0])))
print()
for i,(stem,pat) in enumerate(RULES):
    ex=[r.get('name') for r in mins if rule_of.get(r['id'])==i]
    label=stem or ("(silent: %s)" % pat.split("|")[0].strip("\b."))
    print("  %-24s %3d   %s" % (label,counts[i],", ".join(ex[:5])+(" ..." if len(ex)>5 else "")))
json.dump({str(k):v for k,v in assign.items()}, open(sys.argv[1],'w'), indent=1)
