using System;
using System.Collections.Generic;
using System.Reflection;
using Emberfall.Core.Collection;
using Emberfall.Net.Client.Collection;
using Emberfall.Presentation;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Editor
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

                Set(controller, "_cardBackOwnership", CollectionSnapshot.Unconstrained(default,
                    cardBacks: new Dictionary<int, long> { [CardBackDefinitions.DefaultId] = 1 }));
                build.Invoke(controller, null);
                Require(grid.childCount == 1 && save.enabledSelf, "Owned back must be selectable.");
                Require(grid[0].Q<Label>() == null, "Artwork must remain free of captions.");

                Set(controller, "_cardBackOwnership", CollectionSnapshot.Unconstrained(default,
                    cardBacks: new Dictionary<int, long>()));
                build.Invoke(controller, null);
                Require(grid.childCount == 0 && !save.enabledSelf, "Unowned backs must not be offered, even to a superuser.");

                Set(controller, "_cardBackOwnership", null);
                build.Invoke(controller, null);
                Require(grid.childCount == 0 && !save.enabledSelf, "Unknown ownership must not unlock backs.");
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
            Debug.Log("[CardBackOwnershipVerify] moved art, Addressables, owned/unowned/unknown picker: PASS");
        }

        private static void Set(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
