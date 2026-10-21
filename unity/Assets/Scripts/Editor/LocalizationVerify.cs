using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Presentation;
using UnityEditor;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>
    /// Gates the string tables under <c>Resources/Localization/</c> (2026-10-20). What it catches is
    /// the failure nobody sees in English: an enum member with no glossary row (it prints its key), a
    /// keyword whose English name drifted from the word the card body bolds, and a translation whose
    /// <c>%n$</c> slots do not match the English row (it prints the wrong value, or throws), and a
    /// literal key the code asks for that English does not have (it prints the key).
    ///
    /// <para>Registered in <see cref="HeadlessVerify"/>.</para>
    /// </summary>
    public static class LocalizationVerify
    {
        private const string TableRoot = "Assets/Resources/Localization";

        [MenuItem("Kingdom/Verify/Localization")]
        public static void Run()
        {
            Func<int>[] checks =
            {
                EveryEnumHasAGlossaryRow,
                EnglishTermsMatchWhatTheyReplaced,
                KeywordNamesMatchTheBoldedVocabulary,
                AndroidFormatConverts,
                PluralCategories,
                TranslationsKeepTheirSlots,
                EveryKeyTheCodeAsksForExists,
            };
            int failed = 0;
            foreach (Func<int> check in checks) failed += check();

            if (failed > 0)
                Debug.LogError($"[LocalizationVerify] {failed} check(s) FAILED.");
            else
                Debug.Log($"[LocalizationVerify] GREEN — {checks.Length}/{checks.Length} checks passed.");
        }

        private static int EveryEnumHasAGlossaryRow()
        {
            var missing = new List<string>();
            foreach (Keyword k in Enum.GetValues(typeof(Keyword)))
            {
                Need("keyword_" + Id(k), missing);
                Need("keyword_" + Id(k) + "_desc", missing);
            }
            foreach (Tribe t in Enum.GetValues(typeof(Tribe)))
            {
                if (t == Tribe.NONE) continue;
                Need("tribe_" + Id(t), missing);
                Need("tribe_" + Id(t) + "_plural", missing);
            }
            foreach (CardClass c in Enum.GetValues(typeof(CardClass))) Need("class_" + Id(c), missing);
            foreach (CardType t in Enum.GetValues(typeof(CardType))) Need("type_" + Id(t), missing);
            foreach (Rarity r in Enum.GetValues(typeof(Rarity))) Need("rarity_" + Id(r), missing);
            foreach (SetId s in Enum.GetValues(typeof(SetId)))
                if (s != SetId.CORE) Need("set_" + Id(s), missing);

            return missing.Count == 0 ? 0 : Fail("EveryEnumHasAGlossaryRow", "no row for " + string.Join(", ", missing));
        }

        // Every surface GameTerms took over printed the enum id title-cased, underscores to spaces.
        // The English rows must still read exactly that, so moving to the table changed nothing on screen.
        private static int EnglishTermsMatchWhatTheyReplaced()
        {
            var wrong = new List<string>();
            void Same(string got, Enum value)
            {
                string want = Regex.Replace(value.ToString().ToLowerInvariant().Replace('_', ' '),
                    @"\b\w", m => m.Value.ToUpperInvariant());
                if (got != want) wrong.Add($"{value}: \"{got}\" (was \"{want}\")");
            }
            foreach (CardClass c in Enum.GetValues(typeof(CardClass))) Same(GameTerms.Class(c), c);
            foreach (CardType t in Enum.GetValues(typeof(CardType))) Same(GameTerms.Type(t), t);
            foreach (Rarity r in Enum.GetValues(typeof(Rarity))) Same(GameTerms.Rarity(r), r);
            foreach (Tribe t in Enum.GetValues(typeof(Tribe))) if (t != Tribe.NONE) Same(GameTerms.Tribe(t), t);
            foreach (SetId s in Enum.GetValues(typeof(SetId))) if (s != SetId.CORE) Same(GameTerms.Set(s), s);
            if (!Loc.IsSource) return 0;
            return wrong.Count == 0 ? 0 : Fail("EnglishTermsMatchWhatTheyReplaced", string.Join("; ", wrong));
        }

        // CardTextStyling bolds the generated English body by matching CardTextGenerator's words. A
        // glossary name that differs would print one word on the tooltip and another on the card.
        private static int KeywordNamesMatchTheBoldedVocabulary()
        {
            if (!Loc.IsSource) return 0;
            var vocab = new HashSet<string>(CardTextGenerator.EmphasisVocabulary());
            var wrong = new List<string>();
            foreach (Keyword k in Enum.GetValues(typeof(Keyword)))
                if (!vocab.Contains(GameTerms.Keyword(k))) wrong.Add($"{k} → \"{GameTerms.Keyword(k)}\"");
            foreach (string key in new[] { "label_toll", "label_attack", "label_damaged", "label_healed",
                         "label_turn_end", "label_spell_cast", "label_aura", "label_unearth",
                         "label_freeze", "label_unfreeze" })
                if (!vocab.Contains(Loc.Get(key))) wrong.Add($"{key} → \"{Loc.Get(key)}\"");
            return wrong.Count == 0 ? 0 : Fail("KeywordNamesMatchTheBoldedVocabulary",
                "not in CardTextGenerator.EmphasisVocabulary: " + string.Join(", ", wrong));
        }

        private static int AndroidFormatConverts()
        {
            var cases = new (string raw, string want)[]
            {
                ("%1$s + %2$s", "{0} + {1}"),
                ("Win %1$d match", "Win {0} match"),
                ("Don\\'t", "Don't"),
                ("\\\"Quoted\\\"", "\"Quoted\""),
                ("Line\\nbreak", "Line\nbreak"),
                ("100%% sure %1$s", "100% sure {0}"),
                ("50% off", "50% off"),
            };
            var wrong = new List<string>();
            foreach ((string raw, string want) in cases)
            {
                string got = AndroidStrings.Convert(raw);
                if (got != want) wrong.Add($"\"{raw}\" → \"{got}\", want \"{want}\"");
            }
            return wrong.Count == 0 ? 0 : Fail("AndroidFormatConverts", string.Join("; ", wrong));
        }

        private static int PluralCategories()
        {
            var cases = new (string lang, int n, string want)[]
            {
                ("en", 0, "other"), ("en", 1, "one"), ("en", 2, "other"),
                ("fr", 0, "one"), ("fr", 1, "one"), ("fr", 2, "other"),
                ("pt-BR", 0, "one"), ("de", 1, "one"), ("ja", 1, "other"),
                ("ru", 1, "one"), ("ru", 3, "few"), ("ru", 5, "many"), ("ru", 11, "many"), ("ru", 21, "one"),
                ("pl", 22, "few"), ("pl", 12, "many"), ("cs", 3, "few"), ("cs", 5, "other"),
            };
            var wrong = new List<string>();
            foreach ((string lang, int n, string want) in cases)
            {
                string got = PluralRules.Category(lang, n);
                if (got != want) wrong.Add($"{lang} {n}: {got}, want {want}");
            }
            return wrong.Count == 0 ? 0 : Fail("PluralCategories", string.Join("; ", wrong));
        }

        // A translated row must use exactly the English row's slots; reordering is fine.
        private static int TranslationsKeepTheirSlots()
        {
            Dictionary<string, string> source = ReadFolder(Path.Combine(TableRoot, "values"));
            var wrong = new List<string>();
            foreach (string dir in Directory.GetDirectories(TableRoot, "values-*"))
            {
                string lang = Path.GetFileName(dir).Substring("values-".Length);
                foreach (KeyValuePair<string, string> row in ReadFolder(dir))
                {
                    if (!source.TryGetValue(row.Key, out string english))
                    {
                        // A case form a card template asks for ({tribe:undead:plural:dat} reads
                        // tribe_undead_plural_dat): English has no cases, only the row it inflects.
                        int cut = row.Key.LastIndexOf('_');
                        if (cut > 0 && IsTermKey(row.Key) && source.ContainsKey(row.Key.Substring(0, cut))) continue;
                        wrong.Add($"{lang}/{row.Key}: no English row");
                        continue;
                    }
                    string want = Slots(english), got = Slots(row.Value);
                    if (want != got) wrong.Add($"{lang}/{row.Key}: slots [{got}], English [{want}]");
                }
            }
            return wrong.Count == 0 ? 0 : Fail("TranslationsKeepTheirSlots", string.Join("; ", wrong));
        }

        // A key missing from English prints as the key itself, which nobody reads until a player
        // reports it. Literal keys only; a key built at runtime (GameTerms) is covered above.
        private static int EveryKeyTheCodeAsksForExists()
        {
            Dictionary<string, string> source = ReadFolder(Path.Combine(TableRoot, "values"));
            var wrong = new SortedSet<string>();
            var call = new Regex(@"\bLoc\.(Get|Format|Plural)\(\s*""([^""]+)""\s*[,)]");
            foreach (string file in Directory.GetFiles("Assets/Scripts", "*.cs", SearchOption.AllDirectories))
                foreach (Match m in call.Matches(File.ReadAllText(file)))
                {
                    string key = m.Groups[2].Value;
                    bool found = m.Groups[1].Value == "Plural" ? source.ContainsKey(key + "#other") : source.ContainsKey(key);
                    if (!found) wrong.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value} \"{key}\"");
                }
            return wrong.Count == 0 ? 0 : Fail("EveryKeyTheCodeAsksForExists", string.Join("; ", wrong));
        }

        private static bool IsTermKey(string key)
            => key.StartsWith("tribe_") || key.StartsWith("label_") || key.StartsWith("keyword_");

        private static Dictionary<string, string> ReadFolder(string dir)
        {
            var rows = new Dictionary<string, string>();
            foreach (string file in Directory.GetFiles(dir, "*.xml"))
            {
                var doc = new XmlDocument();
                doc.Load(file);
                foreach (XmlNode node in doc.DocumentElement.ChildNodes)
                {
                    if (!(node is XmlElement el)) continue;
                    if (el.Name == "string") rows[el.GetAttribute("name")] = el.InnerText;
                    else if (el.Name == "plurals")
                        foreach (XmlNode item in el.ChildNodes)
                            if (item is XmlElement it)
                                rows[el.GetAttribute("name") + "#" + it.GetAttribute("quantity")] = it.InnerText;
                }
            }
            return rows;
        }

        private static string Slots(string text)
        {
            var slots = new SortedSet<string>();
            foreach (Match m in Regex.Matches(text, @"%\d+\$[sd]")) slots.Add(m.Value);
            return string.Join(" ", slots);
        }

        private static void Need(string key, List<string> missing)
        {
            if (Loc.Find(key) == null) missing.Add(key);
        }

        private static string Id(Enum value) => value.ToString().ToLowerInvariant();

        private static int Fail(string check, string detail)
        {
            Debug.LogError($"[LocalizationVerify] FAIL {check}: {detail}");
            return 1;
        }
    }
}
