using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>Device-only haptic adapter. Match-event interpretation lives in the pure policy.</summary>
    internal static class HeroHaptics
    {
        public static void PulseDamage()
        {
            if (!Application.isPlaying) return;

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject activity =
                    unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (AndroidJavaObject window = activity.Call<AndroidJavaObject>("getWindow"))
                using (AndroidJavaObject view = window.Call<AndroidJavaObject>("getDecorView"))
                using (var constants = new AndroidJavaClass("android.view.HapticFeedbackConstants"))
                {
                    // CONTEXT_CLICK is short and tactile, and performHapticFeedback respects the
                    // player's Android system haptics setting without requiring vibration access.
                    int contextClick = constants.GetStatic<int>("CONTEXT_CLICK");
                    view.Call<bool>("performHapticFeedback", contextClick);
                }
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning("Hero damage haptic was unavailable: " + exception.Message);
            }
#elif UNITY_IOS && !UNITY_EDITOR
            // Unity's portable iOS path is intentionally kept behind the native platform define;
            // desktop and editor play mode must never vibrate the development machine.
            Handheld.Vibrate();
#endif
        }
    }
}
