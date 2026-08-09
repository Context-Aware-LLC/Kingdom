using System;
using System.Threading.Tasks;
using Emberfall.Net.Client.Auth;
using Emberfall.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The first-launch registration overlay (EF-074e / NOR-231, magic link NOR-246 2026-08-08). An
    /// email address, then a confirmation link that hands the player back to the game.
    ///
    /// <para><b>Sam's ruling, 2026-08-08: registration happens on first load and cannot be skipped.</b>
    /// There are no guest accounts and there never will be. So this overlay has no Cancel, no Skip and
    /// no "continue as guest" — not a disabled one, none at all, because a dead button invites
    /// clicking. The only way out is a session.</para>
    ///
    /// <para><b>Magic link over a typed code (Sam, NOR-246 2026-08-08).</b> The eight-digit code was
    /// the original build's answer to "no deep link plumbing on any platform" — see the retired
    /// <see cref="Step"/> path below, kept rather than deleted. Going Android-only removed that
    /// objection: Android hands a tapped mail link straight back to the app via an intent, and eight
    /// digits read from an inbox and retyped by hand is a worse ask than six ever was, per Sam. The
    /// code path stays in <see cref="AuthTokenProvider"/> and <see cref="SupabaseAuthBackend"/>
    /// untouched — only this overlay's wiring changed.</para>
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
    /// says so in plain words rather than showing a rejected link.</para>
    /// </summary>
    public sealed class SignInGateController
    {
        /// <summary>What the overlay is waiting for. There is deliberately no fourth state for
        /// "gave up".</summary>
        private enum Step
        {
            /// <summary>Asking for an address.</summary>
            Email,

            /// <summary>The mail is away; waiting for the player to tap the link in it.</summary>
            Waiting,
        }

        private readonly VisualElement _modal;
        private readonly Label _title;
        private readonly Label _help;
        private readonly TextField _emailField;
        private readonly Label _error;
        private readonly Button _primary;
        private readonly Button _back;
        private readonly Button _resend;

        private readonly AuthTokenProvider _auth;

        private Step _step = Step.Email;
        private bool _busy;
        private Action _onSignedIn;

        /// <summary>The address the link was actually mailed to. Held rather than re-read from the
        /// field, because the field is hidden once we are waiting on the redirect.</summary>
        private string _sentTo = "";

        public SignInGateController(VisualElement root, AuthTokenProvider auth)
        {
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));

            _modal = root.Q<VisualElement>("signin-modal");
            _title = root.Q<Label>("signin-title");
            _help = root.Q<Label>("signin-help");
            _emailField = root.Q<TextField>("signin-email");
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
            _resend?.RegisterCallback<ClickEvent>(_ => SendMagicLink(_sentTo));

            if (_emailField != null)
            {
                _emailField.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (IsSubmit(evt)) OnPrimary();
                });
                _emailField.RegisterValueChangedCallback(_ => SetError(""));
            }

            // Android hands a tapped mail link back to a running instance through this. A cold start
            // (the OS launched us fresh off the link) is caught separately in Open(), because this
            // event does not fire for the launch that is already in progress when it is subscribed.
            Application.deepLinkActivated += OnDeepLinkActivated;
        }

        /// <summary>Drop the deep-link subscription. Must be called when the owning screen is
        /// disabled/destroyed — <see cref="Application.deepLinkActivated"/> is a static event, so an
        /// instance that never unsubscribes outlives its modal and keeps handling links for a UI that
        /// is gone. Scene reloads (<see cref="DeviceAccounts.SwitchTo"/>) make a fresh instance on
        /// every re-enable, so this is not optional.</summary>
        public void Close()
        {
            Application.deepLinkActivated -= OnDeepLinkActivated;
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

            // A cold start: the OS may have launched this process directly off the confirmation link,
            // which means deepLinkActivated already fired (or never will, for this launch) before
            // anything here could have subscribed to it. Application.absoluteURL still carries it.
            if (TryExtractCode(Application.absoluteURL, out string coldCode))
            {
                _modal.RemoveFromClassList("mm-modal--hidden");
                CompleteFromDeepLink(coldCode);
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
                             "will send you a confirmation link.";

            Display(_emailField, true);
            Display(_primary, true);
            Display(_back, false);
            Display(_resend, false);

            SetError(message);
            RefreshPrimary();
            _emailField?.Focus();
        }

        private void ToWaitingStep(string email)
        {
            _step = Step.Waiting;
            _sentTo = email;

            if (_title != null) _title.text = "Check your email";
            if (_help != null)
                _help.text = $"We sent a confirmation link to {email}. Open it on this device to continue.";

            Display(_emailField, false);
            Display(_primary, false);
            Display(_back, true);
            Display(_resend, true);

            SetError("");
            RefreshPrimary();
        }

        private void OnPrimary()
        {
            if (_busy) return;
            if (_step == Step.Email) SendMagicLink(_emailField?.value ?? "");
        }

        // ── Calls ───────────────────────────────────────────────────────────────────────

        // async void is correct for a UI handler and only here: UnityWebRequestAuthHttp resumes its
        // Task on the main thread, so everything after the await is free to touch elements. The
        // try/catch is what keeps async void honest — an escaped exception would otherwise be a
        // crash rather than a message on the panel.
        private async void SendMagicLink(string typedEmail)
        {
            string email = (typedEmail ?? "").Trim();
            if (email.Length == 0) { SetError("Enter your email address."); return; }
            if (!LooksLikeAnEmail(email)) { SetError("That does not look like an email address."); return; }

            SetBusy(true, "Sending…");
            try
            {
                AuthResult result = await _auth.BeginMagicLinkAsync(email);
                SetBusy(false, null);

                if (result.Ok) { ToWaitingStep(email); return; }

                SetError(Explain(result, "We could not send the confirmation link."));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                Debug.LogError($"[SignIn] sending a magic link threw: {ex}");
                SetError("Something went wrong sending the link. Try again.");
            }
        }

        /// <summary>Handles a tapped link while this process was already running (backgrounded, then
        /// resumed via a new intent). Silently ignored when nothing on this device is waiting on a
        /// sign-in — a stale or foreign link must not disrupt whatever the player is doing.</summary>
        private void OnDeepLinkActivated(string url)
        {
            if (_modal == null) return;
            if (!_auth.Pending.IsStarted) return;
            if (!TryExtractCode(url, out string code)) return;

            _modal.RemoveFromClassList("mm-modal--hidden");
            CompleteFromDeepLink(code);
        }

        private async void CompleteFromDeepLink(string code)
        {
            ToWaitingStep(_sentTo.Length > 0 ? _sentTo : "your email");
            SetBusy(true, "Confirming…");
            try
            {
                AuthResult result = await _auth.CompleteSignInAsync(code);
                SetBusy(false, null);

                if (result.Ok)
                {
                    _modal?.AddToClassList("mm-modal--hidden");
                    Action done = _onSignedIn;
                    _onSignedIn = null;   // the continuation runs once, never on a later re-open
                    done?.Invoke();
                    return;
                }

                ToEmailStep(Explain(result, "That confirmation link did not work. Try again."));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                Debug.LogError($"[SignIn] completing from a deep link threw: {ex}");
                ToEmailStep("Something went wrong confirming your email. Try again.");
            }
        }

        /// <summary>Pull the <c>code</c> query parameter out of the game's deep link
        /// (<c>emberfall://auth?code=...</c>). Hand-rolled rather than <c>HttpUtility</c>, which is not
        /// guaranteed present under every IL2CPP linker profile.</summary>
        private static bool TryExtractCode(string url, out string code)
        {
            code = "";
            if (string.IsNullOrEmpty(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return false;

            string query = uri.Query.TrimStart('?');
            if (query.Length == 0) return false;

            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                string key = Uri.UnescapeDataString(pair.Substring(0, eq));
                if (key != "code") continue;
                code = Uri.UnescapeDataString(pair.Substring(eq + 1));
                return code.Length > 0;
            }
            return false;
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

        /// <summary>Disable every control for the length of a call.</summary>
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
        }

        private bool CanSubmit() => _step == Step.Email;

        private void RefreshPrimary()
        {
            RefreshPrimaryText();
            _primary?.SetEnabled(!_busy && CanSubmit());
        }

        private void RefreshPrimaryText()
        {
            if (_primary == null) return;
            _primary.text = "Email me a link";
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

        // ── Retired: the eight-digit typed code (NOR-231), superseded 2026-08-08 by the magic link
        // above but kept rather than deleted — Sam: "eight digits is a little unwieldy... let's not
        // delete the code that's in existence." AuthTokenProvider.BeginEmailCodeAsync/
        // VerifyEmailCodeAsync and SupabaseAuthBackend's matching methods are untouched; only this
        // overlay's wiring to them was removed. Reviving this means: restore the "signin-code"
        // TextField in MainMenuScreen.uxml (max-length="8" — EmailCode.Length, NOT 6, that was a live
        // bug fixed 2026-08-08), add back a Step.Code case, and wire OnPrimary/Verify to
        // BeginEmailCodeAsync/VerifyEmailCodeAsync the way this file did before this change.
    }
}
