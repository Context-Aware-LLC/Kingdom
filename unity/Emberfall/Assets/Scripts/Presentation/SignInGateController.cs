using System;
using System.Threading.Tasks;
using Emberfall.Net.Client.Auth;
using Emberfall.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The first-launch registration overlay (EF-074e / NOR-231). An email address, then the eight-digit
    /// code that arrives in the reply — and no way past it.
    ///
    /// <para><b>Sam's ruling, 2026-08-08: registration happens on first load and cannot be skipped.</b>
    /// There are no guest accounts and there never will be. So this overlay has no Cancel, no Skip and
    /// no "continue as guest" — not a disabled one, none at all, because a dead button invites
    /// clicking. The only way out is a session.</para>
    ///
    /// <para><b>The code, not a link (Sam, 2026-08-08).</b> A deep link would have to be registered on
    /// every platform we ship and is flakiest on desktop; a code the player reads and types behaves
    /// identically everywhere and never takes them out of the game. The practical consequence lives in
    /// <see cref="SupabaseAuthBackend.BeginEmailCodeAsync"/>: this device holds nothing between
    /// sending and verifying, so force-quitting mid-sign-in costs the player nothing.</para>
    ///
    /// <para><b>The gate only closes when there is a project to sign into.</b>
    /// <see cref="IsRequired"/> is false on a build with no <c>SupabaseAuthSettings</c> asset, because
    /// gating a player behind a sign-in that cannot succeed is a locked door with no key — the game
    /// would simply not start. It arms itself the moment that asset lands, with no code change.
    /// RETIREMENT TRIGGER: when the Supabase project is provisioned and the asset is committed, this
    /// paragraph and the unconfigured branch in <see cref="Open"/> stop being hypothetical — re-read
    /// them and confirm the boot still behaves the way you want on a device with no network.</para>
    ///
    /// <para><b>Unskippable sign-in means the game needs a network to start, Single Player included.</b>
    /// That is the accepted price of the ruling, not an oversight. It is why an offline first launch
    /// says so in plain words rather than showing a rejected code.</para>
    /// </summary>
    public sealed class SignInGateController
    {
        /// <summary>What the overlay is waiting for. There is deliberately no fourth state for
        /// "gave up".</summary>
        private enum Step
        {
            /// <summary>Asking for an address.</summary>
            Email,

            /// <summary>The mail is away; asking for the code that is in it.</summary>
            Code,
        }

        private readonly VisualElement _modal;
        private readonly Label _title;
        private readonly Label _help;
        private readonly TextField _emailField;
        private readonly TextField _codeField;
        private readonly Label _error;
        private readonly Button _primary;
        private readonly Button _back;
        private readonly Button _resend;

        private readonly AuthTokenProvider _auth;

        private Step _step = Step.Email;
        private bool _busy;
        private Action _onSignedIn;

        /// <summary>The address the code was actually mailed to. Held rather than re-read from the
        /// field, because Supabase keys the code to the address — a player who edits the field after
        /// asking for a code would otherwise get a refusal that reads like a wrong code.</summary>
        private string _sentTo = "";

        public SignInGateController(VisualElement root, AuthTokenProvider auth)
        {
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));

            _modal = root.Q<VisualElement>("signin-modal");
            _title = root.Q<Label>("signin-title");
            _help = root.Q<Label>("signin-help");
            _emailField = root.Q<TextField>("signin-email");
            _codeField = root.Q<TextField>("signin-code");
            _error = root.Q<Label>("signin-error");
            _primary = root.Q<Button>("btn-signin-primary");
            _back = root.Q<Button>("btn-signin-back");
            _resend = root.Q<Button>("btn-signin-resend");

            if (_modal == null)
            {
                Debug.LogError("[SignIn] signin-modal is not in the UXML; the sign-in gate cannot open.");
                return;
            }

            _primary?.RegisterCallback<ClickEvent>(_ => OnPrimary());
            _back?.RegisterCallback<ClickEvent>(_ => ToEmailStep(""));
            _resend?.RegisterCallback<ClickEvent>(_ => SendCode(_sentTo));

            if (_emailField != null)
            {
                _emailField.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (IsSubmit(evt)) OnPrimary();
                });
                _emailField.RegisterValueChangedCallback(_ => SetError(""));
            }

            if (_codeField != null)
            {
                // Rewrite the field to just its digits as the player types. A code copied out of a
                // mail client arrives as "123 456", with a trailing space, or wrapped in punctuation
                // often enough that refusing those reads as the code being wrong — and the player's
                // next move is to request a new one, which invalidates the one they have.
                _codeField.RegisterValueChangedCallback(evt =>
                {
                    string digits = EmailCode.Normalize(evt.newValue);
                    if (digits != evt.newValue) _codeField.SetValueWithoutNotify(digits);
                    SetError("");
                    RefreshPrimary();
                });
                _codeField.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (IsSubmit(evt)) OnPrimary();
                });
            }
        }

        /// <summary>
        /// True when this launch must not reach the menu until the player signs in.
        ///
        /// <para>False on a build with no Supabase project — see the type remarks for why that is the
        /// only safe answer and not a hole in the ruling.</para>
        /// </summary>
        public bool IsRequired => _auth.IsConfigured && !_auth.IsSignedIn;

        /// <summary>
        /// Show the gate. <paramref name="onSignedIn"/> runs once, on the main thread, after a session
        /// is stored — that is the continuation the menu's own first-run work hangs off.
        /// </summary>
        public void Open(Action onSignedIn)
        {
            _onSignedIn = onSignedIn;

            if (_modal == null)
            {
                // The overlay is missing from the UXML. Refusing to continue would brick the boot over
                // a broken screen, which is a worse outcome than an unsigned session the server treats
                // as a guest — and the error above already says what happened.
                onSignedIn?.Invoke();
                return;
            }

            if (!_auth.IsConfigured)
            {
                Debug.LogWarning("[SignIn] no Supabase project is configured, so first-launch " +
                                 "registration cannot run. Create Assets/Resources/SupabaseAuthSettings.asset " +
                                 "to arm it.");
                onSignedIn?.Invoke();
                return;
            }

            ToEmailStep("");
            _modal.RemoveFromClassList("mm-modal--hidden");
            _emailField?.Focus();
        }

        // ── Steps ───────────────────────────────────────────────────────────────────────

        private void ToEmailStep(string message)
        {
            _step = Step.Email;
            _sentTo = "";

            if (_title != null) _title.text = "Create your account";
            if (_help != null)
                _help.text = "Emberfall needs an account before you can play. Enter your email and we " +
                             "will send you an eight-digit code.";

            Display(_emailField, true);
            Display(_codeField, false);
            Display(_back, false);
            Display(_resend, false);
            _codeField?.SetValueWithoutNotify("");

            SetError(message);
            RefreshPrimary();
            _emailField?.Focus();
        }

        private void ToCodeStep(string email)
        {
            _step = Step.Code;
            _sentTo = email;

            if (_title != null) _title.text = "Check your email";
            if (_help != null) _help.text = $"We sent an eight-digit code to {email}. Enter it below.";

            Display(_emailField, false);
            Display(_codeField, true);
            Display(_back, true);
            Display(_resend, true);
            _codeField?.SetValueWithoutNotify("");

            SetError("");
            RefreshPrimary();
            _codeField?.Focus();
        }

        private void OnPrimary()
        {
            if (_busy) return;
            if (_step == Step.Email) SendCode(_emailField?.value ?? "");
            else Verify();
        }

        // ── Calls ───────────────────────────────────────────────────────────────────────

        // async void is correct for a UI handler and only here: UnityWebRequestAuthHttp resumes its
        // Task on the main thread, so everything after the await is free to touch elements. The
        // try/catch is what keeps async void honest — an escaped exception would otherwise be a
        // crash rather than a message on the panel.
        private async void SendCode(string typedEmail)
        {
            string email = (typedEmail ?? "").Trim();
            if (email.Length == 0) { SetError("Enter your email address."); return; }
            if (!LooksLikeAnEmail(email)) { SetError("That does not look like an email address."); return; }

            SetBusy(true, "Sending…");
            try
            {
                AuthResult result = await _auth.BeginEmailCodeAsync(email);
                SetBusy(false, null);

                if (result.Ok) { ToCodeStep(email); return; }

                // Staying on the code step for a failed RESEND matters: the player still holds a
                // working code from the first mail, and dropping them back to the email field would
                // read as having lost it.
                SetError(Explain(result, "We could not send the code."));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                Debug.LogError($"[SignIn] sending a code threw: {ex}");
                SetError("Something went wrong sending the code. Try again.");
            }
        }

        private async void Verify()
        {
            string code = EmailCode.Normalize(_codeField?.value ?? "");
            if (code.Length != EmailCode.Length) { SetError("Enter the eight-digit code from the email."); return; }

            SetBusy(true, "Signing in…");
            try
            {
                AuthResult result = await _auth.VerifyEmailCodeAsync(_sentTo, code);
                SetBusy(false, null);

                if (result.Ok)
                {
                    _modal?.AddToClassList("mm-modal--hidden");
                    Action done = _onSignedIn;
                    _onSignedIn = null;   // the continuation runs once, never on a later re-open
                    done?.Invoke();
                    return;
                }

                // The mailed code survives a wrong attempt (AuthTokenProvider.VerifyEmailCodeAsync),
                // so clear the field and leave the player where they are rather than sending them
                // back to their inbox.
                _codeField?.SetValueWithoutNotify("");
                RefreshPrimary();
                _codeField?.Focus();
                SetError(Explain(result, "That code was not accepted."));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                Debug.LogError($"[SignIn] verifying a code threw: {ex}");
                SetError("Something went wrong signing in. Try again.");
            }
        }

        /// <summary>
        /// Turn a failure into a sentence a player can act on.
        ///
        /// <para><b>Offline is called out by name.</b> It is the failure an unskippable first-launch
        /// sign-in makes most likely, and it is the one where Supabase's own wording — when there even
        /// is any — describes nothing the player did.</para>
        /// </summary>
        private static string Explain(AuthResult result, string fallback)
        {
            switch (result.Failure)
            {
                case AuthFailure.Network:
                    return "No connection. Emberfall needs the internet to sign you in.";
                case AuthFailure.NotConfigured:
                    return "This build has no sign-in service configured.";
                default:
                    return string.IsNullOrEmpty(result.Detail) ? fallback : result.Detail;
            }
        }

        // ── Chrome ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Disable every control for the length of a call.
        ///
        /// <para>The one that matters is Resend: a second tap mails a second code, and Supabase
        /// invalidates the first — so an impatient double-tap would silently break the code the player
        /// is already reading.</para>
        /// </summary>
        private void SetBusy(bool busy, string label)
        {
            _busy = busy;

            if (_primary != null)
            {
                _primary.SetEnabled(!busy && CanSubmit());
                if (busy && label != null) _primary.text = label;
                else RefreshPrimaryText();
            }
            _back?.SetEnabled(!busy);
            _resend?.SetEnabled(!busy);
            _emailField?.SetEnabled(!busy);
            _codeField?.SetEnabled(!busy);
        }

        private bool CanSubmit()
            => _step == Step.Email || EmailCode.IsComplete(_codeField?.value ?? "");

        private void RefreshPrimary()
        {
            RefreshPrimaryText();
            _primary?.SetEnabled(!_busy && CanSubmit());
        }

        private void RefreshPrimaryText()
        {
            if (_primary == null) return;
            _primary.text = _step == Step.Email ? "Email me a code" : "Sign in";
        }

        private void SetError(string message)
        {
            // A space rather than an empty string so the line keeps its height and the buttons below
            // do not jump when a message appears or clears — the same rule the name modal follows.
            if (_error != null) _error.text = string.IsNullOrEmpty(message) ? " " : message;
        }

        private static void Display(VisualElement element, bool shown)
        {
            if (element != null) element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static bool IsSubmit(KeyDownEvent evt)
            => evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter;

        /// <summary>
        /// The cheapest check that catches a typo, and deliberately no more. Validating an address for
        /// real is what sending the mail does; a stricter pattern here can only refuse addresses that
        /// would have worked.
        /// </summary>
        private static bool LooksLikeAnEmail(string email)
        {
            int at = email.IndexOf('@');
            if (at <= 0 || at != email.LastIndexOf('@')) return false;
            int dot = email.IndexOf('.', at + 2);
            return dot > 0 && dot < email.Length - 1 && !email.Contains(" ");
        }
    }
}
