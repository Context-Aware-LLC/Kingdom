using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>How many copies of each card a player already holds — the input to duplicate protection.</summary>
    /// <remarks>
    /// An interface rather than a dictionary parameter because the authority differs by caller: the
    /// server projects it off <c>IEntitlementLedger</c> balances, a test hands over a literal, and the
    /// client (which never decides an open — ADR-0004) only ever renders the result.
    /// </remarks>
    public interface IOwnedCardCounts
    {
        /// <summary>Copies of this card the player owns. 0 for a card they have never seen.</summary>
        int CopiesOwned(string cardId);
    }

    /// <summary>The trivial dictionary-backed <see cref="IOwnedCardCounts"/> for tests, dev and projections.</summary>
    public sealed class OwnedCardCounts : IOwnedCardCounts
    {
        private readonly Dictionary<string, int> _counts;

        public OwnedCardCounts() => _counts = new Dictionary<string, int>(StringComparer.Ordinal);

        public OwnedCardCounts(IReadOnlyDictionary<string, int> counts)
        {
            _counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (counts != null)
                foreach (var kv in counts) _counts[kv.Key] = kv.Value;
        }

        public int CopiesOwned(string cardId)
            => cardId != null && _counts.TryGetValue(cardId, out int n) ? n : 0;

        public void Set(string cardId, int copies) => _counts[cardId] = copies;
    }

    /// <summary>Why a slot's rarity is higher than the dice said. <see cref="None"/> = the dice said so.</summary>
    public enum PackUpgrade
    {
        /// <summary>The rolled rarity stood.</summary>
        None,

        /// <summary>NOR-109 §4: every pack contains at least one Rare-or-better.</summary>
        RareFloor,

        /// <summary>NOR-109 §4: <see cref="PackPityState.EpicPityPacks"/> packs without an Epic.</summary>
        EpicPity,

        /// <summary>NOR-109 §4: the first-window or <see cref="PackPityState.LegendaryPityPacks"/> Legendary guarantee.</summary>
        LegendaryPity,
    }

    /// <summary>
    /// One card slot of an opened pack: which card, at what rarity, and — when the player was already at
    /// their copy limit for every card of that rarity in the pool — the shards it became instead.
    /// </summary>
    /// <remarks>
    /// <see cref="CardId"/> is populated even when <see cref="ConvertedToShards"/> is true. The dupe is
    /// still a real card and the reveal (NOR-107c) shows it before it dissolves into shards; suppressing
    /// the id here would leave the UI with a shard count and nothing to animate.
    /// </remarks>
    public readonly struct PackSlotAward
    {
        public readonly string CardId;
        public readonly Rarity Rarity;

        /// <summary>True when this slot was a duplicate past the copy limit and paid out in shards.</summary>
        public readonly bool ConvertedToShards;

        /// <summary>Shards paid for this slot. 0 unless <see cref="ConvertedToShards"/>.</summary>
        public readonly int Shards;

        /// <summary>Which guarantee, if any, set this slot's rarity rather than the dice.</summary>
        public readonly PackUpgrade Upgrade;

        public PackSlotAward(string cardId, Rarity rarity, bool convertedToShards, int shards, PackUpgrade upgrade)
        {
            CardId = cardId;
            Rarity = rarity;
            ConvertedToShards = convertedToShards;
            Shards = shards;
            Upgrade = upgrade;
        }

        public override string ToString() =>
            $"{Rarity} {CardId}" + (ConvertedToShards ? $" -> {Shards} shards" : "")
            + (Upgrade == PackUpgrade.None ? "" : $" [{Upgrade}]");
    }

    /// <summary>
    /// The full, replayable outcome of one pack open. Everything the ledger needs to grant, everything
    /// the reveal needs to animate, and both pity states so a caller can persist the advance separately
    /// from the grant.
    /// </summary>
    public sealed class PackOpenResult
    {
        public string PackId { get; }
        public SetId Expansion { get; }

        /// <summary>The index that, with the seed, reproduces this pack byte-identically.</summary>
        public int PackIndex { get; }

        public IReadOnlyList<PackSlotAward> Awards { get; }

        /// <summary>The counters this open was resolved against.</summary>
        public PackPityState PityBefore { get; }

        /// <summary>The counters after it — what a caller persists once the grant lands.</summary>
        public PackPityState PityAfter { get; }

        public PackOpenResult(string packId, SetId expansion, int packIndex,
                              IReadOnlyList<PackSlotAward> awards,
                              PackPityState pityBefore, PackPityState pityAfter)
        {
            PackId = packId;
            Expansion = expansion;
            PackIndex = packIndex;
            Awards = awards ?? Array.Empty<PackSlotAward>();
            PityBefore = pityBefore;
            PityAfter = pityAfter;
        }

        /// <summary>Total shards from every duplicate in this pack.</summary>
        public int TotalShards
        {
            get
            {
                int sum = 0;
                for (int i = 0; i < Awards.Count; i++) sum += Awards[i].Shards;
                return sum;
            }
        }

        /// <summary>The slot rarities, in slot order — the input to <see cref="PackPityState.AfterPack"/>.</summary>
        public IReadOnlyList<Rarity> Rarities()
        {
            var list = new List<Rarity>(Awards.Count);
            for (int i = 0; i < Awards.Count; i++) list.Add(Awards[i].Rarity);
            return list;
        }
    }

    /// <summary>
    /// The deterministic booster-pack open (NOR-109 §2). <b>Pure</b>: same
    /// <c>(pack, pool, owned, pity, seed, packIndex)</c> in, byte-identical result out, on any platform,
    /// forever. It touches no clock, no <c>System.Random</c>, no float and no store — which is what lets
    /// a golden fixture pin it and what lets the server resolve an open authoritatively (ADR-0004: the
    /// client renders the result, it never speculates one).
    ///
    /// <para><b>The RNG call order is the contract, not an implementation detail.</b> The pack draws
    /// exactly <c>CardCount</c> rarities and then exactly <c>CardCount</c> card picks, in slot order.
    /// Guarantees are applied <i>between</i> those two phases, so whether pity fires does not shift the
    /// stream — two players on the same seed and index get the same cards wherever their counters
    /// happen to sit. Reordering or adding a draw invalidates every baked fixture.</para>
    ///
    /// <para><b>Seeding.</b> <c>DeterministicRandom.Substream(seed, packIndex)</c> — the same splitmix64
    /// stream jumped to its <c>packIndex</c>-th cursor in O(1), so pack #4000 is reproducible without
    /// replaying 3,999 packs, and no second entropy source enters the core.</para>
    /// </summary>
    public static class BoosterPackOpener
    {
        /// <summary>Rarities from rarest to commonest — the order guarantees are applied in.</summary>
        private static readonly Rarity[] DescendingRarity =
        {
            Rarity.LEGENDARY, Rarity.EPIC, Rarity.RARE, Rarity.COMMON,
        };

        /// <summary>Open one pack against a database. See the overload for the algorithm.</summary>
        public static PackOpenResult Open(BoosterPack pack, ICardDatabase db, IOwnedCardCounts owned,
                                          PackPityState pity, ulong seed, int packIndex)
        {
            if (pack == null) throw new ArgumentNullException(nameof(pack));
            return Open(pack, pack.Pool(db), owned, pity, seed, packIndex);
        }

        /// <summary>
        /// Open one pack against an explicit pool.
        ///
        /// <para><b>The pool must already be in a deterministic order</b> — <see cref="BoosterPack.Pool"/>
        /// sorts by card id and this overload trusts that, because re-sorting on every open would cost a
        /// sort per pack for a list the caller can hoist. Handing an unsorted pool in produces a result
        /// that is stable on one machine and different on the next, which no test here can catch for
        /// you.</para>
        /// </summary>
        public static PackOpenResult Open(BoosterPack pack, IReadOnlyList<CardDefinition> pool,
                                          IOwnedCardCounts owned, PackPityState pity,
                                          ulong seed, int packIndex)
        {
            if (pack == null) throw new ArgumentNullException(nameof(pack));
            if (pool == null) throw new ArgumentNullException(nameof(pool));
            if (packIndex < 0) throw new ArgumentOutOfRangeException(nameof(packIndex));

            var byRarity = GroupByRarity(pool);
            if (TotalCards(byRarity) == 0)
                throw new ArgumentException(
                    $"Pack '{pack.Id}' has an empty pool — no collectible {SetId.CORE}/{pack.Expansion} cards.",
                    nameof(pool));

            var rng = DeterministicRandom.Substream(seed, packIndex);

            // ── phase 1: CardCount rarity draws, one RNG value each ──────────────────────────
            var rarities = new Rarity[pack.CardCount];
            var upgrades = new PackUpgrade[pack.CardCount];
            for (int i = 0; i < pack.CardCount; i++)
            {
                rarities[i] = Substitute(pack.Odds.Roll(rng), byRarity);
                upgrades[i] = PackUpgrade.None;
            }

            // ── guarantees: no RNG, so pity never shifts the stream ──────────────────────────
            ApplyGuarantees(rarities, upgrades, byRarity, pity);

            // ── phase 2: CardCount card picks, one RNG value each ────────────────────────────
            // Copies awarded earlier in THIS pack count against later slots, so a five-card pack
            // cannot hand out three copies of the same common while claiming dupe protection.
            var awardedThisPack = new Dictionary<string, int>(StringComparer.Ordinal);
            var awards = new List<PackSlotAward>(pack.CardCount);

            for (int i = 0; i < pack.CardCount; i++)
            {
                Rarity rarity = rarities[i];
                var candidates = byRarity[rarity];

                // §6 new-cards-before-dupes: only cards still under their copy limit are eligible,
                // and the limit is the deck/collection limit itself (DeckValidator L2/L3) — one source
                // of truth, so a format change to the copy cap cannot leave pack odds behind.
                int limit = DeckValidator.MaxCopies(rarity);
                var eligible = new List<CardDefinition>(candidates.Count);
                for (int c = 0; c < candidates.Count; c++)
                {
                    var def = candidates[c];
                    if (EffectiveOwned(owned, awardedThisPack, def.Id) < limit) eligible.Add(def);
                }

                bool dupe = eligible.Count == 0;
                var draw = dupe ? candidates : eligible;
                var picked = draw[rng.NextInt(draw.Count)];

                if (dupe)
                {
                    // Every card of this rarity in the pool is already maxed: the player cannot hold
                    // another copy, so the slot pays out in shards instead (§6).
                    awards.Add(new PackSlotAward(picked.Id, rarity, true, ShardValues.ForRarity(rarity), upgrades[i]));
                }
                else
                {
                    awardedThisPack[picked.Id] = EffectiveOwnedDelta(awardedThisPack, picked.Id) + 1;
                    awards.Add(new PackSlotAward(picked.Id, rarity, false, 0, upgrades[i]));
                }
            }

            var after = pity.AfterPack(ToRarityList(rarities));
            return new PackOpenResult(pack.Id, pack.Expansion, packIndex, awards, pity, after);
        }

        // ── guarantees ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Apply the three §4 guarantees in rarest-first order: Legendary pity, then Epic pity, then the
        /// Rare floor. Each upgrades <b>one</b> slot and never the same slot twice, so a pack that owes
        /// both a Legendary and an Epic delivers both rather than collapsing them into one.
        ///
        /// <para><b>Which slot.</b> The lowest-rarity slot, breaking ties by the highest index. Lowest
        /// first so an upgrade destroys as little as possible; highest index on a tie purely so the rule
        /// is total — an arbitrary but fixed choice beats an arbitrary and drifting one, and a fixture
        /// pins it.</para>
        ///
        /// <para>A guarantee whose rarity has no cards in the pool is skipped rather than faked: there
        /// is nothing to award, and inventing a slot of a rarity the pairing does not contain would
        /// crash the pick phase.</para>
        /// </summary>
        private static void ApplyGuarantees(Rarity[] rarities, PackUpgrade[] upgrades,
                                            Dictionary<Rarity, List<CardDefinition>> byRarity,
                                            PackPityState pity)
        {
            if (pity.LegendaryDueNextPack)
                Upgrade(rarities, upgrades, byRarity, Rarity.LEGENDARY, PackUpgrade.LegendaryPity);

            if (pity.EpicDueNextPack)
                Upgrade(rarities, upgrades, byRarity, Rarity.EPIC, PackUpgrade.EpicPity);

            // The Rare floor is last and usually moot: a pity upgrade above already satisfies it.
            Upgrade(rarities, upgrades, byRarity, Rarity.RARE, PackUpgrade.RareFloor);
        }

        /// <summary>
        /// Ensure at least one slot is <paramref name="target"/>-or-better, upgrading one slot if not.
        /// No-op when the guarantee is already satisfied or the pool cannot honour it.
        /// </summary>
        private static void Upgrade(Rarity[] rarities, PackUpgrade[] upgrades,
                                    Dictionary<Rarity, List<CardDefinition>> byRarity,
                                    Rarity target, PackUpgrade reason)
        {
            bool satisfied = reason == PackUpgrade.RareFloor
                ? AnyAtLeast(rarities, target)   // "Rare-or-better"
                : AnyExactly(rarities, target);  // "an Epic" / "a Legendary" — the named rarity itself

            if (satisfied) return;
            if (!byRarity.TryGetValue(target, out var cards) || cards.Count == 0) return;

            int slot = -1;
            for (int i = 0; i < rarities.Length; i++)
            {
                if (upgrades[i] != PackUpgrade.None) continue;          // one guarantee per slot
                if (slot < 0 || rarities[i] <= rarities[slot]) slot = i; // lowest rarity, ties -> highest index
            }
            if (slot < 0) return;   // every slot already spoken for by an earlier guarantee

            rarities[slot] = target;
            upgrades[slot] = reason;
        }

        private static bool AnyExactly(Rarity[] rarities, Rarity target)
        {
            for (int i = 0; i < rarities.Length; i++) if (rarities[i] == target) return true;
            return false;
        }

        private static bool AnyAtLeast(Rarity[] rarities, Rarity target)
        {
            for (int i = 0; i < rarities.Length; i++) if (rarities[i] >= target) return true;
            return false;
        }

        // ── pool helpers ─────────────────────────────────────────────────────────────────────

        private static Dictionary<Rarity, List<CardDefinition>> GroupByRarity(IReadOnlyList<CardDefinition> pool)
        {
            var byRarity = new Dictionary<Rarity, List<CardDefinition>>();
            foreach (Rarity r in DescendingRarity) byRarity[r] = new List<CardDefinition>();

            for (int i = 0; i < pool.Count; i++)
            {
                var card = pool[i];
                if (card == null) continue;
                if (!byRarity.TryGetValue(card.Rarity, out var bucket))
                    byRarity[card.Rarity] = bucket = new List<CardDefinition>();
                bucket.Add(card);
            }
            return byRarity;
        }

        private static int TotalCards(Dictionary<Rarity, List<CardDefinition>> byRarity)
        {
            int n = 0;
            foreach (var bucket in byRarity.Values) n += bucket.Count;
            return n;
        }

        /// <summary>
        /// A rolled rarity the pool cannot honour falls back to the nearest rarity it can — down first,
        /// then up. Degenerate by construction (a pairing with no Legendaries), but the alternative is
        /// an index-out-of-range in the pick phase on a content gap, which is the worst possible place
        /// to discover one.
        /// </summary>
        private static Rarity Substitute(Rarity rolled, Dictionary<Rarity, List<CardDefinition>> byRarity)
        {
            if (Has(byRarity, rolled)) return rolled;
            for (int r = (int)rolled - 1; r >= 0; r--)
                if (Has(byRarity, (Rarity)r)) return (Rarity)r;
            for (int r = (int)rolled + 1; r <= (int)Rarity.LEGENDARY; r++)
                if (Has(byRarity, (Rarity)r)) return (Rarity)r;
            throw new InvalidOperationException("Pool is empty — guarded on entry to Open.");
        }

        private static bool Has(Dictionary<Rarity, List<CardDefinition>> byRarity, Rarity r)
            => byRarity.TryGetValue(r, out var bucket) && bucket.Count > 0;

        private static int EffectiveOwned(IOwnedCardCounts owned, Dictionary<string, int> thisPack, string cardId)
            => (owned != null ? owned.CopiesOwned(cardId) : 0) + EffectiveOwnedDelta(thisPack, cardId);

        private static int EffectiveOwnedDelta(Dictionary<string, int> thisPack, string cardId)
            => thisPack.TryGetValue(cardId, out int n) ? n : 0;

        private static IReadOnlyList<Rarity> ToRarityList(Rarity[] rarities)
        {
            var list = new List<Rarity>(rarities.Length);
            for (int i = 0; i < rarities.Length; i++) list.Add(rarities[i]);
            return list;
        }
    }
}
