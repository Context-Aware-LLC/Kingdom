using Kingdom.Core.Contracts;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Turning a <see cref="CardClass"/> or a <see cref="Rarity"/> into the strings the UI needs:
    /// a player-facing label, a lowercase slug, and USS class tokens.
    ///
    /// <para><b>Why one place.</b> Five screens were each spelling these out. Most were identical,
    /// but two had quietly drifted apart: <c>CollectionController.ClassLabel</c> replaced underscores
    /// with spaces and <c>CharacterRosterController.Pretty</c> did not, so the same class would have
    /// rendered differently on two screens the moment a class name contained one. No <c>CardClass</c>
    /// member has an underscore today, which is exactly why nobody noticed — the drift was real and
    /// unreachable at the same time. <see cref="Label"/> is the underscore-aware form, so the two
    /// agree now and stay agreeing.</para>
    ///
    /// <para><b>Rarity is deliberately NOT collapsed to one function.</b> Three screens map the same
    /// four rarities onto three DIFFERENT USS vocabularies — <c>rarity--</c> on the collection tile,
    /// <c>craft-rarity--</c> in the craft modal, <c>reveal-card--</c> on a pack reveal — because each
    /// sheet styles its own thing. That is not duplication to be merged into one token; it is one
    /// SHAPE over three vocabularies, so <see cref="RarityToken"/> takes the prefix. Collapsing them
    /// to a single string would have silently restyled two screens.</para>
    /// </summary>
    public static class CardClassTokens
    {
        /// <summary>Player-facing class name, in the player's language (<see cref="GameTerms.Class"/>).</summary>
        public static string Label(CardClass c) => GameTerms.Class(c);

        /// <summary>Lowercase identifier, for building a UXML element name or a USS token.</summary>
        public static string Slug(CardClass c) => c.ToString().ToLowerInvariant();

        /// <summary>The collection tile's class-edge token, e.g. <c>edge--gravekeep</c>.</summary>
        public static string Edge(CardClass c) => "edge--" + Slug(c);

        /// <summary>A rarity token under the caller's own vocabulary, e.g.
        /// <c>RarityToken(Rarity.EPIC, "craft-rarity--")</c> → <c>"craft-rarity--epic"</c>.</summary>
        public static string RarityToken(Rarity r, string prefix) => prefix + r switch
        {
            Rarity.RARE => "rare",
            Rarity.EPIC => "epic",
            Rarity.LEGENDARY => "legendary",
            _ => "common",
        };

        /// <summary>Every token this prefix can produce — what <see cref="SwapPrefixed"/> clears.</summary>
        public static string[] AllRarity(string prefix) =>
            new[] { prefix + "common", prefix + "rare", prefix + "epic", prefix + "legendary" };

        /// <summary>Every class-edge token.</summary>
        public static string[] AllEdge()
        {
            var values = (CardClass[])System.Enum.GetValues(typeof(CardClass));
            var outp = new string[values.Length];
            for (int i = 0; i < values.Length; i++) outp[i] = Edge(values[i]);
            return outp;
        }

        /// <summary>Leave exactly one token from <paramref name="all"/> on the element.
        ///
        /// <para>Written as enable-one/disable-the-rest rather than remove-then-add: an element that
        /// briefly carries none of them resolves to the unstyled default for a frame, and an element
        /// that carries two resolves by sheet order rather than by intent.</para></summary>
        public static void SwapPrefixed(VisualElement e, string[] all, string keep)
        {
            foreach (var c in all) e.EnableInClassList(c, c == keep);
        }
    }
}
