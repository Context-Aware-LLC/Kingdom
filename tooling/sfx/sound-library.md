# Card SFX authoring sheet — shared library v1

Generate these in ElevenLabs Studio, save with the **exact stem** in the first column,
drop the file in `unity/Emberfall/Assets/Sound/SFX/`. Addressables registers that folder
automatically as `sfx/<stem>`.

**Filename rule (validator C30):** the stem must match `^[a-z0-9][a-z0-9_-]*$` — lowercase,
no spaces, no punctuation beyond `_` and `-`. Studio will offer you `The Great Mechanism.mp3`
by default; rename before saving or the card row is rejected.

**Clips are named for the sound, not the card.** Sharing happens by reference: two cards
pointing at the same `soundId` is the normal case, not a shortcut.

Suggested settings unless noted: **duration 1.5–2.5s, prompt_influence 0.4, loop off.**
Keep tails short — these fire mid-turn and overlap each other.

---

## Spell cast — one per class (`ON_CAST`, 81 spells)

Highest value set in the sheet. A class-flavoured cast sound tells the player what hit them
before the card text resolves.

| stem | class | prompt |
|---|---|---|
| `ember_spell_cast` | EMBER (9) | A sharp gout of flame erupting: fast air intake, then a roaring whoosh of fire with crackling embers, tail dying quickly. |
| `tide_spell_cast` | TIDE (11) | A surge of water conjured and released: rushing liquid, a heavy splash, droplets scattering, no reverb tail. |
| `verdant_spell_cast` | VERDANT (9) | Rapid plant growth: creaking wood, vines uncoiling and whipping through leaves, a soft earthy settle. |
| `grave_spell_cast` | GRAVE (8) | Necrotic magic released: a low hollow moan, dry bone rattling, a cold dusty exhale. |
| `night_spell_cast` | NIGHT (10) | Shadow magic: a muffled inward suction of air, a dark whispering swell, ending on a soft dead thud. |
| `storm_spell_cast` | STORM (13) | A close lightning strike: crackling electrical build-up then a sharp cracking discharge with a short thunder snap. |
| `clock_spell_cast` | CLOCK (9) | Clockwork mechanism firing: gears ratcheting fast, a spring releasing, a small brass chime. |
| `warden_spell_cast` | WARDEN (9) | Consecrated stone magic: a deep resonant bell tone with grinding granite underneath and a bright shimmer on top. |
| `neutral_spell_cast` | NEUTRAL / CORE (2) | Generic arcane cast: a rising shimmering swell of magical energy releasing into a soft bright pop. |

## Minions

| stem | trigger | prompt |
|---|---|---|
| `minion_summon_light` | `ON_PLAY` | A small creature appearing on a battlefield: a light rush of displaced air and a quick soft thud of feet on packed dirt. |
| `minion_summon_heavy` | `ON_PLAY` | Something large arriving: a deep ground-shaking stomp, armour plates settling, a low guttural breath. |
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
| `weapon_swing` | `ON_ATTACK` | A weapon swung hard through air: a sharp fast whoosh ending in a solid metallic strike. |

## Hero powers (8 cards)

| stem | trigger | prompt |
|---|---|---|
| `hero_power_activate` | `ON_CAST` | A hero ability activating: a short confident magical surge with a bright metallic accent, decisive and clean. |

## Turn boundaries

Wire these on **one** card each at most, or every card carrying the row fires simultaneously.
See the warning below.

| stem | trigger | prompt |
|---|---|---|
| `turn_start_chime` | `ON_TURN_START` | A clean single UI chime marking a turn beginning: bright, short, bell-like, no reverb. |
| `turn_end_chime` | `ON_TURN_END` | A soft low UI tone marking a turn ending: brief, muted, settling downward. |

---

## Wiring, and the thing that will bite

**There is no fallback.** `CardSoundCueRouter.Append` emits only the `soundEffects[]` rows
present on a card definition. A card with no row is silent — generating `attack_blade_light`
does not make 190 minions play it. Each card needs its own row pointing at the shared stem.

That is 293 cards × 1–3 rows, and the Unity authoring window owns those files. Two ways out:

1. **Author the rows** — correct, but a lot of clicking, and it is the only option that needs
   no code change.
2. **Add a fallback to the router** — a type/class-keyed default consulted when a card has no
   row for that trigger. Bespoke sounds stay as explicit rows and override it. This is a small
   change in one file and turns 293 edits into ~20.

Option 2 is the recommendation. It also makes the shared library actually shared, rather than
293 copies of the same string.

**`ON_TURN_START` / `ON_TURN_END` fire per observing carrier, not once per turn** —
`AppendTurnObservers` walks every carrier on both sides. A turn chime authored onto many cards
plays once per card. Wire it on nothing, or on exactly one permanent.

**`ON_HEAL` means "this minion was healed" and never fires on a hero**, and nine VERDANT heal
cards target `FRIENDLY_HERO` — so `heal_restore` will be silent on most of the class's healing
until the VERDANT pass lands. Do not judge the clip by those cards.

## Budget

23 clips at ~2s is ~46s of audio. ElevenLabs bills 40 credits/second, so ~1,840 credits —
roughly a tenth of a 190-effect allowance. Bespoke legendary sounds come out of the rest.
