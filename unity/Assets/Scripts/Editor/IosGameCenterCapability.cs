#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.iOS.Xcode;

namespace Emberfall.Editor
{
    /// <summary>
    /// Gives the generated Xcode project what Game Center needs: the
    /// <c>com.apple.developer.game-center</c> entitlement on the app, and <c>GameKit.framework</c>
    /// linked into UnityFramework, which is the target <c>Assets/Plugins/iOS/KobiGameCenter.mm</c>
    /// compiles into.
    ///
    /// <para><b>Unity has no Player Settings field for either</b>, which is the only reason they
    /// are written here. Without the entitlement the build still links and still runs —
    /// <c>GKLocalPlayer</c> just never authenticates, and the only trace is one line in the device
    /// log.</para>
    ///
    /// <para><b>An entitlements file that already exists is extended, never replaced.</b> Unity
    /// writes one itself when a capability is switched on in Player Settings, and a build setting
    /// already pointing at it is honoured rather than redirected.</para>
    /// </summary>
    public sealed class IosGameCenterCapability : IPostprocessBuildWithReport
    {
        private const string EntitlementKey = "com.apple.developer.game-center";
        private const string EntitlementsSetting = "CODE_SIGN_ENTITLEMENTS";
        private const string DefaultEntitlementsPath = "Unity-iPhone.entitlements";

        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.iOS) return;

            string buildPath = report.summary.outputPath;
            string projectPath = PBXProject.GetPBXProjectPath(buildPath);
            var project = new PBXProject();
            project.ReadFromFile(projectPath);

            string appTarget = project.GetUnityMainTargetGuid();
            string entitlementsPath = project.GetBuildPropertyForAnyConfig(appTarget, EntitlementsSetting);
            if (string.IsNullOrEmpty(entitlementsPath))
            {
                entitlementsPath = DefaultEntitlementsPath;
                project.AddFile(entitlementsPath, entitlementsPath);
                project.SetBuildProperty(appTarget, EntitlementsSetting, entitlementsPath);
            }

            project.AddFrameworkToProject(project.GetUnityFrameworkTargetGuid(), "GameKit.framework", false);
            project.WriteToFile(projectPath);

            string entitlementsFile = Path.Combine(buildPath, entitlementsPath);
            var entitlements = new PlistDocument();
            if (File.Exists(entitlementsFile)) entitlements.ReadFromFile(entitlementsFile);
            entitlements.root.SetBoolean(EntitlementKey, true);
            entitlements.WriteToFile(entitlementsFile);
        }
    }
}
#endif
