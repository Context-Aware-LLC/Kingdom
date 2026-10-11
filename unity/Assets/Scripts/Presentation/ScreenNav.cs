using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The one way a screen hands off to another screen, and the one place scene names are spelled.
    ///
    /// <para><b>Why this exists.</b> Seven controllers each carried a private <c>Go(string)</c> that
    /// was the same eight lines differing only by a log prefix (measured 2026-09-06:
    /// MainMenu, Collection, CharacterRoster, CharacterCreate, PackVault, Shop, plus AccountCreate
    /// calling into one). Duplication that size is cheap to carry and expensive to *know about*:
    /// each copy had learned something the others had not — one held the reason for the API choice,
    /// one held a build-list hazard, one logged the transition and six did not. Consolidating the
    /// code is incidental; consolidating what the copies knew is the point, and all of it is
    /// below.</para>
    ///
    /// <para><b>Why <c>CanStreamedLevelBeLoaded</c> and not <c>SceneUtility.GetBuildIndexByScenePath</c>.</b>
    /// The latter wants a full asset path and answers -1 for a bare scene name, so a guard built on
    /// it rejects every load it was written to protect.</para>
    ///
    /// <para><b>The build-list hazard.</b> A scene missing from the build list fails here, not at
    /// author time — the button simply does nothing. <c>Kingdom/Build/Sync Scene List</c> fixes it
    /// in the open Editor, but that menu item does NOT reach <c>EditorBuildSettings.asset</c> on
    /// disk; hand-edit the asset to match, or the fix is lost the next time the Editor is closed.
    /// This warning previously existed in exactly one of the seven copies.</para>
    /// </summary>
    public static class ScreenNav
    {
        // The scene names, spelled once. Every one of these was a bare string literal at ~30 call
        // sites, and two files (ConnectionFailedOverlay, and MatchEndSequence under its old name) had already
        // independently declared their own `private const string MainMenuSceneName` -- two separate
        // reaches for the same missing constant, which is what says it was missing.
        public const string MainMenu = "MainMenu";
        public const string AccountCreate = "AccountCreate";
        public const string CharacterRoster = "CharacterRoster";
        public const string CharacterCreate = "CharacterCreate";
        public const string Collection = "Collection";
        public const string PackVault = "PackVault";
        public const string Shop = "Shop";
        public const string BoardNetworked = "BoardNetworked";

        /// <summary>
        /// Load <paramref name="sceneName"/>, refusing loudly if it is not in the build list.
        ///
        /// The transition is logged with the scene being left, which no caller has to supply and
        /// which is strictly more useful than the hardcoded per-controller prefix it replaces --
        /// that told you which FILE asked, this tells you where the player actually was.
        /// </summary>
        public static void Go(string sceneName)
        {
            if (!CanLoad(sceneName)) return;
            LogTransition(sceneName);
            SceneManager.LoadScene(sceneName);
        }

        /// <summary>
        /// Start a non-blocking scene load after the caller has painted any source-scene transition
        /// surface it owns. The operation may outlive the caller when its source scene is destroyed.
        /// </summary>
        public static AsyncOperation GoAsync(string sceneName)
        {
            if (!CanLoad(sceneName)) return null;
            LogTransition(sceneName);
            return SceneManager.LoadSceneAsync(sceneName);
        }

        private static bool CanLoad(string sceneName)
        {
            if (Application.CanStreamedLevelBeLoaded(sceneName)) return true;
            Debug.LogError(
                $"[Nav] Scene '{sceneName}' is not in the build list, so this cannot do " +
                "anything. Run Kingdom/Build/Sync Scene List — and note that menu item does " +
                "not reach EditorBuildSettings.asset on disk, so hand-edit it to match.");
            return false;
        }

        private static void LogTransition(string sceneName)
        {
            Debug.Log($"[Nav] {SceneManager.GetActiveScene().name} → {sceneName}");
        }
    }
}
