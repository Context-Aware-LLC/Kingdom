using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Enforces import settings for all card/hero/UI/VFX art by folder path,
    /// per techart/pipeline.md §3–4. Artists (human or agent) drop PNGs into
    /// Assets/Art/... and never touch the importer — settings are applied here,
    /// deterministically, on every (re)import.
    ///
    /// Folder contract (pipeline.md §8):
    ///   Assets/Art/Cards/&lt;setId&gt;/&lt;artId&gt;.png  — streamed card illustrations
    ///   Assets/Art/Hero/&lt;heroId&gt;.png            — hero portraits (same treatment)
    ///   Assets/Art/UI/...                        — frames/gems/icons/plates (atlas, no mips)
    ///   Assets/Art/VFX/Textures/...              — FX masks/flipbooks (linear data)
    /// </summary>
    internal sealed class CardArtPostprocessor : AssetPostprocessor
    {
        const string CardsRoot = "Assets/Art/Cards/";
        const string HeroRoot = "Assets/Art/Hero/";
        const string UiRoot = "Assets/Art/UI/";
        const string VfxRoot = "Assets/Art/VFX/Textures/";

        void OnPreprocessTexture()
        {
            var importer = (TextureImporter)assetImporter;
            var path = assetPath.Replace('\\', '/');

            if (path.StartsWith(CardsRoot) || path.StartsWith(HeroRoot))
                ApplyIllustration(importer);
            else if (path.StartsWith(UiRoot))
                ApplyUiAtlas(importer);
            else if (path.StartsWith(VfxRoot))
                ApplyVfxMap(importer);
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

        // UI atlas sources (frames/gems/icons/plates): fixed pixel size, mips OFF.
        static void ApplyUiAtlas(TextureImporter importer)
        {
            ApplySpriteCommon(importer);
            importer.mipmapEnabled = false;

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
