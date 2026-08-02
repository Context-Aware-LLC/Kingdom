using System.Collections.Generic;
using System.Text;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Presentation
{
    /// <summary>
    /// EF-049. Turns a card definition plus its LIVE instance into the three blocks of text the
    /// reveal panel shows. Pure — no UnityEngine types — so CardRevealVerify can assert the copy
    /// headlessly instead of the panel being verifiable only by looking at it.
    ///
    /// <para><b>Live state, not just the definition.</b> A buffed, silenced or damaged minion reads
    /// nothing like its printed card, and the reveal is the surface a player uses to work out
    /// whether a trade is good. Printed stats here would be worse than no panel, because they would
    /// be confidently wrong. Where the two differ the panel shows the live value and says so.</para>
    /// </summary>
    internal readonly struct CardRevealContent
    {
        /// <summary>Card name — display face, one line.</summary>
        internal string Title { get; }

        /// <summary>Cost, type and live stats — one line, always populated.</summary>
        internal string Subtitle { get; }

        /// <summary>Authored rules text followed by the glossary for every keyword the card
        /// currently carries. The glossary is the reason this panel exists: the card face has room
        /// for a keyword's NAME and never for what it does.</summary>
        internal string Body { get; }

        private CardRevealContent(string title, string subtitle, string body)
        {
            Title    = title;
            Subtitle = subtitle;
            Body     = body;
        }

        /// <summary>
        /// Stats and keywords come from <paramref name="live"/>, so granted keywords appear and
        /// silenced ones disappear. Pass null for a card with no live instance (a collection
        /// browser, a preview); the printed definition is then the whole truth.
        /// </summary>
        internal static CardRevealContent Build(CardDefinition def, ICardLiveState live)
        {
            if (def == null) return new CardRevealContent(null, null, null);

            IReadOnlyCollection<Keyword> keywords =
                live != null ? live.Keywords : def.Keywords;

            return new CardRevealContent(
                def.Name,
                BuildSubtitle(def, live),
                BuildBody(def, keywords));
        }

        private static string BuildSubtitle(CardDefinition def, ICardLiveState live)
        {
            var sb = new StringBuilder();
            sb.Append(def.Cost).Append(" mana  ·  ").Append(TypeWord(def.Type));

            // Attack/Health/Durability are nullable on the definition — a spell carries none of
            // them — so every read is defaulted rather than assumed present.
            switch (def.Type)
            {
                case CardType.MINION:
                {
                    int printedAtk = def.Attack ?? 0;
                    int printedHp  = def.Health ?? 0;
                    int atk = live != null ? live.Attack : printedAtk;
                    int hp  = live != null ? live.Health : printedHp;
                    sb.Append("  ·  ").Append(atk).Append(" / ").Append(hp);
                    // Only worth the words when they actually disagree — on an untouched minion the
                    // annotation is noise, and noise in a panel this small costs a glossary line.
                    if (live != null && (atk != printedAtk || hp != printedHp))
                        sb.Append("   (printed ").Append(printedAtk).Append('/').Append(printedHp).Append(')');
                    break;
                }

                case CardType.WEAPON:
                    sb.Append("  ·  ").Append(def.Attack ?? 0).Append(" / ").Append(def.Durability ?? 0);
                    break;
            }

            return sb.ToString();
        }

        private static string BuildBody(CardDefinition def, IReadOnlyCollection<Keyword> keywords)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(def.CardText))
                sb.Append(def.CardText);

            if (keywords != null)
            {
                foreach (Keyword kw in keywords)
                {
                    string line = KeywordCopy.Glossary(kw);
                    // Null means the enum gained a value and nobody wrote copy for it. Skipping
                    // keeps the panel readable; CardRevealVerify is what refuses to let it ship.
                    if (string.IsNullOrEmpty(line)) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append("<b>").Append(KeywordCopy.DisplayName(kw)).Append("</b>  ").Append(line);
                }
            }

            if (!string.IsNullOrEmpty(def.FlavorText))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("<i>").Append(def.FlavorText).Append("</i>");
            }

            return sb.ToString();
        }

        private static string TypeWord(CardType type) => type switch
        {
            CardType.MINION     => "Minion",
            CardType.SPELL      => "Spell",
            CardType.WEAPON     => "Weapon",
            CardType.HERO_POWER => "Hero Power",
            _                   => type.ToString(),
        };
    }
}
