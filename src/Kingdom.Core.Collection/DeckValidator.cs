using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Collection
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

        /// <summary>
        /// The deck's own pairing is not two distinct real classes (ADR-0009).
        ///
        /// <para><b>This slot used to be <c>L5_CharacterBound</c></b> — "the deck is bound to exactly
        /// one Character". Characters were eliminated on 2026-09-04 and the pairing moved onto the
        /// deck, so a deck can no longer fail to be bound to itself and that rule had nothing left to
        /// check. The slot is REPURPOSED rather than removed, and not to preserve numbering (nothing
        /// serializes this enum): the well-formedness the old rule got for free from
        /// <c>Character.IsValidPairing</c> is now a property of the deck, and a wire-supplied deck can
        /// carry NEUTRAL/NEUTRAL or Pyra/Pyra. It is a real rule that needs a real slot.</para>
        /// </summary>
        L5_PairingWellFormed,

        /// <summary>You used more copies of a card than you own (NOR-238).</summary>
        L6_Ownership,

        /// <summary>
        /// The card is a draft or retired row (#398, #86): resolvable, but not in the live pool, so
        /// it cannot be in a deck. This is what keeps a superuser account, whose at-limit record owns every pool card, from
        /// building a deck out of an unreleased set — ownership says yes to every id.
        /// </summary>
        L7_NotPublished,
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
    /// Ported from the original Unity-only Kingdom.Data.DeckValidator (C-G0-5 spike). L1-L3
    /// unchanged; L4 follows the dual-class ruling (Sam, 2026-07-26): a card is legal if it's NEUTRAL
    /// or either half of the pairing, not just one hero class.
    ///
    /// Rules L1-L6:
    ///   L1  deck size == 30
    ///   L2  max 2 copies (COMMON/RARE/EPIC)
    ///   L3  max 1 copy (LEGENDARY)
    ///   L4  every card is NEUTRAL or one of the deck's two classes
    ///   L5  the deck's pairing is two distinct real classes
    ///   L6  you own at least the copies used  — only when an ownership source is supplied
    ///   L7  every card is PUBLISHED — a draft or retired row resolves but cannot be drafted
    ///
    /// <para><b>The pairing is read off the deck (ADR-0009, 2026-09-04).</b> Every entry point here
    /// used to take a <c>Character</c> alongside the deck, purely to reach two enums; the deck now
    /// carries them and the parameter is gone. Server-side this removes a real hazard rather than
    /// just a parameter — <c>DeckSubmission</c> was constructing a throwaway <c>Character</c> from
    /// wire fields so that the shapes lined up, which meant the pairing being validated and the
    /// pairing the deck would be played with were two objects that happened to agree.</para>
    ///
    /// <para><b>L6 is opt-in at the call, and a null ownership source is for callers with no account.</b>
    /// Every entry point here takes an optional <see cref="IOwnedCardCounts"/>; null means "do not
    /// constrain", which is what a test, a bot or a tooling seat passes. A player's device never does: with
    /// no verified record (<see cref="IOwnedCardStore"/> loads null) it asks the server and refuses to
    /// build or queue meanwhile. That is the difference between an account whose ownership is unknown and
    /// one that owns nothing. Pass a real source and L6 applies with no other change.</para>
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
        public static int CopyLimit(Rarity rarity, IOwnedCardCounts? owned, int cardId)
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
        public static bool CanAdd(DeckList deck, CardDefinition def, IOwnedCardCounts? owned = null)
        {
            if (deck == null || def == null) return false;
            if (deck.TotalCount >= DeckSize) return false;                      // L1 ceiling
            if (!deck.AllowsClass(def.Class)) return false;                     // L4
            return deck.CountOf(def.Id) < CopyLimit(def.Rarity, owned, def.Id); // L2 / L3 / L6
        }

        /// <summary>
        /// Full validation of a decklist against the card database. Order-stable violations.
        ///
        /// <para>Supply <paramref name="owned"/> to enforce L6; null leaves L1-L5 exactly as they were.
        /// Validation never mutates and never repairs — a deck that has become unownable (a card dusted,
        /// an account restored) reports it and stays a perfectly savable draft (§1).</para>
        /// </summary>
        public static DeckValidation Validate(DeckList? deck, ICardDatabase db,
                                              IOwnedCardCounts? owned = null)
        {
            var violations = new List<DeckViolation>();
            if (deck == null)
                return new DeckValidation(false, violations);

            // L5 — the pairing itself. Checked first and returned on, because every L4 message below
            // is spoken IN TERMS of the pairing ("not a PYRA/MAREN card"), and a malformed one would
            // make that sentence nonsense at the exact moment the player most needs it to be clear.
            // Unreachable from the deckbuilder, whose picker offers valid pairings only; reachable
            // from the wire, which is the whole reason it is a rule rather than an assumption.
            if (!deck.HasValidPairing)
            {
                violations.Add(new DeckViolation(DeckRule.L5_PairingWellFormed,
                    $"Invalid class pairing {deck.PrimaryClass}/{deck.SecondaryClass}."));
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

                // L7 — lifecycle. A retired card in a saved deck reads "(retired)" and is fixable by
                // the player (#86); a draft one should never have arrived.
                if (def.Lifecycle != CardLifecycle.PUBLISHED)
                    violations.Add(new DeckViolation(DeckRule.L7_NotPublished,
                        def.Lifecycle == CardLifecycle.RETIRED
                            ? $"{def.Name} (retired) can't be played any more."
                            : $"{def.Name} isn't released yet."));

                // L2 / L3 — copy caps.
                int max = MaxCopies(def.Rarity);
                if (entry.Count > max)
                    violations.Add(new DeckViolation(
                        max == 1 ? DeckRule.L3_LegendaryCap : DeckRule.L2_CopyCap,
                        $"Only {max} of {def.Name}."));

                // L4 — class membership (either half of the pairing, or NEUTRAL).
                if (!deck.AllowsClass(def.Class))
                    violations.Add(new DeckViolation(DeckRule.L4_ClassMembership,
                        $"{def.Name} is not a {deck.PrimaryClass}/{deck.SecondaryClass} card."));

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
