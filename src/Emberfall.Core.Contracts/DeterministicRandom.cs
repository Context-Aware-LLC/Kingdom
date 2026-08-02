namespace Emberfall.Core.Contracts
{
    /// <summary>
    /// The ONE entropy source for the entire core (dev-docs/architecture/determinism-and-projection.md:9).
    /// NOT <see cref="System.Random"/>, NOT UnityEngine.Random — both are CI-banned.
    ///
    /// Algorithm is LOCKED for M1: <b>splitmix64</b> (Vigna). It is explicit, portable, and free of
    /// any platform/libc dependence, so a fixture baked on an x64 dev box replays identically on an
    /// ARM CI runner or server. The exact algorithm is part of the determinism contract: changing it
    /// invalidates every baked golden-replay fixture, so it is versioned, never swapped casually
    /// (dev-docs/architecture/determinism-and-projection.md:9).
    ///
    /// <see cref="RawState"/> is serialized into the GameState hash (dev-docs/architecture/determinism-and-projection.md:16): two
    /// replays that reach the same board but consumed the RNG a different number of times are NOT
    /// equal — the next draw would differ. Hashing the cursor catches "right now, wrong future" desyncs.
    /// </summary>
    public sealed class DeterministicRandom
    {
        // splitmix64 increment (the golden gamma constant from the reference implementation).
        private const ulong Gamma = 0x9E3779B97F4A7C15UL;

        private ulong _state; // the raw 64-bit cursor; the whole determinism story lives in this field.

        public DeterministicRandom(ulong seed) => _state = seed;

        /// <summary>
        /// The independent sub-stream keyed by an integer — the cursor the stream seeded at
        /// <paramref name="seed"/> would hold after <paramref name="index"/> draws, computed in O(1).
        ///
        /// <para>splitmix64 advances its cursor by a plain addition of <see cref="Gamma"/>, so the n-th
        /// cursor is exactly <c>seed + n*Gamma</c> — no iteration required and no new algorithm
        /// introduced. Skipping ahead is therefore <b>the same stream</b>, not a second entropy source:
        /// <c>Substream(s, n).NextUInt64()</c> is bit-for-bit the (n+1)-th value of
        /// <c>new DeterministicRandom(s)</c>, which is what makes it safe under the M1 algorithm lock.</para>
        ///
        /// <para>Use it where a family of independent, replayable streams is keyed by an integer and the
        /// caller must not have to replay the earlier members to reach the n-th — the booster-pack open
        /// (NOR-109), where <c>(seed, packIndex)</c> alone must reproduce a pack byte-identically
        /// however many packs came before it.</para>
        /// </summary>
        public static DeterministicRandom Substream(ulong seed, long index)
            => new DeterministicRandom(unchecked(seed + (Gamma * (ulong)index)));

        /// <summary>The raw 64-bit cursor. Serialized with GameState for mid-match resume AND hashing.</summary>
        public ulong RawState => _state;

        /// <summary>Restore a saved cursor (reconnect / replay seeking). Used by the hasher's twin, never by rules.</summary>
        public void SetRawState(ulong state) => _state = state;

        /// <summary>Advance the cursor and return the next 64-bit value (the splitmix64 finalizer).</summary>
        public ulong NextUInt64()
        {
            unchecked
            {
                _state += Gamma;
                ulong z = _state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>
        /// Unbiased integer in [0, maxExclusive). Integer-only — never touches a float
        /// (ARCHITECTURE §3 rule 4). Uses rejection sampling to remove modulo bias so the
        /// distribution (and the RNG call count) is identical on every platform.
        /// </summary>
        public int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(maxExclusive), "maxExclusive must be positive.");

            ulong bound = (ulong)maxExclusive;
            // Largest multiple of bound that fits in 2^64; reject draws above it to kill modulo bias.
            ulong limit = ulong.MaxValue - (ulong.MaxValue % bound);
            ulong r;
            do
            {
                r = NextUInt64();
            } while (r > limit);
            return (int)(r % bound);
        }

        /// <summary>
        /// Deterministic in-place Fisher–Yates shuffle (mulligan / opening deal, game-state §6).
        /// Draws from this RNG in a fixed call order, so two machines with the same seed produce the
        /// identical permutation — the foundation of the RNG-dense opening fixture (hist-docs/source-snapshots/2026-07-31-pre-reorganization/testing/golden-replay.md §7).
        /// </summary>
        public void Shuffle<T>(System.Collections.Generic.IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = NextInt(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }
}
