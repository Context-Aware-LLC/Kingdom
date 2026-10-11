namespace Kingdom.Presentation
{
    // Text sibling of IUiSpriteProvider, for the small always-resident data files that ride
    // alongside UI chrome — currently HeroFrames_<Material>.json, the measured ring/socket/boss
    // geometry split_hero_frames.py writes beside the frame PNGs.
    //
    // WHY A SECOND SEAM. IUiSpriteProvider returns Sprite, so a TextAsset has nowhere to land in
    // it. Everything else matches: keyed by file name, resolved synchronously because the payload
    // is bytes-not-textures and is needed during construction, and served from AssetDatabase in
    // the editor / Addressables in a player.
    public interface IUiTextProvider
    {
        // Returns the text of the asset for this key, or null if it doesn't exist (yet).
        string Get(string key);
    }
}
