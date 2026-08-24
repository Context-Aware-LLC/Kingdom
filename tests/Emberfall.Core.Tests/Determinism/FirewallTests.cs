using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Determinism
{
    /// <summary>
    /// The assembly firewall + determinism firewall (dev-docs/quality/testing-and-ci.md:10, M1 exit criterion 7). Layer-2
    /// backstop behind the asmdef <c>noEngineReferences</c> flag: a reflection scan over the COMPILED
    /// core assemblies' referenced-assembly tables, so a UnityEngine reference introduced by any means
    /// fails the build. Emits <c>artifacts/firewall-scan.json</c>.
    /// </summary>
    [TestFixture]
    [Category("Firewall")]
    public sealed class FirewallTests
    {
        private static readonly string[] CoreAssemblies =
        {
            "Emberfall.Core", "Emberfall.Core.Cards", "Emberfall.Core.Contracts",
            "Emberfall.Core.Serialization",   // pre-existing gap closed alongside A5 enrollment
            "Emberfall.Net.Protocol",         // A5 §10.8
            "Emberfall.Core.Collection"       // D4/D9 — the L1-L5 predicate re-runs server-side, so it is core
        };

        [Test]
        public void Core_Assemblies_Have_Zero_UnityEngine_References()
        {
            var scan = new List<(string name, string[] forbidden)>();
            foreach (var name in CoreAssemblies)
            {
                var asm = Assembly.Load(name);
                var bad = asm.GetReferencedAssemblies()
                    .Where(a => a.Name != null &&
                                (a.Name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                                 a.Name.StartsWith("UnityEditor", StringComparison.Ordinal)))
                    .Select(a => a.Name!)
                    .ToArray();
                scan.Add((name, bad));
            }

            WriteFirewallArtifact(scan);

            foreach (var (name, forbidden) in scan)
                Assert.That(forbidden, Is.Empty,
                    $"{name} references forbidden engine assemblies: {string.Join(", ", forbidden)}");
        }

        private static void WriteFirewallArtifact(List<(string name, string[] forbidden)> scan)
        {
            try
            {
                bool pass = scan.All(s => s.forbidden.Length == 0);
                var sb = new StringBuilder();
                sb.Append("{ \"gate\": \"assembly-firewall\", \"status\": \"").Append(pass ? "pass" : "fail").Append("\",\n");
                sb.Append("  \"assemblies\": [\n");
                for (int i = 0; i < scan.Count; i++)
                {
                    var (name, forbidden) = scan[i];
                    sb.Append("    { \"name\": \"").Append(name).Append("\", \"forbiddenReferences\": [")
                      .Append(string.Join(", ", forbidden.Select(f => "\"" + f + "\""))).Append("], \"bannedApiHits\": [] }");
                    sb.Append(i == scan.Count - 1 ? "\n" : ",\n");
                }
                sb.Append("  ] }\n");
                File.WriteAllText(Path.Combine(RepoPaths.ArtifactsDir, "firewall-scan.json"), sb.ToString());
            }
            catch (DirectoryNotFoundException) { /* non-fatal */ }
        }
    }
}
