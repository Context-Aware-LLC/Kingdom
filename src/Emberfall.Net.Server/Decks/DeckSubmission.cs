using System.Collections.Generic;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Server.Decks
{
    /// <summary>One seat's resolved match contribution: a hero class and a pre-shuffle-ordered
    /// decklist. Produced either from an accepted <see cref="SubmitDeckMsg"/> or, for a seat that
    /// submits nothing, from the prebuilt fallback (<see cref="FromFallback"/>).</summary>
    public readonly struct SeatDeck
    {
        public readonly CardClass HeroClass;
        public readonly IReadOnlyList<int> Deck;

        /// <summary>The pairing's second class, which the opposite seat is told at match start.
        /// <c>NEUTRAL</c> for the prebuilt fallback, which is not a paired deck.</summary>
        public readonly CardClass SecondaryClass;

        public SeatDeck(CardClass heroClass, IReadOnlyList<int> deck,
                        CardClass secondaryClass = CardClass.NEUTRAL)
        {
            HeroClass = heroClass;
            Deck = deck;
            SecondaryClass = secondaryClass;
        }

        /// <summary>The prebuilt <see cref="MatchSetup"/>'s seat, decomposed so a non-submitting seat
        /// keeps exactly today's fixed deck (EF-071 is additive — a plain <c>/ws</c> client is dealt
        /// the same prebuilt deck it is dealt now).</summary>
        public static SeatDeck FromFallback(MatchSetup prebuilt, int seat) => seat == 0
            ? new SeatDeck(prebuilt.P0Class, prebuilt.P0Deck)
            : new SeatDeck(prebuilt.P1Class, prebuilt.P1Deck);
    }

    /// <summary>Accepted (with the resolved <see cref="Seat"/>) or rejected (with a short human
    /// <see cref="Reason"/>, verbatim from <see cref="DeckValidator"/> where a rule fired).</summary>
    public readonly struct DeckSubmissionResult
    {
        public readonly bool Accepted;
        public readonly SeatDeck Seat;
        public readonly string Reason;

        private DeckSubmissionResult(bool accepted, SeatDeck seat, string reason)
        {
            Accepted = accepted;
            Seat = seat;
            Reason = reason;
        }

        public static DeckSubmissionResult Ok(SeatDeck seat) => new DeckSubmissionResult(true, seat, "");
        public static DeckSubmissionResult Rejected(string reason) => new DeckSubmissionResult(false, default, reason);
    }

    /// <summary>
    /// EF-071 — server-side revalidation of a client-submitted deck (the "never trust the client" half
    /// of <c>IDeckProvider</c>'s contract; state-sync §6.4).
    ///
    /// <para><b>This is the closure for VULN-001, trigger "Client-supplied deck lists"</b>
    /// (security/known-vulnerabilities/VULN-001 §4). The message carries client-authored card-identity
    /// strings; NONE is trusted. We rebuild a <see cref="DeckList"/> + <see cref="Character"/> from the
    /// wire and run the SAME <see cref="QueueEligibility.Check"/> / <see cref="DeckValidator.Validate"/>
    /// the deckbuilder uses to gate Save. That predicate resolves every id through
    /// <see cref="ICardDatabase.TryGet"/> — never <c>Get</c> — so an unknown id is an L4 rejection, not
    /// an exception and not a mutation (§4 rule 1). A rejected deck never reaches <c>BeginMatch</c>, so
    /// no unresolvable id can enter authoritative state and detonate on the opponent's client.</para>
    /// </summary>
    public static class DeckSubmission
    {
        /// <summary>Validate a submission against <paramref name="db"/> and, on success, return the seat's
        /// hero class + ordered deck. Pure — no I/O, no ambient state, never throws on wire input.
        ///
        /// <para><paramref name="owned"/> is the submitting account's ownership source for L6 (NOR-313).
        /// Null — the default, and what a caller with no account context passes — leaves L6 unenforced
        /// exactly as before; a real source constrains the submission to cards the account holds. The
        /// caller resolves it through <c>AccountOwnedCounts.For</c>, which keeps a superuser account
        /// unconstrained by construction.</para></summary>
        public static DeckSubmissionResult Validate(SubmitDeckMsg msg, ICardDatabase db,
                                                    IOwnedCardCounts? owned = null)
        {
            if (msg is null) return DeckSubmissionResult.Rejected("No deck submitted.");

            // The pairing check that stood here is now DeckValidator L5 (ADR-0009). It ran before L4
            // for the same reason L5 does inside the validator — the class-membership message is
            // phrased in terms of the pairing, so a malformed one has to be caught first — and it is
            // one predicate now rather than the same rule spelled twice, which is the point.

            // Canonicalize the UNTRUSTED wire list into the one-entry-per-id shape DeckValidator assumes.
            // DeckValidator checks the copy cap per Entry, so two entries of the same id (each within the
            // cap) would otherwise sum past it undetected — the deckbuilder never emits that, but the wire
            // is not the deckbuilder. Merging here closes that bypass without touching the shared predicate.
            var summed = new Dictionary<int, int>();
            var order = new List<int>();
            foreach (var line in msg.Cards)
            {
                if (line.Count < 1)
                    return DeckSubmissionResult.Rejected($"Non-positive copy count for card {line.CardId}.");
                if (!summed.TryGetValue(line.CardId, out int have)) order.Add(line.CardId);
                summed[line.CardId] = have + line.Count;
            }

            // ONE object where there were two. This used to build a DeckList and, beside it, a
            // throwaway Character carrying the same two enums, purely so the validator's signature
            // lined up — which meant the pairing being validated and the pairing the deck would be
            // played with were two things that happened to agree (ADR-0009).
            var deck = new DeckList(msg.PrimaryClass, msg.SecondaryClass) { Id = "submitted", Name = "Submitted" };
            foreach (var id in order) deck.Entries.Add(new DeckEntry(id, summed[id]));

            var check = QueueEligibility.Check(deck, db, owned);
            if (!check.IsEligible) return DeckSubmissionResult.Rejected(check.Reason);

            // Expand to the pre-shuffle ordered list MatchSetup wants (BeginMatch shuffles with the seed,
            // so this order feeds determinism, not fairness). The hero — and its hero power — is the
            // PRIMARY class (dual-class ruling: exactly one hero power, from the primary).
            var ordered = new List<int>(DeckValidator.DeckSize);
            foreach (var id in order)
                for (int i = 0; i < summed[id]; i++) ordered.Add(id);

            return DeckSubmissionResult.Ok(new SeatDeck(msg.PrimaryClass, ordered, msg.SecondaryClass));
        }
    }
}
