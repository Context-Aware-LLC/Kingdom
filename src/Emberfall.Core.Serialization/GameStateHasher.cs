using System.Security.Cryptography;
using System.Text;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Serialization
{
    /// <summary>
    /// Canonical state fingerprint, promoted out of the test layer (A2a). The definition is now
    /// literally <c>hash = h(serialize(state))</c> (hist-docs/source-snapshots/2026-07-31-pre-reorganization/project/M2-KICKOFF.md §2 A2): the serializer owns the
    /// one canonical field order; this type only picks the algorithm and the hex spelling.
    /// Consumed by the R1 golden gate today; by the wire protocol's per-message post-command state
    /// hash (A5) and the server's desync detection (B1) next.
    /// </summary>
    public static class GameStateHasher
    {
        public const string Algorithm = "sha256";

        /// <summary>Canonical bytes → SHA-256 → lowercase hex.</summary>
        public static string Hash(GameState state)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            GameStateSerializer.Serialize(state, new HashSink(sha));
            var digest = sha.GetHashAndReset();
            var sb = new StringBuilder(digest.Length * 2);
            foreach (var b in digest) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
