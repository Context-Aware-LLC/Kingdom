using System;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Turns a dotted or underscored id (<c>pack.emberfall</c>, <c>ember_flame_imp</c>) into
    /// something printable. Shared by the store and the pack reveal, which is the only reason it
    /// lives in neither.
    ///
    /// <para><b>A fallback, never a first choice.</b> Wherever a real display name is reachable —
    /// a card definition, a catalog offer — print that. This exists for the case where the id is
    /// genuinely all there is, and it will happily mangle anything else: it keeps only the last
    /// segment, so <c>pack.emberfall</c> reads "Kingdom" and a card id keeps only its name half.
    /// </para>
    /// </summary>
    internal static class TokenText
    {
        /// <summary>What a player calls a pack: "pack.emberfall" → "Pendingham",
        /// "pack.emberfall.starter" → "Pendingham Starter". The setId stays the codename
        /// (<c>Kingdom</c>, issue #397); only this text changes, so every pack name a player sees
        /// goes through here rather than through <see cref="Pretty"/> on the raw id.</summary>
        internal static string PackName(string packId)
        {
            string[] parts = packId?.Split('.');
            if (parts == null || parts.Length < 2) return "Pack";
            string name = parts[1] == "emberfall" ? "Pendingham" : Pretty(parts[1]);
            if (name.Length == 0) return "Pack";
            return parts.Length > 2 ? name + " " + Pretty(parts[2]) : name;
        }

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
