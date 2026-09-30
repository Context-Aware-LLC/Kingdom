using UnityEngine;

namespace Emberfall.Presentation
{
    // UI atlas resolution seam (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/pipeline.md §2 — frames/gems/icons live on a Sprite Atlas,
    // distinct from streamed per-card illustrations served by IArtProvider). The rendering layer
    // asks for a sprite by its atlas key (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/frame-rarity-system.md §1/§2/§4, e.g. "frame_card",
    // "gem_epic", "socket_stat") and never knows where it comes from. The third example used to be
    // "icon_kw_bulwark"; keyword icons were removed 2026-09-04 and no such key resolves any more.
    // UI sprites are small and
    // always resident, so resolution is synchronous (unlike the async card-art callback).
    //
    // Shipping impl is a SpriteAtlas lookup; EditorUiSpriteProvider serves the vertical slice from
    // AssetDatabase until the atlas group is configured.
    public interface IUiSpriteProvider
    {
        // Returns the sprite for this key, or null if it doesn't exist (yet).
        Sprite Get(string key);
    }
}
