#if UNITY_6000_7_OR_NEWER || PROJECT_AUDITOR_3_1_1_IS_INSTALLED
#define SUPPORTS_PACKAGE_DB
#endif

using System;
using System.Collections.Generic;
using Unity.ProjectAuditor.Editor;
using Unity.ProjectAuditor.Editor.Core;
using Unity.ProjectAuditor.Editor.Modules;
using UnityEditor.PackageManager;
using UnityEditorInternal;

namespace Unity.ProjectAuditorRules.PackagesModuleAnalyzers
{
    internal class PackagesAnalyzer : PackagesModuleAnalyzer
    {
        internal const string PAP0001 = nameof(PAP0001);
        internal const string PAP0002 = nameof(PAP0002);
        internal const string PAP0003 = nameof(PAP0003);
        internal const string PAP0004 = nameof(PAP0004);
        internal const string PAP0006 = nameof(PAP0006);
        internal const string PAP0007 = nameof(PAP0007);
        internal const string PAP0008 = nameof(PAP0008);
        internal const string PAP0009 = nameof(PAP0009);
        internal const string PAP0010 = nameof(PAP0010);

        static readonly Descriptor k_RecommendPackageUpgrade = new Descriptor(
            PAP0001,
            "Newer recommended package version",
            Areas.Upgrade,
            "A newer recommended version of this package is available.",
            "Update the package via Package Manager."
        )
        {
            MessageFormat = "Package '{0}' could be updated from version '{1}' to '{2}'",
            DefaultSeverity = Severity.Minor
        };

        static readonly Descriptor k_RecommendPackagePreview = new Descriptor(
            PAP0002,
            "Experimental/Preview packages",
            Areas.Quality,
            "Experimental or Preview packages are in the early stages of development and not yet ready for production.",
            "Experimental packages should only be used for testing purposes and to give feedback to Unity."
        )
        {
            MessageFormat = "Package '{0}' version '{1}' is a preview/experimental version"
        };

        static readonly Descriptor k_RecommendPackageDowngrade = new Descriptor(
            PAP0003,
            "Older recommended package version",
            Areas.Upgrade,
            "An older package is the default for this version of Unity.",
            "Downgrade the package via Package Manager."
        )
        {
            MessageFormat = "Package '{0}' could be downgraded from version '{1}' to '{2}'",
            DefaultSeverity = Severity.Minor
        };

        internal static readonly Descriptor k_ModifiedPackageDescriptor = new Descriptor(
            PAP0004,
            "Modified Package",
            Areas.Quality | Areas.Upgrade,
            "Using modified versions of Unity packages prevents easy updates to newer versions. Unity expects to be able to update these packages in lockstep with Editor versions. The modified version may not be compatible with a newer version of Unity.",
            "Consider whether the package really needs to be customized."
            )
        {
            MessageFormat = "Using modified package '{0}'",
            DefaultSeverity = Severity.Major
        };

        internal static readonly Descriptor k_PackageUpdatedOnUpgrade = new Descriptor(
            PAP0006,
            "Package updated on Editor upgrade",
            Areas.Upgrade,
            "When you upgrade to a newer version of the Unity Editor, this package will be updated to the version that ships with the new Editor.",
            "Review the package's changelog for breaking changes before upgrading the Editor."
            )
        {
            MessageFormat = "Package '{0}' will be updated from version '{1}' to '{2}' when you upgrade to Unity {3}",
            DefaultSeverity = Severity.Minor
        };

        internal static readonly Descriptor k_PackageRemovedOnUpgrade = new Descriptor(
            PAP0007,
            "Package removed on Editor upgrade",
            Areas.Upgrade,
            "When you upgrade to a newer version of the Unity Editor, this package will no longer ship with the Editor.",
            "Find an alternative package, or remove your dependency on this package, before upgrading the Editor."
            )
        {
            MessageFormat = "Package '{0}' will be removed when you upgrade to Unity {1}",
            DefaultSeverity = Severity.Major
        };

        internal static readonly Descriptor k_PackageDeprecatedOnUpgrade = new Descriptor(
            PAP0008,
            "Package deprecated on Editor upgrade",
            Areas.Upgrade,
            "When you upgrade to a newer version of the Unity Editor, this package will be deprecated and no longer supported.",
            "Find an alternative package, or remove your dependency on this package, before upgrading the Editor."
            )
        {
            MessageFormat = "Package '{0}' will be deprecated when you upgrade to Unity {1}",
            DefaultSeverity = Severity.Major
        };
		
        internal static readonly Descriptor k_PackageAutoRemovedOnUpgrade = new Descriptor(
            PAP0009,
            "Package automatically removed on Editor upgrade",
            Areas.Upgrade,
            "When you upgrade to a newer version of the Unity Editor, this package dependency will be automatically removed from Packages/manifest.json.",
            "Verify whether your project depends on features from this package. Check the Unity Upgrade Guide to see if the functionality was integrated into the Editor, replaced, or discontinued."
            )
        {
            MessageFormat = "Package '{0}' will be automatically removed from your project manifest when you upgrade to Unity {1}",
            DefaultSeverity = Severity.Minor
        };

        internal static readonly Descriptor k_PackageDeprecated = new Descriptor(
            PAP0010,
            "Deprecated package",
            Areas.Quality | Areas.Upgrade,
            "This package is deprecated and no longer supported.",
            "Find an alternative package, or remove your dependency on this package."
            )
        {
            MessageFormat = "Package '{0}' is deprecated",
            DefaultSeverity = Severity.Major
        };

        public override void Initialize(Action<Descriptor> registerDescriptor)
        {
            registerDescriptor(k_RecommendPackageUpgrade);
            registerDescriptor(k_RecommendPackagePreview);
            registerDescriptor(k_RecommendPackageDowngrade);
            registerDescriptor(k_ModifiedPackageDescriptor);
#if SUPPORTS_PACKAGE_DB
            registerDescriptor(k_PackageUpdatedOnUpgrade);
            registerDescriptor(k_PackageRemovedOnUpgrade);
            registerDescriptor(k_PackageDeprecatedOnUpgrade);
            registerDescriptor(k_PackageAutoRemovedOnUpgrade);
            registerDescriptor(k_PackageDeprecated);
#endif
        }

        public override IEnumerable<ReportItem> Analyze(PackageAnalysisContext context)
        {
            var package = context.PackageInfo;

            // first check if any package is preview or experimental
            if (package.version.Contains("pre") || package.version.Contains("exp"))
            {
                yield return context.CreateIssue(IssueCategory.ProjectSetting, k_RecommendPackagePreview.Id, package.name, package.version)
                    .WithLocation(package.assetPath);
            }

            // if not preview or experimental, check anyway if there is a recommended version available
            var recommendedVersionString = package.versions.recommended;
            if (!string.IsNullOrEmpty(package.version) && !string.IsNullOrEmpty(recommendedVersionString))
            {
                if (!recommendedVersionString.Equals(package.version))
                {
                    var version = InternalEditorUtility.GetUnityVersion();
                    var versionString = $"{version.Major}.{version.Minor}";

                    int comparison = CompareVersions(package.version, recommendedVersionString);
                    if (comparison < 0)
                    {
                        yield return context.CreateIssue(IssueCategory.ProjectSetting, k_RecommendPackageUpgrade.Id, package.name, package.version, recommendedVersionString)
                            .WithLocation(package.assetPath)
                            .WithUpgradeProperties(versionString, null, null);
                    }
                    else if (comparison > 0)
                    {
                        yield return context.CreateIssue(IssueCategory.ProjectSetting, k_RecommendPackageDowngrade.Id, package.name, package.version, recommendedVersionString)
                            .WithLocation(package.assetPath)
                            .WithUpgradeProperties(versionString, null, null);
                    }
                }
            }

            // custom/modified packages are high risk for upgrades because Unity expects to update them in lockstep with Editor versions
            if (package.source == PackageSource.Embedded || package.source == PackageSource.Local || package.source == PackageSource.LocalTarball)
            {
                // Modified package (Local but exists on Registry)
                if (package.versions != null && !string.IsNullOrEmpty(package.versions.latest) && ProjectAuditor.Editor.ProjectAuditor.KnownUnityVersions.Count > 1)
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_ModifiedPackageDescriptor.Id, package.name)
                        .WithLocation(package.assetPath)
                        .WithUpgradeProperties(ProjectAuditor.Editor.ProjectAuditor.KnownUnityVersions[1], null, null); // KnownUnityVersions[1] is the first future version (assuming the forked package is ok in the current version, otherwise user simply has compile errors to fix and doesn't need this issue reporting on top of those)
                }
                // Custom package (Local and unknown to Registry, no higher risk than normal project code)
            }

            // report how this package changes across future Unity Editor versions
            foreach (var issue in EnumerateUpgradeManifestChanges(context, package))
                yield return issue;
        }
		
		static IEnumerable<ReportItem> EnumerateUpgradeManifestChanges(PackageAnalysisContext context, PackageInfo package)
        {
#if SUPPORTS_PACKAGE_DB
            var manifestDatabase = context.ManifestDatabase;
            if (manifestDatabase == null)
                yield break;

            // Only some types of package are automatically upgraded.
            if (package.source != PackageSource.Registry && package.source != PackageSource.BuiltIn)
                yield break;

            // Collect the package's state in every future Unity version we have manifest data for.
            var states = new List<(string unityVersion, bool removed, bool removedOnProjectUpgrade, bool deprecated, string requiredVersion)>();
            var knownUnityVersions = ProjectAuditor.Editor.ProjectAuditor.KnownUnityVersions;

            // Check for packages that are already deprecated. (See k_PackageDeprecatedOnUpgrade for the future deprecation detection)
            var isCurrentlyDeprecated = knownUnityVersions.Count > 0 &&
                manifestDatabase.TryGetManifestInfo(knownUnityVersions[0], package.name, out var currentManifestInfo) &&
                currentManifestInfo.Status == PackageManifestDatabase.PackageManifestStatus.Deprecated;
            if (isCurrentlyDeprecated)
            {
                yield return context.CreateIssue(IssueCategory.ProjectSetting, k_PackageDeprecated.Id, package.name)
                    .WithLocation(package.assetPath)
                    .WithUpgradeProperties(knownUnityVersions[0], null, null);
            }

            for (var i = 1; i < knownUnityVersions.Count; i++) // Start at the first future version
            {
                var unityVersion = knownUnityVersions[i];
                if (!manifestDatabase.TryGetManifestInfo(unityVersion, package.name, out var manifestInfo))
                    continue;

                if (manifestInfo.RemovedOnProjectUpgrade)
                {
                    states.Add((unityVersion, false, true, false, null));
                }
                else if (manifestInfo.Status == PackageManifestDatabase.PackageManifestStatus.Removed)
                {
                    states.Add((unityVersion, true, false, false, null));
                }
                else if (manifestInfo.Status == PackageManifestDatabase.PackageManifestStatus.Deprecated)
                {
                    states.Add((unityVersion, false, false, true, null));
                }
                else
                {
                    var requiredVersion = string.IsNullOrEmpty(manifestInfo.MinimumVersion) ? manifestInfo.Version : manifestInfo.MinimumVersion;
                    if (!string.IsNullOrEmpty(requiredVersion))
                        states.Add((unityVersion, false, false, false, requiredVersion));
                }
            }

            // Group consecutive versions that share the same state into a [min, maxExclusive) range and
            // report the ranges whose state differs from the currently installed package.
            var index = 0;
            while (index < states.Count)
            {
                var state = states[index];

                var next = index + 1;
                while (next < states.Count && states[next].removed == state.removed &&
                       states[next].removedOnProjectUpgrade == state.removedOnProjectUpgrade &&
                       states[next].deprecated == state.deprecated && states[next].requiredVersion == state.requiredVersion)
                    next++;

                var minVersion = state.unityVersion;
                var maxExclusiveVersion = next < states.Count ? states[next].unityVersion : null;

                if (state.removedOnProjectUpgrade)
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_PackageAutoRemovedOnUpgrade.Id, package.name, minVersion)
                        .WithLocation(package.assetPath)
                        .WithUpgradeProperties(minVersion, maxExclusiveVersion, null);
                }
                else if (state.removed)
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_PackageRemovedOnUpgrade.Id, package.name, minVersion)
                        .WithLocation(package.assetPath)
                        .WithUpgradeProperties(minVersion, maxExclusiveVersion, null);
                }
                else if (state.deprecated)
                {
                    // Consecutive equal states are merged above, so a group at index > 0 is always a genuine
                    // transition from non-deprecated. Only the first group needs to be checked against the
                    // currently installed version's baseline, since it may just be a continuation of that.
                    if (index > 0 || !isCurrentlyDeprecated)
                    {
                        yield return context.CreateIssue(IssueCategory.ProjectSetting, k_PackageDeprecatedOnUpgrade.Id, package.name, minVersion)
                            .WithLocation(package.assetPath)
                            .WithUpgradeProperties(minVersion, maxExclusiveVersion, null);
                    }
                }
                else if (CompareVersions(state.requiredVersion, package.version) > 0)
                {
                    yield return context.CreateIssue(IssueCategory.ProjectSetting, k_PackageUpdatedOnUpgrade.Id, package.name, package.version, state.requiredVersion, minVersion)
                        .WithLocation(package.assetPath)
                        .WithUpgradeProperties(minVersion, maxExclusiveVersion, null);
                }

                index = next;
            }
#else
            yield break;
#endif
        }

        // Compares two package version strings using Semantic Versioning precedence rules
        // (https://semver.org, §11). Crucially, a pre-release version (e.g. "1.0.3-pre.1") has LOWER
        // precedence than its associated release ("1.0.3").
        // Returns -1 if lhs has lower precedence than rhs, 1 if higher, 0 if they are equal.
        internal static int CompareVersions(string lhs, string rhs)
        {
            SplitVersion(lhs, out var leftCore, out var leftPreRelease);
            SplitVersion(rhs, out var rightCore, out var rightPreRelease);

            var coreComparison = CompareCoreVersions(leftCore, rightCore);
            if (coreComparison != 0)
                return Math.Sign(coreComparison);

            // Equal core versions: a release outranks a pre-release of the same version.
            var leftHasPreRelease = leftPreRelease.Length > 0;
            var rightHasPreRelease = rightPreRelease.Length > 0;
            if (!leftHasPreRelease && !rightHasPreRelease)
                return 0;
            if (!leftHasPreRelease)
                return 1;
            if (!rightHasPreRelease)
                return -1;

            return Math.Sign(ComparePreRelease(leftPreRelease, rightPreRelease));
        }

        static void SplitVersion(string version, out string core, out string preRelease)
        {
            version = (version ?? string.Empty).Trim();

            // Build metadata ("+...") does not affect precedence, so discard it.
            var plusIndex = version.IndexOf('+');
            if (plusIndex >= 0)
                version = version.Substring(0, plusIndex);

            var dashIndex = version.IndexOf('-');
            if (dashIndex >= 0)
            {
                core = version.Substring(0, dashIndex);
                preRelease = version.Substring(dashIndex + 1);
            }
            else
            {
                core = version;
                preRelease = string.Empty;
            }
        }

        static int CompareCoreVersions(string leftCore, string rightCore)
        {
            var left = leftCore.Split('.');
            var right = rightCore.Split('.');
            var count = Math.Max(left.Length, right.Length);
            for (var i = 0; i < count; i++)
            {
                var leftValue = i < left.Length ? ParseNumericIdentifier(left[i]) : 0;
                var rightValue = i < right.Length ? ParseNumericIdentifier(right[i]) : 0;
                if (leftValue != rightValue)
                    return leftValue < rightValue ? -1 : 1;
            }
            return 0;
        }

        static int ParseNumericIdentifier(string identifier)
        {
            return int.TryParse(identifier, out var value) ? value : 0;
        }

        // Compares dot-separated pre-release identifiers per SemVer §11: numeric identifiers compare
        // numerically and rank below alphanumeric ones, which compare by ASCII order. When all shared
        // identifiers are equal, the version with more identifiers has higher precedence.
        static int ComparePreRelease(string leftPreRelease, string rightPreRelease)
        {
            var left = leftPreRelease.Split('.');
            var right = rightPreRelease.Split('.');
            var count = Math.Min(left.Length, right.Length);
            for (var i = 0; i < count; i++)
            {
                var comparison = ComparePreReleaseIdentifier(left[i], right[i]);
                if (comparison != 0)
                    return comparison;
            }
            return left.Length.CompareTo(right.Length);
        }

        static int ComparePreReleaseIdentifier(string left, string right)
        {
            var leftIsNumeric = int.TryParse(left, out var leftValue);
            var rightIsNumeric = int.TryParse(right, out var rightValue);

            if (leftIsNumeric && rightIsNumeric)
                return leftValue.CompareTo(rightValue);
            if (leftIsNumeric)
                return -1; // numeric identifiers have lower precedence than alphanumeric ones
            if (rightIsNumeric)
                return 1;
            return string.CompareOrdinal(left, right);
        }
    }
}
