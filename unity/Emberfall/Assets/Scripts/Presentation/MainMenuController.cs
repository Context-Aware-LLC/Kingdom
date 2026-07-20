using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The front door — the first screen a player sees (ui/ux-flows.md: S1 Main Menu).
    /// A UI Toolkit meta-screen in the deckbuilder's lineage: theme.uss tokens, a UIDocument
    /// host, elements queried by name and wired in <see cref="OnEnable"/>.
    ///
    /// First pass is screen-only: the five destinations (Find Game, Single Player, Deck Builder,
    /// Store, Leaderboards) log their intent. Navigation is a later pass — nothing in the project
    /// loads scenes at runtime yet, so this screen is where that pattern will land. Each handler
    /// is a named method so the wire-up is the seam a scene-loader drops into.
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// MainMenuScreen.uxml and whose Panel Settings is MainMenuPanelSettings.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MainMenuController : MonoBehaviour
    {
        private VisualElement _root;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            if (_root == null) { Debug.LogError("[MainMenu] No rootVisualElement — is the UXML assigned?"); return; }

            Wire("btn-find-game", OnFindGame);
            Wire("btn-single-player", OnSinglePlayer);
            Wire("btn-deck-builder", OnDeckBuilder);
            Wire("btn-store", OnStore);
            Wire("btn-leaderboards", OnLeaderboards);
        }

        private void Wire(string name, System.Action onClick)
        {
            var button = _root.Q<Button>(name);
            if (button == null) { Debug.LogWarning($"[MainMenu] Button '{name}' not found in UXML."); return; }
            button.RegisterCallback<ClickEvent>(_ => onClick());
        }

        // ---- Destinations (stubbed — navigation is a later pass) ----
        // Find Game → BoardNetworked.unity (server matchmaking; the only destination with a backend today).
        private void OnFindGame() => Debug.Log("[MainMenu] Find Game → matchmaking (BoardNetworked). Not wired yet.");

        // Single Player → Board.unity (local vs-AI slice via GameBootstrap).
        private void OnSinglePlayer() => Debug.Log("[MainMenu] Single Player → Board (vs AI). Not wired yet.");

        // Deck Builder → DeckbuilderSpike.unity (exists as a spike).
        private void OnDeckBuilder() => Debug.Log("[MainMenu] Deck Builder → DeckbuilderSpike. Not wired yet.");

        // Store → no backend yet.
        private void OnStore() => Debug.Log("[MainMenu] Store → coming soon. No backend yet.");

        // Leaderboards → no backend yet.
        private void OnLeaderboards() => Debug.Log("[MainMenu] Leaderboards → coming soon. No backend yet.");
    }
}
