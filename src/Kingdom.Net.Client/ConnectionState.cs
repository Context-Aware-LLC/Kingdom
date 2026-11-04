namespace Kingdom.Net.Client
{
    /// <summary>
    /// The B3 connection lifecycle (dev-docs/contracts/wire-protocol.md:16). Terminal reasons ride
    /// <see cref="DeadReason"/>. <c>NetworkedGameBootstrap</c> subscribes
    /// <see cref="NetworkedMatchSession.OnConnectionStateChanged"/> and stops the runner on
    /// <see cref="Dead"/>/<see cref="Ended"/> (RC-F10 — the runner cannot infer death itself,
    /// and its only truth source is the mirror, which stops changing after disconnect).
    /// </summary>
    public enum ConnectionState
    {
        Idle,
        Connecting,
        Pairing,
        Live,
        Reconnecting,
        Ended,
        Dead,
    }

    /// <summary>Why a session reached <see cref="ConnectionState.Dead"/> (b3 §4.1).</summary>
    public enum DeadReason
    {
        None,
        ConnectFailed,
        ServerFull,
        Cancelled,
        Forfeited,
        Desync,
        ContractMismatch,
        /// <summary>The wire dropped mid-match and no reconnect got through inside
        /// <see cref="ClientOptions.ReconnectBudgetMs"/>. The match may still be running.</summary>
        ConnectionLost,
        /// <summary>A reconnect reached the server and it refused: the match is no longer running.
        /// It ended, by the idle rule or the opponent, while this client was away, and the result
        /// went to a socket that was not there.</summary>
        MatchGone,
    }
}
