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

        public const string UpdateJoke = "The Crown has issued a new decree, and yours is out of date.";

        public static readonly ConnectionFailureCopy Unreachable = new ConnectionFailureCopy(
            "Can't reach the server. Check your connection and try again.",
            "The courier balloon has drifted off course again.");

        public static readonly ConnectionFailureCopy NotResponding = new ConnectionFailureCopy(
            "The server isn't answering. Try again in a moment.",
            "The courier balloon has drifted off course again.");

        public static readonly ConnectionFailureCopy Lost = new ConnectionFailureCopy(
            "Lost the connection to the server.",
            "The courier balloon has drifted off course again.");

        /// <summary>The client could not get a sign-in token for the connect, or the server
        /// refused a hello that came without one ("sign-in required").</summary>
        public static readonly ConnectionFailureCopy SignInUnconfirmed = new ConnectionFailureCopy(
            "We couldn't confirm your sign-in. Check your connection and try again.",
            "The guards no longer recognise you.");

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
                        "Your deck was turned away. " + detail.Substring(deckRejected.Length),
                        "The Council has returned it with notes.");

                switch (detail)
                {
                    case "server full":
                        return new ConnectionFailureCopy(
                            "The server is full. Try again in a moment.",
                            "The Council has asked everyone to form a queue.");
                    case "token rejected":
                        return new ConnectionFailureCopy(
                            "You've been signed out. Sign in again from the menu.",
                            "The guards no longer recognise you.");
                    case "sign-in required":
                        return SignInUnconfirmed;
                    case "contract mismatch":
                        return new ConnectionFailureCopy(
                            "This version of the game doesn't match the server's.",
                            UpdateJoke);
                    case "challenge expired":
                        return new ConnectionFailureCopy(
                            "The challenge expired before both of you arrived.",
                            "The invitation has gone stale on the mantelpiece.");
                    case "superseded":
                    case "replaced by a newer connection":
                        return new ConnectionFailureCopy(
                            "This account joined from somewhere else.",
                            "The Court does not seat the same petitioner twice.");
                    case "deck timeout":
                    case "hello timeout":
                    case "liveness timeout":
                        return Lost;
                }
                return new ConnectionFailureCopy(
                    "The server turned this match away.",
                    "No reason was given, as is the Council's custom.");
            }

            switch (reason)
            {
                case DeadReason.ContractMismatch:
                    return new ConnectionFailureCopy(
                        "This version of the game doesn't match the server's.",
                        UpdateJoke);
                case DeadReason.Desync:
                    return new ConnectionFailureCopy(
                        "The match fell out of step with the server.",
                        "The scribes' copies no longer agree.");
                case DeadReason.ServerFull:
                    return For(reason, "server full", reachedServer);
                default:
                    return reachedServer ? Lost : Unreachable;
            }
        }
    }
}
