using System;
using Kingdom.Presentation;
using Sentry.Unity;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Kingdom.Editor
{
    /// <summary>
    /// Debug-symbol upload for the Sentry SDK (pre-publication item 1): the IL2CPP ARM64 symbols are
    /// uploaded by the Gradle build the SDK patches, through <c>sentry-cli</c>. The SDK loads
    /// <c>Assets/Plugins/Sentry/SentryCliOptions.asset</c>, whose only job is to point at
    /// <c>KingdomSentryCliOptions.asset</c> beside it, an instance of this class.
    ///
    /// <para><b>The auth token comes from the environment and nowhere else.</b> Set
    /// <see cref="AuthTokenVariable"/> before launching the Editor or the build script. It is a secret
    /// (it can write to the Sentry organisation); it is never committed and never typed into the Sentry
    /// window. With no token — or no DSN or project in <see cref="CrashReportingProject"/> — upload is
    /// switched off before the SDK validates it, so a local build neither fails nor warns. Development
    /// builds upload too, because that is the build the dev crash trigger runs on.</para>
    /// </summary>
    public sealed class KingdomSentryCliOptions : SentryCliOptionsConfiguration
    {
        public const string AuthTokenVariable = "SENTRY_AUTH_TOKEN";

        public override void Configure(SentryCliOptions cliOptions)
        {
            string token = Environment.GetEnvironmentVariable(AuthTokenVariable);
            bool upload = !string.IsNullOrWhiteSpace(token)
                          && !string.IsNullOrWhiteSpace(CrashReportingProject.Dsn)
                          && !string.IsNullOrWhiteSpace(CrashReportingProject.Project);

            cliOptions.UploadSymbols = upload;
            cliOptions.UploadDevelopmentSymbols = upload;
            cliOptions.UploadSources = false;
            cliOptions.IgnoreCliErrors = false;
            cliOptions.Auth = upload ? token : null;
            cliOptions.Organization = CrashReportingProject.Organization;
            cliOptions.Project = CrashReportingProject.Project;
        }
    }

    /// <summary>
    /// Takes the token back out of the in-memory options asset once a build is done. The SDK writes
    /// it to the asset INSTANCE, and anything that later dirties and saves that asset (the Sentry
    /// window does, on any edit) would write the secret into a tracked file.
    /// </summary>
    internal sealed class KingdomSentryCliTokenScrub : IPostprocessBuildWithReport
    {
        public int callbackOrder => int.MaxValue;

        public void OnPostprocessBuild(BuildReport report)
        {
            var cliOptions = AssetDatabase.LoadAssetAtPath<SentryCliOptions>("Assets/Plugins/Sentry/SentryCliOptions.asset");
            if (cliOptions != null) cliOptions.Auth = null;
        }
    }
}
