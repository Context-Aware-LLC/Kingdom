using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Emberfall.Presentation
{
    /// <summary>Returns the player to the menu after the result has had time to read.</summary>
    public sealed class MatchEndMenuReturn : MonoBehaviour
    {
        public const float ReturnDelaySeconds = 3f;
        private const string MainMenuSceneName = "MainMenu";

        private IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(ReturnDelaySeconds);

            if (!Application.CanStreamedLevelBeLoaded(MainMenuSceneName))
            {
                Debug.LogError($"[MatchEnd] Scene '{MainMenuSceneName}' is not in the build list; cannot return to menu.");
                yield break;
            }

            SceneManager.LoadScene(MainMenuSceneName);
        }
    }
}
