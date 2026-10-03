#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.iOS.Xcode;

namespace Emberfall.Editor
{
    /// <summary>
    /// Declares in the generated Xcode project's <c>Info.plist</c> that the game uses no
    /// non-exempt encryption, so App Store Connect stops asking the export-compliance question on
    /// every uploaded build.
    ///
    /// <para><b>Unity has no Player Settings field for this key</b>, which is the only reason it
    /// is written here rather than set in the project.</para>
    ///
    /// <para><b>The answer is <c>false</c> because everything the client encrypts is exempt:</b>
    /// HTTPS and WSS to the game server and Supabase, and the PKCE hash in the sign-in flow, all
    /// through the operating system's own libraries. Shipping a cipher of our own, or a library
    /// that carries one, falsifies it — and the declaration is a legal one, so change it here in
    /// the same commit.</para>
    /// </summary>
    public sealed class IosExportCompliance : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.iOS) return;

            string plistPath = Path.Combine(report.summary.outputPath, "Info.plist");
            var plist = new PlistDocument();
            plist.ReadFromFile(plistPath);
            plist.root.SetBoolean("ITSAppUsesNonExemptEncryption", false);
            plist.WriteToFile(plistPath);
        }
    }
}
#endif
