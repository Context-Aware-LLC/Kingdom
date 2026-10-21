using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The player's language and every translated string the client shows (Sam, 2026-10-20).
    /// Tables are Android-format <c>strings.xml</c> files under
    /// <c>Resources/Localization/values[-&lt;lang&gt;]/</c> — the format translators and translation
    /// services already take — and each folder may hold several files (<c>strings</c>, <c>glossary</c>).
    ///
    /// <para><b>English is the source, and the fallback for every miss.</b> A key missing from the
    /// active language reads the English row; a key missing from English reads as the key itself,
    /// so an unwired string is visible in testing rather than blank. Placeholders are Android
    /// positional (<c>%1$s</c>, <c>%2$d</c>), converted once at load to <c>string.Format</c> slots.</para>
    ///
    /// <para>The language is the player's saved choice, else the device language when a table for
    /// it ships, else English. See <c>docs/dev-docs/specs/localization.md</c>.</para>
    /// </summary>
    public static class Loc
    {
        public const string Source = "en";
        private const string PrefsKey = "settings.language";
        private const string Root = "Localization/";
        private static readonly string[] TableFiles = { "strings", "glossary" };

        private sealed class Table
        {
            public readonly Dictionary<string, string> Strings = new Dictionary<string, string>();
            public readonly Dictionary<string, Dictionary<string, string>> Plurals =
                new Dictionary<string, Dictionary<string, string>>();
        }

        private static Table _source;
        private static Table _active;
        private static string _language;

        /// <summary>Raised after <see cref="SetLanguage"/> changes the active table.</summary>
        public static event Action LanguageChanged;

        /// <summary>The active language code, e.g. <c>en</c>, <c>de</c>, <c>pt-BR</c>.</summary>
        public static string Language { get { EnsureLoaded(); return _language; } }

        /// <summary>True while the player reads the source language, so callers can skip work.</summary>
        public static bool IsSource => Language == Source;

        /// <summary>The string for <paramref name="key"/>, falling back to English, then the key.</summary>
        public static string Get(string key) => Find(key) ?? key;

        /// <summary>The string for <paramref name="key"/>, or null when not even English has it.</summary>
        public static string Find(string key)
        {
            EnsureLoaded();
            if (key == null) return null;
            if (_active.Strings.TryGetValue(key, out string s)) return s;
            return _source.Strings.TryGetValue(key, out s) ? s : null;
        }

        /// <summary>The string for <paramref name="key"/> with its <c>%n$</c> slots filled.</summary>
        public static string Format(string key, params object[] args) => Fill(Get(key), args);

        /// <summary>
        /// The plural form of <paramref name="key"/> that <paramref name="count"/> takes in the active
        /// language, slots filled. The count is not passed as a slot on its own; include it in
        /// <paramref name="args"/> where the string prints it.
        /// </summary>
        public static string Plural(string key, int count, params object[] args)
        {
            EnsureLoaded();
            string form = PluralRules.Category(_language, count);
            string s = PickPlural(_active, key, form) ?? PickPlural(_source, key, PluralRules.Category(Source, count));
            return s == null ? key : Fill(s, args);
        }

        /// <summary>The key whose ENGLISH text is exactly <paramref name="english"/>, or null.</summary>
        public static string KeyForSource(string english)
        {
            EnsureLoaded();
            return english != null && ReverseSource.TryGetValue(english, out string key) ? key : null;
        }

        /// <summary>Switches language, persists the choice, and tells listeners to redraw.</summary>
        public static void SetLanguage(string code)
        {
            EnsureLoaded();
            code = Supported(code) ? code : Source;
            PlayerPrefs.SetString(PrefsKey, code);
            if (code == _language) return;
            Activate(code);
            LanguageChanged?.Invoke();
        }

        /// <summary>Every language this game targets, in priority order, with its name in itself.
        /// <see cref="Available"/> keeps the ones whose table ships.</summary>
        private static readonly (string Code, string Name)[] Targets =
        {
            ("en", "English"), ("de", "Deutsch"), ("fr", "Français"), ("es-419", "Español (Latinoamérica)"),
            ("pt-BR", "Português (Brasil)"), ("ja", "日本語"), ("ko", "한국어"), ("it", "Italiano"),
            ("pl", "Polski"), ("tr", "Türkçe"), ("ru", "Русский"), ("zh-TW", "繁體中文"),
            ("id", "Bahasa Indonesia"), ("th", "ไทย"), ("nl", "Nederlands"), ("uk", "Українська"),
            ("cs", "Čeština"), ("ar", "العربية"),
        };

        /// <summary>The languages a player can pick: English, and each target whose table ships.</summary>
        public static IReadOnlyList<string> Available()
        {
            var codes = new List<string>();
            foreach ((string code, string _) in Targets)
                if (Supported(code)) codes.Add(code);
            return codes;
        }

        /// <summary>A language's name written in that language — "Deutsch", never "German".</summary>
        public static string NativeName(string code)
        {
            foreach ((string c, string name) in Targets)
                if (c == code) return name;
            return code;
        }

        private static Dictionary<string, string> ReverseSource;

        private static void EnsureLoaded()
        {
            if (_source != null) return;
            _source = LoadTable(Source);
            ReverseSource = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> kv in _source.Strings)
                if (!ReverseSource.ContainsKey(kv.Value)) ReverseSource.Add(kv.Value, kv.Key);
            Activate(PlayerPrefs.GetString(PrefsKey, DeviceLanguage()));
        }

        private static void Activate(string code)
        {
            Table table = code == Source ? _source : LoadTable(code);
            if (table.Strings.Count == 0 && table.Plurals.Count == 0) { code = Source; table = _source; }
            _language = code;
            _active = table;
        }

        private static bool Supported(string code) =>
            code == Source || Resources.Load<TextAsset>(Root + "values-" + code + "/strings") != null;

        /// <summary>The device language as a table code; Unity's enum has no region, so the region
        /// is the one this game targets (Brazilian Portuguese, Latin-American Spanish).</summary>
        private static string DeviceLanguage()
        {
            switch (Application.systemLanguage)
            {
                case SystemLanguage.German: return "de";
                case SystemLanguage.French: return "fr";
                case SystemLanguage.Spanish: return "es-419";
                case SystemLanguage.Portuguese: return "pt-BR";
                case SystemLanguage.Italian: return "it";
                case SystemLanguage.Japanese: return "ja";
                case SystemLanguage.Korean: return "ko";
                case SystemLanguage.Polish: return "pl";
                case SystemLanguage.Turkish: return "tr";
                case SystemLanguage.Russian: return "ru";
                case SystemLanguage.Ukrainian: return "uk";
                case SystemLanguage.ChineseTraditional: return "zh-TW";
                case SystemLanguage.Indonesian: return "id";
                case SystemLanguage.Thai: return "th";
                case SystemLanguage.Dutch: return "nl";
                case SystemLanguage.Czech: return "cs";
                case SystemLanguage.Arabic: return "ar";
                default: return Source;
            }
        }

        private static Table LoadTable(string code)
        {
            var table = new Table();
            string folder = Root + (code == Source ? "values" : "values-" + code) + "/";
            foreach (string file in TableFiles)
            {
                var asset = Resources.Load<TextAsset>(folder + file);
                if (asset != null) AndroidStrings.Read(asset.text, table.Strings, table.Plurals);
            }
            return table;
        }

        private static string PickPlural(Table table, string key, string form)
        {
            if (!table.Plurals.TryGetValue(key, out Dictionary<string, string> forms)) return null;
            if (forms.TryGetValue(form, out string s)) return s;
            return forms.TryGetValue("other", out s) ? s : null;
        }

        private static string Fill(string format, object[] args)
        {
            if (args == null || args.Length == 0) return format;
            try { return string.Format(CultureInfo.CurrentCulture, format, args); }
            catch (FormatException) { return format; }
        }
    }

    /// <summary>
    /// Reads Android <c>strings.xml</c>: <c>&lt;string&gt;</c> and <c>&lt;plurals&gt;</c> rows, backslash
    /// escapes, and <c>%n$s</c>/<c>%n$d</c> slots rewritten as <c>{n-1}</c> for <c>string.Format</c>.
    /// Literal braces are not escaped: no row carries one, and <see cref="Loc"/> falls back to the
    /// raw pattern when a format throws.
    /// </summary>
    internal static class AndroidStrings
    {
        internal static void Read(string xml, Dictionary<string, string> strings,
            Dictionary<string, Dictionary<string, string>> plurals)
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            foreach (XmlNode node in doc.DocumentElement.ChildNodes)
            {
                if (!(node is XmlElement el)) continue;
                string name = el.GetAttribute("name");
                if (name.Length == 0) continue;
                if (el.Name == "string")
                {
                    strings[name] = Convert(el.InnerText);
                }
                else if (el.Name == "plurals")
                {
                    var forms = new Dictionary<string, string>();
                    foreach (XmlNode item in el.ChildNodes)
                        if (item is XmlElement it && it.Name == "item")
                            forms[it.GetAttribute("quantity")] = Convert(it.InnerText);
                    plurals[name] = forms;
                }
            }
        }

        /// <summary>Android escapes and slots to a <c>string.Format</c> pattern.</summary>
        internal static string Convert(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '\\' && i + 1 < raw.Length)
                {
                    char n = raw[++i];
                    sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n);
                }
                else if (c == '%' && i + 1 < raw.Length)
                {
                    if (raw[i + 1] == '%') { sb.Append('%'); i++; continue; }
                    int j = i + 1, slot = 0;
                    while (j < raw.Length && char.IsDigit(raw[j])) slot = slot * 10 + (raw[j++] - '0');
                    if (slot > 0 && j + 1 < raw.Length && raw[j] == '$' && (raw[j + 1] == 's' || raw[j + 1] == 'd'))
                    {
                        sb.Append('{').Append(slot - 1).Append('}');
                        i = j + 1;
                    }
                    else sb.Append(c);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>CLDR cardinal plural categories for the languages this game targets.</summary>
    internal static class PluralRules
    {
        internal static string Category(string language, int n)
        {
            int abs = Math.Abs(n), mod10 = abs % 10, mod100 = abs % 100;
            switch (language)
            {
                case "ja": case "ko": case "zh-TW": case "id": case "th": case "vi": case "tr":
                    return language == "tr" && abs == 1 ? "one" : "other";
                case "fr": case "pt-BR":
                    return abs <= 1 ? "one" : "other";
                case "ru": case "uk":
                    if (mod10 == 1 && mod100 != 11) return "one";
                    if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return "few";
                    return "many";
                case "pl":
                    if (abs == 1) return "one";
                    if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)) return "few";
                    return "many";
                case "cs":
                    return abs == 1 ? "one" : abs >= 2 && abs <= 4 ? "few" : "other";
                case "ar":
                    if (abs == 0) return "zero";
                    if (abs == 1) return "one";
                    if (abs == 2) return "two";
                    if (mod100 >= 3 && mod100 <= 10) return "few";
                    if (mod100 >= 11) return "many";
                    return "other";
                default:
                    return abs == 1 ? "one" : "other";
            }
        }
    }
}
