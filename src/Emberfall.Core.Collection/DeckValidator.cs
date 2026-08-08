using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// Which legality rule a violation maps to (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §1, LOCKED).
    ///
    /// <para><b><see cref="L6_Ownership"/> was added by NOR-238, which authorised the one change the
    /// LOCKED marker forbids.</b> The spec reserved L6 by name in prose and the enum did not carry it;
    /// the issue named adding the member as its deliberate act, and required it to land in the same
    /// commit as the rule that raises it, so the enum is never a slot with nothing behind it. Appended
    /// last, never inserted: nothing serializes this enum today, and appending keeps it that way if
    /// something ever does.</para>
    /// </summary>
    public enum DeckRule
    {
        L1_Size,
        L2_CopyCap,
        L3_LegendaryCap,
        L4_ClassMembership,
        L5_CharacterBound,

        /// <summary>You used more copies of a card than you own (NOR-238).</summary>
        L6_Ownership,
    }

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
    /// Ported from the original Unity-only Emberfall.Data.DeckValidator (C-G0-5 spike). L1-L3
    /// unchanged; L4/L5 updated for the dual-class Character ruling (Sam, 2026-07-26): a card is
    /// legal if it's NEUTRAL or either half of the character's pairing (not just one hero class).
    ///
    /// Rules L1-L6:
    ///   L1  deck size == 30
    ///   L2  max 2 copies (COMMON/RARE/EPIC)
    ///   L3  max 1 copy (LEGENDARY)
    ///   L4  every card is NEUTRAL or one of the character's two classes
    ///   L5  the deck is bound to exactly one Character
    ///   L6  you own at least the copies used  — only when an ownership source is supplied
    ///
    /// <para><b>L6 is opt-in at the call, and a null ownership source is the game as it ships today.</b>
    /// Every entry point here takes an optional <see cref="IOwnedCardCounts"/>; null means "ownership is
    /// unknown, do not constrain", which is the full-pool alpha L1-L5 were written under. That is not a
    /// convenience default — it is the difference between an account nobody has granted cards to yet and
    /// an account that owns nothing, and <see cref="IOwnedCardStore"/> is where the distinction is
    /// persisted. Pass a real source and L6 applies with no other change.</para>
    /// </summary>
    public static class DeckValidator
    {
        public const int DeckSize = 30;

        /// <summary>Format copy-cap for a rarity: L3 Legendary = 1, L2 everything else = 2.</summary>
        public static int MaxCopies(Rarity rarity) => rarity == Rarity.LEGENDARY ? 1 : 2;

        /// <summary>
        /// The copies of one card a deck may actually hold — <c>min(<see cref="MaxCopies"/>, owned)</c>,
        /// or the format cap alone when ownership is unknown. The L2/L3 cap and L6 answering as one
        /// number, so a surface that draws "you are at the limit" cannot draw it from only half of the
        /// reason.
        ///
        /// <para><b>Rotation is not ownership.</b> This reads a copy count and a rarity and nothing else
        /// — no set, no format, no release date. A rotated card is still owned and still legal in Open
        /// (card-id permanence, github issue #86), so L6 must never be reachable as a way of saying "not
        /// in this format". If a format axis ever lands it is a rule of its own, not a term in here.</para>
        /// </summary>
        public static int CopyLimit(Rarity rarity, IOwnedCardCounts? owned, string cardId)
        {
            int max = MaxCopies(rarity);
            if (owned == null) return max;
            int have = owned.CopiesOwned(cardId);
            return have < max ? (have < 0 ? 0 : have) : max;
        }

        /// <summary>
        /// Can this card legally take one more copy right now? Drives the +copy control and the
        /// addability of a collection tile (§1 feedback: L2/L3 disable at the cap, L4 hides off-class,
        /// L1-over refuses the 31st add). Unknown ids and full decks are non-addable.
        ///
        /// <para>With an <paramref name="owned"/> source this also refuses an add you cannot back with a
        /// card (L6). It stays a single bool on purpose: the caller is a button, and every reason it is
        /// off has a better surface than a returned reason code — the wall's fade for the copy limit,
        /// the pool for off-class, the count for the 31st.</para>
        /// </summary>
        public static bool CanAdd(DeckList deck, Character character, CardDefinition def,
                                  IOwnedCardCounts? owned = null)
        {
            if (deck == null || character == null || def == null) return false;
            if (deck.TotalCount >= DeckSize) return false;                      // L1 ceiling
            if (!character.AllowsClass(def.Class)) return false;                // L4
            return deck.CountOf(def.Id) < CopyLimit(def.Rarity, owned, def.Id); // L2 / L3 / L6
        }

        /// <summary>
        /// Full validation of a decklist against the card database. Order-stable violations.
        ///
        /// <para>Supply <paramref name="owned"/> to enforce L6; null leaves L1-L5 exactly as they were.
        /// Validation never mutates and never repairs — a deck that has become unownable (a card dusted,
        /// an account restored) reports it and stays a perfectly savable draft (§1).</para>
        /// </summary>
        public static DeckValidation Validate(DeckList? deck, Character? character, ICardDatabase db,
                                              IOwnedCardCounts? owned = null)
        {
            var violations = new List<DeckViolation>();
            if (deck == null)
                return new DeckValidation(false, violations);

            if (character == null)
            {
                violations.Add(new DeckViolation(DeckRule.L5_CharacterBound, "Deck has no character bound."));
                return new DeckValidation(false, violations);
            }

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

                // L4 — class membership (either half of the pairing, or NEUTRAL).
                if (!character.AllowsClass(def.Class))
                    violations.Add(new DeckViolation(DeckRule.L4_ClassMembership,
                        $"{def.Name} is not a {character.PrimaryClass}/{character.SecondaryClass} card."));

                // L6 — ownership. Reported independently of L2/L3: three copies of a common you own one
                // of is over the format cap AND past what you hold, and both sentences are true. Folding
                // them together would print one number that is right for one reason and wrong for the
                // other, on the two occasions the player most needs to know which.
                if (owned != null)
                {
                    int have = owned.CopiesOwned(entry.CardId);
                    if (have < 0) have = 0;
                    if (entry.Count > have)
                        violations.Add(new DeckViolation(DeckRule.L6_Ownership,
                            have == 0
                                ? $"You don't own {def.Name}."
                                : $"You own {have} of {def.Name}."));
                }
            }

            return new DeckValidation(violations.Count == 0, violations);
        }
    }
}
