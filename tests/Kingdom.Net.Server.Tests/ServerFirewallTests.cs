using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests
{
    /// <summary>
    /// B1's enrollment in the determinism firewall (dev-docs/quality/testing-and-ci.md:10, mirroring a5
    /// §10.8). Lives here rather than in Core.Tests' hardcoded list because Core.Tests must not
    /// reference a web exe; the Category("Firewall") stage picks it up across the slnx either way.
    /// The SESSION layer may read the wall clock (through ITimeSource only) — unlike the core —
    /// so DateTime bans differ from BannedApiTests: local-time and System.Random stay banned,
    /// UnityEngine stays banned, floats in fields stay banned.
    /// </summary>
    [TestFixture]
    [Category("Firewall")]
    public sealed class ServerFirewallTests
    {
        [Test]
        public void Server_Assembly_Has_Zero_UnityEngine_References()
        {
            var asm = Assembly.Load("Kingdom.Net.Server");
            var bad = asm.GetReferencedAssemblies()
                .Where(a => a.Name != null &&
                            (a.Name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                             a.Name.StartsWith("UnityEditor", StringComparison.Ordinal)))
                .Select(a => a.Name!)
                .ToArray();
            Assert.That(bad, Is.Empty, $"Kingdom.Net.Server references forbidden engine assemblies: {string.Join(", ", bad)}");
        }

        [Test]
        public void No_Float_Or_Double_Field_In_Server_Types()
        {
            var offenders = new List<string>();
            var asm = Assembly.Load("Kingdom.Net.Server");
            foreach (var type in asm.GetTypes())
            {
                if (type.IsEnum) continue;
                foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.FieldType == typeof(float) || f.FieldType == typeof(double) ||
                        f.FieldType == typeof(float?) || f.FieldType == typeof(double?))
                        offenders.Add($"{type.FullName}.{f.Name} : {f.FieldType.Name}");
                }
            }
            Assert.That(offenders, Is.Empty, "float/double fields in the server layer:\n" + string.Join("\n", offenders));
        }

        [Test]
        public void Server_Source_Avoids_Banned_Tokens()
        {
            string srcDir = FindServerSourceDir();
            string[] banned = { "System.Random", "UnityEngine.", "DateTime.Now" };
            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = StripLineComment(lines[i]);
                    foreach (string token in banned)
                        if (code.Contains(token, StringComparison.Ordinal))
                            offenders.Add($"{Path.GetFileName(file)}:{i + 1} contains '{token}'");
                }
            }
            Assert.That(offenders, Is.Empty, string.Join("\n", offenders));
        }

        private static string StripLineComment(string line)
        {
            int idx = line.IndexOf("//", StringComparison.Ordinal);
            return idx < 0 ? line : line.Substring(0, idx);
        }

        private static string FindServerSourceDir()
        {
            string? path = TestContext.CurrentContext.TestDirectory;
            for (int i = 0; i < 10 && path is not null; i++, path = Path.GetDirectoryName(path))
            {
                string candidate = Path.Combine(path, "src", "Kingdom.Net.Server");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("Could not locate src/Kingdom.Net.Server.");
        }
    }
}
