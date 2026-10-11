using System.Text;

namespace Kingdom.Net.Protocol
{
    /// <summary>
    /// The friend code — the public handle one player gives another to be added as a friend
    /// (#409; Sam's ruling 2026-10-01: a code, not a username or an email, because some accounts
    /// have no email).
    ///
    /// <para><b>The code is the ONLY identifier of another player that reaches a client.</b> The
    /// server's <c>player_id</c> keys the entitlement ledger and never leaves it; every social verb
    /// names its target by code.</para>
    ///
    /// <para><b>Shape.</b> Nine data symbols and one check symbol over a 32-symbol alphabet with
    /// no <c>0</c>/<c>O</c>/<c>1</c>/<c>I</c>, shown as <c>XXXXX-XXXXX</c>. The canonical form —
    /// what is stored and what travels — is the ten symbols with no hyphen.</para>
    ///
    /// <para><b>The check symbol is a CRC-5 (x^5 + x^2 + 1) over the 45 data bits.</b> The
    /// generator is irreducible, so every single-symbol substitution and every swap of two adjacent
    /// symbols fails the check — the two mistakes a person reading a code aloud actually makes.
    /// It lives in this assembly, not the server, so the client can refuse a typo without a round
    /// trip; that costs nothing, because a code that passes the check tells an attacker only what
    /// they could compute themselves.</para>
    /// </summary>
    public static class FriendCode
    {
        /// <summary>32 symbols, five bits each. Order is the encoding — never reorder it.</summary>
        public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

        public const int DataLength = 9;
        public const int Length = DataLength + 1;

        /// <summary>How many distinct codes exist: 32^9.</summary>
        public const long Space = 1L << (5 * DataLength);

        private const int Generator = 0x05;   // x^5 + x^2 + 1, the x^5 term implicit

        /// <summary>The canonical code for a value in <c>[0, <see cref="Space"/>)</c>. Higher bits
        /// are ignored, so any random 64-bit value is a valid input.</summary>
        public static string FromValue(long value)
        {
            var chars = new char[Length];
            int crc = 0;
            for (int i = 0; i < DataLength; i++)
            {
                int symbol = (int)((value >> (5 * (DataLength - 1 - i))) & 31);
                chars[i] = Alphabet[symbol];
                crc = Step(crc, symbol);
            }
            chars[DataLength] = Alphabet[crc];
            return new string(chars);
        }

        /// <summary>
        /// Canonicalises what a person typed — case, hyphens and spaces are forgiven — and checks
        /// it. False for anything that is not a well-formed code; <paramref name="code"/> is then
        /// empty. True says the code is WELL-FORMED, never that a player holds it.
        /// </summary>
        public static bool TryNormalize(string? raw, out string code)
        {
            code = "";
            if (raw == null) return false;

            var sb = new StringBuilder(Length);
            int crc = 0;
            foreach (char typed in raw)
            {
                if (typed == '-' || typed == ' ') continue;
                char c = typed >= 'a' && typed <= 'z' ? (char)(typed - 32) : typed;
                int symbol = Alphabet.IndexOf(c);
                if (symbol < 0 || sb.Length == Length) return false;
                if (sb.Length < DataLength) crc = Step(crc, symbol);
                else if (symbol != crc) return false;
                sb.Append(c);
            }

            if (sb.Length != Length) return false;
            code = sb.ToString();
            return true;
        }

        /// <summary>The form a person reads: <c>XXXXX-XXXXX</c>. Expects a canonical code.</summary>
        public static string Display(string code)
            => code != null && code.Length == Length
                ? code.Substring(0, 5) + "-" + code.Substring(5)
                : code ?? "";

        private static int Step(int crc, int symbol)
        {
            for (int bit = 4; bit >= 0; bit--)
            {
                int feedback = ((crc >> 4) ^ (symbol >> bit)) & 1;
                crc = (crc << 1) & 31;
                if (feedback != 0) crc ^= Generator;
            }
            return crc;
        }
    }
}
