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

tooling/sfx/sound-library.md holds the prompt and the search term for each stem.
"""
import json,glob,collections,re,sys
rows=[]
for f in glob.glob('unity/Emberfall/Assets/CardData/*.cards.json'):
    d=json.load(open(f,encoding='utf-8'))
    rows += d if isinstance(d,list) else d.get('cards',d)
mins=[r for r in rows if r.get('type')=='MINION']

# Ordered, first match wins. CREATURE KIND is ranked above ROLE, because the art shows a
# body, not a job title: "Charnel Priest" is a skeleton in robes, so it rattles like bone
# rather than intoning like a caster.
RULES=[
 ("imp_summon",            r"\bimp\b|imling|emberling pup"),
 ("beast_summon",          r"stag|\bcub\b|hawk|\bbat\b|wolf|hound|crawler|hoof|lifeclaw|ravenous|bog|matron|thornfang"),
 ("titan_summon",          r"colossus|titan|behemoth|leviathan|monolith|great mechanism|world-root|drowned"),
 ("drake_summon",          r"drake|wyrm|whelp|blazewing|venomscale|icescale"),
 ("serpent_summon",        r"viper|\basp\b|adder|venomfin|venomthorn|razorcoil|ink "),
 ("wisp_summon",           r"wisp|sprite|spectral|phantom|moth|shriek|hollow acolyte|memorial|lantern-bearer"),
 ("clockwork_summon",      r"clockwork|automaton|construct|gear|cog|spring|drone|mechanism|artificer|voltaic|modular|blueprint|bronze frame|overclock|rebuilt|masterwork|assembly|mechanical|salvaged golem|nameless bell"),
 ("undead_summon",         r"revenant|risen|bone|skelet|corpse|charnel|tomb|grave|deathweave|plague|ashen|harrower|gatewatch|sable|tollkeeper|bell novice|soulbound|chained"),
 ("treant_summon",         r"root|bark|bramble|thorn|moss|canopy|grove|sapling|seed|dryad|overgrowth|thistleguard|windroot|praxis|ancient|elder"),
 ("stone_summon",          r"stone|granite|boulder|rock|pebble|quarry|fortress|fortified|unbreakable|wall|siege|duneborn|heartstone|cinderplate|slagshell|golem|ironclad|ironmount"),
 ("magma_summon",          r"magma|forge|kiln|pyre|brazier|ember|cinder|coal|blaze|slag|overheat|scald|zealot"),
 ("frost_summon",          r"frost|\bice|glacial|icecrown"),
 ("tide_summon",           r"tide|reef|wave|abyss|saltshore|flood|siren|maren|shell|prophet"),
 ("storm_summon",          r"storm|bolt|spark|static|thunder|crackl|gale|gust|wind|tempest|chain caller|arclight"),
 ("rogue_summon",          r"shadow|veil|spy|rogue|stalker|agent|recon|operative|scout|lurk|nightfall|whisper|dusk|kira|seraphine"),
 ("soldier_summon",        r".",),   # catch-all: an armoured humanoid is the default body
]
assign={};counts=collections.Counter();
for r in mins:
    n=(r.get('name','')+" "+r.get('handle','')).lower()
    for stem,pat in RULES:
        if re.search(pat,n):
            assign[r['id']]=stem;counts[stem]+=1;break
print("MINIONS %d  archetypes %d  total assigned %d" % (len(mins),len([s for s,_ in RULES if counts[s]]),sum(counts.values())))
print()
for stem,_ in RULES:
    ex=[r.get('name') for r in mins if assign.get(r['id'])==stem]
    print("  %-20s %3d   %s" % (stem,counts[stem],", ".join(ex[:5])+(" ..." if len(ex)>5 else "")))
json.dump({str(k):v for k,v in assign.items()}, open(sys.argv[1],'w'), indent=1)
