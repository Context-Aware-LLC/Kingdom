using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using Unity.ProjectAuditor.Editor.Modules;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.ProjectAuditorRules.SettingsModuleAnalyzers
{
    internal class BuiltinRenderPipelineAnalyzer : SettingsModuleAnalyzer
    {
        static readonly GraphicsTier[] k_GraphicsTiers = { GraphicsTier.Tier1, GraphicsTier.Tier2, GraphicsTier.Tier3 };

        internal const string PAS0022 = nameof(PAS0022);
        internal const string PAS0023 = nameof(PAS0023);
        internal const string PAS0024 = nameof(PAS0024);
        internal const string PAS0040 = nameof(PAS0040);
        internal const string PAS0041 = nameof(PAS0041);

        static readonly Descriptor k_ShaderQualityDescriptor = new Descriptor(
            PAS0022,
            "Graphics: Shader Quality uses a mixture of different values",
            Areas.BuildSize,
            "The current build target Graphics Tier Settings use a mixture of different values (Low/Medium/High) for the <b>Standard Shader Quality</b> setting. This will result in a larger number of shader variants being compiled, which will increase build times and your application's download/install size.",
            "Unless you support devices with a very wide range of capabilities for a particular platform, consider editing the platform in Graphics Settings to use the same shader quality setting across all Graphics Tiers.");

        static readonly Descriptor k_ForwardRenderingDescriptor = new Descriptor(
            PAS0023,
            "Graphics: Rendering Path is set to Forward Rendering",
            Areas.GPU,
            "The current build target uses forward rendering, as set in the <b>Rendering Path</b> settings in <b>Project Settings > Graphics > Tier Settings</b>. This can impact GPU performance in projects with nontrivial numbers of dynamic lights.",
            "This rendering path is suitable for applications with simple rendering and lighting requirements - for instance, 2D applications, or applications which mainly use baked lighting. If the project makes use of more than a few dynamic lights, consider experimenting with changing <b>Rendering Path</b> to Deferred to see whether doing so improves GPU rendering times.");

        static readonly Descriptor k_DeferredRenderingDescriptor = new Descriptor(
            PAS0024,
            "Graphics: Rendering Path is set to Deferred Rendering",
            Areas.GPU,
            "The current build target uses deferred rendering, as set in the <b>Rendering Path</b> settings in <b>Project Settings > Graphics > Tier Settings</b>. This can impact GPU performance in projects with simple rendering requirements.",
            "This rendering path is suitable for applications with more complex rendering requirements - for instance, applications that make use of dynamic lighting or certain types of fullscreen post-processing effects. If the project doesn't make use of such rendering techniques, consider experimenting with changing <b>Rendering Path</b> to Forward to see whether doing so improves GPU rendering times.");

        static readonly Descriptor k_DeprecationDescriptor = new Descriptor(
            PAS0040,
            "Graphics: Project uses the Built-in Render Pipeline",
            Areas.Upgrade | Areas.MigrationToURP,
            "The Built-in Render Pipeline will be removed in Unity 7. Unity recommends that you migrate to the Universal Render Pipeline (URP).",
            "Install the URP package, then use the <b>Render Pipeline Converter</b> (<b>Window > Rendering > Render Pipeline Converter</b>) to convert your assets. Finally, address all reported <b>Migration To URP</b> issues.")
        {
            DefaultSeverity = Severity.Major,
            MaximumVersion = "6000.7",
#if UNITY_6000_7_OR_NEWER
            DocumentationUrl = MigrationToURPUtilities.DocumentationUrl,
            FixerLabel = "Migrate to URP",
            Fixer = MigrationToURPUtilities.OpenRenderPipelineConverter
#endif
        };

        static readonly Descriptor k_RemovalDescriptor = new Descriptor(
            PAS0041,
            "Graphics: Project uses the Built-in Render Pipeline",
            Areas.Upgrade,
            "Unity has removed the Built-in Render Pipeline in Unity 7. Projects that have not migrated to the Universal Render Pipeline (URP) will no longer function.",
            "Install the URP package, then use the <b>Render Pipeline Converter</b> (<b>Window > Rendering > Render Pipeline Converter</b>) to convert your assets. Finally, address all reported <b>Migration To URP</b> issues.")
        {
            DefaultSeverity = Severity.Major,
            MinimumVersion = "7000.0",
#if UNITY_6000_7_OR_NEWER
            DocumentationUrl = MigrationToURPUtilities.DocumentationUrl,
            FixerLabel = "Migrate to URP",
            Fixer = MigrationToURPUtilities.OpenRenderPipelineConverter
#endif
        };

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_ShaderQualityDescriptor);
            registerDescriptor(k_ForwardRenderingDescriptor);
            registerDescriptor(k_DeferredRenderingDescriptor);
            registerDescriptor(k_DeprecationDescriptor);
            registerDescriptor(k_RemovalDescriptor);
        }

        public override IEnumerable<ReportItem> Analyze(SettingsAnalysisContext context)
        {
            if (k_DeprecationDescriptor.IsSupported(context.Params))
            {
                yield return context.CreateIssue(IssueCategory.ProjectSetting, k_DeprecationDescriptor.Id)
                    .WithLocation("Project/Graphics")
                    .WithUpgradeProperties("6000.5", "7000.0", null);
            }
            if (k_RemovalDescriptor.IsSupported(context.Params))
            {
                yield return context.CreateIssue(IssueCategory.ProjectSetting, k_RemovalDescriptor.Id)
                    .WithLocation("Project/Graphics")
                    .WithUpgradeProperties("7000.0", null, null);
            }

            if (IsUsingBuiltinRenderPipeline())
            {
                if (IsMixedStandardShaderQuality(context.Params.Platform))
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_ShaderQualityDescriptor.Id)
                        .WithLocation("Project/Graphics");
                }
                if (IsUsingForwardRendering(context.Params.Platform))
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_ForwardRenderingDescriptor.Id)
                        .WithLocation("Project/Graphics");
                }
                if (IsUsingDeferredRendering(context.Params.Platform))
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_DeferredRenderingDescriptor.Id)
                        .WithLocation("Project/Graphics");
                }
            }
        }

        static bool IsUsingBuiltinRenderPipeline()
        {
            return GraphicsSettings.defaultRenderPipeline == null;
        }

        internal static bool IsMixedStandardShaderQuality(BuildTarget platform)
        {
            var buildGroup = BuildPipeline.GetBuildTargetGroup(platform);

            ShaderQuality? first = null;
            foreach (var tier in k_GraphicsTiers)
            {
                var quality = EditorGraphicsSettings.GetTierSettings(buildGroup, tier).standardShaderQuality;
                if (first == null)
                    first = quality;
                else if (first != quality)
                    return true;
            }

            return false;
        }

        internal static bool IsUsingForwardRendering(BuildTarget platform)
        {
            var buildGroup = BuildPipeline.GetBuildTargetGroup(platform);

            foreach (var tier in k_GraphicsTiers)
            {
                var path = EditorGraphicsSettings.GetTierSettings(buildGroup, tier).renderingPath;
                if (path == RenderingPath.Forward)
                    return true;
            }

            return false;
        }

        internal static bool IsUsingDeferredRendering(BuildTarget platform)
        {
            var buildGroup = BuildPipeline.GetBuildTargetGroup(platform);

            foreach (var tier in k_GraphicsTiers)
            {
                var path = EditorGraphicsSettings.GetTierSettings(buildGroup, tier).renderingPath;
                if (path == RenderingPath.DeferredShading)
                    return true;
            }

            return false;
        }
    }
}
