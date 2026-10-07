using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Bots
{
    /// <summary>
    /// A capture played on the rules core alone has to leave the files the client/server loopback
    /// leaves: the replays, the manifest and the telemetry. A corpus is only allowed to forget how
    /// it was captured if it is.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class DirectCaptureTests
    {
        [Test]
        public async Task Direct_capture_writes_the_bytes_the_loopback_writes()
        {
            string root = Path.Combine(Path.GetTempPath(), "emberfall-direct-" + Guid.NewGuid().ToString("N"));
            string loopback = Path.Combine(root, "loopback");
            string direct = Path.Combine(root, "direct");
            try
            {
                string[] capture = { "dataset-matrix", "4", "", "--classes", "EMBERWARD,TIDECALLER,GRAVEKEEP" };
                capture[2] = loopback;
                Assert.That(await Emberfall.Tools.NetSoak.Program.Main(capture), Is.EqualTo(0));
                capture[2] = direct;
                Assert.That(
                    await Emberfall.Tools.NetSoak.Program.Main(
                        capture.Concat(new[] { "--direct", "--workers", "3" }).ToArray()),
                    Is.EqualTo(0));

                string[] files = Relative(loopback);
                Assert.That(files.Count(file => file.StartsWith("replays/", StringComparison.Ordinal)),
                    Is.EqualTo(12), "three pairings of four games");
                Assert.That(Relative(direct), Is.EqualTo(files));
                foreach (string file in files)
                    Assert.That(
                        File.ReadAllBytes(Path.Combine(direct, file)),
                        Is.EqualTo(File.ReadAllBytes(Path.Combine(loopback, file))),
                        file);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static string[] Relative(string directory)
            => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
    }
}
