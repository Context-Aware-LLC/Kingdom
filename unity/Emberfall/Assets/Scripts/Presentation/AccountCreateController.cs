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
            RestoreNameFromIdentity();
            if (_account == null || !_account.NeedsDisplayName) { Continue(); return; }

            ShowOnly(_nameView);
            TellTheTruthAboutTheName();

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

        /// <summary>
        /// Word the name step for the player actually standing in front of it.
        ///
        /// <para><b>Reaching this screen as a RETURNING player means their account has no name stored
        /// against the identity</b> — <see cref="RestoreNameFromIdentity"/> would have taken it and
        /// skipped the step otherwise. That is every account created before names were written there,
        /// which today is all of them. Showing those players "choose carefully, it is permanent, to
        /// change it later you would have to contact support" is the 2026-08-31 finding at its
        /// sharpest: they have already been told that once, and the reinstall they just did is what
        /// disproved it.</para>
        ///
        /// <para>So the promise is made to new players only, where it is now true — the name goes up
        /// to the identity in <see cref="ConfirmName"/> and comes back on the next sign-in. Returning
        /// players get the accurate version, which is that we could not recover the old one.</para>
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
        /// Put the name the identity carries back onto this device, when this device has none.
        ///
        /// <para><b>This is what makes "returning player" mean something on a reinstalled phone.</b>
        /// The roster and decks went with the app; the entitlement ledger is server-side and comes
        /// back on its own. The name sat in neither camp, so a player who signed in with the address
        /// they had always used was asked to choose a "permanent" name a second time — the exact
        /// contradiction #329's neighbours turned up on 2026-08-31. It now rides in the token's
        /// <c>user_metadata</c>; see <see cref="AuthSession.DisplayName"/>.</para>
        ///
        /// <para><b>A local name always wins and is never overwritten.</b> The profile on disk is the
        /// one this player has been playing under, and the token's copy can be stale by a whole
        /// session — Supabase stamps metadata into a JWT when it mints one, so a name set after the
        /// current token was issued is not in it. Writing the older value over the newer one would
        /// silently rename a player who did nothing.</para>
        ///
        /// <para>Failure is a no-op by design: an unreadable profile, a token with no metadata, or an
        /// unconfigured build all end with the name step asking, which is exactly what it did before.</para>
        /// </summary>
        private void RestoreNameFromIdentity()
        {
            if (_account == null || !_account.NeedsDisplayName) return;

            string fromIdentity = AuthGateway.Provider.Current.DisplayName;
            if (string.IsNullOrEmpty(fromIdentity)) return;

            NameCheck check = _account.SetDisplayName(fromIdentity);
            if (check.IsValid)
            {
                Debug.Log("[AccountCreate] restored this account's display name from the signed-in identity.");
                return;
            }

            // A name the server holds and this build now refuses. Rare, and it must not be fatal:
            // fall through to the name step and let the player pick a new one.
            Debug.LogWarning($"[AccountCreate] the display name held against this identity is not valid " +
                             $"for this build ({check.Reason}); asking for a new one.");
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

            // Store the name against the IDENTITY as well as this install, so the next device to sign
            // in as this player gets it back instead of being asked for a "permanent" name a second
            // time. Deliberately fire-and-forget-ish: the name is already saved locally and the player
            // is already past this screen in every way that matters, so a network failure here must
            // not trap them behind a form. It is logged, not surfaced — the cost of losing it is one
            // extra question on a future reinstall, not a broken account.
            if (AuthGateway.IsConfigured && AuthGateway.IsSignedIn)
                PushDisplayNameToIdentity(check.Normalized);

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
        /// Write the display name up to the identity, off the critical path.
        ///
        /// <para><b>Not awaited by <see cref="ConfirmName"/>, and that is the point.</b> The name is
        /// already on disk and already valid; the only thing this buys is a future reinstall, so
        /// blocking the one screen a player cannot skip on a round-trip that might time out would
        /// trade a real problem for a hypothetical one. <c>async void</c> with a total catch is the
        /// honest shape for that — there is no caller to hand a failure to.</para>
        /// </summary>
        private static async void PushDisplayNameToIdentity(string displayName)
        {
            try
            {
                AuthResult result = await AuthGateway.Provider.SetDisplayNameAsync(displayName);
                if (!result.Ok)
                    Debug.LogWarning($"[AccountCreate] could not store the display name against this " +
                                     $"identity ({result.Failure}: {result.Detail}); it is saved on this " +
                                     "device only, so a reinstall will ask for it again.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AccountCreate] storing the display name against this identity threw " +
                                 $"({ex.GetType().Name}: {ex.Message}); it is saved on this device only.");
            }
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
