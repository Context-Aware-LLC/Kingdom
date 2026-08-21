<!-- GENERATED — do not edit by hand. Regenerate with: dotnet run --project src/Emberfall.Tools.OddsDisclosure -- docs/pub-docs/progression/pack-odds.md -->

# Pack Odds — Emberfall

Each pack contains **5 cards** drawn from
the Core set combined with the pack's expansion set.
All packs of the same type use identical odds and the same card pool,
whether purchased with premium or earned currency.

## Base odds per slot

These are the odds applied to each card slot individually, before guarantees.

| Rarity    | Chance per slot |
|-----------|----------------|
| Common    | 71.5%          |
| Rare      | 23.0%          |
| Epic      | 4.5%           |
| Legendary | 1.0%           |

## Guarantees

After the base draw, each pack is adjusted to honour the following rules:

- **Rare floor.** Every pack contains at least one Rare or better.
- **Epic pity.** If you open 9 consecutive packs from a set without an Epic,
  your next pack is guaranteed to contain one (guaranteed on pack 10 of a dry streak).
- **First Legendary window.** Your first Legendary from a set is guaranteed
  by your 10th pack from that set.
- **Legendary pity.** After your first Legendary, one is guaranteed
  every 40 consecutive packs from that set without one.

**Counters are per expansion set.** Opening Emberfall packs does not affect
your pity counter for any other set.

When a pack owes both an Epic and a Legendary, it delivers both —
they upgrade separate slots rather than collapsing into one.

## Duplicates

**There is no duplicate protection.** Every slot is drawn independently from
all cards of its rarity in that pack's pool. Owning a card does not lower your
chance of being given it again, and a pack does not steer toward cards you are
missing. A pack can hand you a copy of something you already hold while cards
you have never seen sit in the same pool.

**Nothing a pack gives you is destroyed or converted.** Copies beyond the
2 a deck may run (1 for a Legendary) stay in
your collection as cards. Your collection has no copy limit; only decks do.

Shards come from **Reduce**, in your collection, where you choose which copy to
break down. A pack never makes that choice for you.

---

*This page is generated from the shipped odds constants and is updated
whenever those constants change. Last regenerated for EF-080f / NOR-260.*
