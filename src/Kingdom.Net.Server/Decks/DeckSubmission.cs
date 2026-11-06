using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Accounts;

namespace Kingdom.Net.Server.Decks
{
    /// <summary>One seat's resolved match contribution: a hero class, a pre-shuffle-ordered
    /// decklist and the card back it plays under. Produced either from an accepted
    /// <see cref="SubmitDeckMsg"/> or, for a seat that submits nothing, from the prebuilt fallback
    /// (<see cref="FromFallback"/>).</summary>
    public readonly struct SeatDeck
    {
        public readonly CardClass HeroClass;
        public readonly IReadOnlyList<int> Deck;
        /// <summary>The back this seat's cards show both players (#421). Already judged: the default
        /// unless <c>MatchGate</c> found the back owned by the account it resolved. A bot's deck and
        /// the prebuilt fallback never carry one of their own.</summary>
        public readonly int CardBackId;

        public SeatDeck(CardClass heroClass, IReadOnlyList<int> deck,
                        int cardBackId = CardBackDefinitions.DefaultId)
        {
            HeroClass = heroClass;
            Deck = deck;
            CardBackId = cardBackId;
        }

        public SeatDeck WithCardBack(int cardBackId) => new SeatDeck(HeroClass, Deck, cardBackId);

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
        /// caller resolves it through <c>AccountOwnedCounts.ForAsync</c>, which gives a superuser the
        /// all-at-limit record: checked like anyone else, owning every pool card.</para></summary>
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

            // The back is NOT taken from the message here: this method has no account, and a
            // client-authored id is only ever a request. MatchGate judges it (ResolveCardBackAsync).
            return DeckSubmissionResult.Ok(new SeatDeck(msg.PrimaryClass, ordered));
        }

        /// <summary>
        /// Judge the client-authored <see cref="SubmitDeckMsg.CardBackId"/> against
        /// <paramref name="owned"/>, the account's card-back ownership as
        /// <see cref="CardBackOwnership.ReadAsync"/> returns it (known backs only), or null when it
        /// could not be read. <b>Never refuses:</b> anything but an owned back is the default, so a
        /// bad id costs the player their back and never their match. Pure.
        /// </summary>
        public static CardBackChoice ResolveCardBack(int requested, IReadOnlyDictionary<int, long>? owned)
        {
            if (requested == 0) return new CardBackChoice(CardBackDefinitions.DefaultId, CardBackVerdict.Unset);
            if (!CardBackDefinitions.TryGet(requested, out _))
                return new CardBackChoice(CardBackDefinitions.DefaultId, CardBackVerdict.Unknown);
            if (owned is null)
                return new CardBackChoice(CardBackDefinitions.DefaultId, CardBackVerdict.OwnershipUnavailable);
            return owned.TryGetValue(requested, out long count) && count > 0
                ? new CardBackChoice(requested, CardBackVerdict.Owned)
                : new CardBackChoice(CardBackDefinitions.DefaultId, CardBackVerdict.Unowned);
        }

        /// <summary>
        /// <see cref="ResolveCardBack"/> with the ownership read from <paramref name="ledger"/> for
        /// <paramref name="account"/>, the account <c>MatchGate</c> already resolved for the seat. A
        /// seat with no account (tokenless tooling) or no ledger, and a read that fails, are
        /// <see cref="CardBackVerdict.OwnershipUnavailable"/>: the default, never an exception.
        /// Superusers are judged like anyone else; they bypass card legality, not entitlements.
        /// </summary>
        public static async Task<CardBackChoice> ResolveCardBackAsync(int requested, Account? account,
            IEntitlementLedger? ledger, CancellationToken ct = default)
        {
            // Nothing to read for an unset or unknown back, and no account to read it for.
            if (requested == 0 || !CardBackDefinitions.TryGet(requested, out _)
                || account is null || ledger is null)
                return ResolveCardBack(requested, null);

            IReadOnlyDictionary<int, long>? owned;
            try
            {
                owned = await CardBackOwnership.ReadAsync(ledger, account, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                Console.Error.WriteLine("[gate] card-back ownership unreadable for player_id={0}: {1}",
                    account.PlayerId, ex.Message);
                owned = null;
            }
            return ResolveCardBack(requested, owned);
        }
    }

    /// <summary>Why a seat plays under the back it does (#421). Only <see cref="Owned"/> honours
    /// the request; every other verdict is <see cref="CardBackDefinitions.DefaultId"/>.</summary>
    public enum CardBackVerdict
    {
        /// <summary>The deck asked for none (0).</summary>
        Unset,
        Owned,
        /// <summary>A real back this account does not hold, or no longer holds.</summary>
        Unowned,
        /// <summary>Not in <see cref="CardBackDefinitions"/>.</summary>
        Unknown,
        /// <summary>No account, no ledger, or a ledger read that failed.</summary>
        OwnershipUnavailable,
    }

    public readonly struct CardBackChoice
    {
        public readonly int BackId;
        public readonly CardBackVerdict Verdict;

        public CardBackChoice(int backId, CardBackVerdict verdict)
        {
            BackId = backId;
            Verdict = verdict;
        }
    }
}
