using System;
using System.Collections.Generic;
using Emberfall.Core.Collection;
using Emberfall.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Settings overlay (C-G1-3 / decision 23 §5), mounted both in-match and on the main menu
    /// (Sam, 2026-08-09). A UI Toolkit meta-surface with a persistent corner button that opens a
    /// modal panel. In-match the top item is CONCEDE, behind a confirm ("leaving IS forfeiting");
    /// <see cref="Init"/> hides that row on a menu mount, where there is no match to concede.
    /// Sound landed 2026-09-05 — see <see cref="BindAudioSettings"/> and <see cref="AudioSettings"/>.
    /// Video is still deferred. Switch account (dev/testing only) lives here too now — see <see cref="BindAccountModal"/>.
    /// It is hidden in-match (Sam, 2026-08-09): switching accounts mid-match reloads the scene and
    /// ejects the player, so the bootstraps pass <c>allowAccountSwitch: false</c> into <see cref="Init"/>.
    ///
    /// Attach to a GameObject with a <see cref="UIDocument"/> whose Source Asset is
    /// MatchSettingsScreen.uxml and whose Panel Settings does NOT clear color (so whatever is
    /// behind — board or menu — shows through). The bootstraps and MainMenuController build this
    /// at runtime via Resources and call <see cref="Init"/>.
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

        // The shared confirm view's contents, filled per use. Two things confirm now (concede and
        // sign out) and a second copy of the panel would drift from the first.
        private Label _confirmTitle;
        private Label _confirmSubtitle;
        private Button _confirmButton;
        private Action _onConfirm;

        private VisualElement _accountModal;
        private VisualElement _accountList;
        private Label _accountError;

        private bool _isOpen;
        private bool _accountModalOpen;
        private bool _pointerOverGear;

        /// <summary>True while the overlay or the account modal is open, or the pointer is over the
        /// corner button. InputRouter suppresses board input for the duration (UI Toolkit won't do
        /// it for us).</summary>
        public bool IsBlockingInput => _isOpen || _accountModalOpen || _pointerOverGear;

        private bool _allowAccountSwitch;

        /// <param name="onConcede">Forfeit callback. Pass null on a menu mount — there is no match
        /// to concede, and <paramref name="showConcede"/> hides the row that would call it.</param>
        /// <param name="showConcede">False on a menu mount (Sam, 2026-08-09): a Concede button that
        /// does nothing when clicked is worse than no button at all.</param>
        /// <param name="allowAccountSwitch">False in-match (Sam, 2026-08-09): switching accounts
        /// mid-match reloads the scene and ejects the player, so the affordance is hidden entirely
        /// rather than offered and then eating the match. <b>Sign out rides on this same flag</b>
        /// (2026-09-02) — it reloads the scene for the identical reason, so the parameter now means
        /// "this mount may reload the scene" rather than anything about accounts specifically.</param>
        public void Init(Action onConcede, bool showConcede = true, bool allowAccountSwitch = true)
        {
            _onConcede = onConcede;
            _allowAccountSwitch = allowAccountSwitch;

            var root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[MatchSettings] No rootVisualElement — is the UXML/PanelSettings assigned before Init?");
                return;
            }

            _overlay     = root.Q<VisualElement>("overlay");
            _viewMenu    = root.Q<VisualElement>("view-menu");
            _viewConfirm = root.Q<VisualElement>("view-confirm");

            _confirmTitle    = root.Q<Label>("confirm-title");
            _confirmSubtitle = root.Q<Label>("confirm-subtitle");
            _confirmButton   = root.Q<Button>("btn-confirm");

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

            var concede = root.Q<Button>("btn-concede");
            if (concede != null) concede.style.display = showConcede ? DisplayStyle.Flex : DisplayStyle.None;

            Wire(root, "btn-concede", ConfirmConcede);
            Wire(root, "btn-close", Close);
            Wire(root, "btn-confirm", () => { _onConfirm?.Invoke(); });
            Wire(root, "btn-cancel", ShowMenu);
            Wire(root, "btn-quit", OnQuit);

            BindAudioSettings(root);
            BindSignOut(root);

            // Click-out closes the overlay. It never concedes — closing is always safe.
            var scrim = root.Q<VisualElement>("scrim");
            scrim?.RegisterCallback<ClickEvent>(_ => Close());

            BindAccountModal(root);

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

        /// <summary>
        /// Fill the shared confirm view and show it. <paramref name="danger"/> is the red treatment —
        /// passed rather than assumed, because the first user of this panel was Concede and the
        /// styling followed it into the UXML: a sign-out rendered in the concede red would read as
        /// destructive when it is entirely recoverable.
        /// </summary>
        private void ShowConfirm(string title, string subtitle, string confirmLabel, bool danger,
                                 Action onConfirm)
        {
            _onConfirm = onConfirm;

            if (_confirmTitle != null)    _confirmTitle.text    = title;
            if (_confirmSubtitle != null) _confirmSubtitle.text = subtitle;
            if (_confirmButton != null)
            {
                _confirmButton.text = confirmLabel;
                _confirmButton.EnableInClassList("ms-item--danger", danger);
            }

            if (_viewMenu != null)    _viewMenu.style.display    = DisplayStyle.None;
            if (_viewConfirm != null) _viewConfirm.style.display = DisplayStyle.Flex;
        }

        private void ConfirmConcede()
            => ShowConfirm("Concede this match?", "This counts as a loss.", "Concede", danger: true,
                           onConfirm: () => { _onConcede?.Invoke(); Close(); });

        // ── Audio (Sam, 2026-09-05) ─────────────────────────────────────────────────────

        /// <summary>
        /// Wire the mute toggle and the two volume sliders to <see cref="AudioSettings"/>. Reads the
        /// CURRENT applied state on every Init rather than trusting a cached value — this panel is
        /// rebuilt on every scene load, and <see cref="AudioSettings"/> is a static that outlives it.
        /// </summary>
        private void BindAudioSettings(VisualElement root)
        {
            var mute  = root.Q<Toggle>("toggle-mute");
            var music = root.Q<Slider>("slider-music");
            var sfx   = root.Q<Slider>("slider-sfx");
            if (mute == null || music == null || sfx == null)
            {
                Debug.LogWarning("[MatchSettings] audio controls not found in UXML.");
                return;
            }

            AudioSettings.EnsureLoaded();
            mute.SetValueWithoutNotify(AudioSettings.Muted);
            music.SetValueWithoutNotify(AudioSettings.MusicVolume);
            sfx.SetValueWithoutNotify(AudioSettings.SfxVolume);
            music.SetEnabled(!AudioSettings.Muted);
            sfx.SetEnabled(!AudioSettings.Muted);
            // .ms-toggle--on drives the checkmark fill (matchsettings.uss) — set explicitly rather
            // than trusting UI Toolkit's ":checked" pseudo-state, which needs the class as a hook.
            mute.EnableInClassList("ms-toggle--on", AudioSettings.Muted);

            // Sliders stay enabled-but-dimmed rather than hidden while muted (.ms-slider:disabled)
            // so the player can still see what they are set to.
            mute.RegisterValueChangedCallback(e =>
            {
                AudioSettings.SetMuted(e.newValue);
                music.SetEnabled(!e.newValue);
                sfx.SetEnabled(!e.newValue);
                mute.EnableInClassList("ms-toggle--on", e.newValue);
            });
            music.RegisterValueChangedCallback(e => AudioSettings.SetMusicVolume(e.newValue));
            sfx.RegisterValueChangedCallback(e => AudioSettings.SetSfxVolume(e.newValue));
        }

        // ── Sign out ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Show the sign-out row only when there is a session to leave.
        ///
        /// <para><b>Not gated on a development build, unlike Switch account.</b> That one is a
        /// testing affordance for a device-local concept players never see; this is the ordinary
        /// player-facing way to leave an identity, and a game that can only be signed out of by
        /// uninstalling is one where the only way to change accounts destroys the device's
        /// data.</para>
        ///
        /// <para>Read once at Init rather than watched: the panel is rebuilt on every scene load,
        /// and signing IN happens behind the gate before the menu exists.</para>
        ///
        /// <para><b>Hidden in-match, on the same flag as Switch account</b> — that parameter is
        /// really "this mount can reload the scene", and signing out reloads it exactly as switching
        /// does. Offering it mid-match would eject the player and forfeit the match without ever
        /// saying the word concede.</para>
        /// </summary>
        private void BindSignOut(VisualElement root)
        {
            var entry = root.Q<Button>("btn-sign-out");
            if (entry == null) return;

            if (!_allowAccountSwitch) { entry.style.display = DisplayStyle.None; return; }

            bool signedIn;
            try { signedIn = AuthGateway.IsSignedIn; }
            catch (Exception ex)
            {
                // Composing the provider touches a settings asset and player prefs. A throw here
                // must not take the settings panel down with it — no row is the honest answer.
                Debug.LogWarning($"[MatchSettings] could not read the sign-in state ({ex.GetType().Name}); " +
                                 "hiding Sign out.");
                signedIn = false;
            }

            if (!signedIn) { entry.style.display = DisplayStyle.None; return; }

            entry.style.display = DisplayStyle.Flex;
            Wire(root, "btn-sign-out", ConfirmSignOut);
        }

        private void ConfirmSignOut()
        {
            string email = "";
            try { email = AuthGateway.SignedInEmail ?? ""; } catch { /* named below, or not */ }

            ShowConfirm(
                email.Length > 0 ? $"Sign out of {email}?" : "Sign out?",
                // Says what survives, because the whole point of the roster work is that the answer
                // changed: signing out is no longer anything like losing the account.
                "Your cards and decks stay on your account. You will need to sign in " +
                "again to play.",
                "Sign out",
                danger: false,
                onConfirm: SignOut);
        }

        /// <summary>
        /// Clear the stored session and rebuild the scene against a signed-out device.
        ///
        /// <para><b>Local files are deliberately NOT deleted.</b> The scope folder is bound to the
        /// identity by pointer, so signing back in finds the same folder — and if a different
        /// identity signs in, <c>DeviceAccounts.BindToIdentity</c> mints or switches to that
        /// identity's own account rather than handing over this one's. Deleting the roster here
        /// would destroy the only local copy of anything the server has not accepted yet.</para>
        ///
        /// <para><b>Then <see cref="DeviceAccounts.Invalidate"/> and a scene reload</b> — the same
        /// blunt approach as switching accounts, and for the identical reason: every cached piece
        /// of per-account state (the auth provider, PlayerAccount.Local, both gateways) has to be
        /// dropped, and re-resolving them one by one is a list that will be incomplete the first
        /// time somebody adds to it. The reload puts the sign-in gate back up, because
        /// <c>SignInGateController.IsRequired</c> is now true again.</para>
        /// </summary>
        private void SignOut()
        {
            try
            {
                AuthGateway.Provider.SignOut();
                DeviceAccounts.Invalidate();
                Debug.Log("[MatchSettings] signed out; reloading the scene.");
            }
            catch (Exception ex)
            {
                // A failure here leaves the session in place, which is the safe direction: the
                // player stays signed in rather than landing in a half-signed-out state.
                Debug.LogError($"[MatchSettings] could not sign out: {ex.Message}");
                ShowMenu();
                return;
            }

            Close();
            ReloadThisScene();
        }

        // Application.Quit() is a no-op in the Editor (there is no player process to close), so
        // the Editor half stops Play Mode instead — the closest equivalent to "quit" there.
        private static void OnQuit()
        {
            Debug.Log("[MatchSettings] Quit");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void OnDisable()
        {
            _isOpen = false;
            _accountModalOpen = false;
            _pointerOverGear = false;
        }

        // ── Account switcher (testing) ──────────────────────────────────────────────────

        /// <summary>
        /// Wire the accounts modal and reveal its entry point — but only in the editor and in
        /// development builds (Sam 2026-07-27: two accounts on one device, strictly for testing;
        /// moved here from MainMenuController 2026-08-09 so it works wherever this panel is
        /// mounted, not just on the main menu), and only when <see cref="_allowAccountSwitch"/>
        /// (Sam, 2026-08-09: hidden in-match — see <see cref="Init"/>).
        ///
        /// <para><b>The gate is on the affordance, not the mechanism.</b> <see cref="DeviceAccounts"/>
        /// compiles and runs in every configuration; a release build simply resolves the one
        /// grandfathered account and never shows this button.</para>
        /// </summary>
        private void BindAccountModal(VisualElement root)
        {
            _accountModal = root.Q<VisualElement>("account-modal");
            _accountList = root.Q<VisualElement>("account-list");
            _accountError = root.Q<Label>("account-error");

            Wire(root, "btn-account-close", CloseAccountModal);
            Wire(root, "btn-account-new", OnNewAccount);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var entry = root.Q<Button>("btn-switch-account");
            if (_allowAccountSwitch)
            {
                Wire(root, "btn-switch-account", OpenAccountModal);
                // The USS default is display:none so a release build stays hidden even if this is reached.
                if (entry != null) entry.style.display = DisplayStyle.Flex;
            }
            else if (entry != null)
            {
                entry.style.display = DisplayStyle.None;
            }
#endif
        }

        private void OpenAccountModal()
        {
            // Only one modal on screen at a time — the switcher replaces the menu, not layers over it.
            Close();
            SetError(_accountError, "");
            RebuildAccountList();
            if (_accountModal != null) _accountModal.RemoveFromClassList("mm-modal--hidden");
            _accountModalOpen = true;
        }

        private void CloseAccountModal()
        {
            if (_accountModal != null) _accountModal.AddToClassList("mm-modal--hidden");
            _accountModalOpen = false;
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
                Debug.LogError($"[MatchSettings] could not read the account registry: {ex.Message}");
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
                Debug.Log($"[MatchSettings] created local account {created.Id}. This seat did NOT move — " +
                          "press Switch on it to sign in here, or pin another process to it with " +
                          "-emberfallAccount.");
                // Rebuild rather than reload: creating an account for the other seat must leave this
                // one exactly where it was (DeviceAccounts.Create).
                RebuildAccountList();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MatchSettings] could not create a local account: {ex.Message}");
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
                Debug.LogError($"[MatchSettings] could not switch account: {ex.Message}");
                SetError(_accountError, ex.Message);
            }
        }

        /// <summary>Reload the current scene so everything rebuilds against the new account — the
        /// same blunt approach MainMenuController used (see its ReloadThisScene): re-resolving each
        /// piece of state individually is a list that will be incomplete the first time somebody
        /// adds to it. This would eject the player mid-match, which is why <see cref="Init"/>'s
        /// <c>allowAccountSwitch</c> hides the affordance entirely in-match (Sam, 2026-08-09) —
        /// this path is unreachable there.</summary>
        private static void ReloadThisScene()
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            UnityEngine.SceneManagement.SceneManager.LoadScene(scene.name);
        }

        /// <summary>A space, not an empty string, so the error line keeps its height and the buttons
        /// below do not jump when a refusal appears or clears.</summary>
        private static void SetError(Label label, string message)
        {
            if (label != null) label.text = string.IsNullOrEmpty(message) ? " " : message;
        }
    }
}
