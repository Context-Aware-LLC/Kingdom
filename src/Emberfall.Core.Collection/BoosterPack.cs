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
    /// (ADR-0003, enforced by <c>Emberfall.Tools.DeterminismGate</c> check 1 — CI Stage 4; it was
    /// <c>BannedApiTests.No_Float_Or_Double_Field_In_Core_Types</c> until 2026-08-18). A <c>float</c> field
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
        /// The shipped table (NOR-109 §3, SOURCE OF TRUTH — do not retune here): Common 60%,
        /// Rare 27%, Epic 10%, Legendary 3%.
        ///
        /// <para>Retuned by Sam 2026-10-10 from 640/265/80/15 to round numbers: Epic 8% -> 10%,
        /// Legendary 1.5% -> 3%, Rare 26.5% -> 27%, the 4pp coming off Common. The retune before it
        /// (Sam, 2026-08-20, from the brief's 715/230/45/10) is the one that opened Epic: at 4.5% a
        /// player broadened too slowly across a 27-card epic tier while epic pity kept forcing slots
        /// that landed on the handful they already held.</para>
        ///
        /// <para><b>The starter pack no longer beats this on Epic or Legendary.</b>
        /// <see cref="OnboardingGrant.StarterOdds"/> is 8.5% / 2.5% and was not moved with it.</para>
        /// </summary>
        public static readonly PackRarityOdds Default = new PackRarityOdds(600, 270, 100, 30);

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
    /// What it costs in shards to craft one card of this rarity (NOR-379, GH#156).
    ///
    /// <para><b>PROVISIONAL — Sam's to retune (2026-08-11).</b> These are a first setting to play
    /// with; they do <em>not</em> carry the LOCKED status that <see cref="ShardValues"/> carries.
    /// Changing them is an economy decision — Sam's call and nobody else's — but they are not
    /// frozen by ruling the way the payout table is. They are all in one place so a retune is a
    /// four-line change with no algorithm edit.</para>
    /// </summary>
    public static class ShardCosts
    {
        public const int Common = 50;
        public const int Rare = 150;
        public const int Epic = 450;
        public const int Legendary = 1000;

        /// <summary>Shards required to craft one card of this rarity.</summary>
        public static int ForRarity(Rarity rarity) => rarity switch
        {
            Rarity.COMMON => Common,
            Rarity.RARE => Rare,
            Rarity.EPIC => Epic,
            Rarity.LEGENDARY => Legendary,
            _ => 0,
        };
    }

    /// <summary>
    /// A booster pack: one set, and how many cards it yields (NOR-109 §1).
    ///
    /// <para><b>The set is the pack's identity, and the pool follows from it</b> — there is no
    /// hand-maintained card list to drift. A card added to <see cref="SetId.REKINDLING"/> is in the
    /// Rekindling pack the moment it is collectible, which is the only arrangement that survives a
    /// live-service content cadence.</para>
    ///
    /// <para><b>Core is in no pack</b> (Sam, 2026-10-10). <see cref="SetId.CORE"/> holds what is shaped
    /// like a card without being a collectible one — hero powers, the coin — so it contributes nothing
    /// to a pool and is rejected as a pack's <see cref="Expansion"/>. Until that date a pack was
    /// modelled as "Core plus one expansion"; the pool it produced was the same, because Core has
    /// never held a collectible card.</para>
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

        /// <summary>
        /// The sku suffix separating the GRANTED onboarding pack from the SALEABLE retail pack.
        ///
        /// <para><b>Why the id carries it and not just the odds.</b> <see cref="Id"/> is three keys at
        /// once: the ledger's entitlement item, the pity-counter key and the pack-seed key. The server
        /// reserves and debits against it while taking the pack's <i>shape</i> — <see cref="CardCount"/>
        /// and <see cref="Odds"/> — from whoever calls the opener. So a shared id would make a granted
        /// pack and a bought pack the same balance, and would let a retail entitlement be paid out at
        /// the boosted starter odds. Two ids make both impossible by construction rather than by
        /// discipline at the call site.</para>
        ///
        /// <para>Two ids also means two pity clocks, which is the right answer and not a side effect: a
        /// one-time boosted grant should not spend down the rarity clock a player is accruing on the
        /// packs they buy.</para>
        /// </summary>
        public const string StarterSku = "starter";

        /// <summary>
        /// Stable id — the store SKU, the ledger entitlement item, the pity-counter key and the
        /// pack-seed key. <c>pack.emberfall</c> for a retail pack, <c>pack.emberfall.starter</c> for
        /// the granted onboarding pack. See <see cref="StarterSku"/> for why the two must differ.
        /// </summary>
        public string Id { get; }

        /// <summary>The set this pack draws from. Never <see cref="SetId.CORE"/>.</summary>
        public SetId Expansion { get; }

        public int CardCount { get; }

        public PackRarityOdds Odds { get; }

        /// <param name="sku">
        /// Optional suffix distinguishing a non-retail product built from the same set — today only
        /// <see cref="StarterSku"/>. Null or empty yields the plain retail id, so every existing call
        /// site keeps the id it already had.
        /// </param>
        public BoosterPack(SetId expansion, int cardCount = DefaultCardCount, PackRarityOdds? odds = null,
                           string? sku = null)
        {
            if (expansion == SetId.CORE)
                throw new ArgumentException(
                    "CORE holds no collectible cards (hero powers, the coin); it is not a set a pack can draw from.",
                    nameof(expansion));
            if (cardCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(cardCount), "A pack yields at least one card.");

            Expansion = expansion;
            CardCount = cardCount;
            Odds = odds ?? PackRarityOdds.Default;
            Id = "pack." + expansion.ToString().ToLowerInvariant();
            if (!string.IsNullOrEmpty(sku)) Id += "." + sku;
        }

        /// <summary>Every pack that can be built from the declared sets, in <see cref="SetId"/> order.</summary>
        public static IReadOnlyList<BoosterPack> All()
        {
            var packs = new List<BoosterPack>();
            foreach (SetId set in (SetId[])Enum.GetValues(typeof(SetId)))
                if (set != SetId.CORE) packs.Add(new BoosterPack(set));
            return packs;
        }

        /// <summary>Is this the set the pack draws from?</summary>
        public bool Contains(SetId setId) => setId == Expansion;

        /// <summary>
        /// The pack's card pool: every collectible card in its set, <b>sorted by id</b>.
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

            pool.Sort((a, b) => a.Id.CompareTo(b.Id));
            return pool;
        }
    }
}
