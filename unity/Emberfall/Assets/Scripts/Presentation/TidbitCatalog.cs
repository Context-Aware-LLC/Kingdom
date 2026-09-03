using System.Collections.Generic;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The moment a tidbit is offered at. A trigger is a place in the journey, not a screen and not
    /// a button: <see cref="CharacterCreateArrival"/> is "the player has just been asked to choose
    /// classes", which is why its copy explains the choice rather than the screen.
    ///
    /// <para>A trigger maps to an ORDERED LIST, never to one entry — see
    /// <see cref="TidbitCatalog.For"/>.</para>
    /// </summary>
    public enum TidbitTrigger
    {
        /// <summary>Arriving at <see cref="CharacterCreateController"/>, before the first pick.</summary>
        CharacterCreateArrival,

        /// <summary>Inside <see cref="PackVaultController"/>, after the last granted pack is opened.</summary>
        PackVaultGiftComplete,
    }

    /// <summary>One card of first-run copy. Title, at most two lines of body, one illustration.</summary>
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
    /// The first-run copy, as data. Slice 4 of
    /// <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>.
    ///
    /// <para><b>Built for three, registered for one — and that is a ruling, not an omission.</b>
    /// Spec §4, slice 4: both tail tidbits describe verbs a player cannot perform. Crafting is a
    /// <c>LogWarning</c> stub in <see cref="CollectionController"/> and the store still answers
    /// <i>"Purchases are waiting for the server wallet connection."</i> Teaching a verb that is not
    /// wired is worse than teaching nothing, so <see cref="Held"/> stays out of <see cref="For"/>
    /// until those two have routes. Moving an entry across is a one-line change and is the whole
    /// reason the held copy is written out here rather than left in the spec.</para>
    ///
    /// <para><b>A trigger returns a SEQUENCE.</b> <see cref="TidbitTrigger.PackVaultGiftComplete"/>
    /// is specified to fire shards and then store, back to back, so a one-entry return type would
    /// have to be widened by the same commit that unblocks them.</para>
    ///
    /// <para><b>RETIREMENT TRIGGER:</b> when crafting gets a route, move the <c>shards</c> entry from
    /// <see cref="Held"/> into <see cref="Registered"/>; when the store does, move <c>store</c>. When
    /// both have moved, delete <see cref="Held"/> and this paragraph, and drop the "registered for
    /// one" claim from the remarks above.</para>
    /// </summary>
    public static class TidbitCatalog
    {
        /// <summary>What actually fires today.</summary>
        private static readonly Tidbit[] Registered =
        {
            new Tidbit(
                "characters",
                TidbitTrigger.CharacterCreateArrival,
                "Two classes, one character",
                "Your character is two classes joined. It decides which cards you can bring — " +
                "and you can keep more than one.",
                "tidbit_characters"),
        };

        /// <summary>
        /// Written, ordered, and deliberately unreachable — see the type remarks. These are not
        /// drafts: the copy is final and the only thing missing is the verb it describes.
        /// </summary>
        private static readonly Tidbit[] Held =
        {
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
        };

        /// <summary>
        /// The tidbits that fire at <paramref name="trigger"/>, in the order they are shown. Empty
        /// for a trigger whose entries are all still held, which is the normal state for
        /// <see cref="TidbitTrigger.PackVaultGiftComplete"/> today — a host must therefore treat an
        /// empty result as "carry on", never as a bug.
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
