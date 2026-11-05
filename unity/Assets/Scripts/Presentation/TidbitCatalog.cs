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
        /// <summary>The main menu, on a new account's first arrival, just before the starter grant
        /// is presented on the menu's empty right side (Sam, 2026-11-04).</summary>
        StarterGrant,

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
        public Tidbit(string id, TidbitTrigger trigger, string titleKey, string bodyKey, string artKey,
            string voiceAddress = null)
        {
            Id = id;
            Trigger = trigger;
            TitleKey = titleKey;
            BodyKey = bodyKey;
            ArtKey = artKey;
            VoiceAddress = voiceAddress;
        }

        /// <summary>Stable across releases — it is half of the <see cref="Session.FirstRunMarkers"/>
        /// key, so renaming one replays a beat the player has already seen.</summary>
        public string Id { get; }

        public TidbitTrigger Trigger { get; }

        /// <summary>The <see cref="Loc"/> keys of the card's copy.</summary>
        public string TitleKey { get; }
        public string BodyKey { get; }

        /// <summary>Resolved at read time, in the active language.</summary>
        public string Title => Loc.Get(TitleKey);
        public string Body => Loc.Get(BodyKey);

        /// <summary>An <see cref="IUiSpriteProvider"/> key — the file name under
        /// <c>Assets/Art/UI/</c> with no extension. A key that resolves to nothing hides the
        /// illustration; it never blocks the card.</summary>
        public string ArtKey { get; }

        /// <summary>The recorded narration, independent of localized display copy.</summary>
        public string VoiceAddress { get; }
    }

    /// <summary>
    /// The first-look copy for every screen that has one, as data (#403). It began as slice 4 of
    /// <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>.
    ///
    /// <para><b>The instruction comes first and the joke second, on its own line.</b> A player who
    /// reads only the first line must still know what to do on the screen underneath.</para>
    ///
    /// <para><b>Teach only a verb the player can perform.</b> An entry describing something that is
    /// not wired is worse than no entry. Gem bundles have been buyable since 2026-10-29, which is
    /// why the store card may mention Gems.</para>
    ///
    /// <para><b>A trigger returns a SEQUENCE.</b> <see cref="TidbitTrigger.PackVaultGiftComplete"/>
    /// fires shards and then store, back to back.</para>
    /// </summary>
    public static class TidbitCatalog
    {
        // Copy lives in strings.xml as <artKey>_title / <artKey>_body; the catalog holds keys only,
        // resolved when read, so a language switch reaches the next card shown.
        private static readonly Tidbit[] Registered =
        {
            new Tidbit("grant", TidbitTrigger.StarterGrant, "tidbit_grant_title", "tidbit_grant_body", "tidbit_grant", "vo/vo_herald_tidbit_grant"),
            new Tidbit("menu", TidbitTrigger.MainMenuArrival, "tidbit_menu_title", "tidbit_menu_body", "tidbit_menu", "vo/vo_herald_tidbit_menu"),
            new Tidbit("decks", TidbitTrigger.RosterArrival, "tidbit_decks_title", "tidbit_decks_body", "tidbit_decks", "vo/vo_herald_tidbit_decks"),
            new Tidbit("match-pick", TidbitTrigger.MatchPickArrival, "tidbit_match_pick_title", "tidbit_match_pick_body", "tidbit_match_pick", "vo/vo_herald_tidbit_match_pick"),
            new Tidbit("characters", TidbitTrigger.CharacterCreateArrival, "tidbit_characters_title", "tidbit_characters_body", "tidbit_characters", "vo/vo_herald_tidbit_characters"),
            new Tidbit("collection", TidbitTrigger.CollectionArrival, "tidbit_collection_title", "tidbit_collection_body", "tidbit_collection", "vo/vo_herald_tidbit_collection"),
            new Tidbit("deck-editor", TidbitTrigger.DeckEditorArrival, "tidbit_deck_editor_title", "tidbit_deck_editor_body", "tidbit_deck_editor", "vo/vo_herald_tidbit_deck_editor"),
            new Tidbit("vault", TidbitTrigger.PackVaultArrival, "tidbit_vault_title", "tidbit_vault_body", "tidbit_vault", "vo/vo_herald_tidbit_vault"),
            new Tidbit("shards", TidbitTrigger.PackVaultGiftComplete, "tidbit_shards_title", "tidbit_shards_body", "tidbit_shards", "vo/vo_herald_tidbit_shards"),
            new Tidbit("store", TidbitTrigger.PackVaultGiftComplete, "tidbit_store_title", "tidbit_store_body", "tidbit_store", "vo/vo_herald_tidbit_store"),
            new Tidbit("shop", TidbitTrigger.ShopArrival, "tidbit_shop_title", "tidbit_shop_body", "tidbit_shop", "vo/vo_herald_tidbit_shop"),
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
