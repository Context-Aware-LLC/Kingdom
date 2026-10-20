using System;
using Kingdom.Core.Collection;
using Kingdom.Net.Client.Auth;
using Kingdom.Session;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
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
    /// <para><b>THOSE NUMBERS ARE PRE-#349 UNITS.</b> The panel was 914 x 411 when they were
    /// measured; it is 2400 x 1080 authored px now, so nothing here is a live budget. The
    /// structural conclusion — one clearance, no padding nested inside it — is what survives, and
    /// it did not depend on the unit. THIS SCREEN HAS NOT BEEN RE-MEASURED since the change.</para>
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
            UiLocalizer.Attach(_root);

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
        /// of overlays, so nothing can end up layered on a screen this short.</summary>
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
            // The gate has already bound this device to the signed-in identity, so Local is now the
            // account that identity owns rather than whichever one the device happened to mint.
            _account = PlayerAccount.Local;
            // The name lives against the identity (2026-10-18), so a returning player on a new
            // phone already has it in the token they just signed in with, and skips this step.
            if (!AuthGateway.NeedsDisplayName) { Continue(); return; }

            ShowOnly(_nameView);
            TellTheTruthAboutTheName();

            if (_nameField != null)
            {
                _nameField.SetValueWithoutNotify("");
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

        /// <summary>
        /// Word the name step for the player actually standing in front of it.
        ///
        /// <para><b>A RETURNING player reaches this screen only when their identity holds no
        /// name</b> — an account named before 2026-09-01, when names were first written there, or
        /// one whose name lived only in the old device copy. Telling them "choose carefully, it is
        /// permanent" would be the 2026-08-31 finding again: they chose once already. So they get
        /// the accurate version, which is that we could not recover the old one.</para>
        /// </summary>
        private void TellTheTruthAboutTheName()
        {
            if (_signIn == null || !_signIn.PlayerSaidTheyAreReturning) return;

            var title = _root.Q<Label>("name-title");
            var help = _root.Q<Label>("name-help");

            if (title != null) title.text = "What should we call you?";
            if (help != null)
                help.text = "We could not recover the name on this account, so please choose one " +
                            "again. It travels with your account from now on.";
        }

        /// <summary>
        /// Store the name against the identity and wait for it.
        ///
        /// <para><b>Awaited, unlike the fire-and-forget push it replaces.</b> The identity is the
        /// only copy now: the match server names this seat from the token, and a failed write left
        /// unreported would send the player into matches as a nameless seat. The game cannot be
        /// played without the server anyway, so waiting on it here costs nothing a player could
        /// otherwise have had.</para>
        ///
        /// <para>The write is followed by a token refresh inside
        /// <see cref="AuthTokenProvider.SetDisplayNameAsync"/>. If the refreshed session still has
        /// no name, the write is treated as failed and the player is asked to try again, because
        /// both the next launch and the match server read the name from that token.</para>
        /// </summary>
        // async void is correct for a UI handler: these awaits resume on the main thread (they ride
        // UnityWebRequestAuthHttp, same as everywhere else auth touches UI Toolkit), so everything
        // after them is free to touch elements. The try/catch keeps it honest.
        private async void ConfirmName()
        {
            if (_nameBusy) return;

            NameCheck check = PlayerName.Check(_nameField?.value ?? "");
            if (!check.IsValid)
            {
                // The refusal reason is authored player-facing in PlayerName — rendered verbatim so
                // there is one wording, not one per form.
                SetError(check.Reason);
                return;
            }

            string password = _namePassword?.value ?? "";
            string passwordConfirm = _namePasswordConfirm?.value ?? "";
            // Optional (Sam, NOR-246): an empty field skips it rather than refusing to continue.
            bool setPassword = password.Length > 0 || passwordConfirm.Length > 0;
            if (setPassword)
            {
                if (password.Length < 6) { SetError("Password must be at least 6 characters."); return; }
                if (password != passwordConfirm) { SetError("Passwords do not match."); return; }
            }

            SetBusy(true);
            try
            {
                AuthResult named = await AuthGateway.Provider.SetDisplayNameAsync(check.Normalized);
                if (!named.Ok || AuthGateway.NeedsDisplayName)
                {
                    SetBusy(false);
                    Debug.LogWarning($"[AccountCreate] the display name did not reach the identity " +
                                     $"({named.Failure}: {named.Detail}).");
                    SetError("We couldn't save your name. Check your connection and try again.");
                    return;
                }

                if (setPassword)
                {
                    AuthResult result = await AuthGateway.Provider.SetPasswordAsync(password);
                    if (!result.Ok)
                    {
                        SetBusy(false);
                        SetError(string.IsNullOrEmpty(result.Detail)
                            ? "Could not set a password. Try again, or clear both fields to skip it."
                            : result.Detail);
                        return;
                    }
                }
                SetBusy(false);
            }
            catch (Exception ex)
            {
                SetBusy(false);
                Debug.LogError($"[AccountCreate] confirming the name threw: {ex}");
                SetError("Something went wrong. Try again.");
                return;
            }

            Continue();
        }

        /// <summary>
        /// Leave, and never come back. A brand-new account has no deck, and every button on the
        /// Main Menu needs one — so an empty roster goes to CharacterRoster rather than the menu.
        /// This is the continuation the old <c>MainMenuController.ConfirmName</c> ended with, moved
        /// intact.
        ///
        /// <para>It asked for characters until ADR-0009. The condition is unchanged in substance:
        /// the thing you need before the menu is useful is a DECK, and a character was only ever
        /// the container one arrived in.</para>
        /// </summary>
        private void Continue()
        {
            if (_account != null && _account.Decks().Count == 0)
            {
                CharacterRosterController.ReturnScene = ScreenNav.MainMenu;
                ScreenNav.Go(ScreenNav.CharacterRoster);
                return;
            }
            ScreenNav.Go(ScreenNav.MainMenu);
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
    }
}
