using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// The queue gate's answer for one deck: eligible, or a <b>draft</b> with a reason.
    ///
    /// <para>"Draft" is deliberately the word the spec and the UI use
    /// (ui/collection-deckbuilder.md §1) — a draft is a legitimate saved state, not a failure, and
    /// the only thing it is denied is the queue.</para>
    /// </summary>
    public readonly struct QueueCheck
    {
        /// <summary>The full L1–L5 result, for a caller that wants every violation rather than the first.</summary>
        public readonly DeckValidation Validation;

        public QueueCheck(DeckValidation validation) { Validation = validation; }

        /// <summary>True iff this deck may be submitted at deck pick and accepted at match-join.</summary>
        public bool IsEligible => Validation.IsValid;

        /// <summary>The complement of <see cref="IsEligible"/>, named as the UI names it.</summary>
        public bool IsDraft => !Validation.IsValid;

        /// <summary>
        /// The first violation's message, verbatim, or <c>""</c> when eligible — the deck-pick row's
        /// inline reason and the decklist legality box both show exactly this (§2.7, §2.11). The copy
        /// lives in <see cref="DeckValidator"/>; nothing restates it.
        /// </summary>
        public string Reason =>
            Validation.Violations != null && Validation.Violations.Count > 0
                ? Validation.Violations[0].Message
                : "";
    }

    /// <summary>
    /// <b>The</b> predicate that gates queueing. Legality gates queue-eligibility, not saving
    /// (ui/collection-deckbuilder.md §1) — <see cref="IDeckStore.Save"/> takes any deck, and this is
    /// the single place that decides whether a saved deck may be played.
    ///
    /// <para><b>Why a name of its own rather than <c>Validate().IsValid</c> at each call site.</b>
    /// Two call sites have to agree forever: the deck-pick chooser (which greys the row) and
    /// match-join server-side (which rejects the submission). Open-coded, they drift the first time
    /// the gate stops being exactly "L1–L5 pass" — and it will: L6 (ownership ≥ copies used) joins in
    /// M3, and it gates the queue without changing what a deck may contain while you build it. When
    /// that lands it lands here, once, and both call sites move with it.</para>
    ///
    /// <para>Deck submission is client-authored input; the server re-runs this, never trusting the
    /// client's answer (`security/known-vulnerabilities/VULN-001` §4, trigger "Client-supplied deck
    /// lists"). <see cref="DeckValidator.Validate"/> resolves every card through
    /// <c>ICardDatabase.TryGet</c>, so an unknown id is a violation, never an exception.</para>
    /// </summary>
    public static class QueueEligibility
    {
        /// <summary>The gate, with its reason. One evaluation serves both the badge and the copy.</summary>
        public static QueueCheck Check(DeckList? deck, Character? character, ICardDatabase db) =>
            new QueueCheck(DeckValidator.Validate(deck, character, db));

        /// <summary>
        /// The gate for a caller holding a roster rather than a resolved character — match-join, which
        /// receives a deck and looks its owner up. A <see cref="DeckList.CharacterId"/> that resolves
        /// to nothing yields the L5 violation, which is the orphaned-deck case reporting itself
        /// (§3): the deck is denied the queue and says why, and nothing is repaired.
        /// </summary>
        public static QueueCheck Check(DeckList? deck, IEnumerable<Character> roster, ICardDatabase db) =>
            Check(deck, CharacterQuery.ById(roster, deck?.CharacterId ?? ""), db);

        /// <summary>Bool-only convenience for a caller that has no use for the reason.</summary>
        public static bool IsEligible(DeckList? deck, Character? character, ICardDatabase db) =>
            Check(deck, character, db).IsEligible;

        /// <summary>Bool-only convenience for a caller that has no use for the reason.</summary>
        public static bool IsEligible(DeckList? deck, IEnumerable<Character> roster, ICardDatabase db) =>
            Check(deck, roster, db).IsEligible;

        /// <summary>True iff this deck is a draft — listed everywhere, selectable nowhere.</summary>
        public static bool IsDraft(DeckList? deck, Character? character, ICardDatabase db) =>
            Check(deck, character, db).IsDraft;

        /// <summary>True iff this deck is a draft — listed everywhere, selectable nowhere.</summary>
        public static bool IsDraft(DeckList? deck, IEnumerable<Character> roster, ICardDatabase db) =>
            Check(deck, roster, db).IsDraft;
    }
}
