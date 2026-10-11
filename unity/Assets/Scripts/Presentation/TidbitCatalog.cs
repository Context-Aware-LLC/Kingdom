using System.Collections.Generic;

namespace Kingdom.Presentation
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
                "The Kingdom, is yours to explore.\n",
                "\n" +
                "Go questing, stare at your collection, or engage in glorious battle\n" +
                "\n" +
                "These are the quintissential joys of life in the Kingdom.",
                "tidbit_menu"),
            new Tidbit(
                "decks",
                TidbitTrigger.RosterArrival,
                "Your Decks",
                "Tap the \'+\' to create a deck. Tap a deck once to see options, twice to edit it.\n" +
                "\n" +
                "The Council has determinned that you shall have not more than nine.\n" +
                "\n" +
                "Nine is the limit. Not 8 and not 10. Because the council said so, that is why.",
                "tidbit_decks"),
            new Tidbit(
                "match-pick",
                TidbitTrigger.MatchPickArrival,
                "Choose A Deck",
                "Tap a deck to select it, then press Play.\n" +
                "\n" +
                "Yes. It is complicated. You will get used to it.\n" +
                "\n" +
                "If you are lucky your opponent will not figure this part out and you will be the champion.",
                "tidbit_match_pick"),
            new Tidbit(
                "characters",
                TidbitTrigger.CharacterCreateArrival,
                "Two Classes, One Character",
                "These days, everyone needs a side hustle.\n" +
                "\n" +
                "Your first choice determines your hero power.\n" +
                "The second adds to the card pool you build from.\n" +
                "\n" +
                "Two personalities is not usually enough to become King but you never know...",
                "tidbit_characters"),
            new Tidbit(
                "collection",
                TidbitTrigger.CollectionArrival,
                "The Collection",
                "Every card you own is here. The cards you wish you owned are here also.\n" +
                "\n" +
                "Tap one to Render a copy, or to Reduce a spare into shards.\n" +
                "\n" +
                "Cards come in 4 different rarity levels: \n" +
                "Common - Pink, Rare - Blue, Epic - Purple, Legendary - Red.\n" +
                "\n" +
                "The Decks button shows you the thing it says. Don't over think it.",
                "tidbit_collection"),
            new Tidbit(
                "deck-editor",
                TidbitTrigger.DeckEditorArrival,
                "Build a Deck",
                "Tap a card to add. Tap a row to subtract.\n" +
                "\n" +
                "A deck is 30 cards, two copies max, only one of any Legendary.\n" +
                "\n" +
                "Sing a little song to help you remember. Tap Add, Tap Subtract. Tap foot tap tap.",
                "tidbit_deck_editor"),
            new Tidbit(
                "vault",
                TidbitTrigger.PackVaultArrival,
                "The Card Vault",
                "Drag packs to the center to open.\n" +
                "\n" +
                "Pick one up and hold it over the rail to open multiple\n" +
                "\n" +
                "Everything inside is yours. The paperwork has already been filed in triplicate.",
                "tidbit_vault"),
            new Tidbit(
                "shards",
                TidbitTrigger.PackVaultGiftComplete,
                "Entering The Kingdom.",
                "For most this would be a bad idea.\n" +
                "\n" +
                "You are something special. I can tell.\n" +
                "\n" +
                "Your going to go out there and make the kingdom a better place, I'm sure.",
                "tidbit_shards"),
            new Tidbit(
                "store",
                TidbitTrigger.PackVaultGiftComplete,
                "Earn Gold",
                "You don't have to be rich to live like a king.\n" +
                "\n" +
                "If you toil every day completing quests you might grow up to be one.\n" +
                "\n" +
                "Or so I was told as a child.",

                "tidbit_store"),
            new Tidbit(
                "shop",
                TidbitTrigger.ShopArrival,
                "The Store",
                "If you get into trouble in the kingdom you can usually tap your way out of it\n" +
                "\n" +
                "Just as easily you can tap your way into some new cards\n" +
                "\n" +
                "Packs you buy end up in the Card Vault awaiting your tap.\n" +
                "\n" +
                "Pay with your hard-earned Gold like every other peasant or purchase gems to skip the toil.",
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
