using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using Unity.ProjectAuditor.Editor.Modules;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.ProjectAuditorRules.ShadersModuleAnalyzers
{
    // Capability-based BiRP -> URP migration rule: flags a shader that has NO pass the Universal Render
    // Pipeline can render. URP draws only passes whose LightMode tag is in a fixed set (SRPDefaultUnlit /
    // UniversalForward / ...), and only from subshaders it selects (untagged, or tagged
    // RenderPipeline = UniversalPipeline). It reads compiled pass/subshader tags (so it also covers Shader Graphs).
    internal class ShaderUrpRenderabilityAnalyzer : ShaderModuleAnalyzer
    {
        internal const string PAA5003 = nameof(PAA5003);

        // LightMode pass tag values that URP's default (built-in) renderer draws. Includes the LWRP-era
        // "LightweightForward", which URP still draws for backward compatibility. This does NOT cover passes
        // drawn by custom ScriptableRenderPass / Renderer Features, which can draw any LightMode - a shader
        // rendered only that way may still be flagged. Kept as plain strings (not ShaderTagId) so no
        // Shader.TagToID call runs during static initialization, which is not allowed in the context where
        // analyzers are constructed; ShaderTagId is built at analysis time.
        static readonly string[] k_UrpLightModeTagNames =
        {
            "SRPDefaultUnlit",
            "UniversalForward",
            "UniversalForwardOnly",
            "UniversalGBuffer",
            "Universal2D",
            "LightweightForward",
        };

        internal static readonly Descriptor k_UrpRenderabilityDescriptor = new Descriptor
            (
            PAA5003,
            "Shader has no pass that URP can render",
            Areas.MigrationToURP,
            "This shader has no pass that the Universal Render Pipeline's default renderer draws (no UniversalForward or SRPDefaultUnlit LightMode pass in a URP-eligible subshader), so under URP a material that uses it would be drawn with the error (magenta) shader. Passes drawn only by a custom ScriptableRenderPass or Renderer Feature are not considered.",
            "If using URP, convert or rewrite the shader for URP, for example as a Shader Graph or a URP-compatible shader. If the shader is not used with URP, no change is required."
            )
        {
            MessageFormat = "Shader '{0}' has no URP-compatible pass and would render with the URP error shader",
#if UNITY_6000_7_OR_NEWER
            DocumentationUrl = MigrationToURPUtilities.DocumentationUrl
#endif
        };

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_UrpRenderabilityDescriptor);
        }

        public override IEnumerable<ReportItem> Analyze(ShaderAnalysisContext context)
        {
            if (!k_UrpRenderabilityDescriptor.IsSupported(context.Params))
                yield break;

            var shader = context.Shader;
            if (shader == null)
                yield break;

            // Package shaders are upstream / read-only and not the user's migration concern (for example URP's
            // own internal Hidden/* shaders). ShadersModule collects with "t:shader" (no assets-only scope), so
            // Packages/ shaders reach us and must be filtered here.
            if (context.AssetPath.StartsWith("Packages/", StringComparison.Ordinal))
                yield break;

            // A shader that already fails to compile is reported separately as a Shader error, and its pass
            // tags can be unreliable. Skip it here to avoid misleading, duplicate noise.
            if (ShaderUtil.ShaderHasError(shader))
                yield break;

            if (!IsRenderableByUrp(shader))
            {
                yield return context.CreateIssue(IssueCategory.AssetIssue,
                    k_UrpRenderabilityDescriptor.Id, shader.name)
                    .WithLocation(context.AssetPath);
            }
        }

        // True if the subshader URP would select has a pass URP draws. URP renders exactly ONE subshader: the
        // first one (in declaration order) whose RenderPipeline tag is "UniversalPipeline" or absent; a subshader
        // tagged for another pipeline (e.g. HDRenderPipeline) is skipped. URP does not combine passes across
        // subshaders, so only that first selectable subshader decides the result - if it has no URP pass the
        // shader draws as the error shader even when a later subshader would have carried one. Within the selected
        // subshader, an untagged pass (LightMode == none) is treated by URP as SRPDefaultUnlit for backward
        // compatibility, so it counts as renderable. The check reads the shader's declared tags, not the active
        // pipeline (so it works when auditing a Built-in project for URP readiness); it does not model GPU or
        // Shader LOD support, which can also influence which subshader URP finally selects.
        static bool IsRenderableByUrp(Shader shader)
        {
            const string k_LightModeTagName = "LightMode";
            const string k_RenderPipelineTagName = "RenderPipeline";
            const string k_UniversalPipelineTagValue = "UniversalPipeline";

            // Built here (analysis time), not in a static initializer - see k_UrpLightModeTagNames.
            var renderPipelineTag = new ShaderTagId(k_RenderPipelineTagName);
            var lightModeTag = new ShaderTagId(k_LightModeTagName);

            for (int subshaderIndex = 0; subshaderIndex < shader.subshaderCount; subshaderIndex++)
            {
                var renderPipeline = shader.FindSubshaderTagValue(subshaderIndex, renderPipelineTag);
                if (renderPipeline != ShaderTagId.none && renderPipeline.name != k_UniversalPipelineTagValue)
                    continue;

                // First subshader URP can select. URP renders this one and looks no further, so its passes alone
                // decide renderability.
                int passCount = shader.GetPassCountInSubshader(subshaderIndex);
                for (int passIndex = 0; passIndex < passCount; passIndex++)
                {
                    var lightMode = shader.FindPassTagValue(subshaderIndex, passIndex, lightModeTag);

                    // Untagged pass -> SRPDefaultUnlit (legacy) -> URP renders it.
                    if (lightMode == ShaderTagId.none)
                        return true;

                    var lightModeName = lightMode.name;
                    for (int t = 0; t < k_UrpLightModeTagNames.Length; t++)
                    {
                        if (lightModeName == k_UrpLightModeTagNames[t])
                            return true;
                    }
                }

                // The selected subshader has no pass URP draws -> error shader. Do not consider later subshaders.
                return false;
            }

            return false;
        }
    }
}
