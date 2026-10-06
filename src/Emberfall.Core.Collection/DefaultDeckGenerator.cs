using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>Raised when a pairing's pool cannot supply a legal 30-card deck. Never a partial deck.</summary>
    public sealed class DeckGenerationException : Exception
    {
        public DeckGenerationException(string message) : base(message) { }
    }

    /// <summary>
    /// Builds the starter deck a brand-new <see cref="DeckList"/> is generated as (Sam, 2026-07-26).
    ///
    /// <para><b>Explicitly a placeholder</b> — it stands in until hand-authored default decks are
    /// worth the time. It is not a deckbuilding AI and does not try to be: there is no synergy model,
    /// no removal count, no tribal awareness. It draws a legal, curve-weighted 30 so that a new deck
    /// is immediately playable instead of empty.</para>
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
    ///
    /// <para><b>It draws from what the player OWNS, not from the pool (2026-09-03).</b> Until this
    /// date it drew from <see cref="ICardDatabase.AllCollectible"/> unfiltered, and the deck it
    /// handed a brand-new player was therefore illegal for that player to queue with: the
    /// signup grant is every common and rare in the CORE+EMBERFALL pairing
    /// (<see cref="OnboardingGrant"/>), while the collectible pool is that plus all of REKINDLING
    /// plus every epic and legendary. Measured against the live card data, <b>31–46% of every
    /// pairing's draw weight was a card the account had never been granted</b>, so effectively
    /// every generated starter deck failed L6 — and it failed it nowhere the player could see it,
    /// because the generator validates L1–L5 only. The error arrived at the queue as a closed
    /// socket reading "deck rejected: You don't own Repair Drone."</para>
    ///
    /// <para><b>A null <paramref name="owned"/> still means "do not constrain"</b>, the same reading
    /// <see cref="IOwnedCardStore"/> and <see cref="DeckValidator"/> L6 give it — a test, a bot and
    /// a soak run all generate against no ownership record and must keep getting the whole pool.
    /// Every real player-facing call site passes one.</para>
    /// </summary>
    public static class DefaultDeckGenerator
    {
        /// <summary>
        /// Draw weight for a card of the given cost. Cheap is likelier; the floor of 1 keeps every
        /// card reachable, so a pool that needs its expensive cards to reach 30 can still get there.
        /// </summary>
        public static int Weight(int cost) => cost >= 8 ? 1 : 9 - cost;

        /// <summary>
        /// Can this pairing's pool supply 30 legal copies at all? Lets the UI refuse a pairing up
        /// front rather than catching an exception from <see cref="Generate"/> — the thin-pool case is
        /// real (EMBERWARD has 9 collectible rows, 17 copies, and only clears 30 because every
        /// pairing also gets NEUTRAL and a second class).
        /// </summary>
        public static bool CanGenerate(CardClass primary, CardClass secondary, ICardDatabase db,
                                       IOwnedCardCounts? owned = null, int? maxSecondaryCopies = null,
                                       bool includeNeutral = true)
            => db != null
               && Capacity(Candidates(primary, secondary, db, owned, maxSecondaryCopies == 0, !includeNeutral), owned)
                  >= DeckValidator.DeckSize;

        /// <summary>
        /// A legal 30-card deck for the given pairing, drawn from
        /// <c>NEUTRAL ∪ Primary ∪ Secondary</c> and weighted toward cheap cards.
        ///
        /// <para>The result is run through <see cref="DeckValidator.Validate"/> before it is returned.
        /// That is not defensive padding — "must pass L1–L5 as generated" is the requirement, and a
        /// requirement checked by the code that claims it is the only kind that survives a change to
        /// the weighting.</para>
        /// </summary>
        /// <param name="deckId">Caller-supplied so this assembly mints no ambient identity.</param>
        /// <param name="rng">Seeded by the caller; the same seed yields the same deck.</param>
        /// <param name="maxSecondaryCopies">
        /// A ceiling on total copies drawn from <paramref name="secondary"/> — <c>0</c> excludes it
        /// entirely, <c>null</c> (the default) leaves it unconstrained. The pairing is still recorded
        /// as <paramref name="primary"/>/<paramref name="secondary"/> regardless (L5 only requires the
        /// pairing be two distinct classes, not that both actually appear; L4 already permits an empty
        /// half). For a real player's starter deck this stays <c>null</c>: dual-class is the shipped
        /// format, and a starter should show both halves of the pairing. It exists for a caller
        /// measuring PRIMARY's own card power — NetSoak's matrix, whose deck otherwise came out
        /// majority the OPPONENT'S class (measured 2026-09-05, EMBERWARD vs GRAVEKEEP: 7 of 30 cards
        /// were the class the cell was named for). <c>0</c> was the fix that day; Sam widened it the
        /// same day to "a touch of secondary, capped at 5" once the class-pool number needed a second
        /// look — a completely excluded secondary was overcorrecting.
        /// </param>
        /// <exception cref="DeckGenerationException">The pool cannot supply 30 legal copies.</exception>
        /// <param name="includeNeutral">
        /// <c>false</c> drops NEUTRAL from the pool too, so the deck is the primary class alone
        /// (Sam, 2026-10-06, for NetSoak's `matrix --primary-only`). Never for a player's deck: a
        /// starter without neutrals is not the shipped format any more than one without a secondary.
        /// </param>
        public static DeckList Generate(CardClass primary, CardClass secondary, ICardDatabase db,
                                        DeterministicRandom rng, string deckId, string? deckName = null,
                                        IOwnedCardCounts? owned = null, int? maxSecondaryCopies = null,
                                        bool includeNeutral = true)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (rng == null) throw new ArgumentNullException(nameof(rng));
            if (string.IsNullOrWhiteSpace(deckId))
                throw new ArgumentException("Generated deck needs an id from the caller.", nameof(deckId));
            if (!DeckList.IsValidPairing(primary, secondary))
                throw new ArgumentException(
                    $"'{primary}/{secondary}' is not a valid pairing — two distinct real classes are " +
                    "required. Generating against it would produce a deck that fails L5 on its own face.",
                    nameof(primary));

            var candidates = Candidates(primary, secondary, db, owned, maxSecondaryCopies == 0, !includeNeutral);
            int capacity = Capacity(candidates, owned);
            if (capacity < DeckValidator.DeckSize)
                throw new DeckGenerationException(
                    $"{primary}/{secondary} can supply only {capacity} legal " +
                    $"copies from {candidates.Count} " +
                    (owned == null ? "collectible cards" : "cards this account owns") +
                    $" — {DeckValidator.DeckSize} are needed. " +
                    (owned == null
                        ? "This is a card-pool shortfall, not a generator failure."
                        : "This is an OWNERSHIP shortfall, not a card-pool one: the pairing may hold plenty " +
                          "of cards this account has never been granted. Offer a blank deck instead."));

            // The per-card ceiling, resolved once: the format cap (L2/L3) and, when ownership is known,
            // the copies actually held (L6). Hoisted out of the draw loop, which reads it up to 30
            // times per card and would otherwise probe the ownership source that many times.
            var caps = new int[candidates.Count];
            for (int i = 0; i < candidates.Count; i++) caps[i] = CapFor(candidates[i], owned);

            var counts = new int[candidates.Count];
            int total = 0;

            // A running budget across every secondary-class candidate, not a per-card cap — the
            // format cap already limits one card to 1-2 copies, but says nothing about how many
            // DIFFERENT secondary cards a deck can hold. maxSecondaryCopies == 0 is handled above by
            // excluding the class from Candidates entirely, so this only ever throttles a positive cap.
            int secondaryBudget = maxSecondaryCopies ?? int.MaxValue;
            int secondaryDrawn = 0;
            bool SecondaryExhausted(int i) => candidates[i].Class == secondary && secondaryDrawn >= secondaryBudget;

            while (total < DeckValidator.DeckSize)
            {
                // Re-weigh every draw over only the cards with room left. Rebuilding the pool each
                // time is O(30 x pool) — a few thousand integer adds — and removes the bookkeeping
                // that an incrementally-maintained weight total would need to get exactly right.
                int weightSum = 0;
                for (int i = 0; i < candidates.Count; i++)
                    if (counts[i] < caps[i] && !SecondaryExhausted(i))
                        weightSum += Weight(candidates[i].Cost);

                // Unreachable while capacity >= 30, and cheap to assert rather than loop forever if
                // that ever stops being true.
                if (weightSum <= 0)
                    throw new DeckGenerationException(
                        $"Ran out of addable cards at {total}/{DeckValidator.DeckSize} for " +
                        $"{primary}/{secondary}.");

                int roll = rng.NextInt(weightSum);
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (counts[i] >= caps[i] || SecondaryExhausted(i)) continue;
                    roll -= Weight(candidates[i].Cost);
                    if (roll < 0)
                    {
                        counts[i]++;
                        total++;
                        if (candidates[i].Class == secondary) secondaryDrawn++;
                        break;
                    }
                }
            }

            // The fallback name is the pairing itself. It used to be the character's name plus
            // " Starter", and there is no character to ask any more — every player-facing call site
            // passes a real name, so this is what a test or a tool gets rather than what a player sees.
            var deck = new DeckList(primary, secondary)
            {
                Id = deckId,
                Name = string.IsNullOrWhiteSpace(deckName) ? $"{primary}/{secondary} Starter" : deckName!,
            };
            // Emitted in candidate order, not draw order, so the same seed writes a byte-identical file.
            for (int i = 0; i < candidates.Count; i++)
                if (counts[i] > 0) deck.Entries.Add(new DeckEntry(candidates[i].Id, counts[i]));

            // Validated WITH ownership, so L6 is checked by the code that claims it — the same reason
            // the L1-L5 check was here already. A generated deck the player cannot queue with is the
            // defect this guards, and it has to surface at creation rather than at the socket.
            var check = DeckValidator.Validate(deck, db, owned);
            if (!check.IsValid)
            {
                var reasons = new List<string>();
                foreach (var v in check.Violations) reasons.Add($"{v.Rule}: {v.Message}");
                throw new DeckGenerationException(
                    "Generated deck failed validation, which is a generator bug: " + string.Join("; ", reasons));
            }

            return deck;
        }

        /// <summary>
        /// Collectible cards legal for this pairing, ordered by id so draws are reproducible — and,
        /// when <paramref name="owned"/> is given, only the ones the account holds a copy of. A card
        /// owned zero times is dropped rather than carried at cap 0, so it costs nothing in the draw
        /// loop and cannot be reached by a later edit that forgets to consult the cap.
        /// </summary>
        private static List<CardDefinition> Candidates(CardClass primary, CardClass secondary,
                                                       ICardDatabase db, IOwnedCardCounts? owned = null,
                                                       bool excludeSecondary = false, bool excludeNeutral = false)
        {
            var list = new List<CardDefinition>();
            if (db == null) return list;
            foreach (var def in db.AllCollectible())
            {
                if (excludeSecondary && def.Class == secondary) continue;
                if (excludeNeutral && def.Class == CardClass.NEUTRAL) continue;
                if (DeckList.AllowsClass(primary, secondary, def.Class) && CapFor(def, owned) > 0) list.Add(def);
            }
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }

        /// <summary>
        /// Copies of one card a deck may legally hold: the format cap (L2/L3), and no more than the
        /// copies owned (L6) once ownership is known. A negative count from a badly-behaved source is
        /// floored at 0 — the same reading <see cref="DeckValidator"/> gives it.
        /// </summary>
        private static int CapFor(CardDefinition def, IOwnedCardCounts? owned)
        {
            int max = DeckValidator.MaxCopies(def.Rarity);
            if (owned == null) return max;
            int have = owned.CopiesOwned(def.Id);
            if (have < 0) have = 0;
            return have < max ? have : max;
        }

        /// <summary>Total copies the candidates can legally supply (L2/L3 caps, and L6 when known).</summary>
        private static int Capacity(List<CardDefinition> candidates, IOwnedCardCounts? owned = null)
        {
            int n = 0;
            foreach (var def in candidates) n += CapFor(def, owned);
            return n;
        }
    }
}
