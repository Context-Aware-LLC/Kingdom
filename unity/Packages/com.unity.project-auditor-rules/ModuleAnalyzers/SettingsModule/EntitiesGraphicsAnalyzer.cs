using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using UnityEditor.PackageManager;

namespace Unity.ProjectAuditorRules.SettingsModuleAnalyzers
{
    class EntitiesGraphicsAnalyzer : SettingsModuleAnalyzer
    {
        internal const string PAS1000 = nameof(PAS1000);
        internal const string PAS1013 = nameof(PAS1013);

        // Legacy: The Hybrid Renderer was replaced by Entities Graphics when Entities 0.51 was released in mid-2022.
        static readonly Descriptor k_HybridDescriptor = new Descriptor(
            PAS1000,
            "Player Settings: Static batching is enabled",
            Areas.CPU,
            "<b>Static Batching</b> is enabled in Player Settings and the package com.unity.rendering.hybrid is installed. Static batching is incompatible with the batching techniques used in the Hybrid Renderer and Scriptable Render Pipeline, and will result in poor rendering performance and excessive memory use.",
            "Disable static batching in Player Settings.")
        {
            Fixer = (issue, analysisParams) =>
            {
                UnityEditor.PlayerSettings.SetStaticBatchingForPlatform(analysisParams.Platform, false);
                return true;
            }
        };

        static readonly Descriptor k_EntitiesGraphicsDescriptor = new Descriptor(
            PAS1013,
            "Player Settings: Static batching is enabled",
            Areas.CPU,
            "<b>Static Batching</b> is enabled in Player Settings and the package com.unity.entities.graphics is installed. Static batching is incompatible with the batching techniques used in Entities Graphics and the Scriptable Render Pipeline, and will result in poor rendering performance and excessive memory use.",
            "Disable static batching in Player Settings.")
        {
            Fixer = (issue, analysisParams) =>
            {
                UnityEditor.PlayerSettings.SetStaticBatchingForPlatform(analysisParams.Platform, false);
                return true;
            }
        };

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_HybridDescriptor);
            registerDescriptor(k_EntitiesGraphicsDescriptor);
        }

        public override IEnumerable<ReportItem> Analyze(SettingsAnalysisContext context)
        {

            if (PackageInfo.IsPackageRegistered("com.unity.entities.graphics"))
            {
                if (UnityEditor.PlayerSettings.GetStaticBatchingForPlatform(context.Params.Platform))
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_EntitiesGraphicsDescriptor.Id)
                        .WithLocation("Project/Player");
                }
            }

            if (PackageInfo.IsPackageRegistered("com.unity.rendering.hybrid"))
            {
                if (UnityEditor.PlayerSettings.GetStaticBatchingForPlatform(context.Params.Platform))
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_HybridDescriptor.Id)
                        .WithLocation("Project/Player");
                }
            }
        }
    }
}
