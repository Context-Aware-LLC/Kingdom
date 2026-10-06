using System;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Policy;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Server.Bots
{
    /// <summary>The existing deterministic greedy policy behind the shared seat-policy seam.</summary>
    public sealed class GreedySeatPolicy : ISeatPolicy
    {
        private readonly ICommandValidator _validator;
        private readonly ICardDatabase _db;
        private readonly bool _humanFacing;

        /// <param name="humanFacing">Seat the variant a human plays against. Off by default, which
        /// is the frozen reference the soak and the RL league measure — see
        /// <see cref="GreedyAi.HumanFacingVersion"/>.</param>
        public GreedySeatPolicy(ICommandValidator validator, ICardDatabase db, bool humanFacing = false)
        {
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _humanFacing = humanFacing;
        }

        public string Identity => _humanFacing ? GreedyAi.HumanFacingVersion : GreedyAi.PolicyVersion;

        public ValueTask<PlayerIntent?> ChooseAsync(
            GameState state,
            PlayerId player,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(GreedyAi.Choose(state, player, _validator, _db, _humanFacing));
        }
    }
}
