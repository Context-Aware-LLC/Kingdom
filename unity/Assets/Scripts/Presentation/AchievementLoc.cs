using Kingdom.Net.Protocol;

namespace Kingdom.Presentation
{
    /// <summary>
    /// An achievement's text in the player's language. <see cref="AchievementTexts"/> stays the
    /// source, because the server hands Play Console its description; the tables carry a copy keyed
    /// <c>achievement_&lt;id&gt;_name</c>, <c>_plain</c> and <c>_citation</c> (values/achievements.xml),
    /// which <c>LocalizationVerify</c> holds to the source. A row a table lacks reads the source.
    /// </summary>
    internal static class AchievementLoc
    {
        public static string Name(AchievementText text) => Read(text, "name", text.Name);
        public static string Plain(AchievementText text) => Read(text, "plain", text.Plain);
        public static string Citation(AchievementText text) => Read(text, "citation", text.Citation);

        public static string Key(string id, string part) => "achievement_" + id + "_" + part;

        private static string Read(AchievementText text, string part, string source) =>
            Loc.Find(Key(text.Id, part)) ?? source;
    }
}
