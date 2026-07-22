using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Telemetry
{
    /// <summary>
    /// One seat's `card_seen` accumulator (telemetry-spec §2). Lives for the length of a match and
    /// is read once, at match end, by <c>MatchSession</c>.
    ///
    /// <para><b>Everything here is OBSERVED from outside the rules core.</b> No core edit, no new
    /// event kind, no hook in <c>CommandProcessor</c> — the ledger is fed the hand after every
    /// accepted command and the def id of every accepted play. That matters because the core is
    /// hash-canonical (golden-replay.md §4.1): a telemetry field is not worth a fixture rebaseline,
    /// and a core that has to remember to tell an observer about a draw is a core that will one day
    /// forget.</para>
    ///
    /// <para><b>What "drawn" means here, precisely: the def id was in this seat's hand at some
    /// observed moment.</b> Observations happen after the opening deal and after every accepted
    /// command, and between two commands the hand cannot change — so this catches every draw with
    /// exactly one exception, and the exception is the honest answer anyway: a card drawn into a
    /// full hand is BURNED on the way in (overdraw) and never lands, so it is not counted as drawn.
    /// It was never a card the player could play, and counting it would put a card in
    /// win-when-drawn's denominator that never reached anybody's hand.</para>
    ///
    /// <para>A card mulliganed away DOES count as drawn — it was in hand and the player made a
    /// decision about it. <see cref="CardSeenRow.KeptInMulligan"/> is what separates the two, which
    /// is the whole reason both flags exist.</para>
    /// </summary>
    internal sealed class CardSeenLedger
    {
        private sealed class Entry
        {
            public bool InDeck;
            public bool Drawn;
            public int? FirstPlayedTurn;
        }

        private readonly Dictionary<string, Entry> _byDef = new Dictionary<string, Entry>();

        /// <summary>
        /// First-appearance order, kept alongside the dictionary. Rows come out deck-order-first and
        /// stable across runs — a set-ordered emission would make two byte-identical replays produce
        /// diffable telemetry, which is the one thing this repo's determinism work exists to prevent.
        /// </summary>
        private readonly List<string> _order = new List<string>();

        private Entry Get(string defId)
        {
            if (!_byDef.TryGetValue(defId, out var e))
            {
                e = new Entry();
                _byDef.Add(defId, e);
                _order.Add(defId);
            }
            return e;
        }

        /// <summary>The decklist — the `inDeck` inclusion-rate denominator. Called once, at Start.</summary>
        public void NoteDeck(IReadOnlyList<string> deck)
        {
            for (int i = 0; i < deck.Count; i++) Get(deck[i]).InDeck = true;
        }

        /// <summary>Every def id currently in hand is now, and stays, drawn.</summary>
        public void NoteHand(IReadOnlyList<CardInstance> hand)
        {
            for (int i = 0; i < hand.Count; i++) Get(hand[i].DefinitionId).Drawn = true;
        }

        /// <summary>
        /// An ACCEPTED play. First one wins: `firstPlayedTurn` is a first, so a second copy played
        /// on turn 9 must not overwrite the turn-2 answer.
        /// </summary>
        public void NotePlayed(string defId, int turnNumber)
        {
            var e = Get(defId);
            // A played card was necessarily in hand, but the play may be the first time we look at
            // it (a card drawn and played inside one command chain never sits in an observed hand).
            e.Drawn = true;
            e.FirstPlayedTurn ??= turnNumber;
        }

        /// <param name="mulliganKept">
        /// Def ids kept at mulligan, as captured pre-submit by <c>MatchSession</c> — the only moment
        /// the answer exists, since the core replaces discarded cards in place.
        /// </param>
        public IReadOnlyList<CardSeenRow> Rows(
            AccountId account, CardClass @class, bool matchWon, IReadOnlyList<string> mulliganKept)
        {
            var kept = new HashSet<string>(mulliganKept);
            var rows = new List<CardSeenRow>(_order.Count);
            for (int i = 0; i < _order.Count; i++)
            {
                string defId = _order[i];
                var e = _byDef[defId];
                rows.Add(new CardSeenRow(
                    account, @class, matchWon, defId,
                    e.InDeck, e.Drawn, kept.Contains(defId), e.FirstPlayedTurn));
            }
            return rows;
        }
    }
}
