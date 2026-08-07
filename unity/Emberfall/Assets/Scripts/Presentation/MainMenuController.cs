using System;
using System.Collections.Generic;
using Emberfall.Core.Collection;
using Emberfall.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The front door — the first screen a player sees (dev-docs/ui/client-surfaces.md:7).
    /// A UI Toolkit meta-screen in the deckbuilder's lineage: theme.uss tokens, a UIDocument
    /// host, elements queried by name and wired in <see cref="OnEnable"/>.
    ///
    /// <para>Each destination has a named handler so a scene-loader drops straight in. The Store opens
    /// its in-scene panel; Deck Builder opens the character roster; the remaining unfinished
    /// destinations still log their intent.</para>
    ///
    /// <para><b>This screen is also where identity begins.</b> A device that has never been named is
    /// asked once here. Character creation lives on the roster screen, never on this menu. Everything
    /// is local: no display name travels the wire yet, so the opponent still sees a seat id.</para>
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// MainMenuScreen.uxml and whose Panel Settings is MainMenuPanelSettings.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MainMenuController : MonoBehaviour
    {
        private VisualElement _root;
        private PlayerAccount _account;
        private LoadingOverlay _loading;

        private Label _identityName;
        private Label _identitySub;

        private VisualElement _nameModal;
        private Label _nameTitle;
        private Label _nameError;
        private TextField _nameField;
        private Button _nameCancel;
        private bool _nameRequired;

        private VisualElement _accountModal;
        private VisualElement _accountList;
        private Label _accountError;
        private ShopPanelController _shop;

        /// <summary>Frame-0 coverage, the same rig GameBootstrap/NetworkedGameBootstrap use
        /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/loading-screen-plate.md §5): the overlay exists before anything renders. The menu's
        /// own content starts hidden (mm-root--hidden in the UXML) so there is no frame where
        /// the unbound menu shows through before <see cref="RevealMenu"/> runs.</summary>
        private void Awake()
        {
            _loading = LoadingOverlay.Create(Camera.main);
        }

        private void OnEnable()
        {
            // GetComponent<UIDocument>().rootVisualElement is Unity's synthetic per-panel
            // wrapper, NOT the UXML's own top-level element — the "root" element (which carries
            // mm-root--hidden) is one level below it. Grabbing the wrapper here made
            // RevealMenu's RemoveFromClassList a silent no-op: the class it targeted was never
            // on the object it called it on, so the menu stayed visibility:hidden forever.
            _root = GetComponent<UIDocument>().rootVisualElement?.Q<VisualElement>("root");
            if (_root == null)
            {
                Debug.LogError("[MainMenu] No rootVisualElement — is the UXML assigned?");
                _loading?.NotifyBootFailed("UI not assigned — see console");
                return;
            }

            Wire("btn-find-game", OnFindGame);
            Wire("btn-single-player", OnSinglePlayer);
            Wire("btn-deck-builder", OnDeckBuilder);
            Wire("btn-store", OnStore);
            Wire("btn-leaderboards", OnLeaderboards);

            _shop = new ShopPanelController(
                _root,
                ShopRuntime.Gateway,
                ShopRuntime.PackagingArt);

            BindIdentity();
            BindNameModal();
            BindAccountModal();

            // A corrupt collection file must not brick the front door. The stores throw rather than
            // reporting an empty collection (so the next save cannot overwrite something
            // recoverable), which means the failure surfaces HERE, on a screen whose other four
            // buttons still work.
            try
            {
                _account = PlayerAccount.Local;
                Debug.Log($"[MainMenu] collection scope → {_account.ScopeDirectory}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MainMenu] could not open this device's collection: {ex.Message}");
                SetIdentity("Collection unreadable", "See the console — nothing was overwritten.");
                FinishBootstrap();
                return;
            }

            RefreshIdentity();
            if (_account.NeedsDisplayName) OpenNameModal(required: true);
            FinishBootstrap();
        }

        /// <summary>Bootstrap (identity/account) is done. Reveal the menu once the boot plate has
        /// actually finished fading — not on this signal directly.
        ///
        /// <para>Screen Space Overlay always paints over LoadingOverlay's camera-parented rig,
        /// regardless of the rig's own fade-out timing — revealing on this signal alone would
        /// race the overlay off the screen the instant identity work finishes, which is close
        /// enough to instant that the plate would never be seen at all. <see cref="RevealMenu"/>
        /// only runs once <see cref="LoadingOverlay.OnFadedOut"/> fires.</para></summary>
        private void FinishBootstrap()
        {
            if (_loading != null)
            {
                _loading.OnFadedOut += RevealMenu;
                _loading.NotifyReady();
            }
            else
            {
                RevealMenu();
            }
        }

        /// <summary>Show the menu (and whatever modal OnEnable decided to open under it). Called
        /// from <see cref="FinishBootstrap"/> — directly when there is no boot plate, otherwise
        /// once the plate has finished fading.</summary>
        private void RevealMenu()
        {
            _root.RemoveFromClassList("mm-root--hidden");
        }

        // System.Action explicitly: Emberfall.Core.Contracts has an Action type of its own (a card
        // effect), and both namespaces are in scope here.
        private void Wire(string name, System.Action onClick)
        {
            var button = _root.Q<Button>(name);
            if (button == null) { Debug.LogWarning($"[MainMenu] Button '{name}' not found in UXML."); return; }
            button.RegisterCallback<ClickEvent>(_ => onClick());
        }

        // ── Identity plate ──────────────────────────────────────────────────────────────

        private void BindIdentity()
        {
            _identityName = _root.Q<Label>("identity-name");
            _identitySub = _root.Q<Label>("identity-sub");
            Wire("identity", OnIdentityClicked);
        }

        private void SetIdentity(string name, string sub)
        {
            if (_identityName != null) _identityName.text = name;
            if (_identitySub != null) _identitySub.text = sub;
        }

        /// <summary>
        /// Redraw the plate from disk. Counts come off the real stores rather than being tracked
        /// incrementally — the numbers are then correct after any write, including ones this screen
        /// did not make.
        /// </summary>
        private void RefreshIdentity()
        {
            if (_account == null) return;

            int characters = _account.Roster().Count;
            string sub = characters == 0
                ? "No characters yet — open Deck Builder"
                : characters == 1 ? "1 character" : $"{characters} characters";

            // Which seat, but only once there is more than one to confuse it with. Two accounts on one
            // machine exist to be played against each other, and the failure that wastes the most time
            // is not noticing you are signed in as the other one. Naturally absent in a release build,
            // where the registry only ever holds the grandfathered account.
            int seat = SeatNumberOrZero();
            if (seat > 0) sub = $"Account {seat} · {sub}";

            // "Unnamed player" rather than an invented default: a saved default is indistinguishable
            // from a chosen name, and the prompt would never fire again (PlayerAccount.DisplayNameOr).
            SetIdentity(_account.DisplayNameOr("Unnamed player"), sub);
        }

        private void OnIdentityClicked()
        {
            if (_account == null) return;
            OpenNameModal(required: _account.NeedsDisplayName);
        }

        // ── Name modal ──────────────────────────────────────────────────────────────────

        private void BindNameModal()
        {
            _nameModal = _root.Q<VisualElement>("name-modal");
            _nameTitle = _root.Q<Label>("name-title");
            _nameError = _root.Q<Label>("name-error");
            _nameField = _root.Q<TextField>("name-field");
            _nameCancel = _root.Q<Button>("btn-name-cancel");

            Wire("btn-name-confirm", ConfirmName);
            Wire("btn-name-cancel", () => Hide(_nameModal));

            if (_nameField == null) return;
            // Enter submits. A single-field form where the only way forward is the mouse is a form
            // people abandon.
            _nameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) ConfirmName();
            });
            // Clear a stale refusal as soon as the player changes anything, so the message always
            // describes what is currently in the field.
            _nameField.RegisterValueChangedCallback(_ => SetError(_nameError, ""));
        }

        /// <summary>
        /// Open the name prompt. <paramref name="required"/> is first run — there is no name to fall
        /// back to, so Cancel is hidden rather than disabled: a dead button invites clicking.
        /// </summary>
        private void OpenNameModal(bool required)
        {
            _nameRequired = required;
            if (_nameTitle != null)
                _nameTitle.text = required ? "What should we call you?" : "Change your name";
            if (_nameCancel != null)
                _nameCancel.style.display = required ? DisplayStyle.None : DisplayStyle.Flex;
            if (_nameField != null)
            {
                _nameField.SetValueWithoutNotify(_account?.Profile.DisplayName ?? "");
                _nameField.Focus();
            }
            SetError(_nameError, "");
            Show(_nameModal);
        }

        private void ConfirmName()
        {
            if (_account == null) return;

            NameCheck check = _account.SetDisplayName(_nameField?.value ?? "");
            if (!check.IsValid)
            {
                // The refusal reason is authored player-facing in PlayerName — rendered verbatim so
                // there is one wording, not one per form.
                SetError(_nameError, check.Reason);
                return;
            }

            Hide(_nameModal);
            RefreshIdentity();

            // Keep the first-run continuation, but move it to the roster: character creation never
            // lives on the Main Menu.
            if (_nameRequired && _account.Roster().Count == 0) Go("CharacterRoster");
        }

        // ── Account switcher (testing) ──────────────────────────────────────────────────

        /// <summary>
        /// Wire the accounts modal and reveal its entry point — but only in the editor and in
        /// development builds (Sam 2026-07-27: two accounts on one device, strictly for testing).
        ///
        /// <para><b>The gate is on the affordance, not the mechanism.</b> <see cref="DeviceAccounts"/>
        /// compiles and runs in every configuration; a release build simply resolves the one
        /// grandfathered account and never shows this button. Gating the registry itself would leave the
        /// shipped path as the one nobody exercises, which is how an unregistered background survived
        /// four shipped assets.</para>
        /// </summary>
        private void BindAccountModal()
        {
            _accountModal = _root.Q<VisualElement>("account-modal");
            _accountList = _root.Q<VisualElement>("account-list");
            _accountError = _root.Q<Label>("account-error");

            Wire("btn-account-close", () => Hide(_accountModal));
            Wire("btn-account-new", OnNewAccount);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Wire("btn-switch-account", OpenAccountModal);
            var entry = _root.Q<Button>("btn-switch-account");
            // The USS default is display:none so a release build stays hidden even if this is reached.
            if (entry != null) entry.style.display = DisplayStyle.Flex;
#endif
        }

        /// <summary>This account's 1-based position, or 0 when it is the only one on the device.</summary>
        private static int SeatNumberOrZero()
        {
            try
            {
                var all = DeviceAccounts.All();
                if (all.Count < 2) return 0;
                string activeId = DeviceAccounts.Active.Id;
                for (int i = 0; i < all.Count; i++)
                    if (all[i].Id == activeId) return i + 1;
            }
            catch (Exception ex)
            {
                // A nameplate must not die of an unreadable registry — the modal reports it instead.
                Debug.LogWarning($"[MainMenu] account registry unreadable ({ex.GetType().Name}).");
            }
            return 0;
        }

        private void OpenAccountModal()
        {
            SetError(_accountError, "");
            RebuildAccountList();
            Show(_accountModal);
        }

        /// <summary>
        /// Rebuild the rows from disk. Rows are built here rather than in UXML because the count is
        /// whatever the registry holds — two today, and there is no reason for the screen to be the
        /// thing that caps it.
        /// </summary>
        private void RebuildAccountList()
        {
            if (_accountList == null) return;
            _accountList.Clear();

            IReadOnlyList<LocalAccount> all;
            string activeId;
            try
            {
                all = DeviceAccounts.All();
                activeId = DeviceAccounts.Active.Id;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MainMenu] could not read the account registry: {ex.Message}");
                SetError(_accountError, "The account list could not be read — see the console.");
                return;
            }

            for (int i = 0; i < all.Count; i++)
            {
                LocalAccount account = all[i];
                bool isActive = account.Id == activeId;

                var row = new VisualElement();
                row.AddToClassList("mm-account-row");
                if (isActive) row.AddToClassList("mm-account-row--active");

                var label = new Label(DeviceAccounts.LabelOf(account, i));
                label.AddToClassList("mm-account-row__label");
                row.Add(label);

                if (isActive)
                {
                    var here = new Label("signed in");
                    here.AddToClassList("mm-account-row__sub");
                    row.Add(here);
                }
                else
                {
                    string id = account.Id;   // captured per row, not the loop variable
                    var switchTo = new Button(() => SwitchToAccount(id)) { text = "Switch" };
                    switchTo.AddToClassList("mm-modal__button");
                    switchTo.AddToClassList("mm-account-row__button");
                    row.Add(switchTo);
                }

                _accountList.Add(row);
            }
        }

        private void OnNewAccount()
        {
            try
            {
                LocalAccount created = DeviceAccounts.Create();
                Debug.Log($"[MainMenu] created local account {created.Id}. This seat did NOT move — " +
                          "press Switch on it to sign in here, or pin another process to it with " +
                          "-emberfallAccount.");
                // Rebuild rather than reload: creating an account for the other seat must leave this
                // one exactly where it was (DeviceAccounts.Create).
                RebuildAccountList();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MainMenu] could not create a local account: {ex.Message}");
                SetError(_accountError, ex.Message);
            }
        }

        private void SwitchToAccount(string accountId)
        {
            try
            {
                DeviceAccounts.SwitchTo(accountId);
                ReloadThisScene();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MainMenu] could not switch account: {ex.Message}");
                SetError(_accountError, ex.Message);
            }
        }

        /// <summary>
        /// Reload the menu so every screen rebuilds against the new account.
        ///
        /// <para><b>Deliberately blunt.</b> This screen is holding an account, a card database, a bound
        /// name field and a roster count, and other screens hold more. Re-resolving each one is a list
        /// that will be incomplete the first time somebody adds to it, and the symptom of an incomplete
        /// switch — one player's character written into another's file — is the worst outcome this
        /// feature has. A scene reload cannot be partial.</para>
        /// </summary>
        private static void ReloadThisScene()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            UnityEngine.SceneManagement.SceneManager.LoadScene(scene.name);
        }

        // ── Shared modal plumbing ───────────────────────────────────────────────────────

        private static void Show(VisualElement modal) => modal?.RemoveFromClassList("mm-modal--hidden");
        private static void Hide(VisualElement modal) => modal?.AddToClassList("mm-modal--hidden");

        /// <summary>A space, not an empty string, so the error line keeps its height and the buttons
        /// below do not jump when a refusal appears or clears.</summary>
        private static void SetError(Label label, string message)
        {
            if (label != null) label.text = string.IsNullOrEmpty(message) ? " " : message;
        }

        // ---- Destinations (stubbed — navigation is a later pass) ----
        // Find Game → BoardNetworked.unity (server matchmaking; the only destination with a backend today).
        /// <summary>
        /// Find Game → <c>BoardNetworked</c>.
        ///
        /// <para><b>There is no queue screen to build.</b> That scene's <c>NetworkedGameBootstrap</c>
        /// already connects, sends its hello and waits, and the server's gate pairs whoever is waiting
        /// with the next arrival (<c>MatchGate.PairAsync</c>) — so this one scene load is the entire
        /// matchmaking entry point. Two clients that both press this find each other.</para>
        /// </summary>
        private void OnFindGame() => Go("BoardNetworked");

        /// <summary>Single Player → <c>Board</c>, the local vs-AI slice (<c>GameBootstrap</c>).</summary>
        private void OnSinglePlayer() => Go("Board");

        /// <summary>
        /// Load a scene by name, reporting the one failure mode that is otherwise silent.
        ///
        /// <para><b>A scene missing from the build list does not throw a useful error</b> — in a player
        /// it is simply not there, and in the Editor <c>LoadScene</c> needs it in
        /// <c>EditorBuildSettings</c> or nothing happens. Both present as a dead button. The build list
        /// is kept in sync by <c>Emberfall/Build/Sync Scene List</c>; this check is what tells you it
        /// wasn't.</para>
        /// </summary>
        private static void Go(string sceneName)
        {
            // CanStreamedLevelBeLoaded, not SceneUtility.GetBuildIndexByScenePath — the latter wants a
            // full asset path and answers -1 for a bare scene name, which would make this guard reject
            // every load it was written to protect.
            if (!Application.CanStreamedLevelBeLoaded(sceneName))
            {
                Debug.LogError($"[MainMenu] Scene '{sceneName}' is not in the build list, so this button " +
                               "cannot do anything. Run Emberfall/Build/Sync Scene List.");
                return;
            }
            Debug.Log($"[MainMenu] → {sceneName}");
            UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
        }

        /// <summary>
        /// Deck Builder → CharacterRoster.unity. The roster owns character selection, creation and
        /// deletion; the Main Menu never does.
        /// </summary>
        private void OnDeckBuilder() => Go("CharacterRoster");

        // Store stays in this scene: its full-screen UI Toolkit panel owns the purchase/reveal flow.
        private void OnStore() => _shop?.Open();

        // Leaderboards → no backend yet.
        private void OnLeaderboards() => Debug.Log("[MainMenu] Leaderboards → coming soon. No backend yet.");
    }
}
