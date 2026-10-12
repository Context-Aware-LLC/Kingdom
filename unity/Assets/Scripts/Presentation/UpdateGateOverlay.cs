using System.Threading.Tasks;
using Kingdom.Net.Client.Gate;
using Kingdom.Net.Protocol;
using Kingdom.Session;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The forced-update gate: asked once per launch, before the main menu shows, and again when a
    /// match is refused over the wire contract. A build under the server's minimum gets one button,
    /// which opens the store; a build that cannot reach the server gets one button, which retries.
    /// Neither has a way past, because there is nothing past it — the game has no offline mode.
    ///
    /// <para>Built at runtime from <c>ConnectionFailedScreen.uxml</c>, the same Resources-loaded
    /// pattern <see cref="ConnectionFailedOverlay"/> uses, so there is one blocking-modal tree
    /// rather than two.</para>
    ///
    /// <para><b>A pass is remembered for the life of the process</b>, so returning to the menu
    /// does not pay a round trip each time. The cost is that a session left open across a release
    /// is not caught here; it is caught at the queue, where <c>NetworkedGameBootstrap</c> asks
    /// again with <c>force</c>.</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class UpdateGateOverlay : MonoBehaviour
    {
        private const int TimeoutSeconds = 15;
        private const string PlayListing = "https://play.google.com/store/apps/details?id=";
        // No App Store listing exists yet; until one does, iOS and desktop go to the site.
        private const string SiteRoot = "https://kingdomofbadideas.com";

        private static bool _passed;

        /// <summary>Never throws: every failure is a verdict.</summary>
        public static async Task<UpdateGateResult> CheckAsync(bool force = false)
        {
            if (_passed && !force) return UpdateGateResult.Pass();

            var gate = new HttpUpdateGate(
                CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(TimeoutSeconds));
            UpdateGateResult result = await gate.CheckAsync(
                Platform, BuildStamp.StoreBuild, ProtocolContract.Version);

            _passed = result.Verdict == UpdateGateVerdict.Pass;
            if (!_passed)
                Debug.LogWarning($"[UpdateGate] {result.Verdict}: {result.Detail}");
            return result;
        }

        public static void Show(UpdateGateResult result)
        {
            var uiGo = new GameObject("UpdateGateUI");
            var doc = uiGo.AddComponent<UIDocument>();
            doc.panelSettings   = Resources.Load<PanelSettings>("MatchSettingsPanelSettings");
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("ConnectionFailedScreen");
            // The menu's settings gear lives on this same panel; sort above it so the backdrop
            // covers it.
            doc.sortingOrder = 1;
            uiGo.AddComponent<UpdateGateOverlay>().Init(result.Verdict);
        }

        private static string Platform =>
            Application.platform == RuntimePlatform.Android ? HttpUpdateGate.Android
            : Application.platform == RuntimePlatform.IPhonePlayer ? HttpUpdateGate.Ios
            : "";

        private static string StoreUrl =>
            Application.platform == RuntimePlatform.Android
                ? PlayListing + Application.identifier
                : SiteRoot;

        private void Init(UpdateGateVerdict verdict)
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[UpdateGate] No rootVisualElement — is the UXML/PanelSettings assigned before Init?");
                return;
            }

            bool stale = verdict == UpdateGateVerdict.UpdateRequired;

            // Nearly opaque: nothing behind this is reachable, so it must not look dismissible.
            root.Q("modal")?.AddToClassList("mm-modal--gate");

            var title = root.Q<Label>("title");
            if (title != null) title.text = stale ? "Update required" : "Couldn't reach the server";

            var reason = root.Q<Label>("reason");
            if (reason != null)
                reason.text = stale
                    ? "A new version of Kingdom of Bad Ideas is out. Update to keep playing."
                    : "Kingdom of Bad Ideas needs a connection to play. Check your network and try again.";

            root.Q<Button>("btn-back")?.RemoveFromHierarchy();

            var action = root.Q<Button>("btn-retry");
            if (action == null) return;
            action.text = stale ? "Update" : "Retry";
            if (stale) action.RegisterCallback<ClickEvent>(_ => Application.OpenURL(StoreUrl));
            else action.RegisterCallback<ClickEvent>(_ => ReloadThisScene());
        }

        private static void ReloadThisScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            SceneManager.LoadScene(scene.name);
        }
    }
}
