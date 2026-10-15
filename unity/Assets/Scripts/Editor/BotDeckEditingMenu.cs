using Kingdom.Presentation;
using UnityEditor;

namespace Kingdom.Editor
{
    /// <summary>Toggles <see cref="BotDeckEditing"/>: the Collection screen edits the bot decks.
    /// Takes effect the next time the Collection screen opens.</summary>
    internal static class BotDeckEditingMenu
    {
        private const string Path = "Kingdom/Dev/Edit Bot Decks";

        [MenuItem(Path)]
        private static void Toggle() => BotDeckEditing.Enabled = !BotDeckEditing.Enabled;

        [MenuItem(Path, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(Path, BotDeckEditing.Enabled);
            return true;
        }
    }
}
