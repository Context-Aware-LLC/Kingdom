using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using Unity.ProjectAuditor.Editor.Modules;

namespace Unity.ProjectAuditorRules.PackagesModuleAnalyzers
{
    // com.unity.postprocessing doenot work with URP.
    internal class PackagesRenderPipelineAnalyzer : PackagesModuleAnalyzer
    {
        internal const string PAP0005 = nameof(PAP0005);

        const string k_PostProcessingV2PackageName = "com.unity.postprocessing";

        internal static readonly Descriptor k_PostProcessingV2Descriptor = new Descriptor
            (
            PAP0005,
            "Post Processing Stack v2 package is not supported by URP",
            Areas.MigrationToURP,
            "The Post Processing Stack v2 package (com.unity.postprocessing) targets the Built-in Render Pipeline. The Universal Render Pipeline has its own integrated post-processing system, so PPv2 profiles, volumes and layers will not work after migrating from the Built-in Render Pipeline.",
            "Convert PPv2 profiles using the Post Processing V2 module in the Render Pipeline Converter, then remove the com.unity.postprocessing package."
            )
        {
#if UNITY_6000_7_OR_NEWER
            DocumentationUrl = MigrationToURPUtilities.DocumentationUrl,
# endif
            MessageFormat = "Package '{0}' (Post Processing Stack v2) is not supported by URP"
        };

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_PostProcessingV2Descriptor);
        }

        public override IEnumerable<ReportItem> Analyze(PackageAnalysisContext context)
        {
            if (k_PostProcessingV2Descriptor.IsSupported(context.Params))
            {
                var package = context.PackageInfo;
                if (package.name == k_PostProcessingV2PackageName)
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_PostProcessingV2Descriptor.Id, package.name)
                        .WithLocation(package.assetPath);
                }
            }
        }
    }
}
