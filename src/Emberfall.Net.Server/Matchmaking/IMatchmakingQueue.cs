using System.Threading;
using System.Threading.Tasks;

namespace Emberfall.Net.Server.Matchmaking
{
    /// <summary>
    /// A transport-free player ticket. <see cref="Id"/> identifies this particular queue attempt,
    /// not the account: reconnecting or re-queueing creates a new ticket.
    /// </summary>
    public sealed record MatchmakingTicket(string Id, int Rating);

    /// <summary>A match in seating order: the older FIFO ticket is always <see cref="First"/>.</summary>
    public sealed record MatchmakingPair(MatchmakingTicket First, MatchmakingTicket Second);

    /// <summary>
    /// The server-owned matchmaking seam. Implementations own ordering and match policy only;
    /// sockets, sessions, and cancellation remain the gate's responsibility.
    /// </summary>
    public interface IMatchmakingQueue
    {
        int Count { get; }

        /// <summary>Add one attempt. Ticket ids must be unique while queued.</summary>
        void Enqueue(MatchmakingTicket ticket);

        /// <summary>Remove a cancelled attempt; false means it was already matched or removed.</summary>
        bool Remove(string ticketId);

        /// <summary>Remove and return the oldest compatible FIFO pair, if one exists now.</summary>
        MatchmakingPair? TryDequeueMatch();

        /// <summary>
        /// Complete when time-based policy may have changed. The gate also reevaluates after every
        /// enqueue and removal; this wake-up lets a queue widen bands without a new arrival.
        /// </summary>
        Task WaitForMatchOpportunityAsync(CancellationToken ct);
    }
}
