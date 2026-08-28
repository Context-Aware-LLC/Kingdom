using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Emberfall.Net.Server.Ranking
{
    public sealed class RatingWorker : BackgroundService, IRankedMatchSink
    {
        private readonly IRatingStore _store;
        private readonly Channel<RankedMatchResult> _queue =
            Channel.CreateUnbounded<RankedMatchResult>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        public RatingWorker(IRatingStore store)
            => _store = store ?? throw new ArgumentNullException(nameof(store));

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_store is PostgresRatingStore postgres)
                await postgres.EnsureSchemaAsync(cancellationToken);
            await base.StartAsync(cancellationToken);
        }

        public void Record(RankedMatchResult result)
        {
            if (!result.IsEligible) return;
            if (!_queue.Writer.TryWrite(result))
                Console.Error.WriteLine("[ranking] result queue was closed before match {0} could be recorded.",
                    result.Match.Value);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await foreach (RankedMatchResult result in _queue.Reader.ReadAllAsync())
            {
                try
                {
                    // The writer is completed before BackgroundService cancellation. Finish every
                    // result already accepted into the process so a graceful deploy cannot lose the
                    // last match merely because shutdown began between dequeue and commit.
                    await _store.ApplyAsync(result, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[ranking] match {0} was not applied: {1}",
                        result.Match.Value, ex.Message);
                }
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _queue.Writer.TryComplete();
            await base.StopAsync(cancellationToken);
        }
    }
}
