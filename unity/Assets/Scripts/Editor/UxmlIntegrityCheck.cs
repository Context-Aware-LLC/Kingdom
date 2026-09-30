using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Editor
{
    /// <summary>
    /// Prove every meta-screen's UXML still parses and still clones to a populated tree.
    ///
    /// <para><b>This exists because of a specific failure that logs nothing at all.</b> A BEM
    /// modifier's double dash inside a UXML comment (<c>&lt;!-- ... mm-modal- -hidden ... --&gt;</c>,
    /// written without the space) is invalid XML. The importer then fails the <b>whole file</b>, and a
    /// failed <see cref="VisualTreeAsset"/> clones to an <b>empty</b> tree — while
    /// <c>rootVisualElement</c> stays non-null, so nothing throws. Every <c>Q&lt;T&gt;()</c> returns
    /// null and the screen presents as "nothing is wired", including buttons that had worked for a
    /// week. The console says nothing, because the import already failed silently on a previous
    /// domain reload and Unity does not re-report it.</para>
    ///
    /// <para><b>Which is why this force-reimports rather than just loading.</b>
    /// <see cref="ImportAssetOptions.ForceSynchronousImport"/> is what makes the underlying
    /// <c>XmlException</c> surface in the console at all; a plain load of an already-failed asset is
    /// quiet. The count is the assertion — a screen whose tree has zero children is broken no matter
    /// what the element names say.</para>
    /// </summary>
    public static class UxmlIntegrityCheck
    {
        /// <summary>Where the runtime screens live. Everything under here is checked.</summary>
        private const string ScreenRoot = "Assets/UI";

        /// <summary>
        /// EVERY <c>.uxml</c> UNDER <see cref="ScreenRoot"/>, DISCOVERED, NEVER LISTED.
        ///
        /// <para>This was a hand-maintained array until 2026-08-31, carrying four of the project's
        /// nine screens and a comment asking the next person to remember the fifth. They did not:
        /// the Shop shipped a double dash inside a UXML comment, rendered completely empty, and
        /// THIS CHECK PASSED — because <c>ShopScreen.uxml</c> was never in the list. AccountCreate,
        /// ConnectionFailed, MatchSettings and PackReveal were unguarded by the same omission.</para>
        ///
        /// <para>A list whose only failure mode is being incomplete cannot be fixed by adding the
        /// missing entry; it can only be fixed by not being a list. Enumerating the DIRECTORY, not
        /// the asset database, is deliberate — a UXML that fails to import hard enough to produce
        /// no <see cref="VisualTreeAsset"/> at all would be invisible to a <c>t:VisualTreeAsset</c>
        /// search, and that is precisely the file this tool must not miss.</para>
        /// </summary>
        private static string[] Screens()
        {
            string abs = System.IO.Path.Combine(
                System.IO.Directory.GetParent(Application.dataPath).FullName, ScreenRoot);
            if (!System.IO.Directory.Exists(abs)) return new string[0];

            string[] files = System.IO.Directory.GetFiles(abs, "*.uxml", System.IO.SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                files[i] = ScreenRoot + files[i]
                    .Substring(abs.Length)
                    .Replace('\\', '/');
            }
            System.Array.Sort(files, System.StringComparer.Ordinal);
            return files;
        }

        [MenuItem("Emberfall/UI/Check UXML Integrity")]
        public static void Check()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("[UxmlIntegrityCheck]");
            bool allGood = true;

            string[] screens = Screens();
            report.AppendLine($"  {screens.Length} screen(s) under {ScreenRoot}");
            if (screens.Length == 0)
            {
                // A discovery that finds nothing is the one result that looks like a pass and is
                // not one -- the same trap as a `dotnet test` matching zero tests.
                report.AppendLine($"  ✗ no .uxml found under {ScreenRoot} — this check verified NOTHING");
                Debug.LogError(report.ToString());
                return;
            }

            foreach (string path in screens)
            {
                AssetDatabase.ImportAsset(path,
                    ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

                var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
                if (asset == null)
                {
                    report.AppendLine($"  ✗ {path} — no VisualTreeAsset at this path");
                    allGood = false;
                    continue;
                }

                VisualElement tree = asset.CloneTree();
                List<string> named = NamedElements(tree);

                if (tree.childCount == 0)
                {
                    report.AppendLine($"  ✗ {path} — clones to an EMPTY tree. The file did not parse; " +
                                      "look for a '--' inside a comment, or any other invalid XML.");
                    allGood = false;
                    continue;
                }

                // #266: character creation deliberately replaced the runtime-sized portrait grid
                // with two fixed columns. Keep that boundary explicit so a future restyle cannot
                // quietly reintroduce the leaking grid/tree shape that OOM-killed Android.
                if (path.EndsWith("CharacterCreateScreen.uxml"))
                {
                    bool hasFixedColumns = tree.Q<VisualElement>("create-hero-column-left") != null
                                           && tree.Q<VisualElement>("create-hero-column-right") != null;
                    bool hasLegacyGrid = tree.Q<VisualElement>("create-hero-grid") != null
                                         || tree.Q<VisualElement>(className: "hero-grid") != null;
                    if (!hasFixedColumns || hasLegacyGrid)
                    {
                        report.AppendLine($"  ✗ {path} — character picker must have two fixed " +
                                          "columns and no legacy hero grid");
                        allGood = false;
                        continue;
                    }
                }

                report.AppendLine($"  ✓ {path} — childCount={tree.childCount}, {named.Count} named elements");
                report.AppendLine($"      {string.Join(", ", named)}");
            }

            if (allGood) Debug.Log(report.ToString());
            else Debug.LogError(report.ToString());
        }

        private static List<string> NamedElements(VisualElement root)
        {
            var names = new List<string>();
            var stack = new Stack<VisualElement>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                VisualElement el = stack.Pop();
                if (!string.IsNullOrEmpty(el.name)) names.Add(el.name);
                foreach (VisualElement child in el.Children()) stack.Push(child);
            }
            return names;
        }
    }
}
