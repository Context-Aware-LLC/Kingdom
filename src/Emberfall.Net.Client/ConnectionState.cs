namespace Emberfall.Net.Client
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
    }
}
