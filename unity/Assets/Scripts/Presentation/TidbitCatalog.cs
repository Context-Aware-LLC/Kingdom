using System.Collections.Generic;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The moment a tidbit is offered at. A trigger is a place in the journey, not a screen and not
    /// a button: <see cref="CharacterCreateArrival"/> is "the player has just been asked to choose
    /// classes", which is why its copy explains the choice rather than the screen. One screen can
    /// own several — the roster teaches a different thing to a player managing decks than to one
    /// choosing a deck to queue with.
    ///
    /// <para>A trigger maps to an ORDERED LIST, never to one entry — see
    /// <see cref="TidbitCatalog.For"/>.</para>
    /// </summary>
    public enum TidbitTrigger
    {
        /// <summary>The main menu has been revealed and is staying on screen — not a launch that is
        /// about to leave for account creation or the starter gift.</summary>
        MainMenuArrival,

        /// <summary>Arriving at <see cref="CharacterRosterController"/> to manage decks.</summary>
        RosterArrival,

        /// <summary>Arriving at <see cref="CharacterRosterController"/> from Find Game, to choose
        /// the deck to queue with.</summary>
        MatchPickArrival,

        /// <summary>Arriving at <see cref="CharacterCreateController"/>, before the first pick.</summary>
        CharacterCreateArrival,

        /// <summary>Arriving at <see cref="CollectionController"/> with no deck open.</summary>
        CollectionArrival,

        /// <summary>A deck has just opened for editing in <see cref="CollectionController"/>,
        /// whether the roster handed it in or the player chose it from the drawer.</summary>
        DeckEditorArrival,

        /// <summary>Inside <see cref="PackVaultController"/>, the first time the rack draws with a
        /// pack on it.</summary>
        PackVaultArrival,

        /// <summary>Inside <see cref="PackVaultController"/>, after the last granted pack is opened.</summary>
        PackVaultGiftComplete,

        /// <summary>Arriving at <see cref="ShopScreenController"/>.</summary>
        ShopArrival,
    }

    /// <summary>One card of first-run copy. Title, a plain instruction, one dry line under it, one
    /// illustration.</summary>
    public sealed class Tidbit
    {
        public Tidbit(string id, TidbitTrigger trigger, string title, string body, string artKey)
        {
            Id = id;
            Trigger = trigger;
            Title = title;
            Body = body;
            ArtKey = artKey;
        }

        /// <summary>Stable across releases — it is half of the <see cref="Session.FirstRunMarkers"/>
        /// key, so renaming one replays a beat the player has already seen.</summary>
        public string Id { get; }

        public TidbitTrigger Trigger { get; }
        public string Title { get; }
        public string Body { get; }

        /// <summary>An <see cref="IUiSpriteProvider"/> key — the file name under
        /// <c>Assets/Art/UI/</c> with no extension. A key that resolves to nothing hides the
        /// illustration; it never blocks the card.</summary>
        public string ArtKey { get; }
    }

    /// <summary>
    /// The first-look copy for every screen that has one, as data (#403). It began as slice 4 of
    /// <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>.
    ///
    /// <para><b>The instruction comes first and the joke second, on its own line.</b> A player who
    /// reads only the first line must still know what to do on the screen underneath.</para>
    ///
    /// <para><b>Teach only a verb the player can perform.</b> An entry describing something that is
    /// not wired is worse than no entry. Gem bundles are the one store verb with no route today, so
    /// no copy here sends anyone to buy Gems.</para>
    ///
    /// <para><b>A trigger returns a SEQUENCE.</b> <see cref="TidbitTrigger.PackVaultGiftComplete"/>
    /// fires shards and then store, back to back.</para>
    /// </summary>
    public static class TidbitCatalog
    {
        private static readonly Tidbit[] Registered =
        {
            new Tidbit(
                "menu",
                TidbitTrigger.MainMenuArrival,
                "The Kingdom, briefly",
                "Find Game starts a match. Collection holds your cards and decks, and the Card Vault " +
                "holds any packs you have not opened.\n" +
                "Quests pay Gold when you finish them. No form is required, for now.",
                "tidbit_menu"),
            new Tidbit(
                "decks",
                TidbitTrigger.RosterArrival,
                "Your decks",
                "Tap + to start a deck. Tap a deck once for its options, or twice to edit it. " +
                "A gold border means it is ready for a match.\n" +
                "You may keep eight. The Council has not said why.",
                "tidbit_decks"),
            new Tidbit(
                "match-pick",
                TidbitTrigger.MatchPickArrival,
                "Choose your deck",
                "Tap the deck you want to play, then Continue. Only a deck with a gold border " +
                "can join a match.\n" +
                "Your opponent has also been told to choose wisely.",
                "tidbit_match_pick"),
            new Tidbit(
                "characters",
                TidbitTrigger.CharacterCreateArrival,
                "Two classes, one character",
                "Your character is two classes joined. Your first choice determines your hero power the second just adds to the card pool you can build from.  — " +
                "Two personalities is not usually enough to become King but you never know..",
                "tidbit_characters"),
            new Tidbit(
                "collection",
                TidbitTrigger.CollectionArrival,
                "The collection",
                "Every card you own is here. Tap one to Render another copy with shards, or to " +
                "Reduce a spare into shards. Decks opens your decks.\n" +
                "The filing took eleven years and is nearly finished.",
                "tidbit_collection"),
            new Tidbit(
                "deck-editor",
                TidbitTrigger.DeckEditorArrival,
                "Building a deck",
                "Tap a card on the wall to add it, and tap it in the deck to take it out. A deck " +
                "is 30 cards, with two copies of each at most and one of any Legendary.\n" +
                "It saves itself, which is more than the Royal Archive can say.",
                "tidbit_deck_editor"),
            new Tidbit(
                "vault",
                TidbitTrigger.PackVaultArrival,
                "The Card Vault",
                "Tap a pack to open it, or drag it out of the rack. You can stack up to ten and " +
                "open them together.\n" +
                "Everything inside is yours. The paperwork has already been filed.",
                "tidbit_vault"),
            new Tidbit(
                "shards",
                TidbitTrigger.PackVaultGiftComplete,
                "Shards",
                "Cards you'll never play break down into shards. Shards craft any card you're missing.",
                "tidbit_shards"),
            new Tidbit(
                "store",
                TidbitTrigger.PackVaultGiftComplete,
                "The Store",
                "More packs live in the Store. Gold comes from playing.",
                "tidbit_store"),
            new Tidbit(
                "shop",
                TidbitTrigger.ShopArrival,
                "Buying packs",
                "Tap a pack, choose how many, and pay with Gold. Packs you buy wait unopened in " +
                "the Card Vault.\n" +
                "The Treasury thanks you in advance.",
                "tidbit_shop"),
        };

        /// <summary>
        /// The tidbits that fire at <paramref name="trigger"/>, in the order they are shown. A host
        /// must treat an empty result as "carry on", never as a bug.
        /// </summary>
        public static IReadOnlyList<Tidbit> For(TidbitTrigger trigger)
        {
            var matches = new List<Tidbit>(Registered.Length);
            foreach (Tidbit tidbit in Registered)
                if (tidbit.Trigger == trigger) matches.Add(tidbit);
            return matches;
        }
    }
}
