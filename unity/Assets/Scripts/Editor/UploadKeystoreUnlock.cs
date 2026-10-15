using System;
using UnityEditor;

namespace Kingdom.Editor
{
    /// <summary>
    /// Fills the Android keystore passwords from the environment whenever the Editor loads, so a
    /// build started from the Build Profiles window does not stop to ask for them.
    ///
    /// <para><b>Unity never saves these two fields</b> — they are empty again after every Editor
    /// launch, which is why they are set here rather than once in Publishing Settings. Nothing is
    /// written to the project: the password stays in <c>KINGDOM_UPLOAD_KEYSTORE_PASS</c>, the
    /// same variable <c>tools/ci/build-android.ps1 -PlayRelease</c> reads.</para>
    ///
    /// <para>One password serves both fields because the upload keystore is PKCS12, which has no
    /// separate key password.</para>
    /// </summary>
    [InitializeOnLoad]
    internal static class UploadKeystoreUnlock
    {
        private const string PasswordVariable = "KINGDOM_UPLOAD_KEYSTORE_PASS";

        static UploadKeystoreUnlock()
        {
            if (!PlayerSettings.Android.useCustomKeystore) return;

            string password = Environment.GetEnvironmentVariable(PasswordVariable);
            if (string.IsNullOrEmpty(password)) return;

            PlayerSettings.Android.keystorePass = password;
            PlayerSettings.Android.keyaliasPass = password;
        }
    }
}
