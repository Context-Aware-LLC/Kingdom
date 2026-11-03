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
    ///
    /// <para><b>A check that is slow says so, and an unreachable one is asked again.</b>
    /// <see cref="UpdateGateRetry"/> asks up to three times with growing pauses (its constants
    /// carry the measurements). If no answer has come after <see cref="WaitingDelayMs"/>, the same
    /// modal shows a waiting state until one does; a fast pass never shows it. Only after the last
    /// attempt does the failure state, with its Retry and its joke, appear. Both callers get this:
    /// the menu at launch, and the queue's re-ask, whose question (store or Retry?) deserves the
    /// same patience.</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class UpdateGateOverlay : MonoBehaviour
    {
        // Past the boot loading screen's two-second floor, so the waiting state never lands on top
        // of it. Measured 2026-10-23 (see UpdateGateRetry): warm 0.72 s and a wake from suspend
        // 1.43 s both pass unseen; only a cold start (6.68 s) or a bad network shows it.
        private const int WaitingDelayMs = 2000;
        private const string PlayListing = "https://play.google.com/store/apps/details?id=";
        // No App Store listing exists yet; until one does, iOS and desktop go to the site.
        private const string SiteRoot = "https://kingdomofbadideas.com";
        private const string FuseClass = "mm-gate__fuse";
        private const int FuseFrames = 3;
        private const long FuseFrameMs = 320;

        private static bool _passed;

        private Label _reason;

        /// <summary>Never throws: every failure is a verdict, and
        /// <see cref="UpdateGateResult.Attempts"/> says how many asks it took (0 for a pass
        /// remembered from earlier in the process). Shows the waiting state while it runs long, and
        /// takes it down before returning; the caller shows any failure with <see cref="Show"/>.</summary>
        public static async Task<UpdateGateResult> CheckAsync(bool force = false)
        {
            if (_passed && !force) return UpdateGateResult.Pass().WithAttempts(0);

            UpdateGateOverlay waiting = null;
            int attempt = 0;
            Task<UpdateGateResult> check = UpdateGateRetry.CheckAsync(async (n, timeoutSeconds) =>
            {
                attempt = n;
                if (waiting != null) waiting.ShowWaiting(n);
                // The funnel's keys ride the request, so the server's gate_served row lands in the
                // same session as this process's own gate row.
                var gate = new HttpUpdateGate(
                    CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(timeoutSeconds),
                    FunnelSession.Id, DeviceIdentity.Token,
                    FirstSessionFunnel.Platform, BuildStamp.ReleaseId);
                UpdateGateResult answer = await gate.CheckAsync(
                    Platform, BuildStamp.StoreBuild, ProtocolContract.Version);
                if (answer.Verdict == UpdateGateVerdict.Unreachable && n < UpdateGateRetry.MaxAttempts)
                    Debug.Log($"[UpdateGate] attempt {n} of {UpdateGateRetry.MaxAttempts} unreachable: {answer.Detail}");
                return answer;
            });

            if (await Task.WhenAny(check, Task.Delay(WaitingDelayMs)) != check)
            {
                waiting = Create();
                if (waiting != null) waiting.ShowWaiting(attempt);
            }

            UpdateGateResult result = await check;
            if (waiting != null) Destroy(waiting.gameObject);

            _passed = result.Verdict == UpdateGateVerdict.Pass;
            if (!_passed)
                Debug.LogWarning($"[UpdateGate] {result.Verdict} after {result.Attempts} attempt(s): {result.Detail}");
            return result;
        }

        public static void Show(UpdateGateResult result)
        {
            UpdateGateOverlay overlay = Create();
            if (overlay != null) overlay.ShowVerdict(result.Verdict);
        }

#if UNITY_EDITOR
        /// <summary>For <c>Kingdom/Preview/Update Gate</c>: one state of this screen with no server
        /// asked. Null is the waiting state. Returns the host so the preview can take it down.</summary>
        public static GameObject Preview(UpdateGateVerdict? verdict)
        {
            UpdateGateOverlay overlay = Create();
            if (overlay == null) return null;
            if (verdict.HasValue) overlay.ShowVerdict(verdict.Value);
            else overlay.ShowWaiting(1);
            return overlay.gameObject;
        }
#endif

        private static UpdateGateOverlay Create()
        {
            var uiGo = new GameObject("UpdateGateUI");
            var doc = uiGo.AddComponent<UIDocument>();
            doc.panelSettings   = Resources.Load<PanelSettings>("MatchSettingsPanelSettings");
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("ConnectionFailedScreen");
            // The menu's settings gear lives on this same panel; sort above it so the backdrop
            // covers it.
            doc.sortingOrder = 1;
            var overlay = uiGo.AddComponent<UpdateGateOverlay>();
            if (overlay.Init()) return overlay;
            Destroy(uiGo);
            return null;
        }

        private static string Platform =>
            Application.platform == RuntimePlatform.Android ? HttpUpdateGate.Android
            : Application.platform == RuntimePlatform.IPhonePlayer ? HttpUpdateGate.Ios
            : "";

        private static string StoreUrl =>
            Application.platform == RuntimePlatform.Android
                ? PlayListing + Application.identifier
                : SiteRoot;

        private bool Init()
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[UpdateGate] No rootVisualElement — is the UXML/PanelSettings assigned before Init?");
                return false;
            }
            UiLocalizer.Attach(root);

            // Nearly opaque: nothing behind this is reachable, so it must not look dismissible.
            root.Q("modal")?.AddToClassList("mm-modal--gate");
            root.Q<Button>("btn-back")?.RemoveFromHierarchy();
            _reason = root.Q<Label>("reason");
            return true;
        }

        /// <summary>No button at all: there is nothing to press while the herald is out, and a
        /// Retry here would only start a second check alongside the first.</summary>
        private void ShowWaiting(int attempt)
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            // The whole row, not just the button, so an empty row does not pad the panel.
            root.Q(className: "mm-modal__actions")?.RemoveFromHierarchy();
            if (root.Q(className: FuseClass) == null) root.Q("panel")?.Insert(0, BurningFuse());

            var title = root.Q<Label>("title");
            if (title != null) title.text = Loc.Get("update_gate_title_waiting");
            if (_reason != null)
                _reason.text = attempt <= 1
                    ? Loc.Get("update_gate_reason_waiting")
                    : Loc.Format("update_gate_reason_waiting_retry", attempt, UpdateGateRetry.MaxAttempts);
        }

        /// <summary>The crown's fuse burns down and is relit, on a loop, so a cold start that holds
        /// this screen for seconds never looks frozen. The schedule belongs to the element, so it
        /// stops when the overlay is destroyed.</summary>
        private static VisualElement BurningFuse()
        {
            var fuse = new VisualElement();
            fuse.AddToClassList(FuseClass);
            int frame = 0;
            fuse.schedule.Execute(() =>
            {
                fuse.style.backgroundPositionX = new BackgroundPosition(
                    BackgroundPositionKeyword.Left, new Length(frame * 50f, LengthUnit.Percent));
                frame = (frame + 1) % FuseFrames;
            }).Every(FuseFrameMs);
            return fuse;
        }

        private void ShowVerdict(UpdateGateVerdict verdict)
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            bool stale = verdict == UpdateGateVerdict.UpdateRequired;

            var title = root.Q<Label>("title");
            if (title != null) title.text = Loc.Get(stale ? "update_gate_title_required" : "update_gate_title_unreachable");

            if (_reason != null)
                _reason.text = stale
                    ? Loc.Format("update_gate_reason_required", ConnectionFailureCopy.UpdateJoke)
                    : Loc.Format("update_gate_reason_unreachable", ConnectionFailureCopy.Unreachable.Joke);

            var action = root.Q<Button>("btn-retry");
            if (action == null) return;
            action.text = Loc.Get(stale ? "update_gate_button_update" : "common_retry");
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
