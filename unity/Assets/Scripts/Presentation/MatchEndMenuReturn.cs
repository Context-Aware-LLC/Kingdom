using System.Collections;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Returns the player to deck selection after the result has had time to read (Sam,
    /// 2026-10-02). It went to the main menu before; Back on the picker still leads there.
    /// </summary>
    public sealed class MatchEndMenuReturn : MonoBehaviour
    {
        public const float ReturnDelaySeconds = 3f;

        private IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(ReturnDelaySeconds);
            MatchDeckSelection.OpenPicker();
        }
    }
}
