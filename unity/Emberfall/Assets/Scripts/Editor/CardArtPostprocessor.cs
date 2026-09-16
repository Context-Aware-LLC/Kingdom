using UnityEditor;
using UnityEngine;
using Emberfall.Presentation;

namespace Emberfall.Editor
{
    /// <summary>
    /// Enforces import settings for all card/hero/UI/VFX art by folder path,
    /// per hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/pipeline.md §3–4. Artists (human or agent) drop PNGs into
    /// Assets/Art/... and never touch the importer — settings are applied here,
    /// deterministically, on every (re)import.
    ///
    /// Folder contract (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/pipeline.md §8):
    ///   Assets/Art/Cards/&lt;setId&gt;/&lt;artId&gt;.png  — streamed card illustrations
    ///   Assets/Art/Cards/Backs/...               — card backs (same import settings as UI sprites)
    ///   Assets/Art/Hero/&lt;heroId&gt;.png            — hero portraits (same treatment)
    ///   Assets/Art/Backgrounds/...               — full-screen imagery (menus/splash): full res desktop, lighter mobile
    ///                                              Matched at ANY depth, so Backgrounds/Loading/
    ///                                              and Backgrounds/Versus/ inherit it for free.
    ///   Assets/Art/Orbs/...                      — elemental spheres composited into plate centres
    ///   Assets/Art/UI/...                        — frames/gems/icons/plates (atlas, no mips)
    ///   Assets/Art/VFX/Textures/...              — FX masks/flipbooks (linear data)
    /// </summary>
    internal sealed class CardArtPostprocessor : AssetPostprocessor
    {
        const string ArtRoot = "Assets/Art/";
        const string CardsRoot = "Assets/Art/Cards/";
        const string HeroRoot = "Assets/Art/Hero/";
        const string PackArtRoot = "Assets/Art/Shop/Packs/";
        const string BackgroundsRoot = "Assets/Art/Backgrounds/";
        const string OrbsRoot = "Assets/Art/Orbs/";
        const string UiRoot = "Assets/Art/UI/";
        const string VfxRoot = "Assets/Art/VFX/Textures/";
        const string IconsRoot = "Assets/Art/Icons/";

        /// <summary>
        /// LEAF DIRECTORY NAME = DECLARED TREATMENT, wherever the folder sits (Sam, 2026-08-06).
        ///
        /// The root-prefix rules below cannot express "this is full-screen imagery" for anything
        /// that legitimately lives inside another lane — a full-screen board backdrop under
        /// Assets/Art/UI/Board/ was silently atlased and un-mipped because an ancestor said "UI".
        /// A leaf name is the artist's own statement of intent at the moment they drop the file,
        /// so it outranks whatever the parent tree happens to be called.
        ///
        /// Add a name here rather than inventing a new root. Compared case-insensitively.
        /// </summary>
        static readonly System.Collections.Generic.Dictionary<string, System.Action<TextureImporter>>
            LeafTreatments = new(System.StringComparer.OrdinalIgnoreCase)
            {
                ["background"] = ApplyBackground,
                ["hptabs"] = ApplyMinifiedIcon,
                ["Frames"] = ApplyUiFrame,
                ["Board"] = ApplyUiFrame,
                ["HpFrames"] = ApplyUiFrame,
                ["currency"] = ApplyCurrencyIcon,
            };

        void OnPreprocessTexture()
        {
            var importer = (TextureImporter)assetImporter;
            var path = assetPath.Replace('\\', '/');
            if (!path.StartsWith(ArtRoot)) return;

            // Backs share the UI sprite contract (including FullRect meshes), not the lower
            // resolution illustration lane. Moving their folder must not change how they render.
            if (path.StartsWith(CardBackCatalog.ArtFolder + "/"))
            {
                ApplyUiAtlas(importer);
                return;
            }

            // Card illustrations, hero portraits and booster packaging win outright. These are
            // setId-keyed and feed the shipped card pipeline, so a folder somebody happens to name
            // "background" must not be able to reclassify them.
            //
            // Pack art is a ROOT rule, not a leaf one, and that distinction cost an evening on
            // 2026-08-21. It was `["packs"] = ApplyIllustration` — correct while the files sat
            // directly in Packs/, dead the moment they were filed by set into Packs/<setId>/,
            // because the leaf is then the SET name. The failure is invisible in the worst way:
            // a move carries the existing .meta, so art imported under the old rule keeps correct
            // settings forever and only NEWLY DROPPED art breaks. It surfaces as a set whose
            // sprites silently fail to register with Addressables — AddFolderSprites searches
            // t:Sprite, and an illustration that imported as a plain texture is not one.
            if (path.StartsWith(CardsRoot) || path.StartsWith(HeroRoot) || path.StartsWith(PackArtRoot))
            {
                ApplyIllustration(importer);
                return;
            }

            string leaf = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "";
            if (LeafTreatments.TryGetValue(leaf, out var treatment))
            {
                treatment(importer);
                return;
            }

            if (path.StartsWith(BackgroundsRoot)) { ApplyBackground(importer); return; }
            if (path.StartsWith(OrbsRoot)) { ApplyOrb(importer); return; }
            if (path.StartsWith(UiRoot)) { ApplyUiAtlas(importer); return; }
            if (path.StartsWith(VfxRoot)) { ApplyVfxMap(importer); return; }
            if (path.StartsWith(IconsRoot)) { ApplyUiAtlas(importer); return; }

            // NO SILENT DEFAULTS. An unmatched texture used to fall through this method
            // untouched, importing with whatever Unity guessed and reporting nothing — which is
            // how Assets/Art/Loading/ shipped on raw defaults without anyone noticing. An asset
            // under Assets/Art/ that matches no lane is a mistake; say so.
            Debug.LogWarning(
                $"[CardArtPostprocessor] '{path}' matched no import lane and is using Unity " +
                "defaults. Move it under a known root, or name its folder for a leaf treatment.");
        }

        // Card illustrations + hero portraits: Sprite, sRGB, mips ON (board zoom),
        // BC7 desktop @1024 / ASTC 6x6 mobile @512. Crunch OFF (block artifacts on gradients).
        static void ApplyIllustration(TextureImporter importer)
        {
            ApplySpriteCommon(importer);
            importer.mipmapEnabled = true;
            SetPlatform(importer, "Standalone", 1024, TextureImporterFormat.BC7);
            SetPlatform(importer, "Android", 512, TextureImporterFormat.ASTC_6x6);
            SetPlatform(importer, "iPhone", 512, TextureImporterFormat.ASTC_6x6);
        }

        // Full-screen imagery (menu/splash backdrops): shown near 1:1, no atlas, mips OFF.
        // Full resolution to desktop; a lighter cap to mobile to bound texture memory / download.
        static void ApplyBackground(TextureImporter importer)
        {
            ApplySpriteCommon(importer);
            importer.mipmapEnabled = false;
            SetPlatform(importer, "Standalone", 4096, TextureImporterFormat.BC7);
            SetPlatform(importer, "Android", 2048, TextureImporterFormat.ASTC_6x6);
            SetPlatform(importer, "iPhone", 2048, TextureImporterFormat.ASTC_6x6);
        }

        // Orbs (Assets/Art/Orbs/): the elemental sphere composited into the CENTRE of a loading
        // or versus plate, at whatever size that plate calls for. Deliberately NOT the
        // background lane even though the settings are close, because an orb sits in FRONT of
        // the plate - filing it under "Backgrounds" would be a lie the day the two treatments
        // diverge. Not the UI lane either: these are 1-2 MB full-alpha spheres and atlasing
        // them would blow the atlas for no packing win.
        //
        // Mips OFF - an orb is drawn at or near its native size over a plate, never minified
        // into the distance. Alpha is load-bearing (VersusOverlay composites it), so no crunch.
        static void ApplyOrb(TextureImporter importer)
        {
            ApplySpriteCommon(importer);
            importer.mipmapEnabled = false;
            SetPlatform(importer, "Standalone", 2048, TextureImporterFormat.BC7);
            SetPlatform(importer, "Android", 1024, TextureImporterFormat.ASTC_6x6);
            SetPlatform(importer, "iPhone", 1024, TextureImporterFormat.ASTC_6x6);
        }

        // UI atlas sources (frames/gems/icons/plates): fixed pixel size, mips OFF.
        static void ApplyUiAtlas(TextureImporter importer) => ApplyUiAtlas(importer, mips: false);

        // Card and board frames: the atlas lane, but mips ON. These are 1024-2048px paintings drawn
        // at 110-384px, and without a mip chain their filigree and ember cracks alias into speckle
        // (2026-09-15, compared side by side in art-pipeline/mip-compare/). Sliced sprites still
        // work with mips; heavily shrunk slices only go slightly soft.
        static void ApplyUiFrame(TextureImporter importer) => ApplyUiAtlas(importer, mips: true);

        static void ApplyUiAtlas(TextureImporter importer, bool mips)
        {
            ApplySpriteCommon(importer);
            importer.mipmapEnabled = mips;

            // Sliced draw mode (BoardController.MakeSprite sizes frames/gems/icons by
            // SpriteRenderer.size) requires a Full Rect sprite mesh; the default Tight
            // mesh misrenders and warns when .size is set. spriteMeshType lives on
            // TextureImporterSettings, so read-modify-write it back.
            var spriteSettings = new TextureImporterSettings();
            importer.ReadTextureSettings(spriteSettings);
            spriteSettings.spriteMeshType = SpriteMeshType.FullRect;
            importer.SetTextureSettings(spriteSettings);

            SetPlatform(importer, "Standalone", 2048, TextureImporterFormat.BC7);
            SetPlatform(importer, "Android", 2048, TextureImporterFormat.ASTC_6x6);
            SetPlatform(importer, "iPhone", 2048, TextureImporterFormat.ASTC_6x6);
        }

        // Painterly icons drawn far below source size (the class-tab hero-power art: 1024px+ shown
        // at ~76px). The UI lane's mips-OFF is right for frames drawn near 1:1 and wrong here —
        // without a mip chain bright detail aliases into glitter (2026-09-15). Mips ON, capped at
        // 256, since no screen draws these larger.
        static void ApplyMinifiedIcon(TextureImporter importer)
        {
            ApplySpriteCommon(importer);
            importer.mipmapEnabled = true;
            SetPlatform(importer, "Standalone", 256, TextureImporterFormat.BC7);
            SetPlatform(importer, "Android", 256, TextureImporterFormat.ASTC_6x6);
            SetPlatform(importer, "iPhone", 256, TextureImporterFormat.ASTC_6x6);
        }

        // Currency art is minified beside numbers at multiple UI sizes.
        static void ApplyCurrencyIcon(TextureImporter importer)
        {
            ApplyMinifiedIcon(importer);
            importer.filterMode = FilterMode.Trilinear;
        }

        // FX masks / flipbooks: linear data, not sprites. Single-channel-friendly formats.
        static void ApplyVfxMap(TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = false;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.crunchedCompression = false;
            SetPlatform(importer, "Standalone", 256, TextureImporterFormat.BC4);
            SetPlatform(importer, "Android", 256, TextureImporterFormat.ASTC_8x8);
            SetPlatform(importer, "iPhone", 256, TextureImporterFormat.ASTC_8x8);
        }

        static void ApplySpriteCommon(TextureImporter importer)
        {
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.sRGBTexture = true;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            importer.crunchedCompression = false;
        }

        static void SetPlatform(TextureImporter importer, string platform, int maxSize, TextureImporterFormat format)
        {
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                name = platform,
                overridden = true,
                maxTextureSize = maxSize,
                format = format,
                compressionQuality = 50,
            });
        }
    }
}
