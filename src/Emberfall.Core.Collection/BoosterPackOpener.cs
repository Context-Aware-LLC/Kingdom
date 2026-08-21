using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    // IOwnedCardCounts and OwnedCardCounts moved to OwnedCards.cs (NOR-238) — same namespace, same
    // members. They are the collection's ownership model now, not the pack opener's dupe-protection
    // input, because DeckValidator L6 reads them too.

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
    /// One card slot of an opened pack: which card, at what rarity. <b>Every slot is a card.</b>
    /// </summary>
    /// <remarks>
    /// <para><b>A pack never converts a slot to shards</b> (Sam, 2026-08-12). This type used to carry
    /// <c>ConvertedToShards</c> and <c>Shards</c> for the case where the player was already at their copy
    /// limit on every card of the rolled rarity; that slot now hands over the card, and the player decides
    /// what becomes of it. Reducing a card to dust is a thing a player chooses, never a thing an open does
    /// to them — the same ruling that caps <see cref="Crafting"/> at the deck limit while leaving ownership
    /// uncapped, and for the same reason: card-to-card TRADING is a live possibility, and a system that
    /// destroys a spare copy on the player's behalf has already spent the card that trade needed.</para>
    ///
    /// <para>Consequences worth knowing. The "new cards before dupes" preference in
    /// <see cref="BoosterPackOpener"/> is untouched — a pack still tries to hand you something you are
    /// under the limit on; it just no longer punishes the case where it cannot. A collection can hold
    /// unbounded copies (<see cref="IOwnedCardCounts"/> was always an unbounded count), and
    /// <see cref="DeckValidator.CopyLimit"/> is <c>min(MaxCopies, owned)</c>, so a 47th copy plays 2 and
    /// is otherwise inert but real.</para>
    /// </remarks>
    public readonly struct PackSlotAward
    {
        public readonly string CardId;
        public readonly Rarity Rarity;

        /// <summary>Which guarantee, if any, set this slot's rarity rather than the dice.</summary>
        public readonly PackUpgrade Upgrade;

        public PackSlotAward(string cardId, Rarity rarity, PackUpgrade upgrade)
        {
            CardId = cardId;
            Rarity = rarity;
            Upgrade = upgrade;
        }

        public override string ToString() =>
            $"{Rarity} {CardId}" + (Upgrade == PackUpgrade.None ? "" : $" [{Upgrade}]");
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
    /// <c>(pack, pool, pity, seed, packIndex)</c> in, byte-identical result out, on any platform,
    /// forever. It touches no clock, no <c>System.Random</c>, no float and no store — which is what lets
    /// a golden fixture pin it and what lets the server resolve an open authoritatively (ADR-0004: the
    /// client renders the result, it never speculates one).
    ///
    /// <para><b><c>owned</c> is no longer one of those inputs.</b> Since the 2026-08-20 ruling a slot
    /// draws uniformly from its rarity bucket, so two players on the same seed and index open the same
    /// pack whatever their collections look like. The parameter is still threaded through both
    /// overloads, and the server still passes real ownership, purely so the commented-out preference in
    /// the pick phase can be restored in place. Do not read its presence as an influence on the
    /// result.</para>
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

        /// <summary>Open one pack against a database. See the backdraft for the algorithm.</summary>
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
        /// sorts by card id and this backdraft trusts that, because re-sorting on every open would cost a
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
            // A slot draws UNIFORMLY from every card of its rarity in the pool. Ownership is not an
            // input: what you already hold does not change what you can be handed, so a pack can give
            // you a third copy of a common while commons you have never seen sit beside it in the pool.
            //
            // That is the ruling, not an oversight (Sam, 2026-08-20). NOR-109 §6's "new cards before
            // dupes" preference is retained below as commented code rather than deleted, because a
            // LESS GENEROUS form of it may be reintroduced and the shape is worth keeping. Nothing is
            // owed to the old behaviour in the meantime: read the live loop, not the block under it.
            var awards = new List<PackSlotAward>(pack.CardCount);

            for (int i = 0; i < pack.CardCount; i++)
            {
                Rarity rarity = rarities[i];
                var candidates = byRarity[rarity];

                var picked = candidates[rng.NextInt(candidates.Count)];
                awards.Add(new PackSlotAward(picked.Id, rarity, upgrades[i]));

                // ── DISABLED: NOR-109 §6 new-cards-before-dupes (Sam, 2026-08-20) ────────────
                // To restore, delete the two lines above and uncomment this, plus `awardedThisPack`
                // below the loop header and the two EffectiveOwned helpers at the foot of the class.
                // The `owned` parameter is still threaded through both Open overloads for exactly
                // this, and BoosterPackGrantService still projects and passes real ownership.
                //
                // Cards still under their copy limit were PREFERRED, the limit being the
                // deck/collection limit itself (DeckValidator L2/L3) so that a format change to the
                // copy cap could not leave pack odds behind. Copies awarded earlier in THIS pack
                // counted against later slots, so one pack could not hand out three copies of a
                // common while claiming dupe protection.
                //
                // It was only ever a preference: when every card of the rarity was already maxed the
                // slot drew from the full pool anyway.
                //
                //   int limit = DeckValidator.MaxCopies(rarity);
                //   var eligible = new List<CardDefinition>(candidates.Count);
                //   for (int c = 0; c < candidates.Count; c++)
                //   {
                //       var def = candidates[c];
                //       if (EffectiveOwned(owned, awardedThisPack, def.Id) < limit) eligible.Add(def);
                //   }
                //
                //   var draw = eligible.Count == 0 ? candidates : eligible;
                //   var picked = draw[rng.NextInt(draw.Count)];
                //
                //   awardedThisPack[picked.Id] = EffectiveOwnedDelta(awardedThisPack, picked.Id) + 1;
                //   awards.Add(new PackSlotAward(picked.Id, rarity, upgrades[i]));
                //
                // and above the loop:
                //   var awardedThisPack = new Dictionary<string, int>(StringComparer.Ordinal);
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
        /// to find one.
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

        // DISABLED with the new-cards-before-dupes preference in the pick phase (Sam, 2026-08-20).
        // Uncomment both together with that block; nothing else calls them.
        //
        //   private static int EffectiveOwned(IOwnedCardCounts owned, Dictionary<string, int> thisPack, string cardId)
        //       => (owned != null ? owned.CopiesOwned(cardId) : 0) + EffectiveOwnedDelta(thisPack, cardId);
        //
        //   private static int EffectiveOwnedDelta(Dictionary<string, int> thisPack, string cardId)
        //       => thisPack.TryGetValue(cardId, out int n) ? n : 0;

        private static IReadOnlyList<Rarity> ToRarityList(Rarity[] rarities)
        {
            var list = new List<Rarity>(rarities.Length);
            for (int i = 0; i < rarities.Length; i++) list.Add(rarities[i]);
            return list;
        }
    }
}
