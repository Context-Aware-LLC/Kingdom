using System;
using System.Threading.Tasks;
using Kingdom.Net.Client.Auth;
using Kingdom.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The first-launch registration overlay (EF-074e / NOR-231). An email address, then the
    /// six-digit code that arrives in the reply — typed by hand, or auto-filled by tapping the
    /// email's button — and no way past it.
    ///
    /// <para><b>Sam's ruling, 2026-08-08: registration happens on first load and cannot be skipped.</b>
    /// There are no guest accounts and there never will be. So this overlay has no Cancel, no Skip and
    /// no "continue as guest" — not a disabled one, none at all, because a dead button invites
    /// clicking. The only way out is a session.</para>
    ///
    /// <para><b>The code stays primary; the button is a shortcut for the SAME code, not a second auth
    /// flow (Sam, 2026-08-08).</b> A magic-link build (NOR-246) briefly replaced the code entirely —
    /// see commit <c>acd12c53</c>, kept in history rather than pasted back in — but it threw a
    /// client-side exception sending the link that was never root-caused, deep in Supabase's PKCE
    /// exchange (<see cref="AuthTokenProvider.BeginMagicLinkAsync"/>, <c>Pkce</c>). This overlay does
    /// not use that path and never has since the revert. Instead the deep link is a plain
    /// <c>emberfall://auth?code=...&amp;email=...</c> URL the EMAIL TEMPLATE constructs directly from
    /// Supabase's <c>{{ .Token }}</c> and <c>{{ .Email }}</c> template variables — no
    /// <c>redirect_to</c>, no PKCE, no Supabase-side exchange at all. Tapping it just hands this
    /// controller the same six digits a player could have typed, redeemed through the exact
    /// <see cref="AuthTokenProvider.VerifyEmailCodeAsync"/> call the manual path already uses and that
    /// NOR-264 proved end to end. The undiagnosed magic-link exception cannot recur here because none
    /// of its code runs.</para>
    ///
    /// <para><b>The mails no longer carry that link (Sam, 2026-11-03).</b> Mail clients strip a
    /// custom scheme, so the link did nothing for most players, and the signup template carried ONLY
    /// the link — a new player got no code at all. Both templates now show the code alone, and
    /// <see cref="FillCodeFromClipboard"/> takes it off the clipboard when the player comes back. The
    /// handler below still accepts the link if one arrives. The Android half: <c>PlayerSettings.Android.useCustomMainManifest</c> is
    /// on and <c>Assets/Plugins/Android/AndroidManifest.xml</c> declares the <c>emberfall://auth</c>
    /// intent-filter on the launcher activity, base-copied from Unity's own last generated
    /// <c>unityLibrary</c> manifest so every Unity-owned attribute (activity class, IL2CPP lib name,
    /// theme) matches what Unity would have produced itself. <b>Still cannot be exercised in the
    /// Editor</b> — Play mode is never launched by the OS off a URL, so an actual Android build is the
    /// only way to prove the Gradle manifest merge holds, however clean the config looks.</para>
    ///
    /// <para><b>The gate only closes when there is a project to sign into.</b>
    /// <see cref="IsRequired"/> is false on a build with no <c>SupabaseAuthSettings</c> asset, because
    /// gating a player behind a sign-in that cannot succeed is a locked door with no key — the game
    /// would simply not start. It arms itself the moment that asset lands, with no code change.</para>
    ///
    /// <para><b>THAT TRIGGER FIRED, and the answer was no (2026-08-30, #329).</b> The asset is
    /// committed — <c>Assets/Resources/SupabaseAuthSettings.asset</c> carries a real project URL and
    /// publishable key — so <see cref="IsRequired"/> now returns true on any device without a
    /// session, and the boot did NOT behave the way anyone wanted. It was not the unconfigured
    /// branch that bit: it was the one below it. The view lookup had been commented out in the
    /// 2026-08-25 move to a scene, so <c>_modal</c> was permanently null, <see cref="Open"/> took
    /// its "view is missing" escape hatch on every launch, and the unskippable gate skipped itself
    /// every single time — invisibly, because that path logs nothing and looks like success.
    ///
    /// The visible symptom was not a missing sign-in screen. It was an infinite MainMenu →
    /// AccountCreate bounce at four cycles a second, which only appeared once a player had a display
    /// name, because until then the name step was what stopped the hand-back. <b>An escape hatch
    /// that continues past a gate must also stop the gate reporting itself as outstanding</b>, or
    /// the caller that routed on it routes for ever; that is what <see cref="Skipped"/> is.</para>
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
            /// <summary>New here, or been here before? The first thing a session-less launch sees.
            /// Not a fourth way to authenticate — both branches end at the same Supabase identity;
            /// see <see cref="ToChoiceStep"/> for what it actually decides.</summary>
            Choose,

            /// <summary>Asking for an address.</summary>
            Email,

            /// <summary>The mail is away; asking for the code that is in it.</summary>
            Code,

            /// <summary>A returning player, asked for the address and the password they set when they
            /// made the account. Always offers the mailed code as a way out, because the password was
            /// optional and half the accounts in existence have none.</summary>
            Password,
        }

        private readonly VisualElement _modal;
        private readonly Label _title;
        private readonly Label _help;
        private readonly TextField _emailField;
        private readonly TextField _passwordField;
        private readonly TextField _codeField;
        private readonly Label _error;
        private readonly Button _primary;
        private readonly Button _back;
        private readonly Button _otherEmail;
        private readonly Button _resend;
        private readonly Button _googleButton;
        private readonly Button _codeInstead;
        private readonly VisualElement _or;
        private readonly VisualElement _choice;
        private readonly Button _newButton;
        private readonly Button _returningButton;

        private readonly AuthTokenProvider _auth;

        private Step _step = Step.Choose;
        private bool _busy;
        private Action _onSignedIn;

        /// <summary>True between opening the Google consent page and the redirect coming back.
        /// <see cref="OnFocusChanged"/> is what ends it when the redirect never arrives.</summary>
        private bool _awaitingOAuth;

        /// <summary>How long after regaining focus to wait for the OAuth redirect before deciding the
        /// player cancelled. Long enough that a successful sign-in never shows "cancelled" — the deep
        /// link and the focus change race with no ordering guarantee — and short enough that a real
        /// cancel does not feel like a hang.</summary>
        private const long OAuthReturnGraceMs = 1500;

        /// <summary>
        /// True once the player said they had been here before.
        ///
        /// <para><b>It steers one bit of authentication, and otherwise only presentation
        /// (revised 2026-09-01).</b> The one bit is <c>create_user</c>: this branch asks Supabase NOT
        /// to mint an account for an address it does not know, so a mistyped email is refused instead
        /// of silently becoming a second, empty account. Everything else about the call is identical,
        /// and the server still cannot tell a returning player from a new one — nor should it, since
        /// "new" is a claim the client makes about a person and
        /// the only fact that exists is which identity the token carries. What it buys is that the
        /// wording, the credential asked for, and the display-name step all match what the player just
        /// told us about themselves.</para>
        /// </summary>
        private bool _returning;

        /// <summary>
        /// True when the player said they had been here before — for the screen that runs after this
        /// one.
        ///
        /// <para><b>It exists for the one case this whole change cannot fix: a returning player whose
        /// account predates names being stored against the identity.</b> They sign in, nothing comes
        /// back in <c>user_metadata</c>, and the name step fires — telling them, for the second time,
        /// that the name they are about to choose is permanent. That sentence was already the
        /// documented lie (2026-08-31); saying it to somebody who has just told us they are returning
        /// is the worst version of it. <c>AccountCreateController</c> reads this to say something
        /// true instead.</para>
        ///
        /// <para>Instance state and not static, unlike <see cref="Skipped"/>: the screen that asks and
        /// the screen that reads it are the same scene and the same controller, so there is nothing
        /// here to outlive.</para>
        /// </summary>
        public bool PlayerSaidTheyAreReturning => _returning;

        /// <summary>The address the code was actually mailed to. Held rather than re-read from the
        /// field, because Supabase keys the code to the address — a player who edits the field after
        /// asking for a code would otherwise get a refusal that reads like a wrong code.</summary>
        private string _sentTo = "";

        public SignInGateController(VisualElement root, AuthTokenProvider auth)
        {
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));

            // THE GATE'S OWN VIEW. `signin-view`, not `signin-modal`: first run became a SCENE on
            // 2026-08-25 and the .mm-modal overlay was replaced by two sibling views. This lookup
            // was commented out in that move rather than renamed, which left this field readonly,
            // permanently null, and every Open() falling into the "view is missing" escape hatch
            // below -- so the unskippable gate silently skipped itself on every launch. #329.
            _modal = root.Q<VisualElement>("signin-view");
            _title = root.Q<Label>("signin-title");
            _help = root.Q<Label>("signin-help");
            _emailField = root.Q<TextField>("signin-email");
            _passwordField = root.Q<TextField>("signin-password");
            _codeField = root.Q<TextField>("signin-code");
            _error = root.Q<Label>("signin-error");
            _primary = root.Q<Button>("btn-signin-primary");
            _back = root.Q<Button>("btn-signin-back");
            _otherEmail = root.Q<Button>("btn-signin-other-email");
            _resend = root.Q<Button>("btn-signin-resend");
            _codeInstead = root.Q<Button>("btn-signin-code-instead");
            _googleButton = root.Q<Button>("btn-signin-google");
            _or = root.Q<VisualElement>("signin-or");
            _choice = root.Q<VisualElement>("signin-choice");
            _newButton = root.Q<Button>("btn-signin-new");
            _returningButton = root.Q<Button>("btn-signin-returning");

            // NOTHING IS LOGGED HERE, and that is deliberate. MainMenu builds one of these against
            // its OWN root purely to read IsRequired, and that root correctly has no sign-in view --
            // first run is the AccountCreate scene. A constructor that complains about a missing
            // view therefore fires on every single launch, from the one caller for which it is not
            // a problem. Measured on device 2026-08-30: it did exactly that, and said "this launch
            // will proceed unsigned", which was false. The place a missing view is real is Open(),
            // which is the only thing that needs one; it reports there.
            //
            // Not returning early either: every lookup above is null-safe and every handler below is
            // registered with `?.`, so a partially-authored screen still wires up whatever it has.
            _primary?.RegisterCallback<ClickEvent>(_ => OnPrimary());
            // Back returns to the fork, not to the email field. A player who took the wrong branch
            // has no other way to correct it, and on the email step the field is right there to
            // retype — which is why "Use another email" exists on the code step only, where the
            // field is hidden. It lands on the email step with the sent-to address in the field.
            _back?.RegisterCallback<ClickEvent>(_ => ToChoiceStep(""));
            _otherEmail?.RegisterCallback<ClickEvent>(_ =>
            {
                _emailField?.SetValueWithoutNotify(_sentTo);
                ToEmailStep(_returning, "");
            });
            _resend?.RegisterCallback<ClickEvent>(_ => SendCode(_sentTo));
            _codeInstead?.RegisterCallback<ClickEvent>(_ => SendCode(_emailField?.value ?? ""));
            _googleButton?.RegisterCallback<ClickEvent>(_ => OnGoogleSignIn());
            _newButton?.RegisterCallback<ClickEvent>(_ => ToEmailStep(returning: false, ""));
            _returningButton?.RegisterCallback<ClickEvent>(_ => ToEmailStep(returning: true, ""));

            if (_emailField != null)
            {
                _emailField.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (IsSubmit(evt)) OnPrimary();
                });
                _emailField.RegisterValueChangedCallback(_ => SetError(""));
            }

            if (_passwordField != null)
            {
                _passwordField.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (IsSubmit(evt)) OnPrimary();
                });
                _passwordField.RegisterValueChangedCallback(_ => SetError(""));
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

            // Android hands a tapped mail link back to a running instance through this (once the
            // scheme is actually registered — see the type remarks, dependency 2). A cold start (the
            // OS launched us fresh off the link) is caught separately in Open(), because this event
            // does not fire for the launch that is already in progress when it is subscribed.
            Application.deepLinkActivated += OnDeepLinkActivated;
#if UNITY_EDITOR
            EditorDeepLink += OnDeepLinkActivated;
#endif

            // And the other half of the same story: the player coming BACK from the browser without
            // having completed anything. See OnFocusChanged.
            Application.focusChanged += OnFocusChanged;
        }

        /// <summary>Drop the static subscriptions. Must be called when the owning screen is
        /// disabled/destroyed — <see cref="Application.deepLinkActivated"/> and
        /// <see cref="Application.focusChanged"/> are static events, so an instance that never
        /// unsubscribes outlives its modal and keeps handling links for a UI that is gone. Scene
        /// reloads (<see cref="DeviceAccounts.SwitchTo"/>) make a fresh instance on every re-enable,
        /// so this is not optional.</summary>
        public void Close()
        {
            Application.deepLinkActivated -= OnDeepLinkActivated;
#if UNITY_EDITOR
            EditorDeepLink -= OnDeepLinkActivated;
#endif
            Application.focusChanged -= OnFocusChanged;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Stands in for <see cref="Application.deepLinkActivated"/> in the Editor, where nothing ever
        /// raises it: Windows has no handler registered for <c>emberfall://</c>, so the browser stops
        /// at "the scheme does not have a registered handler" and the Google redirect never reaches
        /// the game. <c>Kingdom/Dev/Account/Deliver Deep Link From Clipboard</c> feeds the copied URL in
        /// here, and it takes the same path a device link takes. Editor-only; a player build compiles
        /// none of it.
        /// </summary>
        private static event Action<string> EditorDeepLink;

        /// <summary>Hand <paramref name="url"/> to every open gate as if the OS had delivered it.
        /// Returns false when no gate is listening, meaning the sign-in screen is not up.</summary>
        public static bool EditorDeliverDeepLink(string url)
        {
            Action<string> handlers = EditorDeepLink;
            if (handlers == null) return false;
            handlers(url);
            return true;
        }
#endif

        /// <summary>
        /// The player came back from the browser. If they did not bring a code with them, give them
        /// the screen back.
        ///
        /// <para><b>Cancelling Google sign-in used to brick the launch.</b>
        /// <see cref="OnGoogleSignIn"/> sets the busy state and deliberately leaves it on while the
        /// player is away — re-enabling the UI mid-flight only invites a second tap. Nothing cleared
        /// it if they swiped back out of the consent screen instead of finishing: the gate is
        /// unskippable, so the player returned to a greyed-out screen with no button that did
        /// anything and no way forward except force-quitting the app. Measured on a device
        /// 2026-09-02.</para>
        ///
        /// <para><b>The grace period is the whole difficulty.</b> On a successful sign-in the app
        /// ALSO regains focus — the redirect brings it to the foreground — and the deep link may
        /// arrive fractionally before or after that, with no ordering guarantee. Clearing
        /// immediately would flash "cancelled" over a sign-in that is about to succeed. So this
        /// waits, and only takes the busy state down if the code still has not arrived; the deep
        /// link clears <see cref="_awaitingOAuth"/> the moment it lands, which is what makes the
        /// late check a no-op on the happy path.</para>
        /// </summary>
        private void OnFocusChanged(bool focused)
        {
            if (focused) FillCodeFromClipboard();
            if (!focused || !_awaitingOAuth || _modal == null) return;

            _modal.schedule.Execute(() =>
            {
                if (!_awaitingOAuth) return;   // the redirect landed; nothing to recover

                _awaitingOAuth = false;
                SetBusy(false, null);
                SetError(Loc.Get("signin_google_cancelled"));
            }).ExecuteLater(OAuthReturnGraceMs);
        }

        /// <summary>The last code taken off the clipboard, so a stale one that was refused is not
        /// submitted again every time the player switches back.</summary>
        private string _clipboardTried = "";

        /// <summary>
        /// The player copied the code in their mail app and came back: fill it in and sign in.
        /// The mail cannot copy anything itself, so this is the half of "tap to copy" the game owns.
        /// Only a clipboard holding a code and little else counts — a copied sentence that happens
        /// to contain six digits is not the player handing us a code.
        /// </summary>
        private void FillCodeFromClipboard()
        {
            if (_step != Step.Code || _busy || _modal == null || _codeField == null) return;
            if (_codeField.value.Length > 0) return;

            string raw = (GUIUtility.systemCopyBuffer ?? "").Trim();
            if (raw.Length == 0 || raw.Length > EmailCode.Length + 4) return;
            int digits = 0;
            foreach (char c in raw) if (c >= '0' && c <= '9') digits++;
            if (digits != EmailCode.Length) return;

            string code = EmailCode.Normalize(raw);
            if (code == _clipboardTried) return;
            _clipboardTried = code;
            _codeField.SetValueWithoutNotify(code);
            RefreshPrimary();
            Verify();
        }

        /// <summary>
        /// True when this launch must not reach the menu until the player signs in.
        ///
        /// <para>False on a build with no Supabase project — see the type remarks for why that is the
        /// only safe answer and not a hole in the ruling.</para>
        /// </summary>
        /// <summary>
        /// A gate that gave up and let the launch through unsigned, for THIS PROCESS.
        ///
        /// <para>Static and one-way on purpose. It is not a preference and it is not persisted: it
        /// records that some screen could not present the gate on this run, and it must outlive the
        /// controller that discovered it because the component that has to act on it — MainMenu — is
        /// a different instance in a different scene, rebuilt on every load.</para>
        ///
        /// <para>Without it, "the gate is required" and "the gate can be presented" are independent
        /// facts that no single component can reconcile, which is exactly the shape of #329: MainMenu
        /// forever dispatching to a screen that forever hands back. Anything that skips the gate
        /// must set this in the same breath.</para>
        /// </summary>
        public static bool Skipped { get; private set; }

        /// <summary>True when this build has a project to sign into and no session yet — AND the gate
        /// has not already given up this run. See <see cref="Skipped"/>: a required gate that cannot
        /// be presented must stop reporting itself as outstanding, or every caller that routes on
        /// this property routes in a circle.</summary>
        public bool IsRequired => _auth.IsConfigured && !_auth.IsSignedIn && !Skipped;

        /// <summary>
        /// Show the gate. <paramref name="onSignedIn"/> runs once, on the main thread, after a session
        /// is stored — that is the continuation the menu's own first-run work hangs off.
        /// </summary>
        public void Open(Action onSignedIn)
        {
            _onSignedIn = onSignedIn;

            if (_modal == null)
            {
                // The view is missing from the UXML. Refusing to continue would brick the boot over
                // a broken screen, which is a worse outcome than an unsigned session — one that still
                // cannot queue, because the connect refuses to start without a sign-in token — and the
                // error in the constructor already says what happened.
                //
                // CONTINUING IS NOT ENOUGH ON ITS OWN, and that is what made this a boot loop rather
                // than a degraded launch (#329). MainMenu sends anyone whose gate is outstanding to
                // AccountCreate; if the gate then skips itself and hands the player straight back,
                // MainMenu sends them again, four times a second, for ever. Latch it: the gate is
                // unavailable for this process, so the answer to "is a gate outstanding" has to stop
                // being yes, or nothing downstream can break the cycle.
                Debug.LogError("[SignIn] signin-view is not in the UXML of the screen that opened " +
                               "the gate, so it cannot be shown and this launch proceeds unsigned. " +
                               "See #329.");
                Skipped = true;
                FirstSessionFunnel.SignInSkipped("no_view");
                onSignedIn?.Invoke();
                return;
            }

            if (!_auth.IsConfigured)
            {
                Debug.LogWarning("[SignIn] no Supabase project is configured, so first-launch " +
                                 "registration cannot run. Create Assets/Resources/SupabaseAuthSettings.asset " +
                                 "to arm it.");
                FirstSessionFunnel.SignInSkipped("not_configured");
                onSignedIn?.Invoke();
                return;
            }

            // A cold start: the OS may have launched this process directly off the mailed or OAuth link,
            // which means deepLinkActivated already fired (or never will, for this launch) before
            // anything here could have subscribed to it. Application.absoluteURL carries it on mobile;
            // on desktop standalone the OS passes the URL as a command-line argument instead.
            string coldUrl = Application.absoluteURL;
#if UNITY_STANDALONE && !UNITY_EDITOR
            if (string.IsNullOrEmpty(coldUrl))
            {
                foreach (string arg in System.Environment.GetCommandLineArgs())
                {
                    if (arg.StartsWith("emberfall://", System.StringComparison.OrdinalIgnoreCase))
                    {
                        coldUrl = arg;
                        break;
                    }
                }
            }
#endif
            if (TryExtractCodeAndEmail(coldUrl, out string coldEmail, out string coldCode))
            {
                _modal.RemoveFromClassList("mm-modal--hidden");
                CompleteFromDeepLink(coldEmail, coldCode);
                return;
            }
            if (TryExtractOAuthCode(coldUrl, out string coldOAuthCode))
            {
                _modal.RemoveFromClassList("mm-modal--hidden");
                CompleteFromOAuth(coldOAuthCode);
                return;
            }

            ToChoiceStep("");
            _modal.RemoveFromClassList("mm-modal--hidden");
            if (PlayGamesServices.IsSupported && !DeviceAccounts.HasClaimedAccount) RestoreFromPlayGames();
        }

        /// <summary>
        /// Seamless restore (#425): a new phone signed in to Play Games finds the account that
        /// profile is linked to, and the form never shows — only a status line, as the restore
        /// after any sign-in does. Anything short of a session gives the form back as it was.
        /// </summary>
        private async void RestoreFromPlayGames()
        {
            SetBusy(true, null);
            ShowRestoring(Loc.Get("signin_restoring_account"));

            bool restored = false;
            try { restored = await PlayGamesServices.TryRestoreAsync(_auth); }
            catch (Exception ex) { Debug.LogWarning($"[SignIn] Play Games restore threw: {ex}"); }

            if (restored)
            {
                // A restored account has been here before, whatever the form would have asked.
                _returning = true;
                Succeed("play_games");
                return;
            }
            SetBusy(false, null);
            ShowRestoring(null);
            ToChoiceStep("");
        }

        // ── Steps ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The fork: new here, or been here before.
        ///
        /// <para><b>Asking is the only way to know.</b> A device with no session cannot tell a first
        /// install from a reinstall — the uninstall took <c>PlayerPrefs</c>, the device token and the
        /// whole collection with it, so every launch looks identical from here. Before this step the
        /// answer was assumed to be "new", which is why a returning player was walked through account
        /// creation and handed a fresh empty scope while the account they already owned sat on the
        /// server, intact and unreachable.</para>
        /// </summary>
        private void ToChoiceStep(string message)
        {
            _step = Step.Choose;
            _returning = false;
            _sentTo = "";

            if (_title != null) _title.text = Loc.Get("account_create_signin_title");
            if (_help != null) _help.text = Loc.Get("signin_choose_help");

            Display(_choice, true);
            Display(_emailField, false);
            Display(_passwordField, false);
            Display(_codeField, false);
            Display(_primary, false);
            Display(_back, false);
            Display(_otherEmail, false);
            Display(_resend, false);
            Display(_codeInstead, false);
            Display(_googleButton, true);
            Display(_or, true);
            _passwordField?.SetValueWithoutNotify("");
            _codeField?.SetValueWithoutNotify("");

            SetError(message);
        }

        private void ToEmailStep(bool returning, string message)
        {
            _step = returning ? Step.Password : Step.Email;
            _returning = returning;
            _sentTo = "";

            if (_title != null) _title.text = Loc.Get(returning ? "signin_returning_title" : "signin_new_title");
            if (_help != null)
                _help.text = Loc.Get(returning ? "signin_returning_help" : "account_create_signin_help");

            Display(_choice, false);
            Display(_emailField, true);
            Display(_passwordField, returning);
            Display(_codeField, false);
            Display(_primary, true);
            Display(_back, true);
            Display(_otherEmail, false);
            Display(_resend, false);
            Display(_codeInstead, returning);
            Display(_googleButton, true);
            Display(_or, true);
            _passwordField?.SetValueWithoutNotify("");
            _codeField?.SetValueWithoutNotify("");

            SetError(message);
            RefreshPrimary();
            _emailField?.Focus();
        }

        private void ToCodeStep(string email)
        {
            _step = Step.Code;
            _sentTo = email;

            if (_title != null) _title.text = Loc.Get("signin_code_title");
            if (_help != null) _help.text = Loc.Format("signin_code_help", email);

            Display(_choice, false);
            Display(_emailField, false);
            Display(_passwordField, false);
            Display(_codeField, true);
            Display(_primary, true);
            Display(_back, true);
            Display(_otherEmail, true);
            Display(_resend, true);
            Display(_codeInstead, false);
            Display(_googleButton, false);
            Display(_or, false);
            _codeField?.SetValueWithoutNotify("");

            SetError("");
            RefreshPrimary();
            _codeField?.Focus();
        }

        private void OnPrimary()
        {
            if (_busy) return;
            switch (_step)
            {
                case Step.Choose: return;                                   // the two buttons are the input
                case Step.Email: SendCode(_emailField?.value ?? ""); return;
                case Step.Password: SignInWithPassword(); return;
                default: Verify(); return;
            }
        }

        /// <summary>
        /// Everything a successful sign-in has to do, on every path that can produce one.
        ///
        /// <para><b>It is one method because the binding below must not be possible to forget.</b>
        /// There are four ways into a session here — a typed code, a tapped link on a cold start, a
        /// tapped link on a running process, and Google — and each one used to hide the modal and call
        /// the continuation itself. A fifth (the password) has just been added. A path that skipped
        /// <see cref="DeviceAccounts.TryBindToIdentity"/> would sign the player in and then hand them
        /// somebody else's scope, or a blank one, with nothing logged and nothing to catch it.</para>
        /// </summary>
        private async void Succeed(string cause)
        {
            // The funnel's sign_in row, with the path that produced it. First, so a restore that
            // never finishes below still counts the sign-in that did happen.
            FirstSessionFunnel.SignedIn(cause);

            // Bind BEFORE anything else: both the sync below and AccountCreateController's first act
            // read PlayerAccount.Local, and both have to land in the account this identity owns.
            DeviceAccounts.TryBindToIdentity(_auth.Current.UserId);

            Action done = _onSignedIn;
            _onSignedIn = null;   // the continuation runs once, never on a later re-open

            // Then pull the player's data down, still behind the modal. This is the half that makes a
            // reinstall recoverable: binding finds the folder, these are what put anything back in it.
            // Neither can fail the sign-in — both gateways swallow every outcome into a status and
            // leave the local files alone on all of them but one — so there is no branch here.
            //
            // The collection first and the roster second, because a deck restored before the cards it
            // names would render as a draft for as long as the second call takes.
            // The token comes from TokenForConnectAsync, not from the session just stored. Here it is
            // seconds old and the refresh is a no-op, so this buys nothing TODAY — it buys that there
            // is ONE way to get a token for a request and no second one to copy. The boot path copied
            // the other one and 401'd on every launch an hour after sign-in; see
            // MainMenuController.ResumeThenFinishBootstrap.
            string accessToken = await AuthGateway.TokenForConnectAsync();
            SetBusy(true, null);
            ShowRestoring(Loc.Get("signin_restoring_collection"));
            await CollectionGateway.SyncAsync(accessToken);
            ShowRestoring(Loc.Get("signin_restoring_decks"));
            await RosterGateway.SyncAsync(accessToken);
            SetBusy(false, null);

            _modal?.AddToClassList("mm-modal--hidden");
            // The corner Back lives on the root, not in the view, so it does not hide with it.
            Display(_back, false);
            ShowRestoring(null);
            done?.Invoke();
        }

        /// <summary>
        /// The restore is a wait, not a form, so it shows only its status line where the title
        /// was (Sam, 2026-10-15): the dimmed fields and buttons it used to leave on screen read as
        /// a form that had frozen. Null puts the form's lines back; the step methods reset the
        /// title text themselves.
        /// </summary>
        private void ShowRestoring(string label)
        {
            if (_modal == null) return;
            VisualElement header = _title?.parent;
            foreach (VisualElement line in _modal.Children())
                Display(line, label == null || line == header);
            Display(_help, label == null);
            // A wait offers no way back. Null leaves it to the step method that always follows.
            if (label != null) Display(_back, false);
            if (label != null && _title != null) _title.text = label;
        }

        // ── Calls ───────────────────────────────────────────────────────────────────────

        // async void is correct for a UI handler and only here: UnityWebRequestAuthHttp resumes its
        // Task on the main thread, so everything after the await is free to touch elements. The
        // try/catch is what keeps async void honest — an escaped exception would otherwise be a
        // crash rather than a message on the panel.
        private async void SendCode(string typedEmail)
        {
            string email = (typedEmail ?? "").Trim();
            if (email.Length == 0) { SetError(Loc.Get("signin_error_email_required")); return; }
            if (!LooksLikeAnEmail(email)) { SetError(Loc.Get("signin_error_email_invalid")); return; }

            SetBusy(true, Loc.Get("signin_busy_sending"));
            try
            {
                // createUser: false on the returning branch is the whole guard. With it true, a
                // mistyped address is not an error — Supabase mints an account for the typo, mails it a
                // working code, and the player signs in successfully to an empty collection while their
                // real one sits under the address they meant. Every failure this branch can now produce
                // is louder than that one.
                AuthResult result = await _auth.BeginEmailCodeAsync(email, createUser: !_returning);
                SetBusy(false, null);

                if (result.Ok) { ToCodeStep(email); return; }

                // Staying on the code step for a failed RESEND matters: the player still holds a
                // working code from the first mail, and dropping them back to the email field would
                // read as having lost it.
                Debug.LogWarning($"[SignIn] BeginEmailCodeAsync failed: {result.Failure} — {result.Detail}");
                FirstSessionFunnel.SignInFailed("send_code:" + result.Failure);
                SetError(Explain(result, Loc.Get("signin_error_send_code")));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                Debug.LogError($"[SignIn] sending a code threw: {ex}");
                FirstSessionFunnel.SignInFailed("send_code:exception");
                SetError(Loc.Get("signin_error_send_code_generic"));
            }
        }

        /// <summary>
        /// The returning player's path: an email and the password they set when they made the account.
        ///
        /// <para><b>A refusal here is never sharpened.</b> Supabase answers a wrong password and an
        /// address it has never seen with the same "Invalid login credentials", and that is rendered
        /// verbatim — telling the two apart would make this form an account-enumeration oracle. The
        /// help text carries the useful half instead: the password was always optional, so "no
        /// password" is a legitimate state and the code route stays on screen throughout.</para>
        /// </summary>
        private async void SignInWithPassword()
        {
            string email = (_emailField?.value ?? "").Trim();
            string password = _passwordField?.value ?? "";

            if (email.Length == 0) { SetError(Loc.Get("signin_error_email_required")); return; }
            if (!LooksLikeAnEmail(email)) { SetError(Loc.Get("signin_error_email_invalid")); return; }
            if (password.Length == 0)
            {
                SetError(Loc.Get("signin_error_password_required"));
                return;
            }

            SetBusy(true, Loc.Get("signin_busy_signing_in"));
            try
            {
                AuthResult result = await _auth.SignInWithPasswordAsync(email, password);
                SetBusy(false, null);

                if (result.Ok) { Succeed("password"); return; }

                // The email survives; only the password is cleared. Retyping an address you got right
                // is the most annoying possible response to a mistyped password.
                _passwordField?.SetValueWithoutNotify("");
                RefreshPrimary();
                _passwordField?.Focus();
                Debug.LogWarning($"[SignIn] SignInWithPasswordAsync failed: {result.Failure} — {result.Detail}");
                FirstSessionFunnel.SignInFailed("password:" + result.Failure);
                SetError(Explain(result, Loc.Get("signin_error_password_rejected")));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                Debug.LogError($"[SignIn] signing in with a password threw: {ex}");
                FirstSessionFunnel.SignInFailed("password:exception");
                SetError(Loc.Get("signin_error_generic"));
            }
        }

        private async void Verify()
        {
            string code = EmailCode.Normalize(_codeField?.value ?? "");
            if (code.Length != EmailCode.Length) { SetError(Loc.Get("signin_error_code_required")); return; }

            SetBusy(true, Loc.Get("signin_busy_signing_in"));
            try
            {
                AuthResult result = await _auth.VerifyEmailCodeAsync(_sentTo, code);
                SetBusy(false, null);

                if (result.Ok)
                {
                    Succeed("email_code");
                    return;
                }

                // The mailed code survives a wrong attempt (AuthTokenProvider.VerifyEmailCodeAsync),
                // so clear the field and leave the player where they are rather than sending them
                // back to their inbox.
                _codeField?.SetValueWithoutNotify("");
                RefreshPrimary();
                _codeField?.Focus();
                Debug.LogWarning($"[SignIn] VerifyEmailCodeAsync failed: {result.Failure} — {result.Detail}");
                FirstSessionFunnel.SignInFailed("email_code:" + result.Failure);
                SetError(Explain(result, Loc.Get("signin_error_code_rejected")));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                Debug.LogError($"[SignIn] verifying a code threw: {ex}");
                FirstSessionFunnel.SignInFailed("email_code:exception");
                SetError(Loc.Get("signin_error_generic"));
            }
        }

        /// <summary>Handles a tapped link while this process was already running (backgrounded, then
        /// resumed via a new intent). Silently ignored when already signed in, or when the URL is not
        /// one of ours — a stale or foreign link must not disrupt whatever the player is doing.</summary>
        private void OnDeepLinkActivated(string url)
        {
            if (_modal == null) return;
            if (_auth.IsSignedIn) return;

            if (TryExtractCodeAndEmail(url, out string email, out string code))
            {
                _modal.RemoveFromClassList("mm-modal--hidden");
                CompleteFromDeepLink(email, code);
                return;
            }

            if (TryExtractOAuthCode(url, out string oauthCode))
            {
                _modal.RemoveFromClassList("mm-modal--hidden");
                CompleteFromOAuth(oauthCode);
            }
        }

        /// <summary>
        /// Redeem the code a tapped email link carried, through the exact same
        /// <see cref="AuthTokenProvider.VerifyEmailCodeAsync"/> call the manual code step uses — see
        /// the type remarks for why that, and not a Supabase-side exchange, is deliberate.
        /// </summary>
        private async void CompleteFromDeepLink(string email, string code)
        {
            ToCodeStep(email);
            _codeField?.SetValueWithoutNotify(code);
            RefreshPrimary();

            SetBusy(true, Loc.Get("signin_busy_signing_in"));
            try
            {
                AuthResult result = await _auth.VerifyEmailCodeAsync(email, code);
                SetBusy(false, null);

                if (result.Ok)
                {
                    Succeed("email_link");
                    return;
                }

                // Leave the player on the code step with the mailed code still visible in their inbox
                // — a stale or already-used link should not force a fresh Send.
                RefreshPrimary();
                Debug.LogWarning($"[SignIn] VerifyEmailCodeAsync (deep link) failed: {result.Failure} — {result.Detail}");
                FirstSessionFunnel.SignInFailed("email_link:" + result.Failure);
                SetError(Explain(result, Loc.Get("signin_error_link_code_rejected")));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                Debug.LogError($"[SignIn] completing from a link threw: {ex}");
                FirstSessionFunnel.SignInFailed("email_link:exception");
                SetError(Loc.Get("signin_error_generic"));
            }
        }

        /// <summary>
        /// Google sign-in. On Android it is the system's own Google sheet (<see cref="NativeGoogleSignIn"/>);
        /// everywhere else, and on Android whenever the sheet cannot finish, it is the browser.
        /// </summary>
        private void OnGoogleSignIn()
        {
            if (_busy) return;
            SetBusy(true, null);
            SetError("");

            if (NativeGoogleSignIn.IsSupported && AuthGateway.GoogleNativeClientId.Length > 0)
            {
                SignInWithNativeGoogle();
                return;
            }
            StartBrowserGoogleSignIn();
        }

        /// <summary>
        /// The sheet, then Supabase. <b>Anything but the player closing the sheet falls back to the
        /// browser</b>, so a phone without Google Play services, or a Google Cloud or Supabase set-up
        /// that does not yet accept this build's token, still signs in the way it always has.
        /// </summary>
        private async void SignInWithNativeGoogle()
        {
            try
            {
                NativeGoogleSignIn.Result native =
                    await NativeGoogleSignIn.SignInAsync(AuthGateway.GoogleNativeClientId);

                if (native.Outcome == NativeGoogleSignIn.Outcome.Cancelled)
                {
                    SetBusy(false, null);
                    return;
                }

                if (native.Outcome == NativeGoogleSignIn.Outcome.SignedIn)
                {
                    SetBusy(true, Loc.Get("signin_busy_google"));
                    AuthResult result = await _auth.SignInWithIdTokenAsync("google", native.IdToken, native.RawNonce);
                    if (result.Ok)
                    {
                        SetBusy(false, null);
                        Succeed("google_native");
                        return;
                    }
                    Debug.LogWarning($"[SignIn] Supabase refused the native Google token " +
                                     $"({result.Failure}: {result.Detail}); trying the browser.");
                }
                else
                {
                    Debug.LogWarning($"[SignIn] native Google sign-in unavailable ({native.Detail}); trying the browser.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SignIn] native Google sign-in threw ({ex.GetType().Name}: {ex.Message}); " +
                                 "trying the browser.");
            }

            StartBrowserGoogleSignIn();
        }

        /// <summary>
        /// Open the Google consent page in the platform browser and store the PKCE verifier. The
        /// redirect lands back via the <c>emberfall://auth</c> scheme, which <see cref="OnDeepLinkActivated"/>
        /// and <see cref="Open"/> both watch for, so no further action is needed here beyond kicking off
        /// the browser. Expects the busy state already set.
        /// </summary>
        private void StartBrowserGoogleSignIn()
        {
            PendingSignIn pending = _auth.BeginOAuth("google");
            if (!pending.IsStarted)
            {
                SetBusy(false, null);
                FirstSessionFunnel.SignInFailed("google_start");
                SetError(Loc.Get("signin_error_google_start"));
                return;
            }

            // Armed BEFORE the browser opens, because the app can lose and regain focus faster than
            // anything after OpenURL would run.
            _awaitingOAuth = true;
            Application.OpenURL(pending.AuthorizeUrl);
            // The app will receive the redirect asynchronously. SetBusy stays on — the player is now
            // in the browser; re-enabling the UI while they are away only invites a second tap. If
            // they come back WITHOUT a code, OnFocusChanged is what gives the screen back.
        }

        /// <summary>
        /// Redeem the OAuth code the Google redirect carried via <see cref="AuthTokenProvider.CompleteSignInAsync"/>.
        /// Shows the modal in a waiting state; the player should not need to interact further.
        /// </summary>
        private async void CompleteFromOAuth(string oauthCode)
        {
            // The redirect arrived, so the return-from-browser recovery must not fire behind it.
            _awaitingOAuth = false;
            SetBusy(true, Loc.Get("signin_busy_google"));
            // Show the email step as the backing UI (email field is hidden while busy). Google is
            // offered on both branches and answers "new or returning" by itself — the identity either
            // exists already or it does not — so the flag it is given here is arbitrary and only
            // decides the heading behind a busy panel.
            if (_step != Step.Email) ToEmailStep(returning: false, "");
            _primary?.SetEnabled(false);

            try
            {
                AuthResult result = await _auth.CompleteSignInAsync(oauthCode);
                SetBusy(false, null);

                if (result.Ok)
                {
                    Succeed("google_oauth");
                    return;
                }

                Debug.LogWarning($"[SignIn] CompleteSignInAsync (Google OAuth) failed: {result.Failure} — {result.Detail}");
                FirstSessionFunnel.SignInFailed("google_oauth:" + result.Failure);
                SetError(Explain(result, Loc.Get("signin_error_google_incomplete")));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                Debug.LogError($"[SignIn] Google OAuth completion threw: {ex}");
                FirstSessionFunnel.SignInFailed("google_oauth:exception");
                SetError(Loc.Get("signin_error_google_generic"));
            }
        }

        /// <summary>
        /// Pull <c>code</c> out of an OAuth redirect (<c>emberfall://auth?code=...</c>). Unlike
        /// <see cref="TryExtractCodeAndEmail"/>, email is not required — OAuth redirects carry a code
        /// only and are distinguished by the absence of the <c>email</c> param.
        /// </summary>
        private static bool TryExtractOAuthCode(string url, out string code)
        {
            code = "";
            if (string.IsNullOrEmpty(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || !IsAuthLink(uri)) return false;

            string email = "";
            string query = uri.Query.TrimStart('?');
            if (query.Length == 0) return false;

            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                string key = Uri.UnescapeDataString(pair.Substring(0, eq));
                string value = Uri.UnescapeDataString(pair.Substring(eq + 1));
                if (key == "code") code = value;
                else if (key == "email") email = value;
            }

            // Only claim this as an OAuth redirect when we have a code and no email (the email flow
            // always includes both; returning false here lets TryExtractCodeAndEmail claim it instead).
            return code.Length > 0 && email.Length == 0;
        }

        /// <summary>Pull <c>code</c> and <c>email</c> out of the game's deep link
        /// (<c>emberfall://auth?code=...&amp;email=...</c>). Hand-rolled rather than
        /// <c>HttpUtility</c>, which is not guaranteed present under every IL2CPP linker profile.
        /// Both fields are required — a link missing either is treated as not ours.</summary>
        private static bool TryExtractCodeAndEmail(string url, out string email, out string code)
        {
            email = "";
            code = "";
            if (string.IsNullOrEmpty(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || !IsAuthLink(uri)) return false;

            string query = uri.Query.TrimStart('?');
            if (query.Length == 0) return false;

            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                string key = Uri.UnescapeDataString(pair.Substring(0, eq));
                string value = Uri.UnescapeDataString(pair.Substring(eq + 1));
                if (key == "code") code = value;
                else if (key == "email") email = value;
            }
            return code.Length > 0 && email.Length > 0;
        }

        /// <summary>
        /// <c>emberfall://auth</c>, the one address both sign-in links return to. The scheme has
        /// other hosts now (<c>emberfall://friend</c>, #424), and neither extractor may claim one of
        /// those: a <c>code</c> parameter on any other link would otherwise be redeemed as a
        /// sign-in.
        /// </summary>
        private static bool IsAuthLink(Uri uri)
            => string.Equals(uri.Scheme, "emberfall", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Host, "auth", StringComparison.OrdinalIgnoreCase);

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
                    return Loc.Get("signin_error_offline");
                case AuthFailure.NotConfigured:
                    return Loc.Get("signin_error_not_configured");
                case AuthFailure.NoSuchAccount:
                    // Supabase's own wording here is "Signups not allowed for otp", which is true and
                    // useless — it describes the request we made, not the thing the player got wrong.
                    return Loc.Get("signin_error_no_account");
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
            _otherEmail?.SetEnabled(!busy);
            _resend?.SetEnabled(!busy);
            _codeInstead?.SetEnabled(!busy);
            _googleButton?.SetEnabled(!busy);
            _newButton?.SetEnabled(!busy);
            _returningButton?.SetEnabled(!busy);
            _emailField?.SetEnabled(!busy);
            _passwordField?.SetEnabled(!busy);
            _codeField?.SetEnabled(!busy);
        }

        /// <summary>
        /// Whether the primary button does anything yet. Only the code step gates on content — a
        /// six-digit field either holds six digits or it does not, and a dead "Sign in" is clearer
        /// than one that refuses. Email and password are validated on submit instead, because a
        /// button that stays disabled cannot say WHY.
        /// </summary>
        private bool CanSubmit()
            => _step != Step.Code || EmailCode.IsComplete(_codeField?.value ?? "");

        private void RefreshPrimary()
        {
            RefreshPrimaryText();
            _primary?.SetEnabled(!_busy && CanSubmit());
        }

        private void RefreshPrimaryText()
        {
            if (_primary == null) return;
            _primary.text = Loc.Get(_step == Step.Email ? "account_create_btn_signin_primary" : "signin_sign_in_button");
            // Ember chrome only on the code step (Sam, 2026-09-28). While Google is on screen it is
            // the one ember button and everything else is slate; the code step hides Google, so
            // "Sign in" becomes the call to action there.
            _primary.EnableInClassList("signin-btn--ember", _step == Step.Code);
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
