using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>
    /// Which card ids print as a given name — the one lookup <see cref="CardTextParser"/> cannot do
    /// without the card pool.
    ///
    /// <para><b>Why a parser needs it at all.</b> Every other thing a sentence names is drawn from a
    /// closed vocabulary the parser can enumerate on the spot: selectors, selections, keywords,
    /// numbers. A card id is not. "Summon a 1/1 Wisp." can only become a card NUMBER if
    /// something knows that row exists, and <see cref="CardDefinition"/> alone does not — a card
    /// carries its own id, never the pool's.</para>
    ///
    /// <para><b>It keys on the PRINTED name, not on <see cref="CardDefinition.Name"/>.</b> Those are
    /// different strings today: <c>token_revenant_5_5_bulwark</c> is named "Revenant" and prints as
    /// "5/5 Revenant", because the renderer title-cases the slug rather than reading the name field.
    /// Keying on what is printed is what makes this an inverse of the renderer instead of a second
    /// opinion about it — see <see cref="CardTextGenerator.DescribeCardName"/>, which carries the
    /// note on why that mismatch is a defect nobody is fixing in passing.</para>
    ///
    /// <para><b>Ambiguity is held, never resolved.</b> A name maps to a LIST. The transform is
    /// deliberately lossy — a trailing word in an id is a disambiguator that is never printed, which
    /// is exactly what <c>token_revenant_5_5_bulwark</c> is — so two rows CAN print alike. When they
    /// do, the parser refuses that card rather than taking the first, which is the same bargain the
    /// whole parser runs on: a card nobody can derive beats a card that quietly summons the wrong
    /// token. No global uniqueness rule is asserted here, because a collision between two rows
    /// neither sentence names is nobody's problem until a sentence names one.</para>
    /// </summary>
    public sealed class CardNameIndex : ICardDatabase
    {
        private static readonly IReadOnlyList<int> None = Array.Empty<int>();

        private readonly Dictionary<string, List<int>> _byPrintedName =
            new Dictionary<string, List<int>>(StringComparer.Ordinal);

        // Also the POOL the renderer looks a named card up in. One object rather than two, because
        // a printed name and the row it was printed from disagreeing is the whole bug class here.
        private readonly Dictionary<int, CardDefinition> _byId =
            new Dictionary<int, CardDefinition>();

        /// <summary>An index over no cards. What a caller with no pool to offer passes, and what
        /// keeps every card-id sentence refused rather than guessed.</summary>
        public static readonly CardNameIndex Empty = new CardNameIndex(Array.Empty<CardDefinition>());

        public CardNameIndex(IEnumerable<CardDefinition> cards)
        {
            if (cards == null) throw new ArgumentNullException(nameof(cards));

            foreach (CardDefinition card in cards)
            {
                if (card == null) continue;

                string printed;
                // An id the renderer cannot speak is an id no sentence can name, so it indexes
                // nothing. Throwing here would make the pool's worst row the parser's problem.
                try { printed = CardTextGenerator.DescribeCardName(card); }
                catch (Exception) { continue; }

                _byId[card.Id] = card;

                if (!_byPrintedName.TryGetValue(printed, out List<int> ids))
                    _byPrintedName[printed] = ids = new List<int>(1);
                if (!ids.Contains(card.Id)) ids.Add(card.Id);
            }
        }

        /// <summary>Every id that prints as this name — empty when none does, and more than one when
        /// the printed form cannot tell them apart.</summary>
        public IReadOnlyList<int> Ids(string printedName)
            => printedName != null && _byPrintedName.TryGetValue(printedName, out List<int> ids)
                ? ids
                : None;

        /// <summary>Every id in the index, for a caller that must propose them all and let the
        /// re-render decide which one a sentence meant.</summary>
        public IEnumerable<int> AllIds()
        {
            foreach (KeyValuePair<string, List<int>> entry in _byPrintedName)
                foreach (int id in entry.Value)
                    yield return id;
        }

        /// <summary>Resolves a row by id, for the renderer speaking a card another card names.</summary>
        public bool TryGet(int cardId, out CardDefinition def)
            => _byId.TryGetValue(cardId, out def!);

        public CardDefinition Get(int cardId) => _byId[cardId];

        /// <summary>Every row eligible for a live card pool — <see cref="CardDefinition.PoolEligible"/>,
        /// never a re-derived predicate.</summary>
        public IReadOnlyCollection<CardDefinition> AllCollectible()
        {
            var pool = new List<CardDefinition>();
            foreach (CardDefinition card in _byId.Values)
                if (card.PoolEligible) pool.Add(card);
            return pool;
        }

        /// <summary>Whether any printed name in the pool belongs to more than one id. Not enforced —
        /// reported, so a collision surfaces as a number rather than as a card that silently
        /// resolves to whichever row was loaded first.</summary>
        public int CollidingNames()
        {
            int colliding = 0;
            foreach (KeyValuePair<string, List<int>> entry in _byPrintedName)
                if (entry.Value.Count > 1) colliding++;
            return colliding;
        }
    }
}
