using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using UnityEditor;

namespace Unity.ProjectAuditorRules.SettingsModuleAnalyzers
{
    class DomainReloadSettingsAnalyzer : SettingsModuleAnalyzer
    {
        internal const string PAS0035 = nameof(PAS0035);
        internal const string PAS0036 = nameof(PAS0036);

        static readonly Descriptor k_DomainReloadDescriptorOld = new Descriptor(
            PAS0035,
            "Editor: Reload Domain on Enter Playmode is enabled",
            Areas.IterationTime,
            "The <b>Reload Domain</b> option In Editor Settings is enabled. If Reload Domain is enabled, the entire script state will be reloaded when entering and exiting Play Mode, and after every code change. This can considerably slow down iteration time.",
            "In Editor Settings, enable the <b>Enter Play Mode Settings > Enter Play Mode Options</b> option, then disable the <b>Reload Domain</b> checkbox. Be sure to fix all the issues in the IterationTime area before disabling domain reload."
        )
        {
            DocumentationUrl = "https://docs.unity3d.com/Manual/code-reloading-editor.html",
            MaximumVersion = "2023.4",
            Fixer = (issue, analysisParams) =>
            {
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions |= EnterPlayModeOptions.DisableDomainReload;
                return true;
            }
        };

        static readonly Descriptor k_DomainReloadDescriptor = new Descriptor(
            PAS0036,
            "Editor: Domain Reload is enabled when entering Play Mode",
            Areas.IterationTime,
            "The <b>When entering play mode</b> setting in Editor Settings is configured to reload the domain. Reloading the domain resets the entire script state every time you enter Play Mode, which can considerably slow down iteration time.",
            "In <b>Project Settings > Editor > Enter Play Mode Settings</b>, set <b>When entering play mode</b> to <b>Reload Scene only</b> (or <b>Do not reload Domain or Scene</b>). Be sure to fix all the issues in the IterationTime area before disabling domain reload."
        )
        {
            DocumentationUrl = "https://docs.unity3d.com/Manual/configurable-enter-play-mode.html",
            MinimumVersion = "6000.0",
            Fixer = (issue, analysisParams) =>
            {
                EditorSettings.enterPlayModeOptionsEnabled = true;
                EditorSettings.enterPlayModeOptions |= EnterPlayModeOptions.DisableDomainReload;
                return true;
            }
        };

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_DomainReloadDescriptorOld);
            registerDescriptor(k_DomainReloadDescriptor);
        }

        public override IEnumerable<ReportItem> Analyze(SettingsAnalysisContext context)
        {
            var domainReloadEnabled =
                !EditorSettings.enterPlayModeOptionsEnabled ||
                ((EditorSettings.enterPlayModeOptions & EnterPlayModeOptions.DisableDomainReload) == 0);

            if (!domainReloadEnabled)
                yield break;

            // Choose appropriate wording based on Unity version.
            if (k_DomainReloadDescriptorOld.IsSupported(context.Params))
            {
                yield return context.CreateIssue(IssueCategory.ProjectSetting, k_DomainReloadDescriptorOld.Id)
                    .WithLocation("Project/Editor");
            }
            if (k_DomainReloadDescriptor.IsSupported(context.Params))
            {
                yield return context.CreateIssue(IssueCategory.ProjectSetting, k_DomainReloadDescriptor.Id)
                    .WithLocation("Project/Editor");
            }
        }
    }
}
