using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>Raised when a character's pool cannot supply a legal 30-card deck. Never a partial deck.</summary>
    public sealed class DeckGenerationException : Exception
    {
        public DeckGenerationException(string message) : base(message) { }
    }

    /// <summary>
    /// Builds the starter deck a brand-new <see cref="Character"/> opens on (Sam, 2026-07-26).
    ///
    /// <para><b>Explicitly a placeholder</b> — it stands in until hand-authored default decks are
    /// worth the time. It is not a deckbuilding AI and does not try to be: there is no synergy model,
    /// no removal count, no tribal awareness. It draws a legal, curve-weighted 30 so that a new
    /// character is immediately playable instead of empty.</para>
    ///
    /// <para><b>Curve weighting is the one piece of judgement in here, and it is load-bearing.</b> A
    /// uniform draw over the pool produces a deck whose average cost is the pool's average cost —
    /// around five — which is unplayable and is the first deck anyone sees on launch. Cheap cards are
    /// therefore weighted up (<see cref="Weight"/>). Integer arithmetic only: this assembly is
    /// float-free like the rest of core, and a weighted draw is where a float would sneak in.</para>
    ///
    /// <para><b>Deterministic.</b> Candidates are ordered by id before any draw, because
    /// <see cref="ICardDatabase.AllCollectible"/> makes no ordering promise — a dictionary's
    /// enumeration order is not a contract, and a generator seeded identically must produce an
    /// identical deck or the tests are theatre.</para>
    /// </summary>
    public static class DefaultDeckGenerator
    {
        /// <summary>
        /// Draw weight for a card of the given cost. Cheap is likelier; the floor of 1 keeps every
        /// card reachable, so a pool that needs its expensive cards to reach 30 can still get there.
        /// </summary>
        public static int Weight(int cost) => cost >= 8 ? 1 : 9 - cost;

        /// <summary>
        /// Can this character's pool supply 30 legal copies at all? Lets the UI refuse a pairing up
        /// front rather than catching an exception from <see cref="Generate"/> — the thin-pool case is
        /// real (EMBERWARD has 9 collectible rows, 17 copies, and only clears 30 because every
        /// character also gets NEUTRAL and a second class).
        /// </summary>
        public static bool CanGenerate(Character character, ICardDatabase db)
            => character != null && db != null && Capacity(Candidates(character, db)) >= DeckValidator.DeckSize;

        /// <summary>
        /// A legal 30-card deck for <paramref name="character"/>, drawn from
        /// <c>NEUTRAL ∪ Primary ∪ Secondary</c> and weighted toward cheap cards.
        ///
        /// <para>The result is run through <see cref="DeckValidator.Validate"/> before it is returned.
        /// That is not defensive padding — "must pass L1–L5 as generated" is the requirement, and a
        /// requirement checked by the code that claims it is the only kind that survives a change to
        /// the weighting.</para>
        /// </summary>
        /// <param name="deckId">Caller-supplied so this assembly mints no ambient identity.</param>
        /// <param name="rng">Seeded by the caller; the same seed yields the same deck.</param>
        /// <exception cref="DeckGenerationException">The pool cannot supply 30 legal copies.</exception>
        public static DeckList Generate(Character character, ICardDatabase db, DeterministicRandom rng,
                                        string deckId, string? deckName = null)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            if (string.IsNullOrWhiteSpace(deckId))
                throw new ArgumentException("Generated deck needs an id from the caller.", nameof(deckId));

            var candidates = Candidates(character, db);
            int capacity = Capacity(candidates);
            if (capacity < DeckValidator.DeckSize)
                throw new DeckGenerationException(
                    $"{character.PrimaryClass}/{character.SecondaryClass} can supply only {capacity} legal " +
                    $"copies from {candidates.Count} collectible cards — {DeckValidator.DeckSize} are needed. " +
                    "This is a card-pool shortfall, not a generator failure.");

            var counts = new int[candidates.Count];
            int total = 0;

            while (total < DeckValidator.DeckSize)
            {
                // Re-weigh every draw over only the cards with room left. Rebuilding the pool each
                // time is O(30 x pool) — a few thousand integer adds — and removes the bookkeeping
                // that an incrementally-maintained weight total would need to get exactly right.
                int weightSum = 0;
                for (int i = 0; i < candidates.Count; i++)
                    if (counts[i] < DeckValidator.MaxCopies(candidates[i].Rarity))
                        weightSum += Weight(candidates[i].Cost);

                // Unreachable while capacity >= 30, and cheap to assert rather than loop forever if
                // that ever stops being true.
                if (weightSum <= 0)
                    throw new DeckGenerationException(
                        $"Ran out of addable cards at {total}/{DeckValidator.DeckSize} for " +
                        $"{character.PrimaryClass}/{character.SecondaryClass}.");

                int roll = rng.NextInt(weightSum);
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (counts[i] >= DeckValidator.MaxCopies(candidates[i].Rarity)) continue;
                    roll -= Weight(candidates[i].Cost);
                    if (roll < 0)
                    {
                        counts[i]++;
                        total++;
                        break;
                    }
                }
            }

            var deck = new DeckList
            {
                Id = deckId,
                CharacterId = character.Id,
                Name = string.IsNullOrWhiteSpace(deckName) ? character.Name + " Starter" : deckName!,
            };
            // Emitted in candidate order, not draw order, so the same seed writes a byte-identical file.
            for (int i = 0; i < candidates.Count; i++)
                if (counts[i] > 0) deck.Entries.Add(new DeckEntry(candidates[i].Id, counts[i]));

            var check = DeckValidator.Validate(deck, character, db);
            if (!check.IsValid)
            {
                var reasons = new List<string>();
                foreach (var v in check.Violations) reasons.Add($"{v.Rule}: {v.Message}");
                throw new DeckGenerationException(
                    "Generated deck failed L1-L5, which is a generator bug: " + string.Join("; ", reasons));
            }

            return deck;
        }

        /// <summary>Collectible cards legal for this character, ordered by id so draws are reproducible.</summary>
        private static List<CardDefinition> Candidates(Character character, ICardDatabase db)
        {
            var list = new List<CardDefinition>();
            if (character == null || db == null) return list;
            foreach (var def in db.AllCollectible())
                if (character.AllowsClass(def.Class)) list.Add(def);
            list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return list;
        }

        /// <summary>Total copies the pool can legally supply (L2/L3 caps summed).</summary>
        private static int Capacity(List<CardDefinition> candidates)
        {
            int n = 0;
            foreach (var def in candidates) n += DeckValidator.MaxCopies(def.Rarity);
            return n;
        }
    }
}
