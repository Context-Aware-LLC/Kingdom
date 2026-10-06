using System;
using System.Collections.Generic;
using System.IO;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using UnityEditor.Compilation;

namespace Unity.ProjectAuditorRules.CodeModuleAnalyzers
{
    // A user precompiled assembly built against a target framework that the CoreCLR scripting runtime does
    // not implement may not be compatible with CoreCLR.
    internal class PrecompiledAssemblyTargetFrameworkAnalyzer : CodeModulePrecompiledAssemblyAnalyzer
    {
        internal const string PAC2018 = nameof(PAC2018);

        const string k_NetStandardIdentifier = ".NETStandard";
        const string k_TargetFrameworkVersionMarker = ",Version=v";

        // The CoreCLR scripting runtime implements netstandard2.1. Each .NET Standard version is a strict
        // superset of its predecessors, so an assembly targeting 2.1 or any earlier version resolves
        // against it; only a later .NET Standard than the runtime implements would not.
        static readonly Version k_MaximumNetStandardVersion = new Version(2, 1);

        internal static readonly Descriptor k_TargetFrameworkDescriptor = new Descriptor
            (
            PAC2018,
            "Precompiled assembly not built for .NET Standard",
            Areas.MigrationToCoreCLR,
            "A precompiled managed assembly is built against a target framework the CoreCLR (.NET) scripting runtime does not implement. This assembly may depend on APIs or behaviors that are unavailable after migrating, and might fail to load or run.",
            "Recompile the assembly against <b>netstandard2.1</b>, or obtain a netstandard2.1 build of it from its vendor."
            )
        {
            MessageFormat = "Precompiled assembly '{0}' targets '{1}'",
            DefaultSeverity = Severity.Moderate
        };

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_TargetFrameworkDescriptor);
        }

        public override IEnumerable<ReportItemBuilder> Analyze(PrecompiledAssemblyAnalysisContext context)
        {
            var targetFramework = context.TargetFramework;
            if (string.IsNullOrEmpty(targetFramework) || IsSupportedTargetFramework(targetFramework))
                yield break;

            var assemblyName = Path.GetFileNameWithoutExtension(context.AssemblyPath);

            yield return context.CreateIssue(IssueCategory.Code, k_TargetFrameworkDescriptor.Id, assemblyName, targetFramework)
                .WithCustomProperties(new object[] { assemblyName, string.Empty, false });
        }

        // True for a .NET Standard target the CoreCLR scripting runtime can resolve, that is netstandard2.1
        // or earlier. Any other framework identifier (.NETFramework, .NETCoreApp, ...) is not implemented by
        // the scripting runtime and is reported.
        static bool IsSupportedTargetFramework(string targetFramework)
        {
            // TargetFrameworkAttribute names look like ".NETStandard,Version=v2.1", optionally followed by
            // a ",Profile=..." part.
            var markerIndex = targetFramework.IndexOf(k_TargetFrameworkVersionMarker, StringComparison.Ordinal);
            if (markerIndex < 0)
                return false;

            var identifier = targetFramework.Substring(0, markerIndex);
            if (!identifier.Equals(k_NetStandardIdentifier, StringComparison.OrdinalIgnoreCase))
                return false;

            var versionText = targetFramework.Substring(markerIndex + k_TargetFrameworkVersionMarker.Length);
            var profileIndex = versionText.IndexOf(',');
            if (profileIndex >= 0)
                versionText = versionText.Substring(0, profileIndex);

            if (!Version.TryParse(versionText, out var version))
                return false;

            return new Version(version.Major, version.Minor) <= k_MaximumNetStandardVersion;
        }
    }
}
