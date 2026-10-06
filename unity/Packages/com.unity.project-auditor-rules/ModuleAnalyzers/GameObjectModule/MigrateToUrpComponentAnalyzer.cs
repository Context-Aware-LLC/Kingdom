#if UNITY_6000_5_OR_NEWER

using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using Unity.ProjectAuditor.Editor.Modules;

namespace Unity.ProjectAuditorRules.GameObjectModuleAnalyzers
{
    // Flags components (eg PostProcessVolume, PostProcessLayer) that only work with BiRP.
    internal class MigrateToUrpComponentAnalyzer : GameObjectModuleAnalyzer
    {
        internal const string PAA6019 = nameof(PAA6019);
        internal const string PAA6020 = nameof(PAA6020);

        internal static readonly Descriptor k_PostProcessingComponentDescriptor = new Descriptor
            (
            PAA6019,
            "Post Processing Stack v2 component is not supported by URP",
            Areas.MigrationToURP,
            "Post Processing Stack v2 components (PostProcessVolume, PostProcessLayer) target the Built-in Render Pipeline and are not supported by the Universal Render Pipeline, which has its own integrated post-processing system.",
            "Recreate the post-processing setup using URP's Volume framework, enable post-processing on the URP Camera, then remove the Post Processing Stack v2 components."
            )
        {
#if UNITY_6000_7_OR_NEWER
            DocumentationUrl = MigrationToURPUtilities.DocumentationUrl,
# endif
            MessageFormat = "Post Processing Stack v2 component '{0}' on '{1}' is not supported by URP"
        };

        static readonly Descriptor k_MigrateComponentIssueDescriptor = new Descriptor
            (
            PAA6020,
            "Component is not supported by URP",
            Areas.MigrationToURP,
            "Component is only supported by the Built-in Rendering Pipeline and is not supported by the Universal Render Pipeline.",
            "Replace the legacy Component with the URP alternative."
            )
        {
#if UNITY_6000_7_OR_NEWER
            DocumentationUrl = MigrationToURPUtilities.DocumentationUrl,
#endif
            MessageFormat = "Legacy '{0}' component on '{1}' is not supported by URP and must be replaced. Migrate to '{2}' instead.",
        };

        static readonly string[] k_PostProcessingComponentNames =
        {
            "PostProcessVolume",
            "PostProcessLayer"
        };

        // Copied from SRPReplacementComponentAttribute usages. There are unlikely to be any new ones, so we are copying the current set.
        static readonly Dictionary<string, string> k_MigrationTypes = new Dictionary<string, string>()
        {
            { "LensFlare", "Lens Flare SRP" },
            { "Halo", "Lens Flare SRP" },
            { "Projector", "Decal Projector SRP" },
            { "LightProbeProxyVolume", "Adaptive Probe Volume" }
        };

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_PostProcessingComponentDescriptor);
            registerDescriptor(k_MigrateComponentIssueDescriptor);
        }

        public override IEnumerable<ReportItemBuilder> Analyze(GameObjectAnalysisContext context)
        {
            var gameObject = context.GameObject;

            if (k_PostProcessingComponentDescriptor.IsSupported(context.Params))
            {
                foreach (var componentName in k_PostProcessingComponentNames)
                {
                    if (gameObject.GetComponent(componentName) != null)
                    {
                        yield return context.CreateIssue
                        (
                            IssueCategory.GameObject,
                            k_PostProcessingComponentDescriptor.Id,
                            componentName,
                            gameObject.name
                        );
                    }
                }
            }

            if (k_MigrateComponentIssueDescriptor.IsSupported(context.Params))
            {
                foreach (var migration in k_MigrationTypes)
                {
                    if (gameObject.GetComponent(migration.Key) != null)
                    {
                          yield return context.CreateIssue
                          (
                              IssueCategory.GameObject,
                              k_MigrateComponentIssueDescriptor.Id,
                              migration.Key,
                              gameObject.name,
                              migration.Value
                          );
                    }
                }
            }
        }
    }
}

#endif
