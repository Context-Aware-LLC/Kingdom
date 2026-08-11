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
        /// <summary>
        /// Every runtime UXML that a controller queries by name. Add a screen here when you build one —
        /// the cost of forgetting is a silent empty screen, which is the whole point of the tool.
        /// </summary>
        private static readonly string[] Screens =
        {
            "Assets/UI/MainMenu/MainMenuScreen.uxml",
            "Assets/UI/CharacterRoster/CharacterRosterScreen.uxml",
            // The collection wall and the deckbuilder are one plate now (#153); DeckbuilderScreen.uxml
            // is gone. This entry replaces it and closes a gap rather than just tracking a rename:
            // CollectionScreen.uxml was never listed here, so the screen carrying the loudest warning
            // about the failure this tool exists to catch was the one screen it did not check.
            "Assets/UI/Collection/CollectionScreen.uxml",
        };

        [MenuItem("Emberfall/UI/Check UXML Integrity")]
        public static void Check()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("[UxmlIntegrityCheck]");
            bool allGood = true;

            foreach (string path in Screens)
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
