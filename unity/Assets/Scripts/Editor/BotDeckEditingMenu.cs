using Kingdom.Presentation;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Kingdom.Editor
{
    /// <summary>Toggles <see cref="BotDeckEditing"/>. Checking it opens the Collection scene and
    /// presses Play, so the drawer comes up on the bots; unchecking it stops Play, so the next
    /// session is back on this account's own decks.</summary>
    internal static class BotDeckEditingMenu
    {
        private const string Path = "Kingdom/Dev/Edit Bot Decks";
        private const string CollectionScene = "Assets/Scenes/Collection.unity";

        [MenuItem(Path)]
        private static void Toggle()
        {
            bool on = !BotDeckEditing.Enabled;
            if (!on)
            {
                BotDeckEditing.Enabled = false;
                // The screen latches the mode when it opens, so a running session would keep
                // editing bots; stopping it is what makes unchecking mean "off" (Exit saves first).
                if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
                return;
            }

            // A scene cannot be opened mid-Play, and leaving Play only completes after this returns.
            if (EditorApplication.isPlaying)
            {
                UnityEngine.Debug.LogWarning("[BotDecks] Stop Play first, then check Edit Bot Decks again.");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            BotDeckEditing.Enabled = true;
            EditorSceneManager.OpenScene(CollectionScene);
            EditorApplication.isPlaying = true;
        }

        [MenuItem(Path, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(Path, BotDeckEditing.Enabled);
            return true;
        }
    }
}
