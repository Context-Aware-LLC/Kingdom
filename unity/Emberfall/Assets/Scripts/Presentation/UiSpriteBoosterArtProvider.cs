using System;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Resolves booster packaging art through the ordinary UI sprite pipeline — AssetDatabase in
    /// the Editor, Addressables at <c>ui/&lt;key&gt;</c> in a build — so the shop's art slots need no
    /// loader of their own.
    ///
    /// <para>The catalog's art keys ARE the sprite names (<c>booster_pack_&lt;set&gt;_&lt;character&gt;</c>),
    /// which is what lets this be an adapter rather than a lookup table: art delivery drops a file
    /// named for the key under <c>Assets/Art/UI/</c> and the slot fills with no code change. Subfolders
    /// under <c>Art/UI</c> are organisational only — both providers search by NAME, so the file may
    /// land wherever it reads best.</para>
    ///
    /// <para><b>A missing sprite is the expected case, not a failure.</b> Art arrives per booster and
    /// per class over time; every key that has no file yet resolves to null and
    /// <see cref="ShopPanelController"/> leaves that slot on its named placeholder. Nothing here
    /// substitutes a stand-in image, because a wrong character on a pack is worse than an empty
    /// slot.</para>
    /// </summary>
    public sealed class UiSpriteBoosterArtProvider : IBoosterPackagingArtProvider
    {
        private readonly IUiSpriteProvider _ui;

        public UiSpriteBoosterArtProvider(IUiSpriteProvider ui = null)
        {
            _ui = ui ?? CardBackCatalog.NewProvider();
        }

        public void LoadArt(string artKey, Action<Sprite> onLoaded)
        {
            if (onLoaded == null) return;
            if (string.IsNullOrEmpty(artKey))
            {
                onLoaded(null);
                return;
            }

            // Synchronous by contract: IUiSpriteProvider resolves UI chrome inline (both impls cache,
            // including misses). The callback shape exists for a future streamed provider, so it is
            // still invoked rather than returning a value.
            onLoaded(_ui?.Get(artKey));
        }
    }
}
