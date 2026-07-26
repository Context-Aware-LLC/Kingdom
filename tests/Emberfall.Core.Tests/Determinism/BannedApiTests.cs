using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Determinism
{
    /// <summary>
    /// The determinism firewall — banned-API static checks (ci-gates.md §1.3, ARCHITECTURE §7). Two
    /// independent layers:
    ///  1. <b>Float-in-state reflection scan</b> — no <c>float</c>/<c>double</c> field on any core type.
    ///     This is the static twin of the GameStateHasher's no-float tripwire (golden-replay.md §4.2):
    ///     a float in state would hash platform-dependent bits and silently desync.
    ///  2. <b>Source-text scan</b> — forbids <c>System.Random</c>, <c>UnityEngine.Random</c>,
    ///     <c>DateTime.Now/UtcNow</c>, <c>Time.deltaTime</c> in core source (comments excluded). These are
    ///     not "UnityEngine" but break determinism just as fatally (game-state §6.2).
    /// </summary>
    [TestFixture]
    [Category("Firewall")]
    public sealed class BannedApiTests
    {
        private static readonly string[] CoreAssemblies =
        {
            "Emberfall.Core", "Emberfall.Core.Cards", "Emberfall.Core.Contracts", "Emberfall.Core.Serialization",
            "Emberfall.Net.Protocol",  // A5 §10.8 — the wire is float-free by the same structural guard
            "Emberfall.Core.Collection"
        };

        private static readonly string[] CoreSourceDirs =
        {
            "Emberfall.Core", "Emberfall.Core.Cards", "Emberfall.Core.Contracts", "Emberfall.Core.Serialization",
            "Emberfall.Net.Protocol",
            "Emberfall.Core.Collection"
        };

        private static readonly string[] BannedTokens =
        {
            "System.Random", "UnityEngine.Random", "UnityEngine.", "DateTime.Now", "DateTime.UtcNow", "Time.deltaTime"
        };

        [Test]
        public void No_Float_Or_Double_Field_In_Core_Types()
        {
            var offenders = new List<string>();
            foreach (var name in CoreAssemblies)
            {
                var asm = Assembly.Load(name);
                foreach (var type in asm.GetTypes())
                {
                    if (type.IsEnum) continue;
                    foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (f.FieldType == typeof(float) || f.FieldType == typeof(double) ||
                            f.FieldType == typeof(float?) || f.FieldType == typeof(double?))
                        {
                            offenders.Add($"{type.FullName}.{f.Name} : {f.FieldType.Name}");
                        }
                    }
                }
            }

            Assert.That(offenders, Is.Empty,
                "Floating-point field(s) found in core state — would hash platform-dependent bits and desync:\n  "
                + string.Join("\n  ", offenders));
        }

        [Test]
        public void No_Banned_Determinism_Apis_In_Core_Source()
        {
            var srcRoot = Path.Combine(RepoPaths.RepoRoot, "src");
            var offenders = new List<string>();

            foreach (var dir in CoreSourceDirs)
            {
                var full = Path.Combine(srcRoot, dir);
                if (!Directory.Exists(full)) continue;
                foreach (var file in Directory.GetFiles(full, "*.cs", SearchOption.AllDirectories))
                {
                    string code = StripComments(File.ReadAllText(file));
                    foreach (var token in BannedTokens)
                    {
                        int idx = code.IndexOf(token, StringComparison.Ordinal);
                        if (idx >= 0)
                            offenders.Add($"{Path.GetFileName(file)}: '{token}'");
                    }
                }
            }

            Assert.That(offenders, Is.Empty,
                "Banned determinism-breaking API(s) referenced in core source:\n  " + string.Join("\n  ", offenders));
        }

        /// <summary>
        /// golden-replay.md §4.1 bans reflection from the canonical ENCODE path (reflection field
        /// order is not stable across runtimes). The serializer assembly must therefore contain no
        /// reflection token at all — completeness guards using reflection live HERE, in tests,
        /// never in the walk (a2-serializer-design.md §2.1).
        /// </summary>
        [Test]
        public void No_Reflection_In_Canonical_Serializer_Source()
        {
            var dir = Path.Combine(RepoPaths.RepoRoot, "src", "Emberfall.Core.Serialization");
            Assert.That(Directory.Exists(dir), Is.True, $"Serializer source dir missing: {dir}");

            string[] reflectionTokens = { "System.Reflection", "GetFields", "GetProperties", "GetType().GetField" };
            var offenders = new List<string>();

            foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string code = StripComments(File.ReadAllText(file));
                foreach (var token in reflectionTokens)
                {
                    if (code.IndexOf(token, StringComparison.Ordinal) >= 0)
                        offenders.Add($"{Path.GetFileName(file)}: '{token}'");
                }
            }

            Assert.That(offenders, Is.Empty,
                "Reflection token(s) in the canonical serializer — the encode path must be 100% hand-ordered (§4.1):\n  "
                + string.Join("\n  ", offenders));
        }

        /// <summary>Remove block and line comments so doc-comment mentions of banned tokens don't false-positive.</summary>
        private static string StripComments(string src)
        {
            src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline); // block comments
            var sb = new System.Text.StringBuilder(src.Length);
            foreach (var rawLine in src.Split('\n'))
            {
                int slash = rawLine.IndexOf("//", StringComparison.Ordinal);
                sb.Append(slash >= 0 ? rawLine.Substring(0, slash) : rawLine).Append('\n');
            }
            return sb.ToString();
        }
    }
}
