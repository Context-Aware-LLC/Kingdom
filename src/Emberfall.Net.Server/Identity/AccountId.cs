using System;
using System.Security.Cryptography;
using System.Text;

namespace Emberfall.Net.Server.Identity
{
    /// <summary>
    /// A guest player's stable identity (telemetry-spec §0, accounts posture decision 6a: "identity
    /// = guest device token"; migrates to an account id when Supabase lands — ADR-0001).
    ///
    /// <para><b>Derived, never minted.</b> It is a pure function of the client's device token, so it
    /// survives server restarts. Its predecessor — <c>guest-&lt;Interlocked.Increment&gt;</c> — was
    /// a per-process counter, so identity reset on every restart and no emitted telemetry row could
    /// be attributed to a returning player.</para>
    ///
    /// <para><b>Deliberately not the raw token.</b> The token is a bearer identity: whoever holds it
    /// can claim to be that player. Storing its hash means a leaked telemetry dump cannot be
    /// replayed as somebody's identity, and the raw value never reaches disk. Conversion to a real
    /// account still works — the client presents its token, the server hashes and looks up.</para>
    ///
    /// <para><b>Never on the wire.</b> This type lives server-side only. It must never be placed in
    /// <c>PlayerId</c>, which is broadcast to the opponent in <c>OpponentInfo</c>, and never in
    /// <c>GameState</c>, which is hash-canonical.</para>
    /// </summary>
    public readonly record struct AccountId
    {
        /// <summary>Lowercase hex, 128 bits of SHA-256. Empty for <see cref="Unattributed"/>.</summary>
        public string Value { get; }

        private AccountId(string value) => Value = value;

        /// <summary>
        /// A match with no identity behind it — test harnesses and any pre-identity path. Explicit
        /// on purpose: a caller that cannot attribute a match has to say so, rather than silently
        /// producing a real-looking id. The telemetry emitter refuses to write a row for this.
        /// </summary>
        public static AccountId Unattributed => new AccountId("");

        public bool IsAttributed => !string.IsNullOrEmpty(Value);

        public static AccountId FromDeviceToken(string deviceToken)
        {
            if (string.IsNullOrEmpty(deviceToken))
                throw new ArgumentException(
                    "A device token is required to derive an account id. An unattributable connection " +
                    "must be refused at the door, not given a placeholder identity (C-G1-8).",
                    nameof(deviceToken));

            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(deviceToken), digest);

            // 128 bits is ample to key a guest population and keeps the id eyeball-comparable in
            // a JSONL row; it matches MatchId's 32-hex-char shape.
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(digest[i].ToString("x2"));
            return new AccountId(sb.ToString());
        }

        public override string ToString() => IsAttributed ? Value : "(unattributed)";
    }
}
