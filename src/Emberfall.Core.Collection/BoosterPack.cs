using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// The per-card rarity odds of a booster pack (NOR-109 §3), as <b>integer permille</b>.
    ///
    /// <para><b>Why permille and not a percentage float.</b> The odds are consumed by
    /// <see cref="DeterministicRandom.NextInt"/>, which is integer-only by the determinism contract
    /// (ADR-0003 / <c>BannedApiTests.No_Float_Or_Double_Field_In_Core_Types</c>). A <c>float</c> field
    /// anywhere in a core type fails that scan, so the brief's 71.5% is carried as 715/1000 — which is
    /// also exact, where 0.715f is not.</para>
    ///
    /// <para>The four weights must sum to <see cref="Scale"/>. That is checked in the constructor
    /// rather than normalised: a table that does not sum to 1000 is an authoring mistake, and silently
    /// rescaling it would ship the wrong drop rate without anyone seeing a number change.</para>
    /// </summary>
    public readonly struct PackRarityOdds
    {
        /// <summary>Denominator of every weight below. Permille, so 715 == 71.5%.</summary>
        public const int Scale = 1000;

        public readonly int CommonPermille;
        public readonly int RarePermille;
        public readonly int EpicPermille;
        public readonly int LegendaryPermille;

        public PackRarityOdds(int commonPermille, int rarePermille, int epicPermille, int legendaryPermille)
        {
            if (commonPermille < 0 || rarePermille < 0 || epicPermille < 0 || legendaryPermille < 0)
                throw new ArgumentOutOfRangeException(nameof(commonPermille), "Rarity weights cannot be negative.");

            int sum = commonPermille + rarePermille + epicPermille + legendaryPermille;
            if (sum != Scale)
                throw new ArgumentException(
                    $"Rarity weights must sum to {Scale} permille; got {sum}. " +
                    "A table that does not sum is an authoring mistake, not something to normalise.",
                    nameof(commonPermille));

            CommonPermille = commonPermille;
            RarePermille = rarePermille;
            EpicPermille = epicPermille;
            LegendaryPermille = legendaryPermille;
        }

        /// <summary>
        /// The shipped table (NOR-109 §3, SOURCE OF TRUTH — do not retune here): Common 71.5%,
        /// Rare 23%, Epic 4.5%, Legendary 1%.
        /// </summary>
        public static readonly PackRarityOdds Default = new PackRarityOdds(715, 230, 45, 10);

        public int PermilleOf(Rarity rarity) => rarity switch
        {
            Rarity.COMMON => CommonPermille,
            Rarity.RARE => RarePermille,
            Rarity.EPIC => EpicPermille,
            Rarity.LEGENDARY => LegendaryPermille,
            _ => 0,
        };

        /// <summary>
        /// Draw one slot's rarity. Exactly <b>one</b> RNG value is consumed per call — the call count is
        /// part of the determinism contract, so this must never branch into a second draw.
        ///
        /// <para>Cumulative bands are walked rarest-last so that a zero-weight rarity is unreachable
        /// rather than absorbing the boundary value.</para>
        /// </summary>
        public Rarity Roll(DeterministicRandom rng)
        {
            if (rng == null) throw new ArgumentNullException(nameof(rng));

            int r = rng.NextInt(Scale);
            if (r < CommonPermille) return Rarity.COMMON;
            r -= CommonPermille;
            if (r < RarePermille) return Rarity.RARE;
            r -= RarePermille;
            if (r < EpicPermille) return Rarity.EPIC;
            return Rarity.LEGENDARY;
        }
    }

    /// <summary>
    /// What a duplicate past the collection limit converts into (NOR-109 §6).
    ///
    /// <para><b>LOCKED BY SAM, 2026-08-02 (NOR-109).</b> An economy call, not an engineering one: the
    /// mechanism (a dupe past <see cref="DeckValidator.MaxCopies"/> becomes shards instead of a card) is
    /// what this ticket implements, and these are the numbers he chose over a flatter-legendary
    /// alternative. They are deliberately all in one place so that re-pricing the economy is a four-line
    /// change with no algorithm edit and no fixture re-bake beyond the shard totals. <b>Re-pricing is an
    /// economy decision — do not retune these to make a test or a payout feel better.</b></para>
    /// </summary>
    public static class ShardValues
    {
        public const int Common = 10;
        public const int Rare = 25;
        public const int Epic = 100;
        public const int Legendary = 400;

        /// <summary>Shards yielded by one duplicate of this rarity.</summary>
        public static int ForRarity(Rarity rarity) => rarity switch
        {
            Rarity.COMMON => Common,
            Rarity.RARE => Rare,
            Rarity.EPIC => Epic,
            Rarity.LEGENDARY => Legendary,
            _ => 0,
        };

        /// <summary>
        /// The ledger item id shards accrue under (<c>IEntitlementLedger</c>, EF-072). A currency, so a
        /// single item with a signed quantity — never one row per shard.
        /// </summary>
        public const string LedgerItem = "shards";
    }

    /// <summary>
    /// A booster pack: a <b>set pairing</b> (the Core set plus exactly one expansion) and how many
    /// cards it yields (NOR-109 §1).
    ///
    /// <para><b>The pairing is the pack's identity, and the pool follows from it</b> — there is no
    /// hand-maintained card list to drift. A card added to <see cref="SetId.REKINDLING"/> is in the
    /// Rekindling pack the moment it is collectible, which is the only arrangement that survives a
    /// live-service content cadence.</para>
    ///
    /// <para><b>Core is in every pack and is never the expansion half.</b> A "Core pack" would be a
    /// different product with a different pool, so <see cref="SetId.CORE"/> is rejected as an
    /// <see cref="Expansion"/> rather than quietly producing a Core-only pack.</para>
    ///
    /// <para>This type is pure data. It resolves a pool from an <see cref="ICardDatabase"/> and knows
    /// nothing about currency, pricing or the store (NOR-107b), nor about who is opening it.</para>
    /// </summary>
    public sealed class BoosterPack
    {
        /// <summary>
        /// Cards per pack. <b>5, locked by Sam on 2026-08-02 (NOR-109)</b> — the brief left it
        /// unspecified and named 5 as the default to assume; he confirmed it over 4 and 6. Every
        /// guarantee in <see cref="BoosterPackOpener"/> is expressed against <see cref="CardCount"/>,
        /// not against a literal 5, so changing it is a one-line change here.
        /// </summary>
        public const int DefaultCardCount = 5;

        /// <summary>Stable id — the store SKU and the pity-counter key. <c>pack.emberfall</c>-shaped.</summary>
        public string Id { get; }

        /// <summary>The expansion half of the pairing. Never <see cref="SetId.CORE"/>.</summary>
        public SetId Expansion { get; }

        public int CardCount { get; }

        public PackRarityOdds Odds { get; }

        public BoosterPack(SetId expansion, int cardCount = DefaultCardCount, PackRarityOdds? odds = null)
        {
            if (expansion == SetId.CORE)
                throw new ArgumentException(
                    "A pack is the Core set PLUS one expansion; CORE is not itself an expansion half.",
                    nameof(expansion));
            if (cardCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(cardCount), "A pack yields at least one card.");

            Expansion = expansion;
            CardCount = cardCount;
            Odds = odds ?? PackRarityOdds.Default;
            Id = "pack." + expansion.ToString().ToLowerInvariant();
        }

        /// <summary>Every pack that can be built from the declared sets, in <see cref="SetId"/> order.</summary>
        public static IReadOnlyList<BoosterPack> All()
        {
            var packs = new List<BoosterPack>();
            foreach (SetId set in (SetId[])Enum.GetValues(typeof(SetId)))
                if (set != SetId.CORE) packs.Add(new BoosterPack(set));
            return packs;
        }

        /// <summary>Is this set one of the pack's two halves?</summary>
        public bool Contains(SetId setId) => setId == SetId.CORE || setId == Expansion;

        /// <summary>
        /// The pack's card pool: every collectible card in either half, <b>sorted by id</b>.
        ///
        /// <para>The sort is load-bearing, not tidiness. <see cref="ICardDatabase.AllCollectible"/>
        /// makes no ordering promise (it is a dictionary walk on both implementations), and the opener
        /// indexes into this list with a seeded draw — an unsorted pool would make the same
        /// <c>(seed, packIndex)</c> yield different cards on different machines, which is precisely the
        /// desync class the determinism contract exists to prevent.</para>
        /// </summary>
        public IReadOnlyList<CardDefinition> Pool(ICardDatabase db)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));

            var pool = new List<CardDefinition>();
            foreach (var card in db.AllCollectible())
                if (card != null && Contains(card.SetId)) pool.Add(card);

            pool.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return pool;
        }
    }
}
