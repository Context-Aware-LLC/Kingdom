using System;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Policy;
using Kingdom.Core.Contracts;

namespace Kingdom.Net.Server.Bots
{
    /// <summary>The existing deterministic greedy policy behind the shared seat-policy seam.</summary>
    public sealed class GreedySeatPolicy : ISeatPolicy
    {
        private readonly ICommandValidator _validator;
        private readonly ICardDatabase _db;

        public GreedySeatPolicy(ICommandValidator validator, ICardDatabase db)
        {
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public string Identity => GreedyAi.PolicyVersion;

        public ValueTask<PlayerIntent?> ChooseAsync(
            GameState state,
            PlayerId player,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(GreedyAi.Choose(state, player, _validator, _db));
        }
    }
}
