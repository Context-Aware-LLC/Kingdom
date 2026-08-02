using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Standing guard (EF-041): walks every literal Addressable address string in
    /// Assets/Scripts and asserts each one resolves in the current
    /// AddressableAssetSettings. Wired into HeadlessVerify.RunAll() so the check
    /// runs with every local pre-push verify pass.
    ///
    /// <para>Only non-interpolated string literals are extracted — dynamic addresses
    /// such as <c>$"card_art/{fileName}"</c> contain <c>{</c> which the pattern
    /// excludes. C-style comments are stripped before scanning so example addresses
    /// in doc comments do not produce false positives.</para>
    ///
    /// <para>If this check fails, run
    /// <b>Emberfall / Addressables / Setup Groups + Migrate Card Data</b> then
    /// commit the updated <c>AddressableAssetsData/</c> files.</para>
    /// </summary>
    public static class AddressablesGuard
    {
        private static readonly string[] KnownPrefixes =
        {
            "backgrounds/", "fonts/", "loading/", "cards/",
            "ui/", "card_art/", "materials/",
        };

        // Matches a non-interpolated string literal whose payload starts with a known
        // address prefix. The negative lookbehind on $ excludes $"..." interpolated forms.
        // The name section allows at most one internal space so font names such as
        // "AlfaSlabOne-Regular SDF" match, while multi-word error messages do not.
        // '{', '/', and whitespace are excluded from each name word to reject interpolation
        // holes, multi-segment file paths, and long prose respectively.
        private static readonly Regex AddressLiteral = new Regex(
            @"(?<!\$)""((?:backgrounds|fonts|loading|cards|ui|card_art|materials)/[^""\{\s/]+(?:\s[^""\{\s/]+)?)""",
            RegexOptions.Compiled);

        private static readonly Regex LineComment  = new Regex(@"//[^\n]*", RegexOptions.Compiled);
        private static readonly Regex BlockComment = new Regex(@"/\*.*?\*/",
            RegexOptions.Compiled | RegexOptions.Singleline);

        [MenuItem("Emberfall/Addressables/Guard — Verify All Script Addresses")]
        public static void Check()
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            if (settings == null)
            {
                Debug.LogError("[AddressablesGuard] AddressableAssetSettings not found — run Setup Groups + Migrate Card Data first.");
                return;
            }

            var registered = new HashSet<string>(System.StringComparer.Ordinal);
            var allEntries = new List<AddressableAssetEntry>();
            settings.GetAllAssets(allEntries, false);
            foreach (AddressableAssetEntry e in allEntries)
                if (e?.address != null) registered.Add(e.address);

            string scriptsRoot = Path.Combine(Application.dataPath, "Scripts");
            if (!Directory.Exists(scriptsRoot))
            {
                Debug.LogError($"[AddressablesGuard] Scripts folder not found: {scriptsRoot}");
                return;
            }

            string[] csFiles = Directory.GetFiles(scriptsRoot, "*.cs", SearchOption.AllDirectories);
            var missing = new List<(string address, string file)>();
            var seen    = new HashSet<string>(System.StringComparer.Ordinal);

            foreach (string file in csFiles)
            {
                string text = File.ReadAllText(file, System.Text.Encoding.UTF8);
                text = BlockComment.Replace(LineComment.Replace(text, ""), "");
                string relPath = "Assets/Scripts" + file.Substring(scriptsRoot.Length).Replace('\\', '/');

                foreach (Match m in AddressLiteral.Matches(text))
                {
                    string addr = m.Groups[1].Value;
                    bool isKnown = false;
                    foreach (string prefix in KnownPrefixes)
                        if (addr.StartsWith(prefix, System.StringComparison.Ordinal))
                        { isKnown = true; break; }
                    if (!isKnown || !seen.Add(addr)) continue;

                    if (!registered.Contains(addr))
                        missing.Add((addr, relPath));
                }
            }

            if (missing.Count == 0)
            {
                Debug.Log($"[AddressablesGuard] {seen.Count} literal address(es) checked — all registered. OK.");
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[AddressablesGuard] {missing.Count} unregistered address(es) found — run Setup Groups + Migrate Card Data first:");
                foreach (var (addr, f) in missing)
                    sb.AppendLine($"  MISSING  {addr}  (first seen in {f})");
                Debug.LogError(sb.ToString());
            }
        }
    }
}
