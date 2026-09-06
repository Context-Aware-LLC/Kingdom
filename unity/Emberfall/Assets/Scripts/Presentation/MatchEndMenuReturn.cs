using System.Collections;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>Returns the player to the menu after the result has had time to read.</summary>
    public sealed class MatchEndMenuReturn : MonoBehaviour
    {
        public const float ReturnDelaySeconds = 3f;

        private IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(ReturnDelaySeconds);
            ScreenNav.Go(ScreenNav.MainMenu);
        }
    }
}
