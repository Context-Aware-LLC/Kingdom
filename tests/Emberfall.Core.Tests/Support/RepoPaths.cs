using System;
using System.IO;

namespace Emberfall.Core.Tests.Support
{
    /// <summary>
    /// Locates source-tree directories at test runtime by walking up from the test assembly's base
    /// directory. Fixtures are read directly from source rather than copied to output, and the
    /// artifacts land at the repo root where CI publishes them
    /// (hist-docs/source-snapshots/2026-07-31-pre-reorganization/testing/ci-gates.md §4).
    /// </summary>
    public static class RepoPaths
    {
        /// <summary>
        /// The Emberfall.Core.Tests project directory (contains the Fixtures/ tree). The walk-up
        /// works when running inside this project; a sibling test project resolves via the repo
        /// root instead.
        /// </summary>
        public static string TestProjectDir => _testProjectDir ??= TryFindUp(
            d => Directory.Exists(Path.Combine(d, "Fixtures", "CardSets")))
            ?? Path.Combine(RepoRoot, "tests", "Emberfall.Core.Tests");

        /// <summary>The repository root (contains the .git folder or Emberfall.slnx).</summary>
        public static string RepoRoot => _repoRoot ??= FindUp(
            d => Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, "Emberfall.slnx")));

        public static string CardSetsDir => Path.Combine(TestProjectDir, "Fixtures", "CardSets");

        public static string ArtifactsDir
        {
            get
            {
                var dir = Path.Combine(RepoRoot, "artifacts");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string? _testProjectDir;
        private static string? _repoRoot;

        private static string FindUp(Func<string, bool> predicate)
            => TryFindUp(predicate) ?? throw new DirectoryNotFoundException(
                $"Could not locate target directory walking up from '{AppContext.BaseDirectory}'.");

        private static string? TryFindUp(Func<string, bool> predicate)
        {
            var dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (predicate(dir)) return dir;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return null;
        }
    }
}
