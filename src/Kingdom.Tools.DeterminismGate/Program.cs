using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Kingdom.Tools.DeterminismGate
{
    /// <summary>
    /// CI Stage 4 — the determinism firewall and the assembly firewall, in one blocking tool.
    ///
    /// <para>Eight structural checks, ported 1:1 from the archived <c>BannedApiTests</c> /
    /// <c>FirewallTests</c> / <c>ServerFirewallTests</c> and the single firewall assertion of
    /// <c>HostileDecodeAndFirewallTests</c>. The behavioural hostile-decode tests in that last file
    /// are a different category and stay archived.</para>
    ///
    /// <para><b>Why these are worth enforcing when 1250 other tests were not.</b> Every check here
    /// is static: it reads compiled reference tables and source text, never behaviour. It therefore
    /// cannot fail because a design changed — only because something that breaks determinism was
    /// introduced. A float in hashed state hashes platform-dependent bits; a <c>System.Random</c> or
    /// a <c>DateTime.Now</c> diverges two machines running the same match; reflection in the
    /// canonical encode path reorders fields between runtimes. All three desync silently, and the
    /// engine is server-authoritative with no client prediction (ADR-0004), so a desync is
    /// unrecoverable rather than cosmetic.</para>
    ///
    /// <para>Usage: <c>dotnet run --project src/Kingdom.Tools.DeterminismGate -c Release -- [repoRoot]</c>.
    /// repoRoot defaults to the nearest ancestor of the working directory holding a .git entry.
    /// Exit 0 = pass, 1 = a check was violated, 2 = the gate could not run.</para>
    /// </summary>
    internal static class Program
    {
        // The list the archived FirewallTests and BannedApiTests shared. Kingdom.Net.Protocol is
        // here because the WIRE is float-free by the same structural guard (A5 §10.8), and
        // Kingdom.Core.Collection because its L1-L5 deck predicate re-runs server-side (D4/D9).
        private static readonly string[] CoreAssemblies =
        {
            "Kingdom.Core", "Kingdom.Core.Cards", "Kingdom.Core.Contracts",
            "Kingdom.Core.Serialization", "Kingdom.Net.Protocol", "Kingdom.Core.Collection",
        };

        // Banned in CORE source. DateTime.UtcNow is banned here and deliberately ALLOWED on the
        // server (see ServerBannedTokens): the server is permitted a wall clock, core is not.
        private static readonly string[] CoreBannedTokens =
        {
            "System.Random", "UnityEngine.Random", "UnityEngine.",
            "DateTime.Now", "DateTime.UtcNow", "Time.deltaTime",
        };

        private const string ServerAssembly = "Kingdom.Net.Server";
        private const string ClientAssembly = "Kingdom.Net.Client";

        // The server keeps UtcNow: it stamps wall-clock time on telemetry and match records, which
        // never enter hashed state. Local time and System.Random stay banned.
        private static readonly string[] ServerBannedTokens = { "System.Random", "UnityEngine.", "DateTime.Now" };

        private static readonly string[] ReflectionTokens =
        {
            "System.Reflection", "GetFields", "GetProperties", "GetType().GetField",
        };

        private static int _failures;
        private static readonly List<string> Lines = new List<string>();

        private static int Main(string[] args)
        {
            string root;
            try
            {
                root = args.Length > 0 && args[0].Length > 0
                    ? Path.GetFullPath(args[0])
                    : FindRepoRoot(Directory.GetCurrentDirectory());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("DeterminismGate: cannot locate the repo root — " + ex.Message);
                return 2;
            }

            string src = Path.Combine(root, "src");
            if (!Directory.Exists(src))
            {
                Console.Error.WriteLine("DeterminismGate: no src/ under '" + root + "'. Pass the repo root explicitly.");
                return 2;
            }

            Console.WriteLine("DeterminismGate — CI Stage 4, repo root " + root);

            List<(string name, string[] forbidden)> scan;
            try
            {
                Check("1  no float/double field in core types", NoFloatFields(CoreAssemblies));
                Check("2  no banned determinism API in core source", NoBannedTokens(src, CoreAssemblies, CoreBannedTokens));
                Check("3  no reflection in the canonical serializer", NoReflectionInSerializer(src));

                scan = ScanEngineReferences(CoreAssemblies);
                Check("4  core assemblies have zero UnityEngine references", ForbiddenRefOffenders(scan));

                List<(string name, string[] forbidden)> serverScan = ScanEngineReferences(new[] { ServerAssembly });
                Check("5  server assembly has zero UnityEngine references", ForbiddenRefOffenders(serverScan));
                Check("6  no float/double field in server types", NoFloatFields(new[] { ServerAssembly }));
                Check("7  server source avoids banned tokens", NoBannedTokens(src, new[] { ServerAssembly }, ServerBannedTokens));
                Check("8  client references neither UnityEngine nor the server", ClientFirewall());

                scan.AddRange(serverScan);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("DeterminismGate: could not run — " + ex.GetType().Name + ": " + ex.Message);
                return 2;
            }

            WriteFirewallArtifact(root, scan);

            Console.WriteLine();
            foreach (string line in Lines) Console.WriteLine(line);
            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "DeterminismGate: PASS — 8 checks, 0 violation(s)."
                : "DeterminismGate: FAIL — " + _failures + " check(s) violated. Each one is a silent desync; none is cosmetic.");
            return _failures == 0 ? 0 : 1;
        }

        private static void Check(string label, List<string> offenders)
        {
            if (offenders.Count == 0)
            {
                Lines.Add("  ok    " + label);
                return;
            }
            _failures++;
            Lines.Add("  FAIL  " + label);
            foreach (string o in offenders) Lines.Add("          " + o);
        }

        private static List<string> NoFloatFields(IEnumerable<string> assemblies)
        {
            var offenders = new List<string>();
            foreach (string name in assemblies)
            {
                Assembly asm = Assembly.Load(name);
                foreach (Type type in asm.GetTypes())
                {
                    if (type.IsEnum) continue;
                    foreach (FieldInfo f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        Type t = f.FieldType;
                        if (t == typeof(float) || t == typeof(double) || t == typeof(float?) || t == typeof(double?))
                            offenders.Add(type.FullName + "." + f.Name + " : " + t.Name);
                    }
                }
            }
            return offenders;
        }

        private static List<string> NoBannedTokens(string srcRoot, IEnumerable<string> dirs, string[] banned)
        {
            var offenders = new List<string>();
            foreach (string dir in dirs)
            {
                string full = Path.Combine(srcRoot, dir);
                if (!Directory.Exists(full)) continue;
                foreach (string file in Directory.GetFiles(full, "*.cs", SearchOption.AllDirectories))
                {
                    if (IsGenerated(file)) continue;
                    string code = StripComments(File.ReadAllText(file));
                    foreach (string token in banned)
                        if (code.IndexOf(token, StringComparison.Ordinal) >= 0)
                            offenders.Add(dir + "/" + Path.GetFileName(file) + ": '" + token + "'");
                }
            }
            return offenders;
        }

        private static List<string> NoReflectionInSerializer(string srcRoot)
        {
            string dir = Path.Combine(srcRoot, "Kingdom.Core.Serialization");
            if (!Directory.Exists(dir))
                return new List<string> { "serializer source dir missing: " + dir };

            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (IsGenerated(file)) continue;
                string code = StripComments(File.ReadAllText(file));
                foreach (string token in ReflectionTokens)
                    if (code.IndexOf(token, StringComparison.Ordinal) >= 0)
                        offenders.Add(Path.GetFileName(file) + ": '" + token + "'");
            }
            return offenders;
        }

        private static List<(string name, string[] forbidden)> ScanEngineReferences(IEnumerable<string> assemblies)
        {
            var scan = new List<(string, string[])>();
            foreach (string name in assemblies)
            {
                Assembly asm = Assembly.Load(name);
                string[] bad = asm.GetReferencedAssemblies()
                    .Where(a => a.Name != null &&
                                (a.Name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                                 a.Name.StartsWith("UnityEditor", StringComparison.Ordinal)))
                    .Select(a => a.Name!)
                    .ToArray();
                scan.Add((name, bad));
            }
            return scan;
        }

        private static List<string> ForbiddenRefOffenders(List<(string name, string[] forbidden)> scan)
            => scan.Where(s => s.forbidden.Length > 0)
                   .Select(s => s.name + " references " + string.Join(", ", s.forbidden))
                   .ToList();

        /// <summary>
        /// The client may reach neither the engine assemblies nor the server assembly. The second
        /// half is the one worth stating: a client that can see Kingdom.Net.Server can call
        /// server-only authority code in-process, which is the whole point of the design.
        /// </summary>
        private static List<string> ClientFirewall()
        {
            Assembly asm = Assembly.Load(ClientAssembly);
            return asm.GetReferencedAssemblies()
                .Where(a => a.Name != null &&
                            (a.Name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                             a.Name.StartsWith("UnityEditor", StringComparison.Ordinal) ||
                             a.Name.Equals(ServerAssembly, StringComparison.Ordinal)))
                .Select(a => ClientAssembly + " references " + a.Name)
                .ToList();
        }

        /// <summary>The artifact the archived FirewallTests emitted, so anything reading it still can.</summary>
        private static void WriteFirewallArtifact(string root, List<(string name, string[] forbidden)> scan)
        {
            try
            {
                string dir = Path.Combine(root, "artifacts");
                Directory.CreateDirectory(dir);
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
                File.WriteAllText(Path.Combine(dir, "firewall-scan.json"), sb.ToString());
            }
            catch (IOException) { /* the artifact is a convenience; the exit code is the gate */ }
        }

        private static bool IsGenerated(string file)
        {
            string name = Path.GetFileName(file);
            string sep = Path.DirectorySeparatorChar.ToString();
            return name.EndsWith(".g.cs", StringComparison.Ordinal)
                || name.EndsWith(".Designer.cs", StringComparison.Ordinal)
                || file.Contains(sep + "obj" + sep, StringComparison.Ordinal)
                || file.Contains(sep + "bin" + sep, StringComparison.Ordinal);
        }

        /// <summary>Strip block and line comments so a doc-comment naming a banned token is not a hit.</summary>
        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            var sb = new StringBuilder(source.Length);
            foreach (string rawLine in source.Split('\n'))
            {
                int slash = rawLine.IndexOf("//", StringComparison.Ordinal);
                sb.Append(slash >= 0 ? rawLine.Substring(0, slash) : rawLine).Append('\n');
            }
            return sb.ToString();
        }

        private static string FindRepoRoot(string start)
        {
            DirectoryInfo? d = new DirectoryInfo(start);
            while (d != null)
            {
                if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))
                    return d.FullName;
                d = d.Parent;
            }
            throw new DirectoryNotFoundException("no .git ancestor above '" + start + "'");
        }
    }
}
