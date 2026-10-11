using Kingdom.Core.Contracts;

namespace Kingdom.Net.Server
{
    /// <summary>
    /// Integer-milli-token bucket (dev-docs/contracts/wire-protocol.md:37). 1 token = 1000 milli;
    /// refill of N tokens/second is exactly N milli-tokens per millisecond — no float, ever.
    /// </summary>
    public sealed class TokenBucket
    {
        private readonly ServerOptions _opts;
        private long _milli;
        private long _lastRefillMs;

        public TokenBucket(ServerOptions opts, long nowMs)
        {
            _opts = opts;
            _milli = opts.RateCapacity * 1000L;
            _lastRefillMs = nowMs;
        }

        public bool TryTake(int tokens, long nowMs)
        {
            long cap = _opts.RateCapacity * 1000L;
            long refilled = _milli + (nowMs - _lastRefillMs) * _opts.RateRefillPerSecond;
            _milli = refilled > cap ? cap : refilled;
            _lastRefillMs = nowMs;

            long cost = tokens * 1000L;
            if (_milli < cost) return false;
            _milli -= cost;
            return true;
        }
    }

    /// <summary>
    /// Per-CONNECTION policy state (design §6). Deliberately not per-seat: a reconnect binds a
    /// fresh instance, which is exactly what resets the seq baseline (§6.2, RC F7) and the bucket.
    /// </summary>
    public sealed class ConnectionPolicyState
    {
        public ConnectionPolicyState(ServerOptions opts, long nowMs)
        {
            Bucket = new TokenBucket(opts, nowMs);
            LastHeardMs = nowMs;
        }

        /// <summary>Highest inbound envelope seq accepted on this connection (strictly increasing).</summary>
        public ulong SeqHighWater;
        /// <summary>Next outbound envelope seq (per-connection monotonic, assigned by the session).</summary>
        public ulong OutSeq;
        /// <summary>Client's last-acked StateVersion. Recorded, never consumed in B1 (RC Q2 ruling).</summary>
        public ulong AckVersion;
        public int Strikes;
        public int EmptyRejects;
        public long LastHeardMs;
        public TokenBucket Bucket { get; }
    }

    /// <summary>
    /// The §6.3 client-intent allowlist — the second of the two gates (a5 §5 finding 4): the codec's
    /// tag registry decides what DECODES; this decides what a client may SUBMIT. Load-bearing:
    /// CommandProcessor itself accepts AutoConfirmIntent, so this list is the only thing stopping
    /// a client from self-issuing system intents (RC F6).
    ///
    /// <para><b>An intent MISSING from this list is not a stricter server — it is a mechanic that
    /// cannot be completed.</b> <see cref="UnearthChoiceIntent"/> was absent, and because a
    /// <c>PendingChoice</c> makes <c>CommandProcessor.CanExecute</c> refuse every other intent —
    /// <c>EndTurnIntent</c> included — the one card that opens a choice (230 Gear Cache) soft-locked
    /// the CASTER's own turn for every networked client, bot and human alike, until the turn clock
    /// ran out. Nothing named the allowlist: the reject that came back was
    /// <c>UnknownIntent</c>, which reads as a malformed client.</para>
    ///
    /// <para>It belongs here rather than being kept out for safety. It is player-authored by design,
    /// and <c>CommandProcessor.ValidateUnearthChoice</c> binds every field of it to the authoritative
    /// offer — chooser, monotonic sequence, membership of the exact offered set, hand cap — which is
    /// the mitigation VULN-001 §4 requires and already ships. This list was never the control.</para>
    /// </summary>
    public static class IntentAllowlist
    {
        public static bool IsClientLegal(PlayerIntent intent) => intent
            is PlayCardIntent
            or AttackIntent
            or HeroPowerIntent
            or EndTurnIntent
            or MulliganIntent
            or UnearthChoiceIntent
            or ConcedeIntent;

        /// <summary>Turn-scoped intents get the out-of-turn pre-check + elevated bucket cost (§6.4).</summary>
        public static bool IsTurnScoped(PlayerIntent intent) => intent
            is PlayCardIntent
            or AttackIntent
            or HeroPowerIntent
            or EndTurnIntent
            or UnearthChoiceIntent;
    }
}
