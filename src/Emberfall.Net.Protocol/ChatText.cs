using System.Globalization;
using System.Text;

namespace Emberfall.Net.Protocol
{
    /// <summary>
    /// What a chat message may contain (#409, VULN-004). One predicate for both ends: the client
    /// runs it so the player sees the trimmed text before sending, and <c>SocialHub</c> runs it
    /// again on arrival — <b>the server's run is the one that counts</b>, because a patched client
    /// can send anything. A rule relaxed here is relaxed on the wire.
    ///
    /// <para><b>Normalize, do not refuse, wherever the player can see what they typed.</b>
    /// Whitespace runs (newlines included) collapse to one space. Invisible characters — control,
    /// format, private use, an unpaired surrogate — are DROPPED rather than refused: the player
    /// cannot see them, so a refusal would point at nothing, and a bidi override (U+202E) or a
    /// zero-width character is a spoofing primitive, not text. A run of combining marks is cut to
    /// <see cref="MaxMarksPerBase"/>, which keeps every script that needs marks and stops a
    /// "Zalgo" stack from painting over the lines above it.</para>
    ///
    /// <para><b>Length is measured after cleaning</b>, in UTF-16 units, against
    /// <see cref="MaxLength"/>. It bounds the string, not the pixels: every renderer must still
    /// wrap or clip, with rich text off.</para>
    /// </summary>
    public static class ChatText
    {
        public const int MaxLength = 200;

        public const int MaxMarksPerBase = 3;

        /// <summary>The cleaned text, or null when nothing sendable is left or it is too long.</summary>
        public static string? Clean(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;

            var sb = new StringBuilder(raw!.Length);
            bool pendingSpace = false;
            int marks = 0;

            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];

                if (char.IsHighSurrogate(c))
                {
                    // Kept only as a whole pair: an emoji is fine, half of one is garbage.
                    if (i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]))
                    {
                        UnicodeCategory pairCat = CharUnicodeInfo.GetUnicodeCategory(raw, i);
                        if (pairCat != UnicodeCategory.PrivateUse && pairCat != UnicodeCategory.Format)
                        {
                            Flush(sb, ref pendingSpace);
                            sb.Append(c).Append(raw[i + 1]);
                            marks = 0;
                        }
                        i++;
                    }
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }

                switch (CharUnicodeInfo.GetUnicodeCategory(c))
                {
                    case UnicodeCategory.Control:
                    case UnicodeCategory.Format:
                    case UnicodeCategory.PrivateUse:
                    case UnicodeCategory.Surrogate:
                    case UnicodeCategory.OtherNotAssigned:
                        continue;

                    case UnicodeCategory.NonSpacingMark:
                    case UnicodeCategory.SpacingCombiningMark:
                    case UnicodeCategory.EnclosingMark:
                        // A mark with no base (start, or straight after a space) has nothing to sit on.
                        if (sb.Length == 0 || pendingSpace || marks >= MaxMarksPerBase) continue;
                        marks++;
                        sb.Append(c);
                        continue;

                    default:
                        Flush(sb, ref pendingSpace);
                        sb.Append(c);
                        marks = 0;
                        continue;
                }
            }

            if (sb.Length == 0 || sb.Length > MaxLength) return null;
            return sb.ToString();
        }

        private static void Flush(StringBuilder sb, ref bool pendingSpace)
        {
            if (pendingSpace) sb.Append(' ');
            pendingSpace = false;
        }
    }
}
