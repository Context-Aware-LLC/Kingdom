using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Kingdom.Session
{
    /// <summary>
    /// The phone's own share sheet, for a line of text (#424: the friend link). Android sends an
    /// <c>ACTION_SEND</c> chooser; iOS presents a <c>UIActivityViewController</c> through
    /// <c>Plugins/iOS/KobiShare.mm</c>. Everywhere else, the Editor included, there is no sheet and
    /// <see cref="TryShare"/> says so, so the caller can copy the text instead.
    /// </summary>
    public static class SystemShareSheet
    {
        public static bool IsSupported =>
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            true;
#else
            false;
#endif

        /// <summary>Open the sheet. False, with nothing shown, when this platform has none or the
        /// call failed; never throws.</summary>
        public static bool TryShare(string text, string title)
        {
            if (!IsSupported || string.IsNullOrEmpty(text)) return false;
            try
            {
                Open(text, title ?? "");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Share] the share sheet did not open ({ex.GetType().Name}: {ex.Message}).");
                return false;
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static void Open(string text, string title)
        {
            using (var intentClass = new AndroidJavaClass("android.content.Intent"))
            using (var send = new AndroidJavaObject("android.content.Intent"))
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                // Each setter answers the intent itself; those references are released here.
                send.Call<AndroidJavaObject>("setAction", intentClass.GetStatic<string>("ACTION_SEND")).Dispose();
                send.Call<AndroidJavaObject>("setType", "text/plain").Dispose();
                send.Call<AndroidJavaObject>("putExtra", intentClass.GetStatic<string>("EXTRA_TEXT"), text).Dispose();
                using (AndroidJavaObject chooser = intentClass.CallStatic<AndroidJavaObject>("createChooser", send, title))
                    activity.Call("startActivity", chooser);
            }
        }
#elif UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void KobiShareText(string text);

        private static void Open(string text, string title) => KobiShareText(text);
#else
        private static void Open(string text, string title) =>
            throw new PlatformNotSupportedException("No share sheet on this platform.");
#endif
    }
}
