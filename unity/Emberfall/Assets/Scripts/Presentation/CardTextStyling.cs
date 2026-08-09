using System.Text.RegularExpressions;

namespace Emberfall.Presentation
{
    // CardTextGenerator (Emberfall.Core.Cards) marks triggers and keywords with literal
    // "[Name]" brackets — that plain-text form is what CardQuery's search index and
    // ContentLinter's E7-TEXT-LENGTH check read, so the core stays presentation-agnostic.
    // Anything a player actually reads runs the generated text through this first: the
    // bracket becomes bold + a size bump instead of literal punctuation on the card face.
    internal static class CardTextStyling
    {
        private static readonly Regex Marker = new Regex(@"\[([^\]]+)\]", RegexOptions.Compiled);

        internal static string Style(string generatedText) =>
            string.IsNullOrEmpty(generatedText)
                ? generatedText
                : Marker.Replace(generatedText, "<b><size=120%>$1</size></b>");
    }
}
