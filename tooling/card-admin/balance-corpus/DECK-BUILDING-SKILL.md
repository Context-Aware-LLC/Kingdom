# Deck-building skill — Kingdom balance corpus

What was learned drafting the first hand-built decks for the balance matrix
(`tests/Kingdom.Core.Tests/Fixtures/BalanceCorpus/`). This file exists so the next person
drafting a corpus deck does not have to re-derive the card pool's shape from scratch.

**Read the card data, not the card names.** Every claim below was checked against
`unity/Assets/CardData/Pendingham.cards.json`, `Rekindling.cards.json`, and the engine
source. Where a brief and the data disagreed, the data won — see *Corrections to briefs* at the
bottom, which is the most load-bearing section in this file.

**Write about keywords using Kingdom's own vocabulary.** Describe what a keyword *does*; do not
reach for the word another game uses. The one deliberate exception is **Freeze**, permitted by
ruling — see the `FROST` row below. This was enforced by a CI scan until 2026-08-18; it is now a
convention you follow rather than a gate that stops you, because the scan only ever caught innocent
English and never a real leak.

---

## Before you start: what the keywords actually do

Deck-building is impossible without these, and the names do not give them away.

| Keyword | Runtime behaviour | Verified in |
|---|---|---|
| `BULWARK` | Enemies must attack it before anything else on your side | `CommandProcessor.cs` |
| `FERVOR` | May attack the turn it is played | `CommandProcessor.cs` |
| `SKIRMISH` | May attack the turn it is played, but only a minion | `CommandProcessor.cs` |
| `AEGIS` | Absorbs the first hit it takes | `DerivedStats.cs` |
| `SHROUD` | Cannot be attacked **or targeted**; drops the moment it attacks | `CommandProcessor.cs`, `Targeting.cs` |
| `VENOM` | Any damage it deals to a minion is lethal | `DamagePipeline.cs` |
| `REKINDLE` | Its first death resummons a 1-health copy in the same slot | `EffectInterpreter.cs` |
| `ARCANE_SURGE` | Your spells deal +1 while it is on the board | `ActionHandlers.cs` |
| `LIFEDRAIN` | Damage dealt also restores that much to **your hero**, never the minion | `DamagePipeline.cs` |
| `DOUBLESTRIKE` | Two attacks per turn | `CommandProcessor.cs` |
| `OVERWHELM` | Excess damage from killing a minion carries to the enemy hero | `CommandProcessor.cs` |
| `VANGUARD` | No retaliation if the defender dies to the hit | `CommandProcessor.cs` |
| `BACKDRAFT` | **Locks mana crystals next turn.** Not damage to you. | `ActionHandlers.cs` |
| `FROST` | Whatever it damages misses its next attack — **heroes included**, unlike `VENOM` | `DamagePipeline.cs` |

**`FROST` is the one keyword whose id is not its printed word:** the card face and every tooltip say
**Freeze** (`KeywordSemantics.AuthoredName`, Sam 2026-08-11), because *frost* is not a verb. Write
"Freeze" in prose and `FROST` when you mean the enum id. This is the sole exception to the rule
below, and the gate permits it by name.

Two of these will mislead you if you go by the name:

- **`SHROUD` turns your own wall off.** The `BULWARK` check explicitly skips shrouded minions
  (`if (fm.Keywords.Contains(BULWARK) && !fm.Keywords.Contains(SHROUD))`). Shrouding your wall
  makes it unkillable *and* useless as a wall. Shroud value engines, not blockers.
- **`BACKDRAFT` costs tempo, not life.** Healing does not pay for it. An aggro deck can afford
  perhaps 4–6 locked crystals across the whole list; past that it stops being aggro.

Also worth knowing before you evaluate a card: `OVERWHELM` overflow carries damage but **not**
riders, so a `LIFEDRAIN`+`OVERWHELM` minion does not drain off the overflow.

## Mana curve guidelines

Measured from the three decks that shipped in this corpus. `validate_decks.py` prints the curve of
any deck, so you can check yours against these without counting by hand.

| Archetype | Shape that worked | Reasoning |
|---|---|---|
| **Control / attrition** | `1:4 2:8 3:5 4:7 5:3 6:2 7:1` | Fat 2 and 4 slots. The 2s are cheap removal and sticky one-health bodies; the 4s are the real card-advantage engines. Only three cards above 5 — you get there on value, not on top-end count. |
| **Board control / token attrition** | `1:6 2:6 3:8 4:4 5:4 6:1 7:1` | Twenty cards at cost 3 or less establish sacrifice fodder and interaction before board-wide Reinforce effects. Two single-copy finishers are enough when the primary power supplies a body every turn. |
| **Stall / value** | `1:4 2:6 3:6 4:7 5:2 6:2 7:1 8:2` | The only shape here that earns a real 8-slot, because the hero power keeps producing blockers for free while you sit on an expensive hand. Two 8-drops is the ceiling. |
| **Spell / burn value** | `1:6 2:10 3:6 4:4 5:2 7:2` | Sixteen cards cost 1–2 so the deck can pair interaction with its 2-mana draw power. Fourteen minions keep pressure on the board; deterministic hero damage complements the random reach. |
| **Aggro / resilience** | `1:7 2:8 3:7 4:5 5:2 7:1` | 22 of 30 cards cost 3 or less. One card above 5, as a finisher only. If you want a second 6+, you are building midrange. |

Rules of thumb that fell out of this:

- **The hero power is a card you play every turn from turn 2.** Budget 2 mana out of every turn
  when curving. That is why the stall deck can carry two 8-drops and the aggro deck cannot carry one.
- **Count your removal, not your spells.** Control wants 5–8 pieces of interaction. Past that you
  draw answers with nothing to answer.
- **Count your draw.** A deck with no draw engine plays its curve and stops. The control and stall
  decks here carry 5–6 sources each; the aggro deck carries none, deliberately.
- **A draw hero power supports a lower curve, not a slower one.** CLOCKWRIGHT can spend cards
  quickly because its power continually supplies fuel. Prefer draw attached to a body, bounce, or
  another useful action so refilling does not surrender the board.

## Class synergy observations

Read from the whole pool, not from a handful of marquee cards.

- **GRAVEKEEP** — dying is its economy. Nearly every Gravekeep body carries an `ON_DEATH` rule that
  draws, heals, or summons, so its minions are *worth more dead*. That makes trading into them bad
  for the opponent, and turns cheap sacrifice effects (`grave_last_rite`, `grave_charnel_priest`,
  `grave_warden_of_dusk`) into value converters rather than drawbacks. Its hero power grants
  `REKINDLE`, which fires an `ON_DEATH` rule a second time — the class's two halves are the same half.
- **NIGHTVEIL** — evasion and hard removal. It holds the cheapest unconditional `DESTROY` in the
  game (`night_finish_the_job`, 2 mana) plus a stack of `SHROUD` bodies. `SHROUD` is stronger than
  it reads because it blocks spell targeting as well as attacks: a shrouded threat can only be
  answered by board-wide effects.
- **STONEWARDEN** — health, not attack. The class has almost no reach and several minions with 0 or
  1 attack. Its cards buy turns; something else has to convert them. A Stonewarden deck with no win
  condition simply draws out.
- **CLOCKWRIGHT** — card advantage and board-wide pumps. Its `ON_DEATH: DRAW` bodies pair naturally
  with Stonewarden's walls (both *want* to be attacked), and its `MODIFY_STATS -> FRIENDLY_MINIONS`
  effects are the answer to Stonewarden's attack problem. `clock_cogsworth` turning a board
  of 0/3 tokens into 2/5s is the clearest example of the pairing working.
- **TEMPEST** — reach and `DOUBLESTRIKE`. Damage that may go to the enemy hero, plus minions that
  attack twice, so a pump on a Tempest body is worth double. The tax is `BACKDRAFT` on its best
  spells — the class trades next turn for this turn, by design.
- **VERDANT** — sustain and stats. Large-health bodies plus the game's densest healing. Note that
  most Verdant healing targets `FRIENDLY_HERO` specifically, so it does nothing to stabilise a
  board; it only buys turns.
- **EMBERWARD** — proactive damage and attack pressure. `FERVOR`, `SKIRMISH`, direct damage, and
  `BACKDRAFT` exchange future flexibility for immediate tempo; its best decks close before the
  locked crystals become decisive.
- **TIDECALLER** — board denial and durable defence. Frost, bounce, `BULWARK`
  effects buy time and turn small defenders into relevant bodies without relying on raw damage.
- **NEUTRAL** — curve glue rather than a standalone plan. Its cards supply `BULWARK`, healing,
  draw, keyword specialists, and broadly useful bodies when a class pair lacks a cost or role.

## Rekindling integration notes

Rekindling (`rekind_*`) is not a power-creep set; it is a *shape* set. The pattern across its 73
cards is a second small effect stapled onto a familiar body.

- **A Rekindling card is usually the better version of an Kingdom card at the same cost**, at the
  price of being narrower. `grave_tomb_warden` (1/3, `BULWARK`, `ON_DEATH: draw 1`) does two
  jobs where `grave_deathweaver` does one. Prefer Rekindling for a slot you already knew you wanted;
  prefer Kingdom when you want flexibility.
- **The Rekindling legendaries are build-arounds, not just big bodies.** `ward_aldric` and
  `clock_cogsworth` each convert a stalled board into a threat in one card. If a deck wants a
  top end, check that class's Rekindling legendary first.
- **How much Rekindling a deck ends up with is an outcome, not a target.** These three landed at
  12, 13 and 4 cards of 30 respectively — the aggro deck is low because Kingdom's Tempest spells
  are already the cheapest reach in the pool and Rekindling adds no cheaper one.
- **CLOCKWRIGHT / TEMPEST gets tempo draw plus deterministic reach.** Blueprint Archive and
  Tinker's Gambit replace themselves, Storm Channeler supports the dense spell package,
  Thunderstrike layers targeted and enemy-wide damage, and Voltaic Sentinel plus Stormcaller's
  Pact guarantee enemy-hero damage instead of leaving every burn result to random targets.

## Anti-patterns to avoid

- **Random sampling from the eligible pool.** This is the failure mode the corpus exists to fix. A
  sampled deck has no curve, no removal count and no win condition, so a balance matrix built on it
  measures the sampler rather than the classes.
- **Building for the archetype label instead of the card pool.** "Aggro" does not mean "every cheap
  card". The Tempest/Verdant deck is aggro because 22 cards cost ≤3 *and* it carries reach *and* it
  carries one finisher — remove any of the three and it is a bad midrange deck.
- **Stacking legendaries.** Three is comfortable; four is usually one too many; three of them at
  cost 7–8 is unplayable. `L3` caps each at one copy, so every legendary is a card you may never see.
- **Trusting a keyword's name.** See the table above. `BACKDRAFT` and `SHROUD` both do something
  other than what they sound like, and both change deck construction.
- **Shrouding your own walls.** A board-wide grant of `SHROUD` (`night_vanishing_act`,
  `night_seraphine`) is anti-synergy with a `BULWARK` deck. It protects a deck whose threats
  are not blockers.
- **Reporting counts you did not read off a script.** Run `validate_decks.py` and paste what it
  printed. See the top-level `CLAUDE.md` for why this is a hard rule.
- **Overfilling a token deck.** Tidal Ward already supplies one body per use. Too many additional
  token generators board-lock the TIDECALLER player and turn later summon effects into dead cards.
- **Clearing your own wide board without recovery.** Symmetric area damage belongs only where
  death triggers, `REKINDLE`, or immediate token generation recovers the loss.
- **Treating draw count as a win condition.** CLOCKWRIGHT's power and draw-heavy bodies provide
  consistency, but the current pool has no explicit draw-trigger payoff. The deck still needs
  tempo-efficient ways to spend those cards.
- **Depending entirely on random reach.** Pair random enemy damage with effects that explicitly
  hit the enemy hero or a chosen character so soak results are not dominated by target variance.
- **Cutting too many bodies for spell synergy.** `ARCANE_SURGE` minions and other tempo bodies must
  survive long enough for the spells to create an advantage; the spell/burn list keeps 14 minions.

## Corrections to briefs — read this before you trust a hero-power description

The NOR-319 brief described hero powers that do not match the shipped data. The card data and
`src/Kingdom.Core.Cards/HeroPowers.cs` are the source of truth.

| Class | Brief said | `HeroPowers.cs` + card data actually says |
|---|---|---|
| **GRAVEKEEP** | Toll the Bell — restore 2 HP to hero | **Grant `REKINDLE` to a friendly minion.** Not a heal at all. |
| **TIDECALLER** | Tidal Ward — summon a 0/2 `BULWARK` | **Summon `token_tide_wisp`, which is a 1/2 `BULWARK`.** |
| STONEWARDEN | Earthwall — summon a 0/3 `BULWARK` token | Correct (`token_stone_shard`, 0/3, `BULWARK`) |
| TEMPEST | Chain Spark — 1 damage to 2 random enemies | Correct |
| VERDANT | Empower — give a friendly minion +1/+1 | Correct |
| NIGHTVEIL | Shadow Mark — give a friendly minion `SHROUD` | Correct |
| CLOCKWRIGHT | Blueprint — draw a card | Correct |

**A character gets exactly one hero power, from its primary class.** `MatchEngine.cs:49` stamps
`HeroPowers.ForClass(setup.P0Class)` — a single class, not a pair. So in a `TEMPEST / VERDANT`
character, Verdant's *Empower* is unavailable; Verdant contributes cards only. Never plan a deck
around the secondary class's hero power.

---

## Verification

Every deck in the corpus must pass before it is committed:

```
python tooling/balance-corpus/validate_decks.py
```

It checks L1 (exactly 30), L2/L3 (copy caps by rarity), L4 (class membership), `collectible: true`,
duplicate entries, unknown ids, and `(cost, id)` sort order. It mirrors
`src/Kingdom.Core.Collection/DeckValidator.cs`; if the two ever disagree, the C# validator wins.
