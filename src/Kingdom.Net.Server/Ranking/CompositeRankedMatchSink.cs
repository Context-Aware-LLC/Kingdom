using System;
using System.Collections.Generic;

namespace Kingdom.Net.Server.Ranking
{
    /// <summary>
    /// Delivers one finished match to several sinks. It exists because <c>MatchSession</c> holds
    /// exactly one <see cref="IRankedMatchSink"/> and more than one system now cares that a match
    /// finished — the rating store and the Gold faucet — and because fanning out HERE rather than
    /// threading a second sink through <c>MatchGate</c> keeps both consumers reading the same
    /// eligibility answer off the same record.
    ///
    /// <para><b>One sink's failure must not cost another's delivery.</b> A sink is contractually
    /// non-blocking and non-throwing (see <c>ITelemetrySink</c> for the same rule stated at the
    /// other seam), but this is called from inside the match pump at teardown, so a sink that
    /// breaks its contract would otherwise take the match down and skip every sink after it. Caught
    /// and logged per sink instead.</para>
    /// </summary>
    public sealed class CompositeRankedMatchSink : IRankedMatchSink
    {
        private readonly IReadOnlyList<IRankedMatchSink> _sinks;

        public CompositeRankedMatchSink(params IRankedMatchSink[] sinks)
        {
            if (sinks is null) throw new ArgumentNullException(nameof(sinks));
            var kept = new List<IRankedMatchSink>(sinks.Length);
            foreach (IRankedMatchSink sink in sinks)
                if (sink is not null) kept.Add(sink);
            _sinks = kept;
        }

        public void Record(RankedMatchResult result)
        {
            for (int i = 0; i < _sinks.Count; i++)
            {
                try
                {
                    _sinks[i].Record(result);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[match-sink] {0} refused match {1}: {2}",
                        _sinks[i].GetType().Name, result.Match.Value, ex.Message);
                }
            }
        }
    }
}
