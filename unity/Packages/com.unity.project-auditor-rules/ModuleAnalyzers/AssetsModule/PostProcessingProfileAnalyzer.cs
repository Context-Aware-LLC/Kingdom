using System;
using System.Collections.Generic;
using System.IO;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using Unity.ProjectAuditor.Editor.Modules;
using UnityEditor;

namespace Unity.ProjectAuditorRules.AssetsModuleAnalyzers
{
    // PPv2 targets the Built-in Render Pipeline; its Profile assets are not compatible with URP's integrated Volume system.
    // Detected by the asset's main type name so no reference to com.unity.postprocessing is required.
    internal class PostProcessingProfileAnalyzer : AssetsModuleAnalyzer
    {
        internal const string PAA3001 = nameof(PAA3001);

        const string k_PostProcessProfileTypeName = "UnityEngine.Rendering.PostProcessing.PostProcessProfile";

        internal static readonly Descriptor k_PostProcessProfileDescriptor = new Descriptor
            (
            PAA3001,
            "Post Processing Stack v2 Profile asset requires the deprecated Built-in Render Pipeline",
            Areas.MigrationToURP,
            "The Post Processing Stack v2 targets the Built-in Render Pipeline, so its Profile assets are not compatible with SRP's such as the Universal Render Pipeline's integrated Volume system.",
            "If using URP, convert the Profile using the Post Processing V2 module in the Render Pipeline Converter, which creates an equivalent URP Volume Profile asset."
            )
        {
            MessageFormat = "Profile asset '{0}' is a Post Processing Stack v2 Profile, which requires the deprecated Built-in Render Pipeline",
#if UNITY_6000_7_OR_NEWER
            DocumentationUrl = MigrationToURPUtilities.DocumentationUrl
#endif
        };

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_PostProcessProfileDescriptor);
        }

        public override IEnumerable<ReportItem> Analyze(AssetAnalysisContext context)
        {
            if (!k_PostProcessProfileDescriptor.IsSupported(context.Params))
                yield break;

            // PostProcessProfile assets are serialized as .asset files.
            if (!context.AssetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                yield break;

            // Detect by the asset's main type name so we don't need to reference com.unity.postprocessing.
            var assetType = AssetDatabase.GetMainAssetTypeAtPath(context.AssetPath);
            if (assetType == null || assetType.FullName != k_PostProcessProfileTypeName)
                yield break;

            yield return context.CreateIssue
            (
                IssueCategory.AssetIssue,
                k_PostProcessProfileDescriptor.Id,
                Path.GetFileName(context.AssetPath)
            )
            .WithLocation(context.AssetPath);
        }
    }
}
