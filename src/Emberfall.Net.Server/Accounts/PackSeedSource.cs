using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// The seed half of a pack open's <c>(seed, packIndex)</c> pair (NOR-109 §2). One stable seed per
    /// <c>(playerId, packId)</c>; the index is the player's pack count for that pairing, so every pack a
    /// player will ever open has its own replayable sub-stream.
    ///
    /// <para><b>Why this is an interface and not a field on the service.</b> The seed must be stable
    /// (an open has to be reproducible for support and for a crash replay) and <b>unpredictable to the
    /// client</b> (see <see cref="HmacPackSeedSource"/>). Those two pull in opposite directions and the
    /// resolution is a server-held secret — which is a deployment concern, not an algorithm one, and so
    /// belongs behind a seam that a test can replace with a constant.</para>
    /// </summary>
    public interface IPackSeedSource
    {
        Task<ulong> SeedAsync(string playerId, string packId, CancellationToken ct = default);
    }

    /// <summary>
    /// The production seed source: <c>HMAC-SHA256(serverSecret, playerId + "\n" + packId)</c>, truncated
    /// to 64 bits.
    ///
    /// <para><b>SECURITY — this is why the secret exists.</b> The open algorithm is deterministic and
    /// entirely public (it is in the core, and the client renders its output), and a client already
    /// knows its own pity counters and collection. So if the seed were derivable from anything the
    /// client holds — the player id, the device token, a counter — a player could compute the contents
    /// of packs they have not bought yet and buy only the good ones. Keying it under a server-held
    /// secret is the whole defence, and it is the reason a plain hash of the player id is NOT
    /// acceptable here however convenient it looks.</para>
    ///
    /// <para><b>The secret is server config and nothing else.</b> Never derived from a client frame,
    /// never logged (security contract: no raw bearer material in telemetry), never committed. A
    /// deployment that rotates it re-rolls every unopened pack, which is harmless — a pack's contents
    /// are decided when it is opened, not when it is bought — but it does break replay of already-opened
    /// packs, so rotate deliberately.</para>
    ///
    /// <para><b>VULN pointer (security.md §Review triggers: "purchasable inventory").</b> Entitlements
    /// are keyed on <c>player_id</c>, which today resolves from the unauthenticated guest device token
    /// (VULN-003). Packs make that identity <i>valuable</i>: whoever copies a device token inherits the
    /// collection it bought. This class does not fix that and cannot — arming the fix is EF-074's
    /// authenticator. If you are the one landing real accounts, the pack line is one of the things that
    /// must move behind it.</para>
    /// </summary>
    public sealed class HmacPackSeedSource : IPackSeedSource
    {
        /// <summary>Environment variable the server reads the secret from. Fly secret in production.</summary>
        public const string SecretEnvVar = "EMBERFALL_PACK_SEED_SECRET";

        private readonly byte[] _secret;

        public HmacPackSeedSource(byte[] secret)
        {
            if (secret == null || secret.Length == 0)
                throw new ArgumentException(
                    "A pack seed secret is required; without one a client can predict its own pack contents.",
                    nameof(secret));
            _secret = (byte[])secret.Clone();
        }

        public HmacPackSeedSource(string secret) : this(Encoding.UTF8.GetBytes(secret ?? "")) { }

        /// <summary>
        /// Build from <see cref="SecretEnvVar"/>, or null when it is unset.
        ///
        /// <para>Returns null rather than falling back to a default secret <b>on purpose</b>: a default
        /// is a published secret, and the caller failing loudly (or refusing to enable the pack line) is
        /// the only outcome that does not ship a predictable economy. This is the same shape as the
        /// telemetry DB connection string — absent means the feature is off, never means insecure-on.</para>
        /// </summary>
        public static HmacPackSeedSource? FromEnvironment()
        {
            string? secret = Environment.GetEnvironmentVariable(SecretEnvVar);
            return string.IsNullOrEmpty(secret) ? null : new HmacPackSeedSource(secret!);
        }

        public Task<ulong> SeedAsync(string playerId, string packId, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(playerId)) throw new ArgumentException("playerId is required.", nameof(playerId));
            if (string.IsNullOrEmpty(packId)) throw new ArgumentException("packId is required.", nameof(packId));

            byte[] mac;
            using (var hmac = new HMACSHA256(_secret))
                mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(playerId + "\n" + packId));

            // Big-endian fold of the first 8 bytes — an explicit byte order, so the seed does not
            // depend on the host's endianness the way BitConverter would.
            ulong seed = 0;
            for (int i = 0; i < 8; i++) seed = (seed << 8) | mac[i];
            return Task.FromResult(seed);
        }
    }

    /// <summary>
    /// A constant seed, for tests and for replaying one specific open. Never for a live server — see
    /// <see cref="HmacPackSeedSource"/> for what a predictable seed costs.
    /// </summary>
    public sealed class FixedPackSeedSource : IPackSeedSource
    {
        private readonly ulong _seed;
        public FixedPackSeedSource(ulong seed) => _seed = seed;
        public Task<ulong> SeedAsync(string playerId, string packId, CancellationToken ct = default)
            => Task.FromResult(_seed);
    }
}
