using System;
using Emberfall.Core.Collection;
using Emberfall.Net.Client.Auth;
using Emberfall.Session;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// First launch, as a SCENE (Sam, 2026-08-25): create the account, then name it. Two sequential
    /// views — <see cref="SignInGateController"/>'s email/code gate, then the display name and an
    /// optional password — and then the player is handed on and never comes back here.
    ///
    /// <para>Both were <c>.mm-modal</c> overlays inside <c>MainMenuScreen.uxml</c> until then. Same
    /// call character creation got: full-screen, opaque, unskippable, and a destination.</para>
    ///
    /// <para><b>Re-hosting was not the fix on its own — the nesting was the bug.</b>
    /// <c>mainmenu.uss</c> carries a 2026-08-23 measurement that split the pitch from the form
    /// because <i>"the gate lands near 276dp and the name modal near 358dp, against 411
    /// available"</i>. That budget is wrong: both modals carried <c>.emb-bleed</c>
    /// (<c>padding: var(--edge-safe)</c>, 52dp per edge) at the time it was written, so the real box
    /// was 411 - 104 = <b>307dp</b>. The gate fitted with 31dp spare; THE NAME STEP OVERFLOWED BY
    /// 51dp — and it is the step with no way past it, which is why a first launch was a dead end
    /// rather than a cosmetic problem. Here the root IS the panel: one edge clearance, and no
    /// <c>.mm-modal__panel</c> padding inside an already-inset box.</para>
    ///
    /// <para><b><see cref="SignInGateController"/> is reused verbatim.</b> It takes an arbitrary
    /// root and resolves everything by element name, so moving the markup cost it nothing — this
    /// controller and the UXML are the only things that know the flow moved. Its ruling still
    /// holds and is why neither view has a Cancel: registration happens on first load and cannot be
    /// skipped (Sam, 2026-08-08). There is no back gesture out of this scene either — deliberately
    /// no <c>emb-back</c> anywhere in the UXML.</para>
    ///
    /// <para><b>Where it hands off, and why not always to the menu.</b> The old
    /// <c>ConfirmName</c> ended by sending a player with an empty roster to CharacterRoster, since
    /// a first launch has no character to play. That is preserved exactly: empty roster goes to the
    /// roster, anything else to the menu. Losing it would leave a brand-new account on a menu whose
    /// every button needs a character.</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class AccountCreateController : MonoBehaviour
    {
        private VisualElement _root;
        private VisualElement _signInView, _nameView;
        private PlayerAccount _account;
        private SignInGateController _signIn;

        private Label _nameError;
        private TextField _nameField, _namePassword, _namePasswordConfirm;
        private Button _nameConfirm;
        private bool _nameBusy;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement?.Q<VisualElement>("root");
            if (_root == null)
            {
                Debug.LogError("[AccountCreate] No rootVisualElement — is the UXML assigned?");
                return;
            }

            _account = PlayerAccount.Local;
            _signInView = _root.Q<VisualElement>("signin-view");
            _nameView = _root.Q<VisualElement>("name-view");

            BindNameStep();

            _signIn = new SignInGateController(_root, AuthGateway.Provider);
            if (_signIn.IsRequired)
            {
                ShowOnly(_signInView);
                _signIn.Open(onSignedIn: BeginNameStep);
                return;
            }

            BeginNameStep();
        }

        private void OnDisable() => _signIn?.Close();

        /// <summary>Exactly one view is in the tree at a time. They are siblings rather than a stack
        /// of overlays, so nothing can end up layered on a 411dp panel.</summary>
        private void ShowOnly(VisualElement view)
        {
            Show(_signInView, ReferenceEquals(view, _signInView));
            Show(_nameView, ReferenceEquals(view, _nameView));
        }

        private static void Show(VisualElement element, bool shown)
        {
            if (element != null) element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ── Name step ───────────────────────────────────────────────────────────────────

        private void BindNameStep()
        {
            _nameError = _root.Q<Label>("name-error");
            _nameField = _root.Q<TextField>("name-field");
            _namePassword = _root.Q<TextField>("name-password");
            _namePasswordConfirm = _root.Q<TextField>("name-password-confirm");
            _nameConfirm = _root.Q<Button>("btn-name-confirm");

            _nameConfirm?.RegisterCallback<ClickEvent>(_ => ConfirmName());

            if (_nameField == null) return;
            // Enter submits. A single-field form whose only way forward is the pointer is a form
            // people abandon.
            _nameField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter) ConfirmName();
            });
            // Clear a stale refusal as soon as anything changes, so the message always describes
            // what is currently in the field.
            _nameField.RegisterValueChangedCallback(_ => SetError(""));
            _namePassword?.RegisterValueChangedCallback(_ => SetError(""));
            _namePasswordConfirm?.RegisterValueChangedCallback(_ => SetError(""));
        }

        /// <summary>The gate is satisfied. If a name is still owed, ask for it; otherwise this scene
        /// has nothing left to do and gets out of the way immediately.</summary>
        private void BeginNameStep()
        {
            _account = PlayerAccount.Local;
            if (_account == null || !_account.NeedsDisplayName) { Continue(); return; }

            ShowOnly(_nameView);

            if (_nameField != null)
            {
                _nameField.SetValueWithoutNotify(_account.Profile.DisplayName ?? "");
                _nameField.Focus();
            }

            bool offerPassword = AuthGateway.IsConfigured;
            Show(_root.Q<Label>("name-password-help"), offerPassword);
            Show(_namePassword, offerPassword);
            Show(_namePasswordConfirm, offerPassword);
            _namePassword?.SetValueWithoutNotify("");
            _namePasswordConfirm?.SetValueWithoutNotify("");

            SetError("");
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
                SetError(check.Reason);
                return;
            }

            string password = _namePassword?.value ?? "";
            string passwordConfirm = _namePasswordConfirm?.value ?? "";

            // Optional (Sam, NOR-246): an empty field skips it rather than refusing to continue —
            // the name is what the unskippable sign-in ruling actually requires, and a network
            // hiccup here must not trap a player who already holds a valid account in this scene.
            if (password.Length > 0 || passwordConfirm.Length > 0)
            {
                if (password.Length < 6) { SetError("Password must be at least 6 characters."); return; }
                if (password != passwordConfirm) { SetError("Passwords do not match."); return; }

                SetBusy(true);
                try
                {
                    AuthResult result = await AuthGateway.Provider.SetPasswordAsync(password);
                    SetBusy(false);
                    if (!result.Ok)
                    {
                        SetError(string.IsNullOrEmpty(result.Detail)
                            ? "Could not set a password. Try again, or clear both fields to skip it."
                            : result.Detail);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    SetBusy(false);
                    Debug.LogError($"[AccountCreate] setting a password threw: {ex}");
                    SetError("Something went wrong setting a password. Try again, or clear both fields to skip it.");
                    return;
                }
            }

            Continue();
        }

        /// <summary>
        /// Leave, and never come back. A brand-new account has no character, and every button on the
        /// Main Menu needs one — so an empty roster goes to CharacterRoster rather than the menu.
        /// This is the continuation the old <c>MainMenuController.ConfirmName</c> ended with, moved
        /// intact.
        /// </summary>
        private void Continue()
        {
            if (_account != null && _account.Roster().Count == 0)
            {
                CharacterRosterController.ReturnScene = "MainMenu";
                Go("CharacterRoster");
                return;
            }
            Go("MainMenu");
        }

        private void SetBusy(bool busy)
        {
            _nameBusy = busy;
            _nameConfirm?.SetEnabled(!busy);
            _nameField?.SetEnabled(!busy);
            _namePassword?.SetEnabled(!busy);
            _namePasswordConfirm?.SetEnabled(!busy);
        }

        private void SetError(string message)
        {
            if (_nameError == null) return;
            // A space, not empty: an empty label collapses to zero height and the form jumps by a
            // line every time a refusal appears or clears.
            _nameError.text = string.IsNullOrEmpty(message) ? " " : message;
        }

        private static void Go(string sceneName)
        {
            if (Application.CanStreamedLevelBeLoaded(sceneName))
            {
                SceneManager.LoadScene(sceneName);
                return;
            }
            Debug.LogError($"[AccountCreate] Scene '{sceneName}' is not in the build list — staying put.");
        }
    }
}
