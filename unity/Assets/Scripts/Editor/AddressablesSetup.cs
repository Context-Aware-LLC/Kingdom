using System.Collections.Generic;
using System.IO;
using Kingdom.Presentation;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>
    /// One-shot (idempotent) Addressables bootstrap per hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/pipeline.md §8:
    ///   UI_Core           — Assets/Art/UI/** and Cards/Backs/** at "ui/&lt;fileName&gt;" (always loaded)
    ///   CardArt_&lt;setId&gt;   — one group per set folder of Assets/Art/Cards/ (excluding Backs), at "card_art/&lt;fileName&gt;".
    ///                       CORE additionally carries Assets/Art/Hero/**.
    ///   CardData          — card set JSON at "cards/&lt;setId&gt;.cards"
    ///   PackArt_&lt;setId&gt;   — one group per subfolder of Assets/Art/Shop/Packs/, sprites at
    ///                       "pack_art/&lt;fileName&gt;" and that set's packart.json at
    ///                       "pack_art/&lt;setId&gt;.manifest".
    /// A new card set needs NO edit here — drop Assets/Art/Cards/&lt;setId&gt;/ and re-run. Re-running
    /// the menu item after an art drop re-syncs entries without duplicating them.
    /// Also migrates Assets/Resources/cards/*.cards.json out of Resources/ (M2 D1).
    /// </summary>
    public static class AddressablesSetup
    {
        private const string CardDataFolder = "Assets/CardData";
        private const string CardArtRoot = "Assets/Art/Cards";
        private const string PackArtRoot = "Assets/Art/Shop/Packs";
        public const string SoundEffectsFolder = "Assets/Sound/SFX";

        /// <summary>
        /// Hero voice lines, addressed <c>vo/&lt;fileName&gt;</c>. Scanned recursively and addressed by
        /// FILE NAME, so the per-class subfolders (Voice/Grave/, Voice/Ember/, …) are organisational
        /// only — the same arrangement Backgrounds uses, and the opposite of Art/Cards/&lt;setId&gt;/,
        /// where the folder picks the bundle. Filenames must therefore be unique across the whole
        /// tree, not just within a class.
        /// </summary>
        public const string VoiceFolder = "Assets/Sound/Voice";

        /// <summary>Background music, addressed <c>music/&lt;fileName&gt;</c>. Each subfolder is a
        /// POOL — its clips carry the label <c>music-&lt;folder lowercased&gt;</c>, and MusicDirector
        /// loads a whole label at once (shuffled, forever) rather than one address at a time like
        /// SFX/VO. Unlike Voice/ and Backgrounds/, the subfolder here is NOT organisational: it
        /// decides which screens a track plays on. Clips loose in the root belong to no pool and
        /// are not registered.</summary>
        public const string MusicFolder = "Assets/Sound/Music";

        /// <summary>Every group name this run touched, in the order it touched them, so the
        /// closing log reports the run rather than a list someone has to remember to edit.</summary>
        private static readonly List<string> SyncedGroups = new List<string>();

        [MenuItem("Kingdom/Regenerate/Addressables Groups")]
        public static void Run()
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            SyncedGroups.Clear();

            var uiCore = GetGroup(settings, "UI_Core");
            AddFolderSprites(settings, uiCore, "Assets/Art/UI", "ui");
            AddFolderSprites(settings, uiCore, CardBackCatalog.ArtFolder, "ui");
            AddFolderAssets(settings, uiCore, "Assets/Materials", "materials", "t:Material");
            // One CardArt_<setId> group per SET FOLDER FOUND ON DISK, not a hardcoded list.
            // REKINDLING shipped card rows and art with no group because the previous three lines
            // named CORE and PENDINGHAM literally: EditorArtProvider resolves by AssetDatabase and
            // finds art whether or not it is addressable, so an unregistered set looks correct in
            // the Editor and renders BLANK in a player build, silently. Enumerating the folder
            // means a new set cannot be forgotten — it only has to exist.
            foreach (string setFolder in Directory.GetDirectories(CardArtRoot))
            {
                string setId = Path.GetFileName(setFolder);
                // Card backs retain the UI provider's addresses and residency; they are not a card set.
                if (setId.Equals(Path.GetFileName(CardBackCatalog.ArtFolder), System.StringComparison.OrdinalIgnoreCase))
                    continue;
                var group = GetGroup(settings, "CardArt_" + setId);
                AddFolderSprites(settings, group, $"{CardArtRoot}/{setId}", "card_art");

                // Hero portraits ride along with CORE — they are set-agnostic and always loaded.
                if (setId == "CORE") AddFolderSprites(settings, group, "Assets/Art/Hero", "card_art");
            }

            // One PackArt_<setId> group per pack-art folder found on disk — same enumerate-the-disk
            // rule as CardArt above, for the same reason: an expansion's packaging must not be
            // forgettable. Sprites address flat as pack_art/<fileName> (so filenames stay unique
            // across sets, exactly as card art already requires); the manifest cannot, because
            // every set's is called packart.json, so it is addressed from its FOLDER name.
            if (Directory.Exists(PackArtRoot))
            {
                foreach (string setFolder in Directory.GetDirectories(PackArtRoot))
                {
                    string setId = Path.GetFileName(setFolder);
                    var packGroup = GetGroup(settings, "PackArt_" + setId);
                    AddFolderSprites(settings, packGroup, $"{PackArtRoot}/{setId}", "pack_art");

                    string manifestPath = $"{PackArtRoot}/{setId}/packart.json";
                    string manifestGuid = AssetDatabase.AssetPathToGUID(manifestPath);
                    if (string.IsNullOrEmpty(manifestGuid))
                    {
                        // Art with no manifest resolves to nothing at runtime: PackArtProvider reads
                        // the manifest first and never scans the folder in a build. Say so here,
                        // because the Editor's AssetDatabase fallback hides it during authoring.
                        Debug.LogWarning($"[AddressablesSetup] {manifestPath} is missing — " +
                                         $"PackArt_{setId} will ship art that nothing can look up.");
                    }
                    else
                    {
                        settings.CreateOrMoveEntry(manifestGuid, packGroup)
                                .SetAddress($"pack_art/{setId}.manifest");
                    }
                }
            }

            var dataGroup = GetGroup(settings, "CardData");
            foreach (string guid in AssetDatabase.FindAssets("t:TextAsset", new[] { CardDataFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith(".cards.json")) continue;
                string setFile = Path.GetFileName(path);                       // "Pendingham.cards.json"
                string address = "cards/" + setFile.Substring(0, setFile.Length - ".json".Length); // "cards/Pendingham.cards"
                var entry = settings.CreateOrMoveEntry(guid, dataGroup);
                entry.SetAddress(address);
            }

            var bgGroup = GetGroup(settings, "Backgrounds");
            AddFolderAssets(settings, bgGroup, "Assets/Art/Backgrounds", "backgrounds", "t:Texture2D");
            // Assets/Art/Loading was a staging folder: of its ten textures exactly three were
            // ever loaded (the orbs), and the other seven only looked live because this scan
            // registered the folder wholesale. Orbs now have their own root; the rest went to
            // art-pipeline/junk/. Backgrounds is scanned recursively and addresses by FILE NAME,
            // so Backgrounds/Loading/ and Backgrounds/Versus/ need no entry of their own and
            // keep the addresses they already had.
            var orbsGroup = GetGroup(settings, "Orbs");
            AddFolderAssets(settings, orbsGroup, "Assets/Art/Orbs", "orbs", "t:Texture2D");
            var fontsGroup = GetGroup(settings, "Fonts");
            AddFolderAssets(settings, fontsGroup, "Assets/Art/Fonts", "fonts", "t:TMP_FontAsset");
            var soundEffectsGroup = GetGroup(settings, "SoundEffects");
            AddFolderAssets(settings, soundEffectsGroup, SoundEffectsFolder, "sfx", "t:AudioClip");
            var voiceGroup = GetGroup(settings, "Voice");
            AddFolderAssets(settings, voiceGroup, VoiceFolder, "vo", "t:AudioClip");
            var musicGroup = GetGroup(settings, "Music");
            foreach (string poolFolder in AssetDatabase.GetSubFolders(MusicFolder))
            {
                AddFolderAssets(settings, musicGroup, poolFolder, "music", "t:AudioClip");
                LabelFolderAssets(settings, poolFolder, "t:AudioClip",
                                  "music-" + Path.GetFileName(poolFolder).ToLowerInvariant());
            }

            settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
            AssetDatabase.SaveAssets();
            // Report what this run actually touched, never a written-down list. The literal one
            // here named seven groups and was already wrong the moment a set folder appeared that
            // it did not mention — which is what a per-set loop guarantees will happen. A summary
            // you have to remember to update is a summary that lies about the run you just did.
            Debug.Log("[AddressablesSetup] groups synced: " + string.Join(", ", SyncedGroups));
        }

        /// <summary>
        /// Registers one clip selected by the card-authoring window immediately, so attaching a
        /// sound is a complete operation and does not depend on remembering to run the bulk setup
        /// menu afterwards.
        /// </summary>
        public static bool TryRegisterSoundEffect(AudioClip clip, out string soundId, out string error)
        {
            soundId = "";
            error = "";
            if (clip == null) return true;

            string path = AssetDatabase.GetAssetPath(clip).Replace('\\', '/');
            string prefix = SoundEffectsFolder + "/";
            if (!path.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
            {
                error = $"Sound effects must live under {SoundEffectsFolder}. Move '{path}' there and attach it again.";
                return false;
            }

            soundId = Path.GetFileNameWithoutExtension(path);
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
            {
                error = $"Unity has no asset GUID for '{path}'.";
                soundId = "";
                return false;
            }

            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            var entries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(entries, false);
            string address = $"sfx/{soundId}";
            foreach (AddressableAssetEntry existing in entries)
                if (existing.address == address && existing.guid != guid)
                {
                    error = $"Another AudioClip already owns Addressables key '{address}'. Sound filenames must be unique.";
                    soundId = "";
                    return false;
                }

            AddressableAssetGroup group = GetGroup(settings, "SoundEffects");
            settings.CreateOrMoveEntry(guid, group).SetAddress(address);
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryModified, null, true, true);
            AssetDatabase.SaveAssets();
            return true;
        }

        private static AddressableAssetGroup GetGroup(AddressableAssetSettings settings, string name)
        {
            if (!SyncedGroups.Contains(name)) SyncedGroups.Add(name);
            AddressableAssetGroup group = settings.FindGroup(name);
            if (group == null)
            {
                group = settings.CreateGroup(name, false, false, false, null,
                    typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                // Local packed groups only for M2 — decision 10(a), approved by Sam 2026-07-12 in
                // hist-docs/source-snapshots/2026-07-31-pre-reorganization/project/M2-KICKOFF.md:200.
                // It accepted CLIENT REBUILDS as the balance-patch delivery mechanism and deferred
                // 10(b), the remote catalog, to M3. The path back is ADR-0008 §6, which proposes
                // executing 10(b). ("pending" was wrong: the decision was made, the option deferred.)
                //
                // RETIREMENT TRIGGER: if ADR-0008 §6 is accepted and these become remote groups,
                // this comment and ADR-0008's Context section both stop describing the present.
                schema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
                schema.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            }
            return group;
        }

        private static void AddFolderSprites(AddressableAssetSettings settings, AddressableAssetGroup group,
                                             string folder, string addressPrefix)
            => AddFolderAssets(settings, group, folder, addressPrefix, "t:Sprite");

        private static void AddFolderAssets(AddressableAssetSettings settings, AddressableAssetGroup group,
                                            string folder, string addressPrefix, string filter)
        {
            if (!AssetDatabase.IsValidFolder(folder))
            {
                Debug.LogWarning($"[AddressablesSetup] folder missing, skipped: {folder}");
                return;
            }
            foreach (string guid in AssetDatabase.FindAssets(filter, new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var entry = settings.CreateOrMoveEntry(guid, group);
                entry.SetAddress($"{addressPrefix}/{Path.GetFileNameWithoutExtension(path)}");
            }
        }

        /// <summary>Tags every matching asset already in the settings with <paramref name="label"/>,
        /// registering the label first if this is its first use. Separate from
        /// <see cref="AddFolderAssets"/> because every other caller wants one address per file, not
        /// a shared label — Music pools are the only assets loaded as a whole rather than by
        /// specific address, so they are the only ones that need this.</summary>
        private static void LabelFolderAssets(AddressableAssetSettings settings, string folder,
                                              string filter, string label)
        {
            if (!settings.GetLabels().Contains(label)) settings.AddLabel(label);
            foreach (string guid in AssetDatabase.FindAssets(filter, new[] { folder }))
                settings.FindAssetEntry(guid)?.SetLabel(label, true, true);
        }
    }
}
