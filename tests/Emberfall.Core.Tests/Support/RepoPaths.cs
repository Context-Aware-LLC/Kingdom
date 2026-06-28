using System;
using System.IO;

namespace Emberfall.Core.Tests.Support
{
    /// <summary>
    /// Locates source-tree directories at test runtime by walking up from the test assembly's base
    /// directory. Fixtures are read directly from source (not copied to output) so the rebaseline
    /// command can rewrite the canonical file, and so the artifacts land at the repo root where CI
    /// publishes them (ci-gates.md §4).
    /// </summary>
    public static class RepoPaths
    {
        /// <summary>The Emberfall.Core.Tests project directory (contains the GoldenReplay/ tree).</summary>
        public static string TestProjectDir => _testProjectDir ??= FindUp(
            d => Directory.Exists(Path.Combine(d, "GoldenReplay", "fixtures")));

        /// <summary>The repository root (contains the .git folder or Emberfall.slnx).</summary>
        public static string RepoRoot => _repoRoot ??= FindUp(
            d => Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, "Emberfall.slnx")));

        public static string FixturesDir => Path.Combine(TestProjectDir, "GoldenReplay", "fixtures");
        public static string CardSetsDir => Path.Combine(TestProjectDir, "GoldenReplay", "cardsets");

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
        {
            var dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (predicate(dir)) return dir;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate target directory walking up from '{AppContext.BaseDirectory}'.");
        }
    }
}
