using Kingdom.Core.Contracts;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The player-facing name of every engine enum a player reads: keywords, tribes, classes, heroes,
    /// card types, rarities, sets. One lookup per vocabulary, read from <c>glossary.xml</c> through
    /// <see cref="Loc"/>, so a card face, a filter chip and a tooltip cannot name the same thing two
    /// ways — in English or in any translation.
    ///
    /// <para>Keys are the enum id in lower case (<c>keyword_arcane_surge</c>, <c>tribe_undead</c>), so a
    /// member added to an enum without a glossary row shows its key on screen rather than a guess.</para>
    /// </summary>
    public static class GameTerms
    {
        public static string Keyword(Keyword kw) => Loc.Get("keyword_" + Id(kw));
        public static string KeywordGlossary(Keyword kw) => Loc.Find("keyword_" + Id(kw) + "_desc");
        public static string Tribe(Tribe t) => Loc.Get("tribe_" + Id(t));
        public static string TribePlural(Tribe t) => Loc.Get("tribe_" + Id(t) + "_plural");
        public static string Class(CardClass c) => Loc.Get("class_" + Id(c));

        /// <summary>The class's hero, or "" for a class without one (Neutral).</summary>
        public static string Hero(CardClass c) => Loc.Find("hero_" + Id(c)) ?? "";

        public static string Type(CardType t) => Loc.Get("type_" + Id(t));
        public static string Rarity(Rarity r) => Loc.Get("rarity_" + Id(r));
        public static string Set(SetId s) => Loc.Get("set_" + Id(s));

        private static string Id<T>(T value) where T : System.Enum => value.ToString().ToLowerInvariant();
    }
}
