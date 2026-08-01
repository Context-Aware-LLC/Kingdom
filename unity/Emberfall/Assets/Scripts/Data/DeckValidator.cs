using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Data
{
    /// <summary>
    /// One line of a decklist: a card definition id and how many copies (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §3).
    /// </summary>
    public readonly struct DeckEntry
    {
        public readonly string CardId;
        public readonly int Count;
        public DeckEntry(string cardId, int count) { CardId = cardId; Count = count; }
        public DeckEntry With(int count) => new DeckEntry(CardId, count);
    }

    /// <summary>
    /// The persisted deck payload (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §3): a hero/class binding, a name,
    /// and a small list of {definitionId, count}. Deliberately UnityEngine-free so the same type and
    /// the <see cref="DeckValidator"/> predicate below can move server-side unchanged.
    /// </summary>
    public sealed class DeckList
    {
        public CardClass HeroClass { get; set; }        // L5: a deck belongs to exactly one hero/class
        public string Name { get; set; } = "New Deck";
        public List<DeckEntry> Entries { get; } = new List<DeckEntry>();

        public int TotalCount
        {
            get
            {
                int n = 0;
                foreach (var e in Entries) n += e.Count;
                return n;
            }
        }

        public int CountOf(string cardId)
        {
            foreach (var e in Entries)
                if (e.CardId == cardId) return e.Count;
            return 0;
        }
    }

    /// <summary>
    /// Which legality rule a violation maps to (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §1, LOCKED).
    /// </summary>
    public enum DeckRule { L1_Size, L2_CopyCap, L3_LegendaryCap, L4_ClassMembership, L5_HeroBound }

    public readonly struct DeckViolation
    {
        public readonly DeckRule Rule;
        public readonly string Message;   // inline, non-blaming copy (§1 feedback table)
        public DeckViolation(DeckRule rule, string message) { Rule = rule; Message = message; }
    }

    public readonly struct DeckValidation
    {
        public readonly bool IsValid;
        public readonly IReadOnlyList<DeckViolation> Violations;
        public DeckValidation(bool isValid, IReadOnlyList<DeckViolation> violations)
        {
            IsValid = isValid;
            Violations = violations;
        }
    }

    /// <summary>
    /// Deck-legality predicate (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §1 — the LOCKED unblocking deliverable).
    /// The deckbuilder enforces these live to gate Save; the identical predicate re-runs server-side at
    /// match-join (never trust the client). Pure and UnityEngine-free by design.
    ///
    /// Rules L1–L5:
    ///   L1  deck size == 30
    ///   L2  max 2 copies (COMMON/RARE/EPIC)
    ///   L3  max 1 copy (LEGENDARY)
    ///   L4  every card is the hero's class or NEUTRAL
    ///   L5  the deck is bound to exactly one hero/class
    ///
    /// NOT enforced in M2: ownership/collection counts (full-pool alpha). The future L6
    /// ("you own >= the copies used") slots in without reworking L1–L5.
    /// </summary>
    public static class DeckValidator
    {
        public const int DeckSize = 30;

        /// <summary>Format copy-cap for a rarity: L3 Legendary = 1, L2 everything else = 2.</summary>
        public static int MaxCopies(Rarity rarity) => rarity == Rarity.LEGENDARY ? 1 : 2;

        /// <summary>
        /// Can this card legally take one more copy right now? Drives the +copy control and the
        /// addability of a collection tile (§1 feedback: L2/L3 disable at the cap, L4 hides off-class,
        /// L1-over refuses the 31st add). Unknown ids and full decks are non-addable.
        /// </summary>
        public static bool CanAdd(DeckList deck, CardDefinition def)
        {
            if (deck == null || def == null) return false;
            if (deck.TotalCount >= DeckSize) return false;                 // L1 ceiling
            if (def.Class != CardClass.NEUTRAL && def.Class != deck.HeroClass) return false; // L4
            return deck.CountOf(def.Id) < MaxCopies(def.Rarity);          // L2 / L3
        }

        /// <summary>Full validation of a decklist against the card database. Order-stable violations.</summary>
        public static DeckValidation Validate(DeckList deck, ICardDatabase db)
        {
            var violations = new List<DeckViolation>();
            if (deck == null)
                return new DeckValidation(false, violations);

            // L1 — deck size.
            int total = deck.TotalCount;
            if (total < DeckSize)
                violations.Add(new DeckViolation(DeckRule.L1_Size, $"Add {DeckSize - total} more cards."));
            else if (total > DeckSize)
                violations.Add(new DeckViolation(DeckRule.L1_Size, $"Remove {total - DeckSize} cards."));

            foreach (var entry in deck.Entries)
            {
                if (!db.TryGet(entry.CardId, out var def))
                {
                    // A card that vanished from the pool after a balance patch surfaces on open (§3).
                    violations.Add(new DeckViolation(DeckRule.L4_ClassMembership,
                        $"Unknown card '{entry.CardId}'."));
                    continue;
                }

                // L2 / L3 — copy caps.
                int max = MaxCopies(def.Rarity);
                if (entry.Count > max)
                    violations.Add(new DeckViolation(
                        max == 1 ? DeckRule.L3_LegendaryCap : DeckRule.L2_CopyCap,
                        $"Only {max} of {def.Name}."));

                // L4 — class membership.
                if (def.Class != CardClass.NEUTRAL && def.Class != deck.HeroClass)
                    violations.Add(new DeckViolation(DeckRule.L4_ClassMembership,
                        $"{def.Name} is not a {deck.HeroClass} card."));
            }

            return new DeckValidation(violations.Count == 0, violations);
        }
    }
}
