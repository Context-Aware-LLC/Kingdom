using System;
using Kingdom.Net.Client.Social;
using UnityEngine;

namespace Kingdom.Session
{
    /// <summary>
    /// A friend link the game was opened with (#424), held until a friends drawer can take it.
    ///
    /// <para><b>It outlives every screen on purpose.</b> A link that cold-starts the game arrives
    /// before sign-in and long before the menu mounts the drawer, and one tapped during a match
    /// arrives where no drawer is mounted. So it is kept here for the life of the process and the
    /// next drawer to mount takes it. A second link replaces the first: the player tapped it last.</para>
    ///
    /// <para><b>It listens for its own links only</b> (<see cref="FriendLink.TryRead"/>). The sign-in
    /// gate listens on the same <see cref="Application.deepLinkActivated"/> for <c>emberfall://auth</c>,
    /// and each ignores the other's.</para>
    /// </summary>
    public static class FriendLinkInbox
    {
        private static string _pending = "";

        /// <summary>A link arrived. Raised on the main thread; the code is waiting in
        /// <see cref="TryTake"/>.</summary>
        public static event Action Arrived;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Listen()
        {
            Application.deepLinkActivated -= Offer;
            Application.deepLinkActivated += Offer;
            Offer(Application.absoluteURL);   // a cold start, which the event never reports
        }

        private static void Offer(string url)
        {
            if (!FriendLink.TryRead(url, out string code)) return;
            _pending = code;
            Arrived?.Invoke();
        }

        /// <summary>The waiting code, canonical, once: taking it empties the inbox.</summary>
        public static bool TryTake(out string code)
        {
            code = _pending;
            _pending = "";
            return code.Length > 0;
        }
    }
}
