using System;
using UnityEngine;

namespace Kingdom.Session
{
    /// <summary>
    /// The device token (C-G1-8; telemetry-spec §0).
    ///
    /// <para><b>Not an identity.</b> A player's identity is their Supabase sign-in, and the client
    /// never connects without one. The device token still goes on every hello as
    /// <c>HelloMsg.DeviceToken</c>, but it is a local folder key and a telemetry id: when a sign-in
    /// token is present the server keys the seat on that and never on this. Only tooling (NetSoak,
    /// in-process test servers) connects on the device token alone, behind
    /// <c>ServerOptions.AllowGuestMatches</c>.</para>
    ///
    /// <para>Generated ONCE on this device and persisted forever after. That persistence is the
    /// entire point: every telemetry row emitted against a non-persistent token is unattributable,
    /// and that loss cannot be recovered.</para>
    ///
    /// <para><b>A UUID by convention.</b></para>
    ///
    /// <para><b>Not a secret, but not public either.</b> The server never sends it to the
    /// opponent.</para>
    /// </summary>
    public static class DeviceIdentity
    {
        private const string PrefsKey = "kingdom.device.token";
        private const string FormerPrefsKey = "emberfall.device.token";

        private static string _cached;

        /// <summary>The stable token for this device, minting and persisting it on first call.</summary>
        public static string Token
        {
            get
            {
                if (!string.IsNullOrEmpty(_cached)) return _cached;

                string existing = RenamedPrefs.GetString(PrefsKey, FormerPrefsKey);
                if (!string.IsNullOrEmpty(existing))
                {
                    _cached = existing;
                    return _cached;
                }

                _cached = Guid.NewGuid().ToString("D");
                PlayerPrefs.SetString(PrefsKey, _cached);
                // Written through immediately: an unsaved token that dies with a crashed first
                // session would hand this device a second identity on next launch.
                PlayerPrefs.Save();
                return _cached;
            }
        }

        /// <summary>Build provenance for the funnel (telemetry-spec §4). Best-effort, never fatal.</summary>
        public static string Platform => Application.platform.ToString();

        public static string BuildId => Application.version;
    }
}
