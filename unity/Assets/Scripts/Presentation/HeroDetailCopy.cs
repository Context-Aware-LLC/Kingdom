using System.Collections.Generic;
using Kingdom.Core.Contracts;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The trait bullets on the character-creation detail overlay (NOR-281), in the player's
    /// language: <c>hero_trait_&lt;class&gt;_1</c>, <c>_2</c> and on until a row is missing
    /// (values/heroes.xml). English is the source and the fallback, like every other table row.
    /// </summary>
    internal static class HeroDetailCopy
    {
        internal static string[] TraitsFor(CardClass heroClass)
        {
            var traits = new List<string>();
            string prefix = "hero_trait_" + heroClass.ToString().ToLowerInvariant() + "_";
            for (int n = 1; ; n++)
            {
                string line = Loc.Find(prefix + n);
                if (line == null) break;
                traits.Add(line);
            }
            return traits.ToArray();
        }
    }
}
