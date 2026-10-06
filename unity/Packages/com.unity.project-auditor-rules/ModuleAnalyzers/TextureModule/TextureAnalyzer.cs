using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using UnityEditor;
using UnityEngine;
#if UNITY_6000_6_OR_NEWER
using Unity.ProjectAuditor.Editor.AssetAnalysis;
#endif

namespace Unity.ProjectAuditorRules.TextureModuleAnalyzers
{
    class TextureAnalyzer : TextureModuleAnalyzer
    {
        internal const string PAA0000 = nameof(PAA0000);
        internal const string PAA0001 = nameof(PAA0001);
        internal const string PAA0002 = nameof(PAA0002);
        internal const string PAA0003 = nameof(PAA0003);
        internal const string PAA0004 = nameof(PAA0004);
        internal const string PAA0009 = nameof(PAA0009);

        internal static readonly Descriptor k_TextureMipmapsNotEnabledDescriptor = new Descriptor(
            PAA0000,
            "Texture: Mipmaps not enabled",
            Areas.GPU | Areas.Quality,
            "<b>Generate Mip Maps</b> in the Texture Import Settings is not enabled. Using textures that are not mipmapped in a 3D environment can impact rendering performance and introduce aliasing artifacts.",
            "Consider enabling mipmaps using the <b>Advanced > Generate Mip Maps</b> option in the Texture Import Settings."
        )
        {
            MessageFormat = "Texture2D '{0}' mipmaps generation is not enabled",
            Fixer = (issue, analysisParams) =>
            {
                var textureImporter = AssetImporter.GetAtPath(issue.RelativePath) as TextureImporter;
                if (textureImporter != null)
                {
                    textureImporter.mipmapEnabled = true;
                    textureImporter.SaveAndReimport();
                    return true;
                }

                return false;
            }
        };

        internal static readonly Descriptor k_TextureMipmapsEnabledDescriptor = new Descriptor(
            PAA0001,
            "Texture: Mipmaps enabled on Sprite/UI texture",
            Areas.BuildSize | Areas.Quality,
            "<b>Generate Mip Maps</b> is enabled in the Texture Import Settings for a Sprite/UI texture. This might reduce rendering quality of sprites and UI.",
            "Consider disabling mipmaps using the <b>Advanced > Generate Mip Maps</b> option in the texture inspector. This will also reduce your build size."
        )
        {
            MessageFormat = "Texture2D '{0}' mipmaps generation is enabled",
            Fixer = (issue, analysisParams) =>
            {
                var textureImporter = AssetImporter.GetAtPath(issue.RelativePath) as TextureImporter;
                if (textureImporter != null)
                {
                    textureImporter.mipmapEnabled = false;
                    textureImporter.SaveAndReimport();
                    return true;
                }

                return false;
            }
        };

        internal static readonly Descriptor k_TextureReadWriteEnabledDescriptor = new Descriptor(
            PAA0002,
            "Texture: Read/Write enabled",
            Areas.Memory,
            "The <b>Read/Write Enabled</b> flag in the Texture Import Settings is enabled. This causes the texture data to be duplicated in memory.",
            "If not required, disable the <b>Read/Write Enabled</b> option in the Texture Import Settings."
        )
        {
            MessageFormat = "Texture2D '{0}' Read/Write is enabled",
            DocumentationUrl = "https://docs.unity3d.com/Manual/class-TextureImporter.html",
            Fixer = (issue, analysisParams) =>
            {
                var textureImporter = AssetImporter.GetAtPath(issue.RelativePath) as TextureImporter;
                if (textureImporter != null)
                {
                    textureImporter.isReadable = false;
                    textureImporter.SaveAndReimport();
                    return true;
                }

                return false;
            }
        };

        internal static readonly Descriptor k_TextureStreamingMipMapEnabledDescriptor = new Descriptor(
            PAA0003,
            "Texture: Mipmaps Streaming not enabled",
            Areas.Memory | Areas.Quality,
            "The <b>Streaming Mipmaps</b> option in the Texture Import Settings is not enabled. As a result, all mip levels for this texture are loaded into GPU memory for as long as the texture is loaded, potentially resulting in excessive texture memory usage.",
            "Consider enabling the <b>Streaming Mipmaps</b> option in the Texture Import Settings."
        )
        {
            MessageFormat = "Texture2D '{0}' mipmaps streaming is not enabled",
            Fixer = (issue, analysisParams) =>
            {
                var textureImporter = AssetImporter.GetAtPath(issue.RelativePath) as TextureImporter;
                if (textureImporter != null)
                {
                    textureImporter.streamingMipmaps = true;
                    textureImporter.SaveAndReimport();
                    return true;
                }

                return false;
            }
        };

        internal static readonly Descriptor k_TextureAnisotropicLevelDescriptor = new Descriptor(
            PAA0004,
            "Texture: Anisotropic level is higher than 1",
            Areas.GPU | Areas.Quality,
            "The <b>Anisotropic Level</b> in the Texture Import Settings is higher than 1. Anisotropic filtering makes textures look better when viewed at a shallow angle, but it can be slower to process on the GPU.",
            "Consider setting the <b>Anisotropic Level</b> to 1."
        )
        {
#if UNITY_6000_4_OR_NEWER
            Platforms = new SerializableEnum<BuildTarget>[] { BuildTarget.Android, BuildTarget.iOS, BuildTarget.Switch },
#else
            Platforms = new[] { BuildTarget.Android, BuildTarget.iOS, BuildTarget.Switch },
#endif
            MessageFormat = "Texture2D '{0}' anisotropic level is set to '{1}'",
            Fixer = (issue, analysisParams) =>
            {
                var textureImporter = AssetImporter.GetAtPath(issue.RelativePath) as TextureImporter;
                if (textureImporter != null)
                {
                    textureImporter.anisoLevel = 1;
                    textureImporter.SaveAndReimport();
                    return true;
                }

                return false;
            }
        };

        static readonly Descriptor k_TexturePVRTCDescriptor = new Descriptor(
            PAA0009,
            "Texture: Deprecated PVRTC compression format",
            Areas.Upgrade,
            "The texture uses a PVRTC compression format. PVRTC texture compression is deprecated from Unity 6.1.",
            "Change the texture's compression format to ASTC (preferred) or ETC in the texture's platform import settings.")
        {
#if UNITY_6000_4_OR_NEWER
            Platforms = new SerializableEnum<BuildTarget>[] { BuildTarget.iOS },
#else
            Platforms = new[] { BuildTarget.iOS },
#endif
            MessageFormat = "Texture '{0}' uses deprecated PVRTC compression ({1})",
            Fixer = (issue, analysisParams) =>
            {
                var textureImporter = AssetImporter.GetAtPath(issue.RelativePath) as TextureImporter;
                if (textureImporter == null)
                    return false;

                var platform = analysisParams.Platform.ToString();
                var platformSettings = textureImporter.GetPlatformTextureSettings(platform);
                if (!IsPVRTCFormat(platformSettings.format))
                    return false;

                platformSettings.overridden = true;
                platformSettings.format = GetASTCReplacementFormat(platformSettings.format);
                textureImporter.SetPlatformTextureSettings(platformSettings);
                textureImporter.SaveAndReimport();
                return true;
            }
        };

#pragma warning disable CS0649
        [DiagnosticParameter("TextureStreamingMipmapsSizeLimit", "Maximum non-streaming Texture size (pixels)", "If a texture is larger than this limit and not setup for streaming then an Issue will be created.  Note: we square the threshold and compare it to the (width * height) of the texture.", 4000)]
        int m_StreamingMipmapsSizeLimit;

        // [DiagnosticParameter("TextureSizeLimit", 2048)]
        // int m_SizeLimit;
#pragma warning restore CS0649

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_TextureMipmapsNotEnabledDescriptor);
            registerDescriptor(k_TextureMipmapsEnabledDescriptor);
            registerDescriptor(k_TextureReadWriteEnabledDescriptor);
            registerDescriptor(k_TextureStreamingMipMapEnabledDescriptor);
            registerDescriptor(k_TextureAnisotropicLevelDescriptor);
            registerDescriptor(k_TexturePVRTCDescriptor);
        }

        public override IEnumerable<ReportItem> Analyze(TextureAnalysisContext context)
        {
            var location = new Location(context.Importer.assetPath);
#if UNITY_6000_6_OR_NEWER
            var dependencyNode = new TextureDependencyNode { Location = location };
#endif

            if (!context.Importer.mipmapEnabled && context.Importer.textureType == TextureImporterType.Default)
            {
                yield return context.CreateIssue(IssueCategory.AssetIssue,
                    k_TextureMipmapsNotEnabledDescriptor.Id, context.Name)
#if UNITY_6000_6_OR_NEWER
                    .WithDependencies(dependencyNode)
#endif
                    .WithLocation(location);
            }

            if (context.Importer.mipmapEnabled &&
                (context.Importer.textureType == TextureImporterType.Sprite || context.Importer.textureType == TextureImporterType.GUI)
            )
            {
                yield return context.CreateIssue(IssueCategory.AssetIssue,
                    k_TextureMipmapsEnabledDescriptor.Id, context.Name)
#if UNITY_6000_6_OR_NEWER
                    .WithDependencies(dependencyNode)
#endif
                    .WithLocation(location);
            }

            if (context.Importer.isReadable)
            {
                yield return context.CreateIssue(IssueCategory.AssetIssue, k_TextureReadWriteEnabledDescriptor.Id, context.Name)
#if UNITY_6000_6_OR_NEWER
                    .WithDependencies(dependencyNode)
#endif
                    .WithLocation(location);
            }

            if (context.Importer.mipmapEnabled && !context.Importer.streamingMipmaps && context.Texture.width * context.Texture.height > Mathf.Pow(m_StreamingMipmapsSizeLimit, 2))
            {
                yield return context.CreateIssue(IssueCategory.AssetIssue, k_TextureStreamingMipMapEnabledDescriptor.Id, context.Name)
#if UNITY_6000_6_OR_NEWER
                    .WithDependencies(dependencyNode)
#endif
                    .WithLocation(location);
            }

            if (k_TextureAnisotropicLevelDescriptor.IsSupported(context.Params) &&
                context.Importer.mipmapEnabled && context.Importer.filterMode != FilterMode.Point && context.Importer.anisoLevel > 1)
            {
                yield return context.CreateIssue(IssueCategory.AssetIssue, k_TextureAnisotropicLevelDescriptor.Id, context.Name, context.Importer.anisoLevel)
#if UNITY_6000_6_OR_NEWER
                    .WithDependencies(dependencyNode)
#endif
                    .WithLocation(location);
            }

            if (k_TexturePVRTCDescriptor.IsSupported(context.Params))
            {
                if (context.ImporterPlatformSettings != null)
                {
                    var format = context.ImporterPlatformSettings.format;

                    if (IsPVRTCFormat(format))
                    {
                        yield return context.CreateIssue(IssueCategory.AssetIssue, k_TexturePVRTCDescriptor.Id, context.Name, format.ToString())
#if UNITY_6000_6_OR_NEWER
                            .WithDependencies(dependencyNode)
#endif
                            .WithLocation(location)
                            .WithUpgradeProperties("6000.1", null, null);
                    }
                }
            }
        }

        static bool IsPVRTCFormat(TextureImporterFormat format)
        {
            // Match any PVRTC enum member
            return format.ToString().IndexOf("PVRTC", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static TextureImporterFormat GetASTCReplacementFormat(TextureImporterFormat format)
        {
#pragma warning disable CS0618
            switch (format)
            {
                case TextureImporterFormat.PVRTC_RGB2:
                case TextureImporterFormat.PVRTC_RGBA2:
                    return TextureImporterFormat.ASTC_8x8; // 2 bits per pixel
                default:
                    return TextureImporterFormat.ASTC_6x6; // ~3.6 bits per pixel, replacing 4 bits per pixel PVRTC
            }
#pragma warning restore CS0618
        }
    }
}
