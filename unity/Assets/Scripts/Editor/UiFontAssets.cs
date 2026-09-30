using TMPro;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace Emberfall.Editor
{
    /// <summary>
    /// Builds the TMP font assets for the board-label cuts of Google Sans Flex and registers them in
    /// the Fonts group under the addresses <c>BoardController.ApplyUiFont</c> loads.
    ///
    /// The .ttf files come from <c>tooling/scripts/instance_font.py</c> (settings chosen in the Font
    /// Bench). Re-cutting a .ttf in place needs NO rebuild here: the assets are dynamic, so they
    /// re-rasterise from the source file. Run this only when a cut is added or an asset is missing.
    /// Settings match GoogleSansFlex-Card SDF: 90 pt sampling, padding 9, SDFAA, one 1024 atlas,
    /// dynamic data cleared on build.
    /// </summary>
    public static class UiFontAssets
    {
        private const string Folder = "Assets/Art/Fonts";
        private static readonly string[] Cuts = { "GoogleSansFlex-UI", "GoogleSansFlex-UIBold" };

        [MenuItem("Emberfall/Fonts/Build Board Label Font Assets")]
        public static void Build()
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            AddressableAssetGroup group = settings.FindGroup("Fonts");
            if (group == null)
            {
                Debug.LogError("[UiFontAssets] no Fonts group — run Emberfall/Addressables/Setup Groups first.");
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

                    var so = new SerializedObject(fa);
                    SerializedProperty clear = so.FindProperty("m_ClearDynamicDataOnBuild");
                    if (clear != null) { clear.boolValue = true; so.ApplyModifiedPropertiesWithoutUndo(); }
                    Debug.Log($"[UiFontAssets] created {assetPath}");
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

        [MenuItem("Emberfall/Fonts/Build Menu Button Font Asset")]
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
