using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// The pity counters for one player against one pack (NOR-109 §4). <b>Per pack</b> — packs of
    /// different expansions never share a counter, so grinding Rekindling cannot mature an Emberfall
    /// legendary.
    ///
    /// <para><b>Three integers, and the fourth fact is derived.</b> "Has this player ever pulled a
    /// legendary from this set?" is needed by the first-window rule, and it is <i>not</i> stored:
    /// <see cref="PacksSinceLegendary"/> and <see cref="PacksOpened"/> advance in lockstep until the
    /// first legendary lands and then diverge permanently, so <c>PacksSinceLegendary &lt; PacksOpened</c>
    /// answers it exactly. A stored bool would be a second writer for a fact the counters already carry,
    /// and the two would eventually disagree.</para>
    ///
    /// <para><b>Immutable.</b> <see cref="AfterPack"/> returns a new state rather than mutating, because
    /// the caller must be able to persist the <i>before</i> and <i>after</i> of an open and to replay an
    /// open without having already consumed it — see <c>BoosterPackGrantService</c> for why that
    /// matters when a grant is idempotent but a counter bump is not.</para>
    /// </summary>
    public readonly struct PackPityState
    {
        /// <summary>Guarantee an Epic on the Nth consecutive pack without one (NOR-109 §4).</summary>
        public const int EpicPityPacks = 10;

        /// <summary>A player's first Legendary from a set arrives no later than this many packs in.</summary>
        public const int LegendaryFirstWindowPacks = 10;

        /// <summary>After the first window, guarantee a Legendary on the Nth consecutive pack without one.</summary>
        public const int LegendaryPityPacks = 40;

        /// <summary>Packs of this pairing this player has ever opened.</summary>
        public readonly int PacksOpened;

        /// <summary>Packs opened since the last one that contained an Epic (0 immediately after one).</summary>
        public readonly int PacksSinceEpic;

        /// <summary>Packs opened since the last one that contained a Legendary (0 immediately after one).</summary>
        public readonly int PacksSinceLegendary;

        public PackPityState(int packsOpened, int packsSinceEpic, int packsSinceLegendary)
        {
            if (packsOpened < 0) throw new ArgumentOutOfRangeException(nameof(packsOpened));
            if (packsSinceEpic < 0) throw new ArgumentOutOfRangeException(nameof(packsSinceEpic));
            if (packsSinceLegendary < 0) throw new ArgumentOutOfRangeException(nameof(packsSinceLegendary));

            // A "packs since X" that exceeds the packs ever opened is not a recoverable rounding
            // difference — it is a corrupt or hand-edited row, and accepting it would silently disarm
            // the first-window derivation below.
            if (packsSinceEpic > packsOpened)
                throw new ArgumentOutOfRangeException(nameof(packsSinceEpic),
                    $"PacksSinceEpic ({packsSinceEpic}) cannot exceed PacksOpened ({packsOpened}).");
            if (packsSinceLegendary > packsOpened)
                throw new ArgumentOutOfRangeException(nameof(packsSinceLegendary),
                    $"PacksSinceLegendary ({packsSinceLegendary}) cannot exceed PacksOpened ({packsOpened}).");

            PacksOpened = packsOpened;
            PacksSinceEpic = packsSinceEpic;
            PacksSinceLegendary = packsSinceLegendary;
        }

        /// <summary>A player who has never opened a pack of this pairing. Also <c>default</c>.</summary>
        public static PackPityState Fresh => new PackPityState(0, 0, 0);

        /// <summary>
        /// Has a Legendary ever come out of this pairing for this player? See the type remarks: the two
        /// counters are equal until the first legendary and strictly ordered forever after, so this is
        /// exact rather than an approximation.
        /// </summary>
        public bool HasEverPulledLegendary => PacksSinceLegendary < PacksOpened;

        /// <summary>Is the <i>next</i> pack the one that must contain an Epic?</summary>
        public bool EpicDueNextPack => PacksSinceEpic + 1 >= EpicPityPacks;

        /// <summary>
        /// Is the <i>next</i> pack the one that must contain a Legendary?
        ///
        /// <para>Two regimes, and which one applies is decided by <see cref="HasEverPulledLegendary"/>,
        /// not by the pack count: before a player's first legendary the clock is the
        /// <see cref="LegendaryFirstWindowPacks"/> window measured from their very first pack; after it,
        /// the clock is <see cref="LegendaryPityPacks"/> measured from the last one. <c>&gt;=</c> rather
        /// than <c>==</c> so a state restored from an older or hand-edited row still fires rather than
        /// sailing past the boundary and never triggering again.</para>
        /// </summary>
        public bool LegendaryDueNextPack => HasEverPulledLegendary
            ? PacksSinceLegendary + 1 >= LegendaryPityPacks
            : PacksOpened + 1 >= LegendaryFirstWindowPacks;

        /// <summary>
        /// The state after opening one pack that contained the given rarities.
        ///
        /// <para><b>A duplicate that converted to shards still counts.</b> The counters key off the
        /// rarity the slot <i>rolled</i>, not off whether the player kept a card — the player saw an
        /// Epic, so the Epic clock resets. Anything else would make dupe protection quietly extend every
        /// pity window for the players who already own the most.</para>
        ///
        /// <para><b>The two clocks are independent.</b> A Legendary does not reset the Epic clock even
        /// though it is "better": §4 reads "guaranteed Epic … if none seen in that window", and coupling
        /// them would mean a lucky legendary silently pushes the epic guarantee ten packs further out.</para>
        /// </summary>
        public PackPityState AfterPack(IReadOnlyList<Rarity> slotRarities)
        {
            if (slotRarities == null) throw new ArgumentNullException(nameof(slotRarities));

            bool epic = false, legendary = false;
            for (int i = 0; i < slotRarities.Count; i++)
            {
                if (slotRarities[i] == Rarity.EPIC) epic = true;
                else if (slotRarities[i] == Rarity.LEGENDARY) legendary = true;
            }

            return new PackPityState(
                PacksOpened + 1,
                epic ? 0 : PacksSinceEpic + 1,
                legendary ? 0 : PacksSinceLegendary + 1);
        }

        public override string ToString() =>
            $"opened={PacksOpened} sinceEpic={PacksSinceEpic} sinceLegendary={PacksSinceLegendary}" +
            (HasEverPulledLegendary ? "" : " (no legendary yet)");
    }
}
