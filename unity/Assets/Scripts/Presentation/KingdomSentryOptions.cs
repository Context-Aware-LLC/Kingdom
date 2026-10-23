using Sentry;
using Sentry.Unity;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Every Sentry option this game sets, in code (pre-publication item 1). The SDK loads
    /// <c>Resources/Sentry/SentryOptions.asset</c> at <c>SubsystemRegistration</c>, and that asset's
    /// only job is to point at <c>Resources/Sentry/KingdomSentryOptions.asset</c>, an instance of this
    /// class; <see cref="Configure"/> then overrides whatever the asset holds. So do not tune options in
    /// the Sentry editor window: anything set here wins, and anything not set here is the SDK default.
    ///
    /// <para><b>Also runs at build time</b>, from the SDK's Android and IL2CPP build steps. The empty-DSN
    /// branch is therefore what keeps an unconfigured build from shipping the Android native SDK.</para>
    ///
    /// <para><b>Tags are not set here.</b> The scope does not exist until the SDK starts;
    /// <see cref="CrashReportingScope"/> sets them right after.</para>
    /// </summary>
    public sealed class KingdomSentryOptions : SentryOptionsConfiguration
    {
        public override void Configure(SentryUnityOptions options)
        {
            string dsn = CrashReportingProject.Dsn;
            if (string.IsNullOrWhiteSpace(dsn))
            {
                // Disabled, and quiet about it: Enabled=false makes the SDK skip init with a Debug-level
                // line, and Debug=false means that line is never printed.
                options.Enabled = false;
                options.Dsn = null;
                options.Debug = false;
                return;
            }

            options.Enabled = true;
            options.Dsn = dsn;

            // Editor play mode is not a release anyone ships; its errors would bury the device's.
            options.CaptureInEditor = false;

            // version+versionCode (the store build), e.g. 0.0.14+31 — what the symbols are uploaded
            // under and what the funnel's `build` key carries.
            options.Release = BuildStamp.ReleaseId;
            options.Environment = Debug.isDebugBuild ? "development" : "production";

            // PII: nothing the SDK would add on its own. The IP is also pinned on the scope
            // (CrashReportingScope) so the server has nothing to infer from; the project's
            // "Prevent Storing of IP Addresses" setting is the server-side half.
            options.SendDefaultPii = false;
            options.IsEnvironmentUser = false;
            options.AttachScreenshot = false;
            options.AttachViewHierarchy = false;

            // Native crashes on Android: the Java + NDK layer, initialised at runtime from these
            // options, with the managed scope (tags, user) synced down so a native crash carries it.
            options.AndroidNativeSupportEnabled = true;
            options.NdkIntegrationEnabled = true;
            options.NdkScopeSyncEnabled = true;
            options.AndroidNativeInitializationType = NativeInitializationType.Runtime;
            options.Il2CppLineNumberSupportEnabled = true;

            // Crashes only: no performance tracing.
            options.TracesSampleRate = 0;

            // SDK diagnostics only where a developer is reading logcat.
            options.Debug = Debug.isDebugBuild;
            options.DiagnosticLevel = SentryLevel.Warning;
        }
    }
}
