using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Kingdom.Core.Contracts;

namespace Kingdom.Net.Server.Telemetry
{
    /// <summary>
    /// `deckId` + `deckHash` for the telemetry-spec §1 per-seat block.
    ///
    /// <para>The two are not redundant. <c>deckId</c> is a NAME and can be renamed; <c>deckHash</c>
    /// is CONTENT and cannot. Archetype rollups key on the hash precisely so that renaming a deck
    /// does not fork its history, and so two people who independently built the same 30 cards land
    /// in the same bucket.</para>
    /// </summary>
    public static class DeckIdentity
    {
        /// <summary>
        /// B1 deals one fixed prebuilt pair (<c>PrebuiltDeckProvider</c>), so the class names the
        /// deck. B2 replaces this the moment clients supply their own lists — at which point the id
        /// comes from the client and the hash below is what keeps it honest.
        /// </summary>
        public static string IdFor(CardClass @class) => "prebuilt-" + @class.ToString().ToLowerInvariant();

        /// <summary>
        /// Content hash of a decklist. SORTED before hashing on purpose: deck ORDER is a shuffle
        /// artifact, not deck identity, so two draws of the same list must hash alike. Truncated to
        /// 128 bits, matching <c>AccountId</c>/<c>MatchId</c> so every id in a telemetry row is the
        /// same eyeball-comparable shape.
        /// </summary>
        public static string HashOf(IReadOnlyList<int> cardIds)
        {
            // NUMERIC sort, so the digest of a given deck CHANGED with the numeric id migration —
            // both because the ids are different tokens and because ordinal order over handles is
            // not numeric order. Rows written before the migration carry the old digest and are not
            // comparable to rows written after it.
            var sorted = new List<int>(cardIds);
            sorted.Sort();

            var joined = new StringBuilder();
            foreach (int id in sorted) { joined.Append(id); joined.Append('\n'); }

            System.Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(joined.ToString()), digest);

            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(digest[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
