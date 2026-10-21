using Kingdom.Net.Client;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// #232: the only way out of a failed networked boot besides killing the app. Before this,
    /// a rejected deck, a full server, and a dead network drop all left the player stranded on
    /// whatever loading/versus overlay was up, with no retry and no back button.
    ///
    /// Built by <see cref="NetworkedGameBootstrap.BootFailed"/> — no scene wiring, same
    /// Resources-loaded UXML/PanelSettings pattern <see cref="MatchSettingsController"/> uses.
    /// Retry reloads this scene (a fresh connect attempt, same shape as
    /// MatchSettingsController.ReloadThisScene); Back to menu loads MainMenu. An ordinary
    /// match end goes to deck selection instead (MatchEndSequence); a failed boot does not,
    /// because the picker would walk the player straight back into the same failure.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ConnectionFailedOverlay : MonoBehaviour
    {
        /// <summary>The plain line is the title; the joke, when there is one, sits under it.</summary>
        public void Init(ConnectionFailureCopy copy)
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[ConnectionFailed] No rootVisualElement — is the UXML/PanelSettings assigned before Init?");
                return;
            }
            UiLocalizer.Attach(root);

            var title = root.Q<Label>("title");
            if (title != null) title.text = copy.Plain;

            var reasonLabel = root.Q<Label>("reason");
            if (reasonLabel != null)
            {
                reasonLabel.text = copy.Joke;
                reasonLabel.style.display = string.IsNullOrEmpty(copy.Joke) ? DisplayStyle.None : DisplayStyle.Flex;
            }

            var retry = root.Q<Button>("btn-retry");
            retry?.RegisterCallback<ClickEvent>(_ => ReloadThisScene());

            var back = root.Q<Button>("btn-back");
            back?.RegisterCallback<ClickEvent>(_ => GoToMenu());
        }

        private static void ReloadThisScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            SceneManager.LoadScene(scene.name);
        }

        private static void GoToMenu() => ScreenNav.Go(ScreenNav.MainMenu);
    }

    /// <summary>
    /// What the player reads when a networked match fails to start: a plain line saying what
    /// happened, then one joke. Before this the panel printed the raw <see cref="DeadReason"/>
    /// name ("ServerFull") or the server's close-frame text ("token rejected").
    ///
    /// <para><b>The server's close text is matched by its exact spelling</b> in
    /// <c>MatchGate</c> and <c>MatchSession</c>. Reword one there and it falls through to the
    /// generic refusal here, which still reads fine but no longer says why. The raw text is
    /// always in the bootstrap's log line.</para>
    /// </summary>
    public readonly struct ConnectionFailureCopy
    {
        public readonly string Plain;
        public readonly string Joke;

        public ConnectionFailureCopy(string plain, string joke)
        {
            Plain = plain;
            Joke = joke;
        }

        // Properties, not cached fields: each read resolves in the current language.
        public static string UpdateJoke => Loc.Get("connection_joke_update");

        public static ConnectionFailureCopy Unreachable => new ConnectionFailureCopy(
            Loc.Get("connection_error_unreachable"),
            Loc.Get("connection_joke_courier"));

        public static ConnectionFailureCopy NotResponding => new ConnectionFailureCopy(
            Loc.Get("connection_error_not_responding"),
            Loc.Get("connection_joke_courier"));

        public static ConnectionFailureCopy Lost => new ConnectionFailureCopy(
            Loc.Get("connection_error_lost"),
            Loc.Get("connection_joke_courier"));

        /// <summary>The client could not get a sign-in token for the connect, or the server
        /// refused a hello that came without one ("sign-in required").</summary>
        public static ConnectionFailureCopy SignInUnconfirmed => new ConnectionFailureCopy(
            Loc.Get("connection_error_sign_in_unconfirmed"),
            Loc.Get("connection_joke_guards"));

        /// <summary>
        /// <paramref name="reachedServer"/> is whether the server got as far as seating this
        /// player. A drop with no reason before that is "can't reach"; after it, "lost".
        /// </summary>
        public static ConnectionFailureCopy For(DeadReason reason, string detail, bool reachedServer)
        {
            if (!string.IsNullOrEmpty(detail))
            {
                const string deckRejected = "deck rejected: ";
                if (detail.StartsWith(deckRejected))
                    return new ConnectionFailureCopy(
                        Loc.Format("connection_error_deck_rejected", detail.Substring(deckRejected.Length)),
                        Loc.Get("connection_joke_deck_rejected"));

                switch (detail)
                {
                    case "server full":
                        return new ConnectionFailureCopy(
                            Loc.Get("connection_error_server_full"),
                            Loc.Get("connection_joke_server_full"));
                    case "token rejected":
                        return new ConnectionFailureCopy(
                            Loc.Get("connection_error_signed_out"),
                            Loc.Get("connection_joke_guards"));
                    case "sign-in required":
                        return SignInUnconfirmed;
                    case "contract mismatch":
                        return new ConnectionFailureCopy(
                            Loc.Get("connection_error_version_mismatch"),
                            UpdateJoke);
                    case "challenge expired":
                        return new ConnectionFailureCopy(
                            Loc.Get("connection_error_challenge_expired"),
                            Loc.Get("connection_joke_challenge_expired"));
                    case "superseded":
                    case "replaced by a newer connection":
                        return new ConnectionFailureCopy(
                            Loc.Get("connection_error_superseded"),
                            Loc.Get("connection_joke_superseded"));
                    case "deck timeout":
                    case "hello timeout":
                    case "liveness timeout":
                        return Lost;
                }
                return new ConnectionFailureCopy(
                    Loc.Get("connection_error_turned_away"),
                    Loc.Get("connection_joke_turned_away"));
            }

            switch (reason)
            {
                case DeadReason.ContractMismatch:
                    return new ConnectionFailureCopy(
                        Loc.Get("connection_error_version_mismatch"),
                        UpdateJoke);
                case DeadReason.Desync:
                    return new ConnectionFailureCopy(
                        Loc.Get("connection_error_desync"),
                        Loc.Get("connection_joke_desync"));
                case DeadReason.ServerFull:
                    return For(reason, "server full", reachedServer);
                default:
                    return reachedServer ? Lost : Unreachable;
            }
        }
    }
}
