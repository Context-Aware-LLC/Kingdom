using System.Collections.Generic;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>One card back a player can put on a deck: what it is called, and where its art is.</summary>
    public readonly struct CardBackOption
    {
        /// <summary>Persisted in <c>DeckList.CardBackId</c>. Stable forever — renaming one repoints
        /// every deck that chose it back to the default.</summary>
        public readonly string Id;

        /// <summary>What the picker shows.</summary>
        public readonly string Name;

        /// <summary>The <see cref="IUiSpriteProvider"/> key — the file name under <c>Assets/Art/UI/</c>,
        /// which is also the Addressables address minus its <c>ui/</c> prefix.</summary>
        public readonly string SpriteKey;

        public CardBackOption(string id, string name, string spriteKey)
        {
            Id = id;
            Name = name;
            SpriteKey = spriteKey;
        }
    }

    /// <summary>
    /// The card backs this build can render.
    ///
    /// <para><b>Client-side, and deliberately not in <c>Emberfall.Core.Collection</c>.</b> A back is
    /// art plus a name; the collection assembly is UnityEngine-free and has no way to resolve either.
    /// So a deck stores an <i>id</i> it never validates and this is the only thing that knows whether
    /// that id means anything — which is why an unknown id falls back to <see cref="Default"/> here
    /// rather than failing to load there (see <c>DeckList.CardBackId</c>).</para>
    ///
    /// <para><b>Entitlement is not modelled.</b> Every back listed here is offered to every player.
    /// The design (<c>design/economy-progression.md</c> §5.1/§6) has backs granted at join, earned,
    /// and purchased — none of that exists in code, so the picker cannot filter by what is owned.
    /// When the ledger lands, filter this list through it rather than shortening it.</para>
    ///
    /// <para><b>The other consumer has not been wired.</b> <c>CardPresenter.SetHidden</c> and
    /// <c>DeckPileView</c> still ask for the bare <c>card_back</c> key, so choosing a back changes
    /// what the roster draws and nothing that happens in a match. Making the match seat-aware is
    /// the second half of this, and it is the half that puts an opponent-authored back id on the
    /// wire — read <c>security.md</c> before that one.</para>
    ///
    /// RETIREMENT TRIGGER: adding a row below is what falsifies "there is only one back". When you
    /// add one, also (a) drop the single-back note the deck-back picker shows in
    /// <c>CharacterRosterController.ShowCardBackPane</c>, and (b) confirm its address is registered
    /// in the <c>UI_Core</c> Addressables group — an unregistered key resolves in the Editor and
    /// returns null in a build.
    /// </summary>
    public static class CardBackCatalog
    {
        private static readonly CardBackOption[] Backs =
        {
            // Gravekeep_AlloverBig, the first back to reach the project (457f9ff). Its sprite key is
            // the flat "card_back" it landed under, NOT its id — the file predates there being more
            // than one, and renaming the asset would mint a new .meta guid and orphan the
            // Addressables entry pointing at it.
            new CardBackOption("gravekeep_allover", "Gravekeep", "card_back"),
        };

        public static IReadOnlyList<CardBackOption> All => Backs;

        /// <summary>What a deck with no chosen back presents.</summary>
        public static CardBackOption Default => Backs[0];

        /// <summary>True while the picker has nothing to pick between — the state the copy explains.</summary>
        public static bool HasChoice => Backs.Length > 1;

        /// <summary>
        /// The back an id names, or <see cref="Default"/> when it names none. Empty (never chosen)
        /// and unknown (a back this build does not ship) resolve identically on purpose: both mean
        /// "draw the default", and distinguishing them would only let a cosmetic break a screen.
        /// </summary>
        public static CardBackOption Resolve(string id)
        {
            if (!string.IsNullOrEmpty(id))
                foreach (CardBackOption back in Backs)
                    if (back.Id == id) return back;
            return Default;
        }

        /// <summary>The art for a stored id, or null when the sprite itself is missing.</summary>
        public static Sprite SpriteFor(string id, IUiSpriteProvider ui)
            => ui?.Get(Resolve(id).SpriteKey);

        /// <summary>
        /// The sprite provider a meta screen should use — AssetDatabase in the Editor, Addressables
        /// in a build, the same split <c>BoardController.Initialize</c> makes.
        /// </summary>
        public static IUiSpriteProvider NewProvider()
        {
#if UNITY_EDITOR
            return new EditorUiSpriteProvider();
#else
            return new AddressableUiSpriteProvider();
#endif
        }
    }
}
