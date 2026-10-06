using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using UnityEditor;
using UnityEditor.Build;

namespace Unity.ProjectAuditorRules.SettingsModuleAnalyzers
{
    class AndroidSdkVersionAnalyzer : SettingsModuleAnalyzer
    {
        internal const string PAS0030 = nameof(PAS0030);

        static readonly Descriptor k_Descriptor_6000_5 = new Descriptor(
            PAS0030,
            "Android: Minimum API Level is below 26",
            Areas.Upgrade,
            "The Android <b>Minimum API Level</b> in Player Settings is below 26. Unity 6.5 raises the minimum supported Android API level to 26, so a project targeting a lower level will not build.",
            "Set <b>Minimum API Level</b> to 26 (Android 8.0) or higher in <b>Project Settings > Player > Android > Other Settings</b>.")
        {
#if UNITY_6000_4_OR_NEWER
            Platforms = new SerializableEnum<BuildTarget>[] { BuildTarget.Android },
#else
            Platforms = new[] { BuildTarget.Android },
#endif
            MessageFormat = "Android Minimum API Level ({0}) is below 26"
        };

        const int k_MinimumSupportedApiLevel_6000_5 = 26;

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_Descriptor_6000_5);
        }

        public override IEnumerable<ReportItem> Analyze(SettingsAnalysisContext context)
        {
            if (k_Descriptor_6000_5.IsSupported(context.Params))
            {
                var minSdk = (int)PlayerSettings.Android.minSdkVersion;
                if (minSdk > 0 && minSdk < k_MinimumSupportedApiLevel_6000_5)
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_Descriptor_6000_5.Id, minSdk)
                        .WithLocation("Project/Player")
                        .WithUpgradeProperties("6000.5", null, null);
                }
            }
        }
    }
}
