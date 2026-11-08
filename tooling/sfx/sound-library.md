# Card SFX authoring sheet — shared library v1

Generate these in ElevenLabs Studio, save with the **exact stem** in the first column,
drop the file in `unity/Assets/Sound/SFX/`. Addressables registers that folder
automatically as `sfx/<stem>`.

**Filename rule (validator C30):** the stem must match `^[a-z0-9][a-z0-9_-]*$` — lowercase,
no spaces, no punctuation beyond `_` and `-`. Studio will offer you `The Great Mechanism.mp3`
by default; rename before saving or the card row is rejected.

**Clips are named for the sound, not the card.** Sharing happens by reference: two cards
pointing at the same `soundId` is the normal case, not a shortcut.

Suggested settings unless noted: **duration 1.5–2.5s, prompt_influence 0.4, loop off.**
Keep tails short — these fire mid-turn and overlap each other.

**Take the stock clip where one fits.** The ElevenLabs library already carries usable
fire, water and impact material; generating is for what it does not have. Either way the
saved stem is what matters — the game resolves `sfx/<stem>`, and nothing downstream knows
or cares whether a clip was generated or picked.

---

## PHASE 1 — the 10 clips to do first

Per Sam, 2026-08-27: the nine class casts plus the hero power. They cover 89 cards, they
are the sounds that tell a player which class just acted, and they need no art judgement.

`emberward_spell_cast` · `tidecaller_spell_cast` · `verdant_spell_cast` · `gravekeep_spell_cast` ·
`nightveil_spell_cast` · `tempest_spell_cast` · `clockwright_spell_cast` · `stonewarden_spell_cast` ·
`neutral_spell_cast` · `hero_power_activate`

Everything below is phase 2.

---

## Spell cast — one per class (`ON_CAST`, 81 spells)

Highest value set in the sheet. A class-flavoured cast sound tells the player what hit them
before the card text resolves.

**The class token is the `CardClass` enum member, lowercased — nothing shorter.** So
`stonewarden_spell_cast`, not `warden_`; `tempest_`, not `storm_`. `CardSoundCueRouter`
derives the stem with `Class.ToString().ToLowerInvariant()` and holds no name map, which is
the same rule `HeroVoiceDirector` uses for `vo_<class>_<moment>_<n>`. One vocabulary across
both sound systems: a second one buys nothing and costs a silent miss, because an unresolved
address is a console warning at most and the call site looks right.

| stem | class | prompt |
|---|---|---|
| `emberward_spell_cast` | EMBER (9) | A sharp gout of flame erupting: fast air intake, then a roaring whoosh of fire with crackling embers, tail dying quickly. |
| `tidecaller_spell_cast` | TIDE (11) | A surge of water conjured and released: rushing liquid, a heavy splash, droplets scattering, no reverb tail. |
| `verdant_spell_cast` | VERDANT (9) | Rapid plant growth: creaking wood, vines uncoiling and whipping through leaves, a soft earthy settle. |
| `gravekeep_spell_cast` | GRAVE (8) | Necrotic magic released: a low hollow moan, dry bone rattling, a cold dusty exhale. |
| `nightveil_spell_cast` | NIGHT (10) | Shadow magic: a muffled inward suction of air, a dark whispering swell, ending on a soft dead thud. |
| `tempest_spell_cast` | STORM (13) | A close lightning strike: crackling electrical build-up then a sharp cracking discharge with a short thunder snap. |
| `clockwright_spell_cast` | CLOCK (9) | Clockwork mechanism firing: gears ratcheting fast, a spring releasing, a small brass chime. |
| `stonewarden_spell_cast` | WARDEN (9) | Consecrated stone magic: a deep resonant bell tone with grinding granite underneath and a bright shimmer on top. |
| `neutral_spell_cast` | NEUTRAL / CORE (2) | Generic arcane cast: a rising shimmering swell of magical energy releasing into a soft bright pop. |

## Minion summons — by what the art shows (`ON_PLAY`, 190 minions)

Not light/heavy. The archetype is the **body in the picture**, because that is what a
player expects to hear: creature kind outranks job title, so `Charnel Priest` is a
skeleton in robes and rattles, and `Thornfang Matron` is a beast, not a serpent.

Counts are from `python tooling/sfx/classify_minions.py`, which assigns all 190 and
prints the tally. Re-run it rather than trusting these numbers — it is the authority,
this table is a copy.

**Search the ElevenLabs library first.** Where a stock clip fits, take it; generate only
what the library does not already have. The `search` column is what to type there.

| stem | n | search | prompt if generating |
|---|---|---|---|
| `undead_summon` | 22 | bones rattle | Dry bones clattering together as a corpse rises: a hollow rattling shift, a low grinding groan, dust falling. |
| `clockwork_summon` | 20 | gears mechanism | A brass mechanism engaging: gear teeth meshing, an escapement ticking up to speed, a small pressurised hiss. |
| `stone_summon` | 20 | boulder grinding | Heavy stone waking: granite slabs grinding against each other, then a deep settling thud that shakes dust loose. |
| `wisp_summon` | 15 | ethereal shimmer | A spirit forming from nothing: a soft rising ethereal shimmer with a faint whispering breath, weightless. |
| `treant_summon` | 14 | wood creaking | Living wood moving: thick timber creaking and splitting, roots tearing free of soil, leaves rustling. |
| `magma_summon` | 14 | forge fire whoosh | Something igniting into being: a bellows-driven whoosh of forge fire with molten crackle and settling embers. |
| `tide_summon` | 12 | water surge | A body surfacing from deep water: a heavy liquid surge, streaming runoff, a wet slap of settling. |
| `rogue_summon` | 12 | cloth blade unsheathe | An assassin stepping out of shadow: a soft cloth whisper, a quiet foot placement, a short blade half-drawn. |
| `beast_summon` | 11 | animal growl paws | A four-legged animal landing and squaring up: paws thudding on dirt, a low warning growl, a snort of breath. |
| `titan_summon` | 11 | earthquake impact | Something enormous arriving: a ground-shaking impact, a long low rumble, debris raining down afterwards. |
| `storm_summon` | 10 | electricity crackle | Charged air discharging into a shape: crackling static building to a sharp electrical snap, ozone fizz. |
| `soldier_summon` | 9 | — | **REJECTED, 2026-08-28** — see below. |
| `drake_summon` | 7 | dragon wings screech | A winged reptile landing: two heavy leathery wingbeats, claws scraping stone, a short rasping screech. |
| `serpent_summon` | 6 | snake hiss scales | A serpent uncoiling: dry scales sliding over stone, then a sharp sustained hiss. |
| `imp_summon` | 5 | small creature squeak | A small mischievous creature popping into existence: a quick puff of air, a high chittering squeak, tiny scampering feet. |
| `frost_summon` | 2 | ice cracking | Ice forming into a shape: sharp crystalline cracking, a cold brittle groan, small shards tinkling down. |

**A body that TALKS does not get a foley clip (Sam, 2026-08-28).** All four `soldier_summon`
generations were rejected on the same grounds: *"they're human beings, and other things that talk
might get voices instead at some point."* A clank of armour is what a soldier's KIT sounds like, not
what a soldier sounds like, and once a person on the board has a voice the foley is the thing that
will read as wrong. So the human archetypes are parked rather than regenerated — a better armour
clip does not answer the objection. Whether that reaches `rogue_summon` (12) as well is undecided;
Sam named the soldiers only.

This is a bet on unbuilt work, and cheap to unwind: nine cards are silent on summon until either a
voice line or a reinstated clip lands, and silence there costs nothing while the rest of the sheet
is being auditioned.

`frost_summon` covers only two cards (`Frostwhisper`, `Icecrown Herald`) — the other icy
minions read as drakes or tide. It is the one bucket that may not earn a bespoke clip;
folding it into `tide_summon` costs little.

## Minion attacks

| stem | trigger | prompt |
|---|---|---|
| `attack_claw` | `ON_ATTACK` | A fast animal strike: a whipping swipe through air ending in a wet tearing impact on flesh. |
| `attack_blade_light` | `ON_ATTACK` | A short dry melee hit: a light blade striking leather and bone, close-miked, no reverb tail. |
| `attack_blade_heavy` | `ON_ATTACK` | A heavy two-handed weapon landing: a slow whoosh then a crushing metallic impact with a low reverberating clang. |
| `death_small` | `ON_DEATH` | A small creature dying: a short choked cry cut off, a light collapse onto dirt. |
| `death_large` | `ON_DEATH` | A large body falling: a deep pained groan, heavy armour crashing to stone, dust settling. |

## Universal reactions

| stem | trigger | prompt |
|---|---|---|
| `damage_impact` | `ON_DAMAGED` | A blunt hit landing on a body: a dull percussive thud with a short low-frequency punch, no tail. |
| `heal_restore` | `ON_HEAL` | Healing magic: a warm rising shimmer with a soft bell-like bloom, gentle and brief. |

## Weapons (14 cards)

| stem | trigger | prompt |
|---|---|---|
| `weapon_equip` | `ON_PLAY` | A weapon being drawn and readied: steel sliding from a leather sheath, then a firm grip creak. |
| `weapon_blunt_attack` | `ON_ATTACK` | A heavy blunt melee hit: a dull deep thud with a meaty low-end impact, no metal ring. Mauls, hammers, and an unarmed hero. |
| `weapon_blade_attack` | `ON_ATTACK` | Two steel swords clashing: a bright ringing clang, then blade grinding along blade. Swords, cleavers, polearms; the default. |
| `weapon_swift_attack` | `ON_ATTACK` | A stealthy dagger swipe: a very fast, thin, sharp whoosh ending in a soft clean cut. Knives, spears, whips, NIGHTVEIL's blades. |

Weapon type is read off the card name by `CardSoundCueRouter.WeaponAttackStem` (2026-11-07);
there is no type field.

## Hero powers (8 cards)

| stem | trigger | prompt |
|---|---|---|
| `hero_power_activate` | `ON_CAST` | A hero ability activating: a short confident magical surge with a bright metallic accent, decisive and clean. |
| `nightveil_hero_power` | `ON_CAST` | Backstab. Reuses the `weapon_swift_attack` takes (2026-11-07). |

## Turn boundaries

Wire these on **one** card each at most, or every card carrying the row fires simultaneously.
See the warning below.

| stem | trigger | prompt |
|---|---|---|
| `turn_start_chime` | `ON_TURN_START` | A clean single UI chime marking a turn beginning: bright, short, bell-like, no reverb. |
| `turn_end_chime` | `ON_TURN_END` | A soft low UI tone marking a turn ending: brief, muted, settling downward. |

---

## Wiring, and the thing that will bite

**The router falls back to the shared library for the card that acted.**
`CardSoundCueRouter.DefaultSoundId` supplies a stem when a card has no `soundEffects[]` row for
the trigger; an authored row overrides it. A stem gets a default only once its clip exists, so a
stem in this file with no case there is silent.

**`ON_TURN_START` / `ON_TURN_END` fire per observing carrier, not once per turn** —
`AppendTurnObservers` walks every carrier on both sides. A turn chime authored onto many cards
plays once per card. Wire it on nothing, or on exactly one permanent.

**`ON_HEAL` means "this minion was healed" and never fires on a hero**, and nine VERDANT heal
cards target `FRIENDLY_HERO` — so `heal_restore` will be silent on most of the class's healing
until the VERDANT pass lands. Do not judge the clip by those cards.

## Budget

23 clips at ~2s is ~46s of audio. ElevenLabs bills 40 credits/second, so ~1,840 credits —
roughly a tenth of a 190-effect allowance. Bespoke legendary sounds come out of the rest.
