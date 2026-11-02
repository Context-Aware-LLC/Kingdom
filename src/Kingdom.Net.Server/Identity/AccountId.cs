using System;
using System.Security.Cryptography;
using System.Text;

namespace Kingdom.Net.Server.Identity
{
    /// <summary>
    /// A tokenless tooling seat's stable telemetry identity, derived from its device token. Players never
    /// take this path: they are identified by their Supabase account (see MatchGate).
    ///
    /// <para><b>Derived, never minted.</b> It is a pure function of the client's device token, so it
    /// survives server restarts. Its predecessor was
    /// a per-process counter, so identity reset on every restart and no emitted telemetry row could
    /// be attributed to a returning player.</para>
    ///
    /// <para><b>Deliberately not the raw token.</b> The token is a bearer identity: whoever holds it
    /// can claim to be that player. Storing its hash means a leaked telemetry dump cannot be
    /// replayed as somebody's identity, and the raw value never reaches disk. The same hash is how
    /// the server looks a token up — it never stores or compares the raw value.</para>
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

        /// <summary>
        /// An authenticated player's stable identity, wrapping the account store's
        /// <c>player_id</c> (EF-074c). The store already derives <c>player_id</c> as a SHA-256
        /// hash of the credential (EF-072), so this factory does not hash again.
        /// </summary>
        public static AccountId FromAuthenticatedAccount(string playerId)
        {
            if (string.IsNullOrEmpty(playerId))
                throw new ArgumentException(
                    "A player id is required to create an authenticated AccountId.",
                    nameof(playerId));
            return new AccountId(playerId);
        }

        public static AccountId FromDeviceToken(string deviceToken)
        {
            if (string.IsNullOrEmpty(deviceToken))
                throw new ArgumentException(
                    "A device token is required to derive an account id. An unattributable connection " +
                    "must be refused at the door, not given a placeholder identity (C-G1-8).",
                    nameof(deviceToken));

            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(deviceToken), digest);

            // 128 bits is ample to key every device token and keeps the id eyeball-comparable in
            // a JSONL row; it matches MatchId's 32-hex-char shape.
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(digest[i].ToString("x2"));
            return new AccountId(sb.ToString());
        }

        public override string ToString() => IsAttributed ? Value : "(unattributed)";
    }
}
