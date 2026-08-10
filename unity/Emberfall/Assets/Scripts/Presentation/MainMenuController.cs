using System;
using Emberfall.Core.Collection;
using Emberfall.Net.Client.Auth;
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
    /// asked once here — <b>once</b> being the whole of it: a display name is permanent (NOR-287), so the
    /// nameplate top-left only reports it and the client carries no rename flow at all.
    /// Character creation lives on the roster screen, never on this menu. Everything
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
        private Label _nameError;
        private TextField _nameField;
        private Label _namePasswordHelp;
        private TextField _namePassword;
        private TextField _namePasswordConfirm;
        private Button _nameConfirm;
        private bool _nameBusy;

        private ShopPanelController _shop;
        private SignInGateController _signIn;

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

            // ShopRuntime.PackagingArt stays an override seam (a test or a future streamed loader
            // can set it); with nobody overriding it the packaging slots resolve through the same
            // sprite pipeline as the rest of the meta screens. The gateway has no such default on
            // purpose — an unbound wallet must read as unavailable, never as zero.
            _shop = new ShopPanelController(
                _root,
                ShopRuntime.Gateway,
                ShopRuntime.PackagingArt ?? new UiSpriteBoosterArtProvider());

            BindIdentity();
            BindNameModal();
            MountSettingsOverlay();
            _signIn = new SignInGateController(_root, AuthGateway.Provider);

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
            BeginFirstRun();
            FinishBootstrap();
        }

        /// <summary>Drop the sign-in gate's deep-link subscription. <see cref="SignInGateController"/>
        /// listens on the static <see cref="Application.deepLinkActivated"/> event so a tapped email
        /// link can complete sign-in while the game is already running, and a fresh instance is built
        /// on every OnEnable (including the scene reload <see cref="DeviceAccounts.SwitchTo"/>
        /// triggers) — without this, a disabled screen's controller keeps handling links for a modal
        /// that no longer exists.</summary>
        private void OnDisable()
        {
            _signIn?.Close();
        }

        /// <summary>
        /// First-run prompts, in the order Sam ruled on 2026-08-08: register, then name yourself.
        ///
        /// <para><b>Registration comes first and cannot be skipped.</b> The two prompts are chained
        /// rather than shown together — the sign-in gate calls back once a session exists, and only
        /// then does the name prompt open. Opening both would stack two modals on a player who has not
        /// seen the game yet, and the name is the smaller of the two questions.</para>
        ///
        /// <para><b>The menu is revealed underneath either way.</b> Sam asked for the registration
        /// prompt as an overlay ON the main menu, not instead of it, and the gate's own scrim is what
        /// stops the buttons behind it being reachable.</para>
        /// </summary>
        private void BeginFirstRun()
        {
            if (_signIn != null && _signIn.IsRequired)
            {
                _signIn.Open(onSignedIn: PromptForNameIfNeeded);
                return;
            }

            PromptForNameIfNeeded();
        }

        private void PromptForNameIfNeeded()
        {
            if (_account != null && _account.NeedsDisplayName) OpenNameModal();
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

        /// <summary>The plate reports; it does not act (NOR-287). Nothing is wired to it.</summary>
        private void BindIdentity()
        {
            _identityName = _root.Q<Label>("identity-name");
            _identitySub = _root.Q<Label>("identity-sub");
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

        // ── Name modal ──────────────────────────────────────────────────────────────────

        private void BindNameModal()
        {
            _nameModal = _root.Q<VisualElement>("name-modal");
            _nameError = _root.Q<Label>("name-error");
            _nameField = _root.Q<TextField>("name-field");
            _namePasswordHelp = _root.Q<Label>("name-password-help");
            _namePassword = _root.Q<TextField>("name-password");
            _namePasswordConfirm = _root.Q<TextField>("name-password-confirm");
            _nameConfirm = _root.Q<Button>("btn-name-confirm");

            Wire("btn-name-confirm", ConfirmName);

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
            _namePassword?.RegisterValueChangedCallback(_ => SetError(_nameError, ""));
            _namePasswordConfirm?.RegisterValueChangedCallback(_ => SetError(_nameError, ""));
        }

        /// <summary>
        /// Open the name prompt. <b>First run is the only time this ever opens (NOR-287)</b> — a display
        /// name is permanent, so there is no second caller and no Cancel: there is no earlier name to
        /// fall back to, and a dead button invites clicking.
        ///
        /// <para><b>Password is offered here too (NOR-246).</b> This is the one moment right after a
        /// magic-link sign-in where the player already has a live session to attach a password to.
        /// Changing a password later would be its own flow, which does not exist yet.</para>
        /// </summary>
        private void OpenNameModal()
        {
            // First-login sizing (NOR-284). This modal only ever opens on first launch now
            // (NOR-287) — the name/password step the unskippable sign-in ruling leads into — so it
            // always wears the phone-scale .mm-modal--onboard treatment the sign-in gate carries.
            // See .mm-modal--onboard in mainmenu.uss.
            _nameModal?.AddToClassList("mm-modal--onboard");

            if (_nameField != null)
            {
                _nameField.SetValueWithoutNotify(_account?.Profile.DisplayName ?? "");
                _nameField.Focus();
            }

            bool offerPassword = AuthGateway.IsConfigured;
            SetVisible(_namePasswordHelp, offerPassword);
            SetVisible(_namePassword, offerPassword);
            SetVisible(_namePasswordConfirm, offerPassword);
            _namePassword?.SetValueWithoutNotify("");
            _namePasswordConfirm?.SetValueWithoutNotify("");

            SetError(_nameError, "");
            Show(_nameModal);
        }

        // async void is correct for a UI handler: SetPasswordAsync resumes on the main thread (it
        // rides UnityWebRequestAuthHttp, same as everywhere else auth touches UI Toolkit), so
        // everything after the await is free to touch elements. The try/catch keeps it honest.
        private async void ConfirmName()
        {
            if (_account == null || _nameBusy) return;

            NameCheck check = _account.SetDisplayName(_nameField?.value ?? "");
            if (!check.IsValid)
            {
                // The refusal reason is authored player-facing in PlayerName — rendered verbatim so
                // there is one wording, not one per form.
                SetError(_nameError, check.Reason);
                return;
            }

            string password = _namePassword?.value ?? "";
            string passwordConfirm = _namePasswordConfirm?.value ?? "";

            // Optional (Sam, NOR-246): an empty field skips it rather than refusing to continue — the
            // name above is what the unskippable sign-in ruling actually requires, and a network hiccup
            // here must not be able to trap a player who already has a valid account behind this modal.
            if (password.Length > 0 || passwordConfirm.Length > 0)
            {
                if (password.Length < 6)
                {
                    SetError(_nameError, "Password must be at least 6 characters.");
                    return;
                }
                if (password != passwordConfirm)
                {
                    SetError(_nameError, "Passwords do not match.");
                    return;
                }

                SetNameBusy(true);
                try
                {
                    AuthResult result = await AuthGateway.Provider.SetPasswordAsync(password);
                    SetNameBusy(false);

                    if (!result.Ok)
                    {
                        SetError(_nameError, string.IsNullOrEmpty(result.Detail)
                            ? "Could not set a password. Try again, or clear both fields to skip it."
                            : result.Detail);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    SetNameBusy(false);
                    Debug.LogError($"[MainMenu] setting a password threw: {ex}");
                    SetError(_nameError, "Something went wrong setting a password. Try again, or clear both fields to skip it.");
                    return;
                }
            }

            Hide(_nameModal);
            RefreshIdentity();

            // Keep the first-run continuation, but move it to the roster: character creation never
            // lives on the Main Menu. First run is the only run that reaches here (OpenNameModal).
            if (_account.Roster().Count == 0) Go("CharacterRoster");
        }

        private void SetNameBusy(bool busy)
        {
            _nameBusy = busy;
            _nameConfirm?.SetEnabled(!busy);
            _nameField?.SetEnabled(!busy);
            _namePassword?.SetEnabled(!busy);
            _namePasswordConfirm?.SetEnabled(!busy);
        }

        private static void SetVisible(VisualElement element, bool shown)
        {
            if (element != null) element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ── Settings overlay (C-G1-3 / decision 23 §5, mounted here too since Sam 2026-08-09) ──

        /// <summary>
        /// Mount the same settings overlay a match uses — same UXML, same gear button, same
        /// bottom-right corner (mainmenu.uss moved the version footer to the left to make room).
        /// Switch account (dev/testing only) lives inside it now; Concede does not apply here, so
        /// <see cref="MatchSettingsController.Init"/> is told to hide that row.
        /// </summary>
        private void MountSettingsOverlay()
        {
            var uiGo = new GameObject("MatchSettingsUI");
            var doc  = uiGo.AddComponent<UIDocument>();
            doc.panelSettings   = Resources.Load<PanelSettings>("MatchSettingsPanelSettings");
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("MatchSettingsScreen");
            var settings = uiGo.AddComponent<MatchSettingsController>();
            settings.Init(onConcede: null, showConcede: false);
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
                // A nameplate must not die of an unreadable registry — the switcher reports it instead.
                Debug.LogWarning($"[MainMenu] account registry unreadable ({ex.GetType().Name}).");
            }
            return 0;
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
        // Find Game → CharacterRoster → BoardNetworked (pick first, then enter server matchmaking).
        /// <summary>
        /// Find Game → <c>CharacterRoster</c> → <c>BoardNetworked</c>.
        ///
        /// <para>The existing character roster is the deck-selection step: it already shows each
        /// character's default deck and queue eligibility. A queue-ready choice then loads the board,
        /// whose network bootstrap submits that exact deck before pairing.</para>
        /// </summary>
        private void OnFindGame()
        {
            MatchDeckSelection.BeginChoosing();
            Go("CharacterRoster");
        }

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
        private void OnDeckBuilder()
        {
            MatchDeckSelection.Cancel();
            Go("CharacterRoster");
        }

        // Store stays in this scene: its full-screen UI Toolkit panel owns the purchase/reveal flow.
        private void OnStore() => _shop?.Open();

        // Leaderboards → no backend yet.
        private void OnLeaderboards() => Debug.Log("[MainMenu] Leaderboards → coming soon. No backend yet.");
    }
}
