using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using UnityEditor;
using UnityEngine.Rendering;

namespace Unity.ProjectAuditorRules.SettingsModuleAnalyzers
{
    class GraphicsApiAnalyzer : SettingsModuleAnalyzer
    {
        const string documentationUrl = "https://docs.unity3d.com/Manual/GraphicsAPIs.html";

        internal const string PAS0006 = nameof(PAS0006);
        internal const string PAS0031 = nameof(PAS0031);
        internal const string PAS0037 = nameof(PAS0037);
        internal const string PAS0038 = nameof(PAS0038);
        internal const string PAS0039 = nameof(PAS0039);

        static readonly Descriptor k_MetalDescriptor = new Descriptor(
            PAS0006,
            "Player (iOS): Metal is not the preferred graphics API",
            Areas.CPU | Areas.GPU,
            "In the iOS Player Settings, a lower-priority graphics API is listed ahead of Metal. Unity uses the first graphics API a device supports, so the player runs on the slower API on devices that also support Metal.",
            "Move Metal to the top of the graphics API list in the iOS Player Settings.")
        {
            DocumentationUrl = documentationUrl,
#if UNITY_6000_4_OR_NEWER
            Platforms = new SerializableEnum<BuildTarget>[] { BuildTarget.iOS }
#else
            Platforms = new[] { BuildTarget.iOS }
#endif
        };

        static readonly Descriptor k_VulkanDescriptor = new Descriptor(
            PAS0031,
            "Player (Android): Vulkan is not the preferred graphics API",
            Areas.CPU | Areas.GPU,
            "In the Android Player Settings, a lower-priority graphics API (such as OpenGLES3) is listed ahead of Vulkan, or Vulkan is not in the list at all. Unity uses the first graphics API a device supports, so the player runs on the slower API on devices that also support Vulkan.",
            "Move Vulkan to the top of the graphics API list in the Android Player Settings so that devices which support it use it.")
        {
            DocumentationUrl = documentationUrl,
#if UNITY_6000_4_OR_NEWER
            Platforms = new SerializableEnum<BuildTarget>[] { BuildTarget.Android }
#else
            Platforms = new[] { BuildTarget.Android }
#endif
        };

        static readonly Descriptor k_Direct3D12Descriptor = new Descriptor(
            PAS0037,
            "Player (Windows): Direct3D12 is not the preferred graphics API",
            Areas.CPU | Areas.GPU,
            "In the Windows Player Settings, a lower-priority graphics API (such as Direct3D11 or OpenGLCore) is listed ahead of Direct3D12, or Direct3D12 is not in the list at all. Unity uses the first graphics API a device supports, so the player runs on the older API on machines that also support Direct3D12. Direct3D12 is the default graphics API for new projects and is required to use modern rendering features such as the GPU Resident Drawer and GPU Occlusion Culling.",
            "Move Direct3D12 to the top of the graphics API list in the Windows Player Settings for better performance and access to modern rendering features.")
        {
            DocumentationUrl = documentationUrl,
            MinimumVersion = "6000.1",
#if UNITY_6000_4_OR_NEWER
            Platforms = new SerializableEnum<BuildTarget>[] { BuildTarget.StandaloneWindows, BuildTarget.StandaloneWindows64 }
#else
            Platforms = new[] { BuildTarget.StandaloneWindows, BuildTarget.StandaloneWindows64 }
#endif
        };

        static readonly Descriptor k_VulkanLinuxDescriptor = new Descriptor(
            PAS0038,
            "Player (Linux): Vulkan is not the preferred graphics API",
            Areas.CPU | Areas.GPU,
            "In the Linux Player Settings, a lower-priority graphics API (such as OpenGLCore) is listed ahead of Vulkan, or Vulkan is not in the list at all. Unity uses the first graphics API a device supports, so the player runs on the slower API on machines that also support Vulkan.",
            "Move Vulkan to the top of the graphics API list in the Linux Player Settings.")
        {
            DocumentationUrl = documentationUrl,
#if UNITY_6000_4_OR_NEWER
            Platforms = new SerializableEnum<BuildTarget>[] { BuildTarget.StandaloneLinux64 }
#else
            Platforms = new[] { BuildTarget.StandaloneLinux64 }
#endif
        };

        static readonly Descriptor k_OpenGLCoreDescriptor = new Descriptor(
            PAS0039,
            "Player: OpenGLCore is prioritized ahead of a modern graphics API",
            Areas.CPU | Areas.GPU,
            "On a desktop platform, OpenGLCore is listed ahead of a modern graphics API (Direct3D12, Direct3D11 or Vulkan) in the Player Settings. Unity uses the first graphics API a device supports, so the player runs on OpenGLCore even on machines that support a modern API. OpenGLCore is a legacy graphics API that does not take advantage of modern hardware capabilities.",
            "Move a modern graphics API (Direct3D12 or Direct3D11 on Windows, Vulkan on Linux) ahead of OpenGLCore in the graphics API list.")
        {
            DocumentationUrl = documentationUrl,
#if UNITY_6000_4_OR_NEWER
            Platforms = new SerializableEnum<BuildTarget>[] { BuildTarget.StandaloneWindows, BuildTarget.StandaloneWindows64, BuildTarget.StandaloneLinux64 }
#else
            Platforms = new[] { BuildTarget.StandaloneWindows, BuildTarget.StandaloneWindows64, BuildTarget.StandaloneLinux64 }
#endif
        };

        // Each rule flags a graphics API list where a lower-priority API is ordered ahead of the
        // preferred one(s). Because Unity uses the first API a device supports, an API that only appears
        // behind another API in the list never activates on hardware that supports both.
        static readonly GraphicsApiRule[] k_Rules =
        {
            new GraphicsApiRule(k_MetalDescriptor,
                preferred: new[] { GraphicsDeviceType.Metal },
                lowerPriority: new[] { GraphicsDeviceType.OpenGLES3 }),
            new GraphicsApiRule(k_VulkanDescriptor,
                preferred: new[] { GraphicsDeviceType.Vulkan },
                lowerPriority: new[] { GraphicsDeviceType.OpenGLES3 }),
            new GraphicsApiRule(k_Direct3D12Descriptor,
                preferred: new[] { GraphicsDeviceType.Direct3D12 },
                lowerPriority: new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.OpenGLCore }),
            new GraphicsApiRule(k_VulkanLinuxDescriptor,
                preferred: new[] { GraphicsDeviceType.Vulkan },
                lowerPriority: new[] { GraphicsDeviceType.OpenGLCore }),
            new GraphicsApiRule(k_OpenGLCoreDescriptor,
                preferred: new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Vulkan },
                lowerPriority: new[] { GraphicsDeviceType.OpenGLCore }),
        };

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            foreach (var rule in k_Rules)
                registerDescriptor(rule.Descriptor);
        }

        public override IEnumerable<ReportItem> Analyze(SettingsAnalysisContext context)
        {
            var graphicsApis = PlayerSettings.GetGraphicsAPIs(context.Params.Platform);

            foreach (var rule in k_Rules)
            {
                if (rule.Descriptor.IsSupported(context.Params) &&
                    IsLowerPriorityApiPreferred(graphicsApis, rule.Preferred, rule.LowerPriority))
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, rule.Descriptor.Id)
                        .WithLocation("Project/Player");
                }
            }
        }

        // Returns true if a lower-priority API is ordered ahead of every preferred API (which includes the
        // case where no preferred API is present at all). This mirrors Unity's runtime behaviour of using
        // the first entry in the list that the device supports.
        static bool IsLowerPriorityApiPreferred(GraphicsDeviceType[] graphicsApis, GraphicsDeviceType[] preferred, GraphicsDeviceType[] lowerPriority)
        {
            return FirstIndexOfAny(graphicsApis, lowerPriority) < FirstIndexOfAny(graphicsApis, preferred);
        }

        // Index of the first entry in graphicsApis that is contained in targets, or int.MaxValue if none is.
        static int FirstIndexOfAny(GraphicsDeviceType[] graphicsApis, GraphicsDeviceType[] targets)
        {
            for (var i = 0; i < graphicsApis.Length; i++)
            {
                if (Array.IndexOf(targets, graphicsApis[i]) != -1)
                    return i;
            }

            return int.MaxValue;
        }

        readonly struct GraphicsApiRule
        {
            public readonly Descriptor Descriptor;
            public readonly GraphicsDeviceType[] Preferred;
            public readonly GraphicsDeviceType[] LowerPriority;

            public GraphicsApiRule(Descriptor descriptor, GraphicsDeviceType[] preferred, GraphicsDeviceType[] lowerPriority)
            {
                Descriptor = descriptor;
                Preferred = preferred;
                LowerPriority = lowerPriority;
            }
        }
    }
}
