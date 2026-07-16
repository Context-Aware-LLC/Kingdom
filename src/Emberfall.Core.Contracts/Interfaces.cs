namespace Emberfall.Core.Contracts
{
    /// <summary>
    /// The single mutation door (interfaces.md §1, game-state-and-actions.md §3). Server runs this
    /// as authority; local mode runs it in-process. Validate + apply atomically: on reject there is
    /// NO mutation and NO events.
    /// </summary>
    public interface ICommandProcessor
    {
        CommandResult Execute(GameState state, PlayerIntent intent);
    }

    /// <summary>Validation-only half — for UI affordances and client-side legality, no mutation.</summary>
    public interface ICommandValidator
    {
        bool CanExecute(GameState state, PlayerIntent intent, out RejectReason? reason);
    }

    // NOTE: ICardDatabase and ICardValidator return CardDefinition, which lives in
    // Emberfall.Core.Cards (Cards → Contracts). Putting them here would invert that dependency,
    // so they are declared in Emberfall.Core.Cards instead (see Cards/CardInterfaces.cs).
    // interfaces.md §1 anticipates this split: "public types in Emberfall.Core.Contracts / Emberfall.Core.Cards".

    /// <summary>
    /// Match host (interfaces.md §2/§2.1). Multiplayer implements both local and networked modes.
    /// <see cref="BeginMatch"/> is CORE-driven: coin flip → deal 3/4 → insert token_spark_coin into
    /// the second player → enter <see cref="TurnPhase.Mulligan"/>, consuming only the seeded RNG.
    /// </summary>
    public interface IMatchHost
    {
        ulong StateVersion { get; }
        GameState BeginMatch(MatchId id, PlayerId p0, PlayerId p1, ulong seed);
        CommandResult Submit(PlayerIntent intent);
        event GameEventStream OnEvents;

        /// <summary>
        /// The wire payload (interfaces.md v3 §2, decisions/ADR-0006): the FULL projected snapshot
        /// for one viewer — a pure GameState→GameState redaction returning the same concrete type,
        /// stripped of what <paramref name="viewer"/> may not see (opponent hand, deck order, live
        /// RNG, undisclosed pools). Every state payload that leaves the server is this output
        /// (state-sync-and-security.md §5.3 — no raw-state path).
        /// </summary>
        GameState Snapshot(PlayerId viewer);
    }
}
