using System;
using UnityEngine;

namespace Emberfall.Session
{
    /// <summary>
    /// The guest device token (C-G1-8; telemetry-spec §0, accounts posture decision 6a).
    ///
    /// <para>Generated ONCE on this device and persisted forever after. That persistence is the
    /// entire point: a token regenerated per launch is the <c>guest-&lt;counter&gt;</c> it replaces,
    /// wearing a UUID. Every telemetry row emitted against a non-persistent token is
    /// unattributable, and unlike the account registry — which can be rebuilt later from tokens
    /// already emitted — that loss cannot be recovered.</para>
    ///
    /// <para><b>A UUID by convention</b>, because Supabase (the ADR-0001 destination) keys users on
    /// UUIDs, so a guest converts one-to-one when real identity lands.</para>
    ///
    /// <para><b>Not a secret, but not public either.</b> It is a bearer identity: whoever holds it
    /// can claim these matches. The server hashes it into an account id on arrival and never sends
    /// it to the opponent — see <c>AccountId</c>.</para>
    /// </summary>
    public static class DeviceIdentity
    {
        private const string PrefsKey = "emberfall.device.token";

        private static string _cached;

        /// <summary>The stable token for this device, minting and persisting it on first call.</summary>
        public static string Token
        {
            get
            {
                if (!string.IsNullOrEmpty(_cached)) return _cached;

                string existing = PlayerPrefs.GetString(PrefsKey, string.Empty);
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
