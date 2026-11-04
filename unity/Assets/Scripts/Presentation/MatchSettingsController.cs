using System;
using Kingdom.Net.Client.Auth;
using Kingdom.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Settings overlay (C-G1-3 / decision 23 §5), mounted both in-match and on the main menu
    /// (Sam, 2026-08-09). A UI Toolkit meta-surface with a persistent corner button that opens a
    /// modal panel. In-match the top item is CONCEDE, behind a confirm ("leaving IS forfeiting");
    /// <see cref="Init"/> hides that row on a menu mount, where there is no match to concede.
    /// Sound landed 2026-09-05 — see <see cref="BindAudioSettings"/> and <see cref="AudioSettings"/>.
    /// Video is still deferred. Sign out is hidden in-match: it reloads the scene and would eject
    /// the player, so the bootstraps pass <c>allowSceneReload: false</c> into <see cref="Init"/>.
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
        private VisualElement _viewAccount;
        private VisualElement _viewLanguage;
        private VisualElement _viewPassword;
        private TextField _passwordNew, _passwordConfirm;
        private Label _passwordStatus;
        private Button _passwordSave;
        private bool _savingPassword;

        // The public site. Deletion is a web request (support mailbox), not an endpoint: the server
        // has no delete-account path, and Play policy only needs the in-app entry to reach it.
        private const string SiteRoot   = "https://kingdomofbadideas.com";
        private const string PrivacyUrl = SiteRoot + "/privacy";
        private const string TermsUrl   = SiteRoot + "/terms";
        private const string DeleteUrl  = SiteRoot + "/privacy#delete";

        // The shared confirm view's contents, filled per use. Two things confirm now (concede and
        // sign out) and a second copy of the panel would drift from the first.
        private Label _confirmTitle;
        private Label _confirmSubtitle;
        private Button _confirmButton;
        private Action _onConfirm;

        private bool _isOpen;
        private bool _pointerOverGear;

        // One overlay per scene. Several of Mount's callers (CollectionController among them) can
        // run OnEnable more than once on the same live GameObject without a scene reload between —
        // a second Mount() there must hand back the existing gear rather than stack a duplicate one
        // on top of it.
        private static MatchSettingsController _current;

        /// <summary>True while the overlay is open, or the pointer is over the corner button.
        /// InputRouter suppresses board input for the duration (UI Toolkit won't do it for us).</summary>
        public bool IsBlockingInput => _isOpen || _pointerOverGear;

        private bool _allowSceneReload;

        /// <summary>
        /// Build and bind the settings overlay the same way on every screen that wants one — the
        /// corner gear, the modal panel, sound and sign-out. Every top-level destination should call
        /// this (main menu, an in-match bootstrap, collection, roster, store, pack vault); a modal or
        /// sub-flow entered from one of those is fine without it (Sam, 2026-09-05).
        /// </summary>
        public static MatchSettingsController Mount(Action onConcede = null, bool showConcede = false,
                                                      bool allowSceneReload = true)
        {
            if (_current != null) return _current;

            var uiGo = new GameObject("MatchSettingsUI");
            var doc  = uiGo.AddComponent<UIDocument>();
            doc.panelSettings   = Resources.Load<PanelSettings>("MatchSettingsPanelSettings");
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("MatchSettingsScreen");
            var settings = uiGo.AddComponent<MatchSettingsController>();
            settings.Init(onConcede, showConcede, allowSceneReload);
            _current = settings;
            return settings;
        }

        /// <param name="onConcede">Forfeit callback. Pass null on a menu mount — there is no match
        /// to concede, and <paramref name="showConcede"/> hides the row that would call it.</param>
        /// <param name="showConcede">False on a menu mount (Sam, 2026-08-09): a Concede button that
        /// does nothing when clicked is worse than no button at all.</param>
        /// <param name="allowSceneReload">False in-match (Sam, 2026-08-09): anything here that
        /// reloads the scene — today, Sign out — would eject the player and eat the match, so it is
        /// hidden entirely rather than offered.</param>
        public void Init(Action onConcede, bool showConcede = true, bool allowSceneReload = true)
        {
            _onConcede = onConcede;
            _allowSceneReload = allowSceneReload;

            var root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[MatchSettings] No rootVisualElement — is the UXML/PanelSettings assigned before Init?");
                return;
            }
            UiLocalizer.Attach(root);

            _overlay     = root.Q<VisualElement>("overlay");
            _viewMenu    = root.Q<VisualElement>("view-menu");
            _viewConfirm = root.Q<VisualElement>("view-confirm");
            _viewAccount = root.Q<VisualElement>("view-account");
            _viewLanguage = root.Q<VisualElement>("view-language");
            _viewPassword = root.Q<VisualElement>("view-password");

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
            Wire(root, "btn-confirm", () => { _onConfirm?.Invoke(); });
            Wire(root, "btn-cancel", ShowMenu);
            Wire(root, "btn-quit", OnQuit);
            Wire(root, "btn-account", ShowAccount);
            Wire(root, "btn-account-back", ShowMenu);
            Wire(root, "btn-language", ShowLanguage);
            Wire(root, "btn-language-back", ShowMenu);
            Wire(root, "btn-privacy", () => Application.OpenURL(PrivacyUrl));
            Wire(root, "btn-terms", () => Application.OpenURL(TermsUrl));
            Wire(root, "btn-delete-account", () => Application.OpenURL(DeleteUrl));

            BindAudioSettings(root);
            BindSignOut(root);
            BindPassword(root);

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

        /// <summary>Exactly one view shows at a time; every Show* goes through here so a new view is
        /// one more entry in this list rather than one more line in every other method.</summary>
        private void ShowOnly(VisualElement view)
        {
            foreach (VisualElement v in new[] { _viewMenu, _viewConfirm, _viewAccount, _viewLanguage, _viewPassword })
                if (v != null) v.style.display = ReferenceEquals(v, view) ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void ShowMenu() => ShowOnly(_viewMenu);

        private void ShowAccount() => ShowOnly(_viewAccount);

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

            ShowOnly(_viewConfirm);
        }

        private void ConfirmConcede()
            => ShowConfirm(Loc.Get("common_concede_this_match"), Loc.Get("match_settings_confirm_subtitle"),
                           Loc.Get("common_concede"), danger: true,
                           onConfirm: () => { _onConcede?.Invoke(); Close(); });

        // ── Language (2026-10-20) ───────────────────────────────────────────────────────

        /// <summary>
        /// One row per language whose table ships, each written in its own language so a player who
        /// cannot read the current one can still find theirs. The current language is the disabled row.
        ///
        /// <para>Off the match, picking one reloads the scene, so every screen redraws in it — the
        /// same reasoning as <see cref="ReloadThisScene"/> for sign-out. In a match a reload would
        /// eat the match, so the panel's own text switches live (UiLocalizer) and the rest follows
        /// on the next screen.</para>
        /// </summary>
        private void ShowLanguage()
        {
            var list = _viewLanguage?.Q<VisualElement>("language-list");
            if (list == null) return;
            list.Clear();
            foreach (string code in Loc.Available())
            {
                string picked = code;
                var row = new Button(() => PickLanguage(picked)) { text = Loc.NativeName(code) };
                row.AddToClassList("ms-item");
                row.SetEnabled(code != Loc.Language);
                list.Add(row);
            }

            ShowOnly(_viewLanguage);
        }

        private void PickLanguage(string code)
        {
            Loc.SetLanguage(code);
            if (_allowSceneReload)
            {
                Close();
                ReloadThisScene();
            }
            else
            {
                ShowMenu();
            }
        }

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
            // .ms-toggle--on drives the checkmark fill (settings.uss) — set explicitly rather
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
        /// <para>This is the player-facing way to leave an identity: a game that can only be signed
        /// out of by uninstalling is one where the only way to change accounts destroys the
        /// device's data.</para>
        ///
        /// <para>Read once at Init rather than watched: the panel is rebuilt on every scene load,
        /// and signing IN happens behind the gate before the menu exists.</para>
        ///
        /// <para><b>Hidden in-match</b> (<c>allowSceneReload: false</c>): signing out reloads the
        /// scene, and offering it mid-match would eject the player and forfeit the match without
        /// ever saying the word concede.</para>
        /// </summary>
        private void BindSignOut(VisualElement root)
        {
            var entry = root.Q<Button>("btn-sign-out");
            if (entry == null) return;

            if (!_allowSceneReload) { entry.style.display = DisplayStyle.None; return; }

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
                email.Length > 0 ? Loc.Format("settings_sign_out_title_email", email) : Loc.Get("settings_sign_out_title"),
                // Says what survives, because the whole point of the roster work is that the answer
                // changed: signing out is no longer anything like losing the account.
                Loc.Get("settings_sign_out_subtitle"),
                Loc.Get("common_sign_out"),
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
        /// <para><b>Then <see cref="DeviceAccounts.Invalidate"/> and a scene reload</b> — because every cached piece
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

        // ── Password (Sam, 2026-11-03) ──────────────────────────────────────────────────

        /// <summary>
        /// Set or replace the signed-in account's password. Until this the name step was the only
        /// place a password could be set, so a Google account, or anyone who skipped it there, could
        /// never use the sign-in screen's "Use my password".
        ///
        /// <para>Shown in-match too: unlike Sign out it reloads nothing. Hidden with no session,
        /// because there is no identity to put it on.</para>
        /// </summary>
        private void BindPassword(VisualElement root)
        {
            var entry = root.Q<Button>("btn-set-password");
            _passwordNew = root.Q<TextField>("password-new");
            _passwordConfirm = root.Q<TextField>("password-confirm");
            _passwordStatus = root.Q<Label>("password-status");
            _passwordSave = root.Q<Button>("btn-password-save");
            if (entry == null || _viewPassword == null) return;

            bool signedIn;
            try { signedIn = AuthGateway.IsSignedIn; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MatchSettings] could not read the sign-in state ({ex.GetType().Name}); " +
                                 "hiding Set a password.");
                signedIn = false;
            }
            entry.style.display = signedIn ? DisplayStyle.Flex : DisplayStyle.None;
            if (!signedIn) return;

            Wire(root, "btn-set-password", ShowPassword);
            Wire(root, "btn-password-back", ShowAccount);
            Wire(root, "btn-password-save", SavePassword);
            _passwordNew?.RegisterValueChangedCallback(_ => SetPasswordStatus("", false));
            _passwordConfirm?.RegisterValueChangedCallback(_ => SetPasswordStatus("", false));
            _passwordConfirm?.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) SavePassword();
            });
        }

        private void ShowPassword()
        {
            _passwordNew?.SetValueWithoutNotify("");
            _passwordConfirm?.SetValueWithoutNotify("");
            SetPasswordStatus("", false);
            ShowOnly(_viewPassword);
            _passwordNew?.Focus();
        }

        /// <summary>Same rules as the name step's optional password, and the same messages, so the
        /// two places a password is set can never disagree about what a valid one is.</summary>
        private async void SavePassword()
        {
            if (_savingPassword) return;
            string password = _passwordNew?.value ?? "";
            string confirm = _passwordConfirm?.value ?? "";
            if (password.Length < 6) { SetPasswordStatus(Loc.Get("account_password_too_short"), true); return; }
            if (password != confirm) { SetPasswordStatus(Loc.Get("account_password_mismatch"), true); return; }

            _savingPassword = true;
            _passwordSave?.SetEnabled(false);
            SetPasswordStatus(Loc.Get("settings_password_saving"), false);
            try
            {
                AuthResult result = await AuthGateway.Provider.SetPasswordAsync(password);
                if (this == null) return;
                if (result.Ok)
                {
                    _passwordNew?.SetValueWithoutNotify("");
                    _passwordConfirm?.SetValueWithoutNotify("");
                    SetPasswordStatus(Loc.Get("settings_password_saved"), false);
                    return;
                }
                Debug.LogWarning($"[MatchSettings] SetPasswordAsync failed: {result.Failure} — {result.Detail}");
                SetPasswordStatus(string.IsNullOrEmpty(result.Detail)
                    ? Loc.Get("settings_password_failed") : result.Detail, true);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MatchSettings] setting a password threw: {ex}");
                if (this != null) SetPasswordStatus(Loc.Get("settings_password_failed"), true);
            }
            finally
            {
                _savingPassword = false;
                if (this != null) _passwordSave?.SetEnabled(true);
            }
        }

        private void SetPasswordStatus(string message, bool error)
        {
            if (_passwordStatus == null) return;
            _passwordStatus.text = string.IsNullOrEmpty(message) ? " " : message;
            _passwordStatus.EnableInClassList("ms-status--error", error);
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
            _pointerOverGear = false;
        }

        private void OnDestroy()
        {
            if (_current == this) _current = null;
        }

        /// <summary>Reload the current scene so everything rebuilds against the new session:
        /// re-resolving each piece of state individually is a list that will be incomplete the first
        /// time somebody adds to it. This would eject the player mid-match, which is why
        /// <see cref="Init"/>'s <c>allowSceneReload</c> hides Sign out in-match (Sam, 2026-08-09) —
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
