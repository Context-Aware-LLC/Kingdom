using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Kingdom.Net.Server.Data;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests
{
    /// <summary>
    /// The corpus digest is computed TWICE, in two languages — <see cref="CorpusDigest"/> here and
    /// <c>corpus_digest()</c> in `tooling/card-admin/build_data.py` — and the whole mechanism is
    /// the comparison between them. Nothing but this test holds the two together: they live in
    /// different toolchains, so an edit to either compiles, passes, and simply makes every future
    /// comparison report "behind" forever, redeploying on every run of the admin build.
    ///
    /// So the assertion is not "the digest is stable"; it is "the OTHER implementation agrees",
    /// which is why this shells out to Python rather than pinning a vector. A pinned vector would
    /// go green against a Python side that had drifted away from it.
    /// </summary>
    [TestFixture]
    public class CorpusDigestTests
    {
        [Test]
        public void Digest_Is_Stable_And_Order_Independent()
        {
            string dir = NewFixtureDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "Beta.cards.json"), "[{\"id\":2}]");
                File.WriteAllText(Path.Combine(dir, "Alpha.cards.json"), "[{\"id\":1}]");
                string first = CorpusDigest.Compute(dir);

                // Rewrite in the opposite order: the digest sorts on the relative path, so which
                // order the filesystem hands the files back must not reach it.
                File.Delete(Path.Combine(dir, "Alpha.cards.json"));
                File.Delete(Path.Combine(dir, "Beta.cards.json"));
                File.WriteAllText(Path.Combine(dir, "Alpha.cards.json"), "[{\"id\":1}]");
                File.WriteAllText(Path.Combine(dir, "Beta.cards.json"), "[{\"id\":2}]");

                Assert.That(CorpusDigest.Compute(dir), Is.EqualTo(first));
                Assert.That(first, Has.Length.EqualTo(CorpusDigest.Length));
                Assert.That(first, Is.Not.EqualTo(CorpusDigest.Empty));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Test]
        public void A_Changed_Byte_And_A_Rename_Both_Move_The_Digest()
        {
            string dir = NewFixtureDir();
            try
            {
                string path = Path.Combine(dir, "Alpha.cards.json");
                File.WriteAllText(path, "[{\"id\":1,\"cost\":3}]");
                string baseline = CorpusDigest.Compute(dir);

                File.WriteAllText(path, "[{\"id\":1,\"cost\":4}]");
                Assert.That(CorpusDigest.Compute(dir), Is.Not.EqualTo(baseline), "a card edit must move the digest");

                File.WriteAllText(path, "[{\"id\":1,\"cost\":3}]");
                Assert.That(CorpusDigest.Compute(dir), Is.EqualTo(baseline), "reverting must return to the same digest");

                // A renamed set file is a different corpus even with identical card bytes: the
                // server globs the directory, so a rename changes what gets loaded twice or not
                // at all. Hashing the path is what makes that visible.
                File.Move(path, Path.Combine(dir, "Renamed.cards.json"));
                Assert.That(CorpusDigest.Compute(dir), Is.Not.EqualTo(baseline), "a rename must move the digest");
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Test]
        public void No_Card_Files_Yields_The_Empty_Digest()
        {
            string dir = NewFixtureDir();
            try
            {
                Assert.That(CorpusDigest.Compute(dir), Is.EqualTo(CorpusDigest.Empty));
                Assert.That(CorpusDigest.Compute(Path.Combine(dir, "does-not-exist")), Is.EqualTo(CorpusDigest.Empty));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Test]
        public void Python_Computes_The_Same_Digest_Over_The_Same_Directory()
        {
            string dir = NewFixtureDir();
            try
            {
                // Deliberately awkward content: a nested directory (LoadDirectory recurses), a
                // non-ASCII card name (the two sides must both hash UTF-8 bytes), and CRLF in one
                // file (a text-mode read on either side would normalise it and diverge here).
                Directory.CreateDirectory(Path.Combine(dir, "nested"));
                File.WriteAllText(Path.Combine(dir, "Alpha.cards.json"),
                    "[{\"id\":1,\"name\":\"Ember\\u00e9\"}]\n", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir, "nested", "Beta.cards.json"),
                    "[{\"id\":2}]\r\n", new UTF8Encoding(false));

                string mine = CorpusDigest.Compute(dir);
                string theirs = PythonDigest(dir);
                Assert.That(theirs, Is.EqualTo(mine),
                    "CorpusDigest.Compute and corpus_digest() in tooling/card-admin/build_data.py " +
                    "have drifted apart. They are mirrored by hand and must move together.");
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        /// <summary>Run build_data.py's digest against an arbitrary directory, or skip the test
        /// when there is no interpreter to run it with — a machine without Python is a real and
        /// unremarkable configuration, and failing there would only teach people to ignore this.</summary>
        private static string PythonDigest(string dir)
        {
            // The program goes to a FILE rather than `-c`. A `-c` argument has to survive Windows
            // command-line quoting on the way through, and the failure that produces is a Python
            // SyntaxError — which is indistinguishable, from here, from the drift this test exists
            // to catch. A file has no quoting at all.
            string script = Path.Combine(Path.GetTempPath(), "emberfall-digest-" + Guid.NewGuid().ToString("n") + ".py");
            File.WriteAllText(script,
                "import sys, pathlib\n" +
                "sys.path.insert(0, r'''" + Path.Combine(RepoRoot(), "tooling", "card-admin") + "''')\n" +
                "import build_data\n" +
                "build_data.CARD_DIR = pathlib.Path(r'''" + dir + "''')\n" +
                "print(build_data.corpus_digest())\n");

            try
            {
                foreach (string exe in new[] { "python", "python3", "py" })
                {
                    var psi = new ProcessStartInfo(exe)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                    };
                    psi.ArgumentList.Add(script);

                    Process? proc;
                    try
                    {
                        // ONLY the launch is guarded, and only against "no such interpreter". A
                        // broad catch around the whole body would swallow NUnit's own assertion
                        // exceptions and turn a genuine mismatch into a silent skip — which is
                        // exactly how this test first went green while proving nothing.
                        proc = Process.Start(psi);
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        continue;
                    }
                    if (proc is null) continue;

                    using (proc)
                    {
                        string stdout = proc.StandardOutput.ReadToEnd();
                        string stderr = proc.StandardError.ReadToEnd();
                        proc.WaitForExit(60_000);
                        if (proc.ExitCode != 0)
                            Assert.Fail($"{exe} failed computing the mirror digest (exit {proc.ExitCode}): {stderr}");
                        return stdout.Trim();
                    }
                }
            }
            finally
            {
                try { File.Delete(script); } catch (IOException) { }
            }

            Assert.Ignore("No Python interpreter on PATH; cannot verify the mirrored digest.");
            return string.Empty;
        }

        private static string NewFixtureDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "emberfall-corpus-digest-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Walk up to the directory holding `tooling/card-admin/build_data.py`. The test
        /// binary's location moves with the build layout (`src/Directory.Build.props` redirects
        /// output to `.dotnet-build/`), so the root is found rather than assumed.</summary>
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tooling", "card-admin", "build_data.py")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            Assert.Ignore("Repo root not found from the test binary; cannot locate build_data.py.");
            return string.Empty;
        }
    }
}
