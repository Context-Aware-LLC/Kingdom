using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Emberfall.Net.Client.Social;
using Emberfall.Net.Protocol;
using Emberfall.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The friends button and its right-side drawer (#409): the player's own code, add by code,
    /// requests to answer, friends, and requests still out. Built at runtime via Resources the way
    /// <see cref="MatchSettingsController"/> is, on the same PanelSettings, and modelled on its
    /// corner gear.
    ///
    /// <para><b>The server is the only copy.</b> Every call answers the whole graph and the lists
    /// are redrawn from that answer; nothing is patched locally and nothing is cached between
    /// opens. The game has no offline mode, so "could not find out" is said rather than papered
    /// over with a stale list.</para>
    ///
    /// <para><b>A friend's name is text another player typed</b> — see <see cref="NameLabel"/>.</para>
    ///
    /// <para><b>Presence comes from <see cref="SocialGateway"/></b>, which mounting starts and
    /// which outlives this drawer: the drawer is rebuilt per scene, the connection is not. A
    /// <c>friends_changed</c> push re-reads the list while the drawer is open.</para>
    ///
    /// <para>Not mounted in a match: UI Toolkit does not block Unity's physics input, and nothing
    /// here is wired into <c>InputRouter.UiBlocksInput</c>.</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class FriendsDrawerController : MonoBehaviour
    {
        private const string OpenClass = "fr-root--open";

        private static FriendsDrawerController _current;

        private VisualElement _root;
        private Label _ownCode;
        private Label _status;
        private TextField _addCode;
        private Button _addButton;
        private ScrollView _lists;

        private VisualElement _challenge;
        private Label _challengeText;
        private Button _challengeYes;
        private Button _challengeNo;
        private int _challengeSecondsShown = -1;

        private string _ownCodeDisplay = "";
        private bool _busy;
        private FriendsSnapshot _shown;

        /// <summary>Is there a drawer on this screen to show a challenge prompt in?</summary>
        public static bool IsMounted => _current != null;

        /// <summary>
        /// Build and bind the button and drawer. Returns null, and builds nothing, when nobody is
        /// signed in: <c>/friends</c> has no guest path, so a button there would open a drawer
        /// that can only apologise.
        /// </summary>
        public static FriendsDrawerController Mount()
        {
            if (_current != null) return _current;

            bool signedIn;
            try { signedIn = AuthGateway.IsSignedIn; }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Friends] could not read the sign-in state ({ex.GetType().Name}); " +
                                 "not mounting the friends button.");
                signedIn = false;
            }
            if (!signedIn) return null;

            var uiGo = new GameObject("FriendsDrawerUI");
            var doc  = uiGo.AddComponent<UIDocument>();
            doc.panelSettings   = Resources.Load<PanelSettings>("MatchSettingsPanelSettings");
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("FriendsDrawer");
            // The top UI layer outside a match: the shared panel sorts at 100, over every screen's
            // own panel (0), and 1 here puts it over the settings overlay's document (0) on that
            // panel too, so the open drawer covers the gear instead of sliding in underneath it.
            doc.sortingOrder = 1;
            var drawer = uiGo.AddComponent<FriendsDrawerController>();
            drawer.Init();
            _current = drawer;
            ChallengeFlow.Listen();
            SocialGateway.EnsureStarted();
            return drawer;
        }

        private void Init()
        {
            VisualElement docRoot = GetComponent<UIDocument>().rootVisualElement;
            _root = docRoot?.Q<VisualElement>("root");
            if (_root == null)
            {
                Debug.LogError("[Friends] No root element — is FriendsDrawer.uxml in Resources?");
                return;
            }

            _ownCode   = _root.Q<Label>("own-code");
            _status    = _root.Q<Label>("status");
            _addCode   = _root.Q<TextField>("add-code");
            _addButton = _root.Q<Button>("btn-add");
            _lists     = _root.Q<ScrollView>("lists");

            _root.Q<Button>("btn-friends")?.RegisterCallback<ClickEvent>(_ => Open());
            _root.Q<Button>("btn-close")?.RegisterCallback<ClickEvent>(_ => Close());
            _root.Q<VisualElement>("scrim")?.RegisterCallback<ClickEvent>(_ => Close());
            _root.Q<Button>("btn-copy-code")?.RegisterCallback<ClickEvent>(_ => CopyOwnCode());
            _addButton?.RegisterCallback<ClickEvent>(_ => AddFriend());

            _challenge     = _root.Q<VisualElement>("challenge");
            _challengeText = _root.Q<Label>("challenge-text");
            _challengeYes  = _root.Q<Button>("challenge-yes");
            _challengeNo   = _root.Q<Button>("challenge-no");
            if (_challengeText != null)
            {
                // The sentence carries a friend's name: peer-authored, see NameLabel.
                _challengeText.enableRichText = false;
                _challengeText.parseEscapeSequences = false;
            }
            _challengeYes?.RegisterCallback<ClickEvent>(_ => ChallengeFlow.Accept());
            _challengeNo?.RegisterCallback<ClickEvent>(_ =>
            {
                if (ChallengeFlow.Phase == ChallengePhase.None) ChallengeFlow.DismissNotice();
                else ChallengeFlow.Withdraw();
            });

            SocialGateway.Received += OnSocial;
            SocialGateway.StateChanged += OnSocialState;
            ChallengeFlow.Changed += OnChallengeChanged;

            Close();
            RenderChallenge();
        }

        private void OnDestroy()
        {
            SocialGateway.Received -= OnSocial;
            SocialGateway.StateChanged -= OnSocialState;
            ChallengeFlow.Changed -= OnChallengeChanged;
            if (_current == this) _current = null;
        }

        /// <summary>Ticks the countdown once a second while an offer is open.</summary>
        private void Update()
        {
            if (ChallengeFlow.ExpiresAt <= 0f) return;
            if (SecondsLeft() != _challengeSecondsShown) RenderChallenge();
        }

        private void OnChallengeChanged()
        {
            // An offer from a friend this drawer has not drawn yet: fetch the list for the name.
            if (ChallengeFlow.Phase == ChallengePhase.Incoming && !ChallengeFlow.KnowsName(ChallengeFlow.Code) && !_busy)
                Run(token => FriendsGateway.FetchAsync(token), okStatus: "");
            if (_shown != null) Render(_shown);   // the Challenge buttons depend on the phase
            RenderChallenge();
        }

        private static int SecondsLeft() =>
            ChallengeFlow.ExpiresAt <= 0f ? -1
                : Mathf.Max(0, Mathf.CeilToInt(ChallengeFlow.ExpiresAt - Time.realtimeSinceStartup));

        /// <summary>The challenge strip, from <see cref="ChallengeFlow"/> and nothing else.</summary>
        private void RenderChallenge()
        {
            if (_challenge == null) return;

            string name = ChallengeFlow.NameOf(ChallengeFlow.Code);
            int seconds = SecondsLeft();
            _challengeSecondsShown = seconds;
            string clock = seconds >= 0 ? $" ({seconds}s)" : "";

            string text = "", yes = "", no = "";
            switch (ChallengeFlow.Phase)
            {
                case ChallengePhase.Incoming:
                    text = $"{name} challenges you to a match{clock}"; yes = "Accept"; no = "Decline"; break;
                case ChallengePhase.Sending:
                    text = $"Sending a challenge to {name}…"; no = "Cancel"; break;
                case ChallengePhase.WaitingForAnswer:
                    text = $"Waiting for {name} to answer{clock}"; no = "Cancel"; break;
                case ChallengePhase.Starting:
                    text = "Starting the match…"; break;
                case ChallengePhase.None when ChallengeFlow.Notice.Length > 0:
                    text = ChallengeFlow.Notice; no = "OK"; break;
                // Picking: the roster's own intro line says what is going on.
            }

            _challenge.EnableInClassList("fr-challenge--hidden", text.Length == 0);
            if (_challengeText != null) _challengeText.text = text.Length == 0 ? " " : text;
            SetButton(_challengeYes, yes);
            SetButton(_challengeNo, no);
        }

        private static void SetButton(Button button, string text)
        {
            if (button == null) return;
            button.style.display = text.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            if (text.Length > 0) button.text = text;
        }

        private bool IsOpen => _root != null && _root.ClassListContains(OpenClass);

        private void OnSocial(SocialMsg msg)
        {
            switch (msg)
            {
                // Someone sent, accepted or withdrew something that involves this player.
                case FriendsChangedMsg _ when IsOpen:
                    Run(token => FriendsGateway.FetchAsync(token), okStatus: "");
                    break;
                case PresenceMsg _ when _shown != null:
                    Render(_shown);
                    break;
            }
        }

        /// <summary>A drop clears presence, so the dots go back to unknown until it is live again.</summary>
        private void OnSocialState(SocialState state)
        {
            if (_shown != null) Render(_shown);
        }

        private void Open()
        {
            _root.AddToClassList(OpenClass);
            SetStatus("Loading…", error: false);
            Run(token => FriendsGateway.FetchAsync(token), okStatus: "");
        }

        private void Close()
        {
            _root?.RemoveFromClassList(OpenClass);
            _expanded = "";
            _confirming = "";
        }

        private void CopyOwnCode()
        {
            if (_ownCodeDisplay.Length == 0) return;
            GUIUtility.systemCopyBuffer = _ownCodeDisplay;
            SetStatus("Code copied.", error: false);
        }

        private void AddFriend()
        {
            // Checked here so a typo is answered without a round trip. The server checks again.
            if (!FriendCode.TryNormalize(_addCode?.value, out string code))
            {
                SetStatus("That is not a friend code. It looks like XXXXX-XXXXX.", error: true);
                return;
            }

            // "Request sent" is all the server ever says, by design: it answers the same whether or
            // not the code belongs to anyone, so a code cannot be tested for existence.
            Run(token => FriendsGateway.RequestAsync(token, code), okStatus: "Request sent.",
                onOk: () => _addCode?.SetValueWithoutNotify(""));
        }

        /// <summary>
        /// One call at a time: take a fresh token, make the call, redraw from the answer.
        ///
        /// <para><b>async void</b> because it is started from a click and nothing can await it; the
        /// try/catch is what keeps it honest. The token is refreshed per call, never the stored one —
        /// a Supabase access token lives an hour and a drawer left open outlives it.</para>
        /// </summary>
        private async void Run(Func<string, Task<FriendsSnapshot>> call, string okStatus, Action onOk = null)
        {
            if (_busy) return;
            _busy = true;
            _addButton?.SetEnabled(false);

            FriendsSnapshot snapshot;
            try
            {
                string token = await AuthGateway.TokenForConnectAsync();
                snapshot = await call(token);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Friends] call failed ({ex.GetType().Name}: {ex.Message}).");
                snapshot = FriendsSnapshot.Failed(FriendsCallStatus.Offline);
            }

            // The scene may have been unloaded while the call was out.
            if (this == null) return;

            _busy = false;
            _addButton?.SetEnabled(true);

            if (snapshot.Status == FriendsCallStatus.Ok)
            {
                Render(snapshot);
                SetStatus(okStatus, error: false);
                onOk?.Invoke();
            }
            else
            {
                // The lists are left as they were: a failed call is not an answer about the graph.
                SetStatus(Describe(snapshot.Status), error: true);
            }
        }

        private static string Describe(FriendsCallStatus status) => status switch
        {
            FriendsCallStatus.Unauthenticated => "Sign in to use friends.",
            FriendsCallStatus.InvalidCode     => "That is not a friend code.",
            FriendsCallStatus.NotFound        => "That request is no longer waiting.",
            FriendsCallStatus.AtCapacity      => "Your list is full.",
            FriendsCallStatus.RateLimited     => "Too many requests. Try again in a minute.",
            FriendsCallStatus.Malformed       => "This build cannot read the server's answer. Update the game.",
            _                                 => "Could not reach the server.",
        };

        private void Render(FriendsSnapshot snapshot)
        {
            _shown = snapshot;
            _ownCodeDisplay = FriendCode.Display(snapshot.Code);
            if (_ownCode != null) _ownCode.text = $"Your code: {_ownCodeDisplay}";

            if (_lists == null) return;
            _lists.Clear();

            if (snapshot.Incoming.Count > 0)
            {
                _lists.Add(Section($"REQUESTS ({snapshot.Incoming.Count})"));
                foreach (FriendEntry entry in snapshot.Incoming)
                {
                    string code = entry.Code;   // captured per row, not the loop variable
                    _lists.Add(Row(entry,
                        ("Accept",  true,  () => Run(t => FriendsGateway.AcceptAsync(t, code),  "Friend added.")),
                        ("Decline", false, () => Run(t => FriendsGateway.DeclineAsync(t, code), "Request declined.")),
                        Confirmed("Block", "block:" + code,
                            () => Run(t => FriendsGateway.BlockAsync(t, code), "Blocked."))));
                }
            }

            _lists.Add(Section($"FRIENDS ({snapshot.Friends.Count})"));
            if (snapshot.Friends.Count == 0)
            {
                var empty = new Label("No friends yet. Share your code, or add someone by theirs.");
                empty.AddToClassList("fr-empty");
                _lists.Add(empty);
            }
            ChallengeFlow.RememberNames(snapshot.Friends);
            bool live = SocialGateway.State == SocialState.Live;
            bool canChallenge = live && ChallengeFlow.Phase == ChallengePhase.None;
            foreach (FriendEntry entry in snapshot.Friends)
            {
                string code = entry.Code;
                var actions = new List<(string, bool, Action)>();
                if (_expanded == code)
                {
                    // The row's second face: what ends the friendship. Each asks once more.
                    actions.Add(Confirmed("Remove", "remove:" + code,
                        () => Run(t => FriendsGateway.RemoveAsync(t, code), "Friend removed.")));
                    actions.Add(Confirmed("Block", "block:" + code,
                        () => Run(t => FriendsGateway.BlockAsync(t, code), "Blocked.")));
                    actions.Add(("Back", false, () => Expand("")));
                }
                else
                {
                    if (canChallenge && SocialGateway.PresenceOf(code) == PresenceState.Online)
                        actions.Add(("Challenge", true, () => { Close(); ChallengeFlow.Challenge(code); }));
                    actions.Add(("…", false, () => Expand(code)));
                }
                VisualElement row = Row(entry, actions.ToArray());
                row.Insert(0, PresenceDot(live, SocialGateway.PresenceOf(code)));
                _lists.Add(row);
            }

            if (snapshot.Outgoing.Count > 0)
            {
                _lists.Add(Section($"SENT ({snapshot.Outgoing.Count})"));
                foreach (FriendEntry entry in snapshot.Outgoing)
                {
                    string code = entry.Code;
                    _lists.Add(Row(entry,
                        ("Cancel", false, () => Run(t => FriendsGateway.DeclineAsync(t, code), "Request withdrawn."))));
                }
            }

            if (snapshot.Blocked.Count > 0)
            {
                _lists.Add(Section($"BLOCKED ({snapshot.Blocked.Count})"));
                foreach (FriendEntry entry in snapshot.Blocked)
                {
                    string code = entry.Code;
                    _lists.Add(Row(entry,
                        ("Unblock", false, () => Run(t => FriendsGateway.UnblockAsync(t, code), "Unblocked."))));
                }
            }
        }

        /// <summary>Which friend's row shows Remove and Block instead of Challenge. One at a time.</summary>
        private string _expanded = "";

        /// <summary>The action waiting for its second tap, as "verb:code". Cleared when the drawer
        /// closes, so a stale "Sure?" never greets the next open to be pressed by accident.</summary>
        private string _confirming = "";

        private void Expand(string code)
        {
            _expanded = code;
            _confirming = "";
            if (_shown != null) Render(_shown);
        }

        /// <summary>
        /// A button that needs two taps: the first turns it into "Sure?", the second does it.
        /// Remove and Block both end a friendship that only the other player can restore.
        /// </summary>
        private (string, bool, Action) Confirmed(string text, string key, Action act)
        {
            if (_confirming == key)
                return ("Sure?", true, () => { _confirming = ""; _expanded = ""; act(); });
            return (text, false, () =>
            {
                _confirming = key;
                if (_shown != null) Render(_shown);
            });
        }

        /// <summary>
        /// Green online, gold in a match, grey offline — and hollow when there is no live social
        /// connection, because then this client does not know, and "offline" would be a claim.
        /// </summary>
        private static VisualElement PresenceDot(bool live, PresenceState state)
        {
            var dot = new VisualElement();
            dot.AddToClassList("fr-presence");
            dot.AddToClassList(!live ? "fr-presence--unknown"
                : state == PresenceState.Online ? "fr-presence--online"
                : state == PresenceState.InMatch ? "fr-presence--in-match"
                : "fr-presence--offline");
            dot.tooltip = !live ? "Status unknown"
                : state == PresenceState.Online ? "Online"
                : state == PresenceState.InMatch ? "In a match"
                : "Offline";
            return dot;
        }

        private static Label Section(string text)
        {
            var label = new Label(text);
            label.AddToClassList("fr-section");
            return label;
        }

        private static VisualElement Row(FriendEntry entry,
                                         params (string Text, bool Primary, Action OnClick)[] actions)
        {
            var row = new VisualElement();
            row.AddToClassList("fr-row");
            row.Add(NameLabel(entry));

            foreach ((string text, bool primary, Action onClick) in actions)
            {
                var button = new Button(onClick) { text = text };
                button.AddToClassList("fr-row__button");
                if (primary) button.AddToClassList("emb-primary");
                row.Add(button);
            }
            return row;
        }

        /// <summary>
        /// The one place a friend's name is drawn. It is PEER-AUTHORED: the server ran
        /// <c>PlayerName.Check</c> before storing it, and this is the renderer's half of the same
        /// obligation the match nameplate carries (security.md, "Friends routes") —
        /// <b>rich text off</b>, here, and <b>a clip in pixels</b>, in <c>.fr-row__name</c>
        /// (friends.uss). An incoming request is from someone the player has not accepted, so this
        /// string reaches the screen before any trust exists. A second renderer inherits both.
        ///
        /// <para>The server sends no name for an outgoing request, and none for a player it holds
        /// no valid name for; those are shown by code.</para>
        /// </summary>
        private static Label NameLabel(FriendEntry entry)
        {
            string text = entry.DisplayName.Length > 0 ? entry.DisplayName : FriendCode.Display(entry.Code);
            var label = new Label(text)
            {
                enableRichText = false,
                parseEscapeSequences = false,
            };
            label.AddToClassList("fr-row__name");
            return label;
        }

        private void SetStatus(string message, bool error)
        {
            if (_status == null) return;
            // A space, not an empty string, so the line keeps its height and the list does not jump.
            _status.text = string.IsNullOrEmpty(message) ? " " : message;
            _status.EnableInClassList("fr-status--error", error);
        }
    }
}
