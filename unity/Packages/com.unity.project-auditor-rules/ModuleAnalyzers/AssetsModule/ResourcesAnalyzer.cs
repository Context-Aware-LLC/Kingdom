using System;
using System.Collections.Generic;
using System.IO;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
#if UNITY_6000_6_OR_NEWER
using Unity.ProjectAuditor.Editor.AssetAnalysis;
#endif

namespace Unity.ProjectAuditorRules.AssetsModuleAnalyzers
{
    internal class AssetsModuleResourcesAnalyzer : AssetsModuleAnalyzer
    {
        internal const string PAA3000 = nameof(PAA3000);

        static readonly Descriptor k_AssetInResourcesFolderDescriptor = new Descriptor
            (
            PAA3000,
            "Resources folder asset",
            Areas.BuildSize,
            "The <b>Resources folder</b> is a common source of many problems in Unity projects. Improper use of the Resources folder can bloat the size of a project's build, lead to uncontrollable excessive memory utilization, and significantly increase application startup times.",
            "Use AssetBundles or Addressables when possible."
            )
        {
            DocumentationUrl = "https://docs.unity3d.com/Manual/LoadingResourcesatRuntime.html",
            MessageFormat = "Asset '{0}' is in a Resources folder"
        };

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_AssetInResourcesFolderDescriptor);
        }

        public override IEnumerable<ReportItem> Analyze(AssetAnalysisContext context)
        {
            if (context.AssetPath.IndexOf("/resources/", StringComparison.OrdinalIgnoreCase) < 0)
                yield break;

            if ((File.GetAttributes(context.AssetPath) & FileAttributes.Directory) == FileAttributes.Directory)
                yield break;

            // skip C# scripts
            if (context.AssetPath.EndsWith(".cs", StringComparison.Ordinal))
                yield break;

            var location = new Location(context.AssetPath);
            var dependencyNode = new AssetDependencyNode
            {
                Location = location
            };

            yield return context.CreateIssue
            (
                IssueCategory.AssetIssue,
                k_AssetInResourcesFolderDescriptor.Id,
                Path.GetFileName(context.AssetPath)
            )
            .WithDependencies(dependencyNode)
            .WithLocation(location);
        }
    }
}
