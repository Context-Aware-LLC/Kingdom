namespace Kingdom.Net.Protocol
{
    /// <summary>
    /// The <b>wire</b> contract label both ends of the handshake are built against — one literal,
    /// not two.
    /// <para><c>MatchGate</c> closes a socket with 1002 "contract mismatch" when the client's
    /// <c>HelloMsg.ClientContractVersion</c> is not ordinally equal to the server's
    /// <c>ServerOptions.WireContractVersion</c>, and <c>NetworkedMatchSession</c> throws when the
    /// <c>MatchStartMsg.WireContractVersion</c> coming back is not equal to
    /// <c>ClientOptions.WireContractVersion</c>. Those two defaults must therefore always be the
    /// same string, and until EF-074b they were two separate literals in two assemblies that
    /// neither toolchain nor test could see disagree.</para>
    /// <para><b>Why this class exists.</b> EF-074b (NOR-228) bumped v4 → v5 in the two production
    /// defaults and left every hard-coded <c>"interfaces.v4"</c> in the socket tests behind. Every
    /// hello in those tests was then refused at the gate: 21 server contract tests and 1 client test
    /// went red at once, all reporting "Socket closed while waiting for MatchStartMsg" — a message
    /// that names neither the version nor the handshake. Master carried it because the PR that
    /// introduced it failed Stage 1 on formatting, so Stage 3 never ran.</para>
    /// <para><b>Bumping it.</b> Change the literal here and nowhere else. A test that needs a
    /// deliberately incompatible version must express that as "not <see cref="Version"/>" rather
    /// than as a literal of its own, or it silently stops testing a mismatch the day this moves.</para>
    /// </summary>
    public static class ProtocolContract
    {
        /// <summary>Wire contract label. v5 added <c>HelloMsg.AuthToken</c> (EF-074b); v6 renamed
        /// the <c>engineContractVersion</c> wire key to <c>wireContractVersion</c> (NOR-240), which
        /// is a rename of the field, not a change to what it holds; v7 added
        /// <c>heroDamageTakenThisTurn</c> to the snapshot and the canonical hash, so a v6 client
        /// fails integrity on every snapshot a v7 server sends. v8 adds repeated Unearth's
        /// remainingOffers and the current-turn effect kinds used by the Queen and Herald. v9 adds
        /// <c>heroAttackThisTurn</c> (Verdant's Empower) to the snapshot and the canonical hash.</summary>
        public const string Version = "interfaces.v9";
    }
}
