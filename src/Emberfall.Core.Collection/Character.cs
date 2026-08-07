using Emberfall.Core.Contracts;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// A player's permanent class pairing (Sam, 2026-07-26 — see gbrain
    /// decisions/characters-dual-class-pairing). Pyra+Maren and Maren+Pyra are DIFFERENT
    /// characters: same card pool, but the hero power always comes from <see cref="PrimaryClass"/>
    /// (the HARD ruling — exactly one hero power on the board, never two). A player may hold several
    /// characters, including multiple pairings of the same primary. Pairing is immutable once chosen
    /// (no repairing after creation — this type has no setter for either class).
    ///
    /// Card pool for a deck bound to this character = PrimaryClass ∪ SecondaryClass ∪ NEUTRAL
    /// (see <see cref="DeckValidator"/> L4). Resolving the actual hero-power card id is a downstream
    /// concern (match setup), not solved here — hero power rows are keyed 1:1 by class already.
    /// </summary>
    public sealed class Character
    {
        public string Id { get; }
        public string Name { get; }
        public CardClass PrimaryClass { get; }
        public CardClass SecondaryClass { get; }

        /// <summary>
        /// The deck this character opens on — what S6 shows when you select it in the roster panel
        /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §2). Set at creation to the generated starter deck
        /// (<see cref="DefaultDeckGenerator"/>) and changed whenever the player picks a different one.
        ///
        /// <para><b>Mutable, unlike the pairing.</b> The pairing is a permanent identity decision with
        /// no legal repair; a default deck is a preference, and the player is expected to change it.
        /// That difference is why this is the one property on this type with a setter.</para>
        ///
        /// <para><b>A dangling id is legal here, and healed by the client, not by this assembly.</b>
        /// Deleting a deck leaves the reference pointing at nothing and
        /// <see cref="CharacterQuery.DefaultDeckOf"/> returns null, exactly as a deck bound to a
        /// deleted character reports L5. Nothing in this assembly repairs that: it has no policy for
        /// which deck should inherit, and a store that quietly rewrote a character on load would make
        /// "what is saved" depend on who read it last.</para>
        ///
        /// <para><b>The UI does repair it, silently</b> (Sam, 2026-08-07 —
        /// <c>CharacterRosterController.HealDefaultDeck</c> re-points a dangling default at the deck
        /// the character is already opening on). This REPLACES an earlier rule here that a dangle
        /// must be surfaced and never repaired, on the grounds that re-pointing means "a deck the
        /// player never chose". The player did not choose the deleted one either, and deleting your
        /// default is itself the request to move off it; what they never asked for was an error
        /// message above their decks naming one that no longer exists. Deleting a character remains
        /// different — that cascade destroys decks (<see cref="CharacterDeletion"/>) and is
        /// authorised by a confirm dialog naming the count.</para>
        ///
        /// RETIREMENT TRIGGER: a second surface that reads this and draws a default must heal it too,
        /// or repeat the bug. If a third appears, move HealDefaultDeck somewhere both can call.
        /// </summary>
        public string DefaultDeckId { get; set; }

        public Character(string id, string name, CardClass primaryClass, CardClass secondaryClass,
                         string defaultDeckId = "")
        {
            Id = id;
            Name = name;
            PrimaryClass = primaryClass;
            SecondaryClass = secondaryClass;
            DefaultDeckId = defaultDeckId ?? "";
        }

        /// <summary>True iff a card of <paramref name="cardClass"/> is legal in a deck bound to this character.</summary>
        public bool AllowsClass(CardClass cardClass) =>
            cardClass == CardClass.NEUTRAL || cardClass == PrimaryClass || cardClass == SecondaryClass;

        /// <summary>
        /// A pairing is valid iff both classes are real (non-NEUTRAL) classes and distinct
        /// (Pyra+Pyra is not a pairing). Construction-time helper — callers decide whether to
        /// reject or just flag an invalid pairing.
        /// </summary>
        public static bool IsValidPairing(CardClass primary, CardClass secondary) =>
            primary != CardClass.NEUTRAL && secondary != CardClass.NEUTRAL && primary != secondary;
    }
}
