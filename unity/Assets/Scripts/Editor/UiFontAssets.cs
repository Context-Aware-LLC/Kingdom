using TMPro;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace Kingdom.Editor
{
    /// <summary>
    /// Builds the TMP font assets for the board-label cuts of Google Sans Flex and registers them in
    /// the Fonts group under the addresses <c>BoardController.ApplyUiFont</c> loads.
    ///
    /// The .ttf files come from <c>tooling/scripts/instance_font.py</c> (settings chosen in the Font
    /// Bench). Run this only when a cut is added or an asset is missing. Settings match
    /// GoogleSansFlex-Card SDF: 90 pt sampling, padding 9, SDFAA, 1024 atlases.
    ///
    /// <para><b>The atlases are baked and committed, not filled at run time</b> (Sam, 2026-10-14).
    /// Every font asset here was dynamic with its data cleared on build, so the committed copy was
    /// an empty 1x1 atlas and every Play in the Editor wrote a thousand lines of glyphs into it.
    /// <see cref="BakeAtlases"/> fills each one with <see cref="Charset"/> and keeps it. They stay
    /// dynamic, so a character outside the set (a player's name) still renders; only such a
    /// character still dirties the asset in the Editor. <b>Re-cutting a .ttf in place now needs a
    /// re-bake</b>: the baked glyphs were rasterised from the old cut.</para>
    /// </summary>
    public static class UiFontAssets
    {
        private const string Folder = "Assets/Art/Fonts";
        private static readonly string[] Cuts = { "GoogleSansFlex-UI", "GoogleSansFlex-UIBold" };

        /// <summary>Every character the game's own text uses: printable ASCII, plus the
        /// punctuation and symbols found in the card data, UXML, USS and C# string literals
        /// (2026-10-14), plus curly quotes. Left out: ← and ⌄, which the USS and UXML use
        /// and none of these fonts has. A character added to game text and missing here still
        /// renders; it just lands in the atlas at run time and dirties the asset.</summary>
        internal const string Charset =
            " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~"
            + "§·×–—•…‘’“”";

        private static readonly string[] TmpAssets =
        {
            "AlfaSlabOne-Regular SDF", "GoogleSansFlex-Card SDF", "GoogleSansFlex-UI SDF", "GoogleSansFlex-UIBold SDF",
        };

        /// <summary>Clear every font asset and refill it with <see cref="Charset"/>, so the
        /// committed atlas is the shipped one. Run after re-cutting a .ttf or changing the set.</summary>
        [MenuItem("Kingdom/Regenerate/Fonts/Bake Atlases")]
        public static void BakeAtlases()
        {
            foreach (string name in TmpAssets)
            {
                var fa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>($"{Folder}/{name}.asset");
                if (fa == null) { Debug.LogError($"[UiFontAssets] {name} is missing."); continue; }
                fa.isMultiAtlasTexturesEnabled = true;
                fa.ClearFontAssetData(true);
                fa.TryAddCharacters(Charset, out string missing, true);
                Keep(fa, name, missing);
            }

            var menu = AssetDatabase.LoadAssetAtPath<UnityEngine.TextCore.Text.FontAsset>(MenuButtonFontPath);
            if (menu == null) Debug.LogError($"[UiFontAssets] {MenuButtonFontPath} is missing.");
            else
            {
                menu.isMultiAtlasTexturesEnabled = true;
                menu.ClearFontAssetData(true);
                menu.TryAddCharacters(Charset, out string missing, true);
                Keep(menu, menu.name, missing);
            }
            AssetDatabase.SaveAssets();
        }

        // Turns off clear-on-build, which would empty the atlas again in every build, and marks
        // the asset for saving.
        private static void Keep(Object fa, string name, string missing)
        {
            var so = new SerializedObject(fa);
            SerializedProperty clear = so.FindProperty("m_ClearDynamicDataOnBuild");
            if (clear != null) { clear.boolValue = false; so.ApplyModifiedPropertiesWithoutUndo(); }
            EditorUtility.SetDirty(fa);
            Debug.Log($"[UiFontAssets] baked {name}"
                      + (string.IsNullOrEmpty(missing) ? "" : $"; not in the font: {missing}"));
        }

        [MenuItem("Kingdom/Regenerate/Fonts/Board Label Assets")]
        public static void Build()
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            AddressableAssetGroup group = settings.FindGroup("Fonts");
            if (group == null)
            {
                Debug.LogError("[UiFontAssets] no Fonts group — run Kingdom/Regenerate/Addressables Groups first.");
                return;
            }

            foreach (string cut in Cuts)
            {
                string ttfPath = $"{Folder}/{cut}.ttf";
                string assetPath = $"{Folder}/{cut} SDF.asset";
                var font = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
                if (font == null)
                {
                    Debug.LogError($"[UiFontAssets] {ttfPath} is missing — cut it with tooling/scripts/instance_font.py.");
                    continue;
                }

                if (AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(assetPath) == null)
                {
                    TMP_FontAsset fa = TMP_FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA,
                                                                     1024, 1024, AtlasPopulationMode.Dynamic, false);
                    fa.name = $"{cut} SDF";
                    AssetDatabase.CreateAsset(fa, assetPath);
                    fa.atlasTexture.name = $"{cut} SDF Atlas";
                    AssetDatabase.AddObjectToAsset(fa.atlasTexture, fa);
                    fa.material.name = $"{cut} SDF Material";
                    AssetDatabase.AddObjectToAsset(fa.material, fa);

                    Debug.Log($"[UiFontAssets] created {assetPath}; run Kingdom/Regenerate/Fonts/Bake Atlases");
                }

                settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(assetPath), group)
                        .SetAddress($"fonts/{cut} SDF");
            }

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
            AssetDatabase.SaveAssets();
        }

        /// <summary>The menu-button face: a UI Toolkit font asset (TextCore, not TMP — the menus are
        /// UI Toolkit and cannot use a TMP asset), which theme.uss points every button at. It is
        /// referenced from USS, so it needs no Addressables entry.</summary>
        public const string MenuButtonFontPath = "Assets/Art/Fonts/ModernAntiqua-Regular UITK.asset";

        [MenuItem("Kingdom/Regenerate/Fonts/Menu Button Asset")]
        public static void BuildMenuButtonFont()
        {
            const string ttfPath = "Assets/Art/Fonts/ModernAntiqua-Regular.ttf";
            var font = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
            if (font == null) { Debug.LogError($"[UiFontAssets] {ttfPath} is missing."); return; }
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.TextCore.Text.FontAsset>(MenuButtonFontPath) != null) return;

            var fa = UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(font);
            fa.name = "ModernAntiqua-Regular UITK";
            AssetDatabase.CreateAsset(fa, MenuButtonFontPath);
            foreach (Texture2D atlas in fa.atlasTextures)
            {
                if (atlas == null) continue;
                atlas.name = fa.name + " Atlas";
                AssetDatabase.AddObjectToAsset(atlas, fa);
            }
            if (fa.material != null)
            {
                fa.material.name = fa.name + " Material";
                AssetDatabase.AddObjectToAsset(fa.material, fa);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[UiFontAssets] created {MenuButtonFontPath}");
        }
    }
}
