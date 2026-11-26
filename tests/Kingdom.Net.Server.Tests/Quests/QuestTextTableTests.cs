using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml;
using Kingdom.Core.Contracts;
using Kingdom.Net.Server.Quests;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Quests
{
    /// <summary>
    /// The client shows a quest from its id, in the player's language, out of
    /// <c>quest_&lt;template&gt;_title</c> and <c>_quip</c> rows in the Unity string table
    /// (MainMenuController.QuestLine). The server's English is the source. This holds every quest
    /// the catalog can deal, for every class, to the English rows: a quest added or reworded here
    /// without its rows shows its English in every language, and nothing on the client says so.
    /// </summary>
    public sealed class QuestTextTableTests
    {
        private static readonly string[] ClassQuestPrefixes = { "win-as-", "play-as-", "cards-of-" };

        [Test]
        public void EveryQuestTheCatalogDealsHasMatchingEnglishRows()
        {
            Dictionary<string, string> rows = ReadTable("strings.xml");
            Dictionary<string, string> glossary = ReadTable("glossary.xml");
            var templates = (Func<CardClass, QuestDefinition>[])typeof(QuestCatalog)
                .GetField("Templates", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var classes = (CardClass[])typeof(QuestCatalog)
                .GetField("BindableClasses", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

            var wrong = new List<string>();
            int checkedQuests = 0;
            foreach (Func<CardClass, QuestDefinition> template in templates)
                foreach (CardClass c in classes)
                {
                    QuestDefinition quest = template(c);
                    checkedQuests++;
                    string stem = Stem(quest.Id, out bool classBound);
                    string className = glossary["class_" + c.ToString().ToLowerInvariant()];
                    Compare("quest_" + stem + "_title", quest.Title, classBound ? className : null);
                    Compare("quest_" + stem + "_quip", quest.Quip, null);
                }

            Assert.That(checkedQuests, Is.GreaterThan(0));
            Assert.That(wrong, Is.Empty, string.Join("\n", wrong));

            void Compare(string key, string expected, string? arg)
            {
                if (!rows.TryGetValue(key, out string? row)) { wrong.Add(key + ": no row"); return; }
                string rendered = arg == null ? row : row.Replace("%1$s", arg);
                if (rendered != expected) wrong.Add($"{key}: \"{rendered}\" is not \"{expected}\"");
            }
        }

        private static string Stem(string id, out bool classBound)
        {
            foreach (string prefix in ClassQuestPrefixes)
                if (id.StartsWith(prefix, StringComparison.Ordinal))
                {
                    classBound = true;
                    return prefix.TrimEnd('-').Replace('-', '_');
                }
            classBound = false;
            return id.Replace('-', '_');
        }

        private static Dictionary<string, string> ReadTable(string file)
        {
            var doc = new XmlDocument();
            doc.Load(Path.Combine(RepoRoot(), "unity", "Assets", "Resources", "Localization", "values", file));
            var rows = new Dictionary<string, string>();
            foreach (XmlNode node in doc.DocumentElement!.ChildNodes)
                if (node is XmlElement el && el.Name == "string")
                    rows[el.GetAttribute("name")] = el.InnerText.Replace("\\'", "'").Replace("\\\"", "\"");
            return rows;
        }

        private static string RepoRoot()
        {
            for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "Kingdom.slnx"))) return d.FullName;
            throw new DirectoryNotFoundException("Kingdom.slnx not found above the test directory.");
        }
    }
}
