using System;
using System.Collections.Generic;
using System.Reflection;
using Kingdom.Core.Collection;
using Kingdom.Net.Client.Collection;
using Kingdom.Presentation;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Editor
{
    public static class CardBackOwnershipVerify
    {
        public static void Run()
        {
            const string guid = "c5630a76852906f4c85f5e02f8814b9b";
            string path = CardBackCatalog.ArtFolder + "/card_back.png";
            Require(AssetDatabase.GUIDToAssetPath(guid) == path, "Moved back must retain its GUID.");
            var provider = new EditorUiSpriteProvider();
            Require(provider.Get("card_back") == AssetDatabase.LoadAssetAtPath<Sprite>(path)
                && provider.Get("card_back") != null, "Editor must resolve the relocated back.");
            var entry = AddressableAssetSettingsDefaultObject.Settings.FindAssetEntry(guid);
            Require(entry != null && entry.address == "ui/card_back" && entry.parentGroup.Name == "UI_Core",
                "Player builds must retain the existing UI_Core address.");

            // Every catalog row draws in the Editor AND in a player build. The Editor provider finds
            // art by AssetDatabase whether or not it is addressable, so a back missing from UI_Core
            // looks right here and renders blank on a device: check the entry, not just the sprite.
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (CardBackDefinition back in CardBackCatalog.All)
            {
                string backPath = CardBackCatalog.ArtFolder + "/" + back.SpriteKey + ".png";
                Sprite sprite = provider.Get(back.SpriteKey);
                Require(sprite != null && sprite == AssetDatabase.LoadAssetAtPath<Sprite>(backPath),
                    $"Back {back.Id} ({back.Handle}) has no sprite at {backPath}.");
                var backEntry = AddressableAssetSettingsDefaultObject.Settings.FindAssetEntry(
                    AssetDatabase.AssetPathToGUID(backPath));
                Require(backEntry != null && backEntry.address == "ui/" + back.SpriteKey
                    && backEntry.parentGroup.Name == "UI_Core",
                    $"Back {back.Id} ({back.Handle}) is not addressed ui/{back.SpriteKey} in UI_Core; run Kingdom/Regenerate/Addressables Groups.");
                Require(keys.Add(back.SpriteKey), $"Back {back.Id} shares sprite key {back.SpriteKey}.");
            }
            Require(CardBackDefinitions.PlaceBackIds.Count == 3 && CardBackDefinitions.PoolIds.Count > 0,
                "The King's Favor needs three place backs and a pool.");
            foreach (int id in CardBackDefinitions.PlaceBackIds)
                Require(CardBackDefinitions.TryGet(id, out _), $"Place back {id} is not in the catalog.");
            foreach (int id in CardBackDefinitions.PoolIds)
                Require(CardBackDefinitions.TryGet(id, out _), $"Pool back {id} is not in the catalog.");

            var host = new GameObject("CardBackOwnershipVerify");
            host.SetActive(false); // Never compose a real account or issue network requests.
            try
            {
                var controller = host.AddComponent<CharacterRosterController>();
                var grid = new VisualElement();
                var save = new Button();
                Set(controller, "_deckBackGrid", grid);
                Set(controller, "_deckModalConfirm", save);
                Set(controller, "_uiSprites", provider);
                Set(controller, "_pendingCardBackId", CardBackDefinitions.DefaultId);
                var build = typeof(CharacterRosterController).GetMethod("BuildCardBackGrid",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                Set(controller, "_cardBackOwnership", CollectionSnapshot.Synced(OwnershipRecord.AllAtLimit, default,
                    cardBacks: new Dictionary<int, long> { [CardBackDefinitions.DefaultId] = 1 }));
                build.Invoke(controller, null);
                Require(grid.childCount == 1 && save.enabledSelf, "Owned back must be selectable.");
                Require(grid[0].Q<Label>() == null, "Artwork must remain free of captions.");

                Set(controller, "_cardBackOwnership", CollectionSnapshot.Synced(OwnershipRecord.AllAtLimit, default,
                    cardBacks: new Dictionary<int, long>
                    {
                        [CardBackDefinitions.DefaultId] = 1,
                        [CardBackDefinitions.PoolIds[0]] = 1,
                    }));
                build.Invoke(controller, null);
                Require(grid.childCount == 2 && save.enabledSelf, "A won back must be offered beside the default, and no other.");

                Set(controller, "_cardBackOwnership", CollectionSnapshot.Synced(OwnershipRecord.AllAtLimit, default,
                    cardBacks: new Dictionary<int, long>()));
                build.Invoke(controller, null);
                Require(grid.childCount == 0 && !save.enabledSelf, "Unowned backs must not be offered, even to a superuser.");

                Set(controller, "_cardBackOwnership", null);
                build.Invoke(controller, null);
                Require(grid.childCount == 0 && !save.enabledSelf, "Unknown ownership must not unlock backs.");
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
            Debug.Log("[CardBackOwnershipVerify] moved art, every back's sprite and UI_Core address, prize lists, owned/unowned/unknown picker: PASS");
        }

        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
