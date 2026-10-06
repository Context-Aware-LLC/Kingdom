using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using UnityEditor;
using UnityEditor.Build;

namespace Unity.ProjectAuditorRules.SettingsModuleAnalyzers
{
    class ManagedStrippingAnalyzer : SettingsModuleAnalyzer
    {
        internal const string PAS0009 = nameof(PAS0009);
        internal const string PAS0025 = nameof(PAS0025);
        internal const string PAS0026 = nameof(PAS0026);

        static readonly Descriptor k_EngineCodeStrippingDescriptor = new Descriptor(
            PAS0009,
            "Player: Engine Code Stripping is disabled",
            Areas.BuildSize,
            "The <b>Strip Engine Code</b> is option in Player Settings is disabled. The generated build will be larger than necessary.",
            "Enable <b>Strip Engine Code</b> in <b>Project Settings > Player > Other Settings > Optimization</b>.")
        {
#if UNITY_6000_4_OR_NEWER
            Platforms = new SerializableEnum<BuildTarget>[] { BuildTarget.Android, BuildTarget.iOS, BuildTarget.WebGL }
#else
            Platforms = new[] { BuildTarget.Android, BuildTarget.iOS, BuildTarget.WebGL }
#endif
        };

        static readonly Descriptor k_AndroidManagedStrippingDescriptor = new Descriptor(
            PAS0025,
            "Player (Android): Managed Code Stripping is set to Disabled, Low or Minimal",
            Areas.BuildSize,
            "The <b>Managed Stripping Level</b> in the Android Player Settings is set to <b>Disabled</b>, <b>Low</b> or <b>Minimal</b>. The generated build will be larger than necessary.",
            "Set <b>Managed Stripping Level</b> in the Android Player Settings to Medium or High.")
        {
#if UNITY_6000_4_OR_NEWER
            Platforms = new SerializableEnum<BuildTarget>[] { BuildTarget.Android }
#else
            Platforms = new[] { BuildTarget.Android }
#endif
        };

        static readonly Descriptor k_iOSManagedStrippingDescriptor = new Descriptor(
            PAS0026,
            "Player (iOS): Managed Code Stripping is set to Disabled, Low or Minimal",
            Areas.BuildSize,
            "The <b>Managed Stripping Level</b> in the iOS Player Settings is set to <b>Disabled</b>, <b>Low</b> or <b>Minimal</b>. The generated build will be larger than necessary.",
            "Set <b>Managed Stripping Level</b> in the iOS Player Settings to Medium or High.")
        {
#if UNITY_6000_4_OR_NEWER
            Platforms = new SerializableEnum<BuildTarget>[] { BuildTarget.iOS }
#else
            Platforms = new[] { BuildTarget.iOS }
#endif
        };

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_EngineCodeStrippingDescriptor);
            registerDescriptor(k_AndroidManagedStrippingDescriptor);
            registerDescriptor(k_iOSManagedStrippingDescriptor);
        }

        public override IEnumerable<ReportItem> Analyze(SettingsAnalysisContext context)
        {
            if (k_EngineCodeStrippingDescriptor.IsSupported(context.Params) && !PlayerSettings.stripEngineCode)
            {
                yield return context.CreateIssue(IssueCategory.ProjectSetting, k_EngineCodeStrippingDescriptor.Id)
                    .WithLocation("Project/Player");
            }

            if (k_AndroidManagedStrippingDescriptor.IsSupported(context.Params))
            {
                var value = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.FromBuildTargetGroup(BuildTargetGroup.Android));
                if (value == ManagedStrippingLevel.Disabled || value == ManagedStrippingLevel.Low || value == ManagedStrippingLevel.Minimal)
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_AndroidManagedStrippingDescriptor.Id)
                        .WithLocation("Project/Player");
                }
            }

            if (k_iOSManagedStrippingDescriptor.IsSupported(context.Params))
            {
                var value = PlayerSettings.GetManagedStrippingLevel(NamedBuildTarget.FromBuildTargetGroup(BuildTargetGroup.iOS));
                if (value == ManagedStrippingLevel.Disabled || value == ManagedStrippingLevel.Low || value == ManagedStrippingLevel.Minimal)
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_iOSManagedStrippingDescriptor.Id)
                        .WithLocation("Project/Player");
                }
            }
        }
    }
}
