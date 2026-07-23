using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// In-match settings overlay (C-G1-3 / decision 23 §5). A UI Toolkit meta-surface mounted
    /// over the world-space match: a persistent corner button that opens a modal panel whose
    /// in-match top item is CONCEDE, behind a confirm ("leaving IS forfeiting"). Sound / Video
    /// are deferred — they will land as rows in #view-menu without touching this wiring.
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// MatchSettingsScreen.uxml and whose Panel Settings does NOT clear color (so the board
    /// shows through). The bootstraps build this at runtime via Resources and call <see cref="Init"/>.
    ///
    /// UI Toolkit does not block Unity's physics input, so <see cref="IsBlockingInput"/> is the
    /// gate InputRouter consults to keep board clicks from passing through the scrim — and to keep
    /// a card-selected click on the corner button from playing a card into empty space.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MatchSettingsController : MonoBehaviour
    {
        private Action _onConcede;
        private VisualElement _overlay;
        private VisualElement _viewMenu;
        private VisualElement _viewConfirm;

        private bool _isOpen;
        private bool _pointerOverGear;

        /// <summary>True while the overlay is open OR the pointer is over the corner button.
        /// InputRouter suppresses board input for the duration (UI Toolkit won't do it for us).</summary>
        public bool IsBlockingInput => _isOpen || _pointerOverGear;

        public void Init(Action onConcede)
        {
            _onConcede = onConcede;

            var root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[MatchSettings] No rootVisualElement — is the UXML/PanelSettings assigned before Init?");
                return;
            }

            _overlay     = root.Q<VisualElement>("overlay");
            _viewMenu    = root.Q<VisualElement>("view-menu");
            _viewConfirm = root.Q<VisualElement>("view-confirm");

            var gear = root.Q<Button>("btn-settings");
            if (gear != null)
            {
                gear.RegisterCallback<ClickEvent>(_ => Open());
                // The corner button has no physics collider, so hovering it must gate board input
                // (otherwise a card-selected click here plays into empty space — see InputRouter).
                gear.RegisterCallback<PointerEnterEvent>(_ => _pointerOverGear = true);
                gear.RegisterCallback<PointerLeaveEvent>(_ => _pointerOverGear = false);
            }
            else
            {
                Debug.LogWarning("[MatchSettings] Corner button 'btn-settings' not found in UXML.");
            }

            Wire(root, "btn-concede", ShowConfirm);
            Wire(root, "btn-close", Close);
            Wire(root, "btn-confirm-concede", () => { _onConcede?.Invoke(); Close(); });
            Wire(root, "btn-cancel", ShowMenu);

            // Click-out closes the overlay. It never concedes — closing is always safe.
            var scrim = root.Q<VisualElement>("scrim");
            scrim?.RegisterCallback<ClickEvent>(_ => Close());

            Close();
        }

        private void Wire(VisualElement root, string name, Action onClick)
        {
            var button = root.Q<Button>(name);
            if (button == null) { Debug.LogWarning($"[MatchSettings] Button '{name}' not found in UXML."); return; }
            button.RegisterCallback<ClickEvent>(_ => onClick());
        }

        private void Open()
        {
            if (_overlay != null) _overlay.style.display = DisplayStyle.Flex;
            ShowMenu();
            _isOpen = true;
        }

        private void Close()
        {
            if (_overlay != null) _overlay.style.display = DisplayStyle.None;
            _isOpen = false;
        }

        private void ShowMenu()
        {
            if (_viewMenu != null)    _viewMenu.style.display    = DisplayStyle.Flex;
            if (_viewConfirm != null) _viewConfirm.style.display = DisplayStyle.None;
        }

        private void ShowConfirm()
        {
            if (_viewMenu != null)    _viewMenu.style.display    = DisplayStyle.None;
            if (_viewConfirm != null) _viewConfirm.style.display = DisplayStyle.Flex;
        }

        private void OnDisable()
        {
            _isOpen = false;
            _pointerOverGear = false;
        }
    }
}
