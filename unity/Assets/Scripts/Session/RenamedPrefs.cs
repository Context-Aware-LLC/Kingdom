using UnityEngine;

namespace Kingdom.Session
{
    /// <summary>
    /// Reads a <c>PlayerPrefs</c> key that was stored under the old codename until 2026-10-10.
    ///
    /// <para>A device that already holds a value under the former key keeps it: the value is moved to
    /// the new key on first read. Without this, the rename would mint every existing install a new
    /// device token — and <see cref="DeviceIdentity"/> says why that loss cannot be recovered.</para>
    /// </summary>
    internal static class RenamedPrefs
    {
        public static string GetString(string key, string formerKey)
        {
            string value = PlayerPrefs.GetString(key, string.Empty);
            if (!string.IsNullOrEmpty(value)) return value;

            string former = PlayerPrefs.GetString(formerKey, string.Empty);
            if (string.IsNullOrEmpty(former)) return string.Empty;

            PlayerPrefs.SetString(key, former);
            PlayerPrefs.DeleteKey(formerKey);
            PlayerPrefs.Save();
            return former;
        }
    }
}
