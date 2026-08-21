using System;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Turns a dotted or underscored id (<c>pack.emberfall</c>, <c>ember_flame_imp</c>) into
    /// something printable. Shared by the store and the pack reveal, which is the only reason it
    /// lives in neither.
    ///
    /// <para><b>A fallback, never a first choice.</b> Wherever a real display name is reachable —
    /// a card definition, a catalog offer — print that. This exists for the case where the id is
    /// genuinely all there is, and it will happily mangle anything else: it keeps only the last
    /// segment, so <c>pack.emberfall</c> reads "Emberfall" and a card id keeps only its name half.
    /// </para>
    /// </summary>
    internal static class TokenText
    {
        internal static string Pretty(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            int split = Math.Max(raw.LastIndexOf('.'), raw.LastIndexOf('_'));
            string token = split >= 0 ? raw.Substring(split + 1) : raw;
            token = token.Replace('-', ' ');
            return token.Length == 0
                ? token
                : char.ToUpperInvariant(token[0]) + token.Substring(1).ToLowerInvariant();
        }
    }
}
