using System.Linq;
using System.Text.RegularExpressions;
using Kingdom.Core.Cards;

namespace Kingdom.Presentation
{
    // CardTextGenerator (Kingdom.Core.Cards) emits plain prose — no markup, no delimiters — which
    // is what CardQuery's search index and ContentLinter's E7-TEXT-LENGTH check read, so the core
    // stays presentation-agnostic. Anything a player actually reads runs through this first, which
    // emphasises the game's own vocabulary: keyword names and timing labels become bold + a size
    // bump. Nothing is removed from the string; only weight is added.
    //
    // This used to bold whatever sat inside square brackets, with the generator emitting brackets
    // purely so this could strip them again. That is why "[Frost] a minion." looked like a card
    // face and was not one. The vocabulary is matched by name now, so the brackets are gone from
    // both ends (Sam, 2026-08-11).
    //
    // KNOWN LIMIT of matching by name: a card NAME quoted inside an effect — "Summon Vanguard
    // Sentinel." — is emphasised on its keyword word if it happens to contain one. The delimiter
    // form could not do that, and this is the trade accepted for not carrying punctuation the
    // player is never meant to see. Whole word and case-sensitive keeps it to that narrow case:
    // lowercase prose ("misses its next attack") never matches the "Attack" timing label.
    internal static class CardTextStyling
    {
        // Built from CardTextGenerator.EmphasisVocabulary so the styled words cannot drift from the
        // printed ones — a keyword added to the enum is emphasised here with no edit to this file.
        // The vocabulary arrives longest-first, so the alternation prefers "Turn Start" over a
        // shorter word sharing its opening.
        private static readonly Regex Vocabulary = new Regex(
            @"\b(?:" + string.Join("|", CardTextGenerator.EmphasisVocabulary().Select(Regex.Escape)) + @")\b",
            RegexOptions.Compiled);

        internal static string Style(string generatedText) =>
            string.IsNullOrEmpty(generatedText)
                ? generatedText
                : Vocabulary.Replace(generatedText, "<b><size=120%>$0</size></b>");

        // A translated card marks its vocabulary as slots, so CardLoc bolds by slot through this
        // instead of matching English words that a translation does not contain.
        internal static string Emphasise(string word) => "<b><size=120%>" + word + "</size></b>";
    }
}
