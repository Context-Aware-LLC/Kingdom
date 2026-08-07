namespace Emberfall.Net.Protocol
{
    /// <summary>
    /// The contract label both ends of the handshake are built against — <b>one literal, not two</b>.
    /// <para><c>MatchGate</c> closes a socket with 1002 "contract mismatch" when the client's
    /// <c>HelloMsg.ClientContractVersion</c> is not ordinally equal to the server's
    /// <c>ServerOptions.EngineContractVersion</c>, and <c>NetworkedMatchSession</c> throws when the
    /// <c>MatchStartMsg.EngineContractVersion</c> coming back is not equal to
    /// <c>ClientOptions.ContractVersion</c>. Those two defaults must therefore always be the same
    /// string, and until EF-074b they were two separate literals in two assemblies that neither
    /// toolchain nor test could see disagree.</para>
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
        /// <summary>Wire/engine contract label. v5 adds <c>HelloMsg.AuthToken</c> (EF-074b).</summary>
        public const string Version = "interfaces.v5";
    }
}
