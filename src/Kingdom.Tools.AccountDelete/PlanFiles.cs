using System;
using System.IO;

namespace Kingdom.Tools.AccountDelete
{
    /// <summary>
    /// Where plan files live: outside any git work tree (Sam, 2026-11-02 — a company admin record, never
    /// a repo file). <c>--out</c>, else <c>KINGDOM_DELETION_PLAN_DIR</c>, else
    /// <c>~/.kingdom/account-deletion</c>. Owner-only permissions where the platform has them.
    /// </summary>
    internal static class PlanFiles
    {
        public const string DirEnvVar = "KINGDOM_DELETION_PLAN_DIR";

        public static string ResolveDirectory(string? explicitDir)
        {
            string dir = !string.IsNullOrWhiteSpace(explicitDir) ? explicitDir!
                : Kingdom.Net.Server.KingdomEnv.Get(DirEnvVar)
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kingdom", "account-deletion");
            return Path.GetFullPath(dir);
        }

        /// <summary>The git work tree <paramref name="path"/> is inside, or null.</summary>
        public static string? EnclosingWorkTree(string path)
        {
            for (DirectoryInfo? d = new DirectoryInfo(Path.GetFullPath(path)); d is not null; d = d.Parent)
            {
                string git = Path.Combine(d.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git)) return d.FullName;
            }
            return null;
        }

        /// <summary>Writes the bytes to a new file and returns its path. Refuses a directory inside a git
        /// work tree, and never overwrites.</summary>
        public static string Write(string dir, byte[] bytes, DateTimeOffset now)
        {
            if (EnclosingWorkTree(dir) is string tree)
                throw new InvalidOperationException(
                    $"Plan files are kept outside the repo, and {dir} is inside the git work tree at {tree}.");

            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            string name = $"{now.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{Guid.NewGuid():N}".Substring(0, 25) + ".plan.json";
            string path = Path.Combine(dir, name);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var fs = new FileStream(path, options)) fs.Write(bytes);
            return path;
        }
    }
}
