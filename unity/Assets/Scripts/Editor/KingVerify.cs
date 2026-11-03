using System;
using System.Collections.Generic;
using System.IO;
using Kingdom.Net.Protocol;
using Kingdom.Presentation;
using UnityEditor;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>
    /// Every word the King can be given has an English row (King of the Week, #421). The server sends
    /// pool indices and a commission id, and <see cref="KingText"/> builds the keys at runtime, so
    /// <c>LocalizationVerify.EveryKeyTheCodeAsksForExists</c>, which reads literal keys only, cannot
    /// see them. A missing row would print the fallback on the menu and in the Roll for a whole week.
    ///
    /// <para>Checks every epithet and fate the server's pools can name
    /// (<see cref="CommissionIds.EpithetCount"/>, <see cref="CommissionIds.FateCount"/>), every part of
    /// every commission in <see cref="CommissionIds.All"/>, which a server test holds every epoch of
    /// the schedule to, that no two epithets or fates read the same, and that every one of those
    /// commissions has its card image on disk. Reads the English table directly, so the player's
    /// language cannot cover a gap.
    /// Registered in <see cref="HeadlessVerify"/>.</para>
    /// </summary>
    public static class KingVerify
    {
        private const string EnglishTable = "Assets/Resources/Localization/values";
        private const string AchievementsFolder = "Assets/Art/UI/Achievements";

        [MenuItem("Kingdom/Verify/King of the Week")]
        public static void Run()
        {
            Dictionary<string, string> english = ReadEnglish();
            var missing = new List<string>();
            var repeated = new List<string>();

            CheckPool("epithet", CommissionIds.EpithetCount, KingText.EpithetKey, english, missing, repeated);
            CheckPool("fate", CommissionIds.FateCount, KingText.FateKey, english, missing, repeated);
            var noSprite = new List<string>();
            foreach (string id in CommissionIds.All)
            {
                foreach (string part in KingText.CommissionParts)
                    Need(KingText.CommissionKey(id, part), english, missing);
                string sprite = SpritePath(id);
                if (!File.Exists(sprite)) noSprite.Add(sprite);
            }

            if (missing.Count > 0)
                throw new InvalidOperationException("No English row for " + string.Join(", ", missing) + ".");
            if (repeated.Count > 0)
                throw new InvalidOperationException("Repeated in the King's pools: " + string.Join("; ", repeated) + ".");
            // The commission card draws this image; a missing one leaves a blank where the card's
            // whole identity should be, and the sprite provider only warns.
            if (noSprite.Count > 0)
                throw new InvalidOperationException("No commission image at " + string.Join(", ", noSprite) + ".");

            Debug.Log($"[KingVerify] PASS: {CommissionIds.EpithetCount} epithets, {CommissionIds.FateCount} fates, " +
                      $"{CommissionIds.All.Count} commissions x {KingText.CommissionParts.Length} parts, " +
                      $"{CommissionIds.All.Count} commission images.");
        }

        /// <summary>Where the commission card's image for <paramref name="id"/> lives. Under
        /// <c>Assets/Art/UI/</c>, so it ships in UI_Core at <c>ui/achievement_{id}</c>.</summary>
        private static string SpritePath(string id) => $"{AchievementsFolder}/{KingText.AchievementSpriteKey(id)}.png";

        private static void CheckPool(string pool, int count, Func<int, string> key,
            Dictionary<string, string> english, List<string> missing, List<string> repeated)
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 0; index < count; index++)
            {
                string k = key(index);
                if (!Need(k, english, missing)) continue;
                string text = english[k];
                if (seen.TryGetValue(text, out string first)) repeated.Add($"{pool} {k} reads as {first}");
                else seen.Add(text, k);
            }
        }

        private static bool Need(string key, Dictionary<string, string> english, List<string> missing)
        {
            if (english.TryGetValue(key, out string text) && !string.IsNullOrWhiteSpace(text)) return true;
            missing.Add(key);
            return false;
        }

        private static Dictionary<string, string> ReadEnglish()
        {
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            var plurals = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(EnglishTable, "*.xml"))
                AndroidStrings.Read(File.ReadAllText(file), strings, plurals);
            if (strings.Count == 0)
                throw new InvalidOperationException($"Read no English rows under {EnglishTable}: this check verified nothing.");
            return strings;
        }
    }
}
