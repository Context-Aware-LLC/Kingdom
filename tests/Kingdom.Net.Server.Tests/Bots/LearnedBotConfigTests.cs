using System;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Bots
{
    /// <summary>
    /// The three configuration cases, because only two of them were handled on the first attempt.
    ///
    /// The rule is that an operator who asks for a learned bot must never silently get a Greedy one.
    /// That was implemented as "never configured runs Greedy quietly, configured-and-broken refuses
    /// to start" -- and the check lived inside the bot-fill branch, so **configured but unreachable**
    /// (a checkpoint set with no roster, or with no account store) slipped past both arms and
    /// produced exactly the silent Greedy server the rule exists to prevent.
    ///
    /// These fix the shape of the rule in place: the pairing and reachability checks are the
    /// behaviour, not an implementation detail of where they happen to sit.
    /// </summary>
    [TestFixture]
    public sealed class LearnedBotConfigTests
    {
        [Test]
        public void A_Checkpoint_Without_Bot_Fill_Refuses_To_Start()
        {
            // The case the first implementation missed. No roster means the factory is never
            // reached, so without this the server boots as Greedy and says nothing.
            var cfg = NewConfig() with
            {
                BotCheckpointPath = "F:/nonexistent/best-model.pt",
                BotRlExportPath = "F:/nonexistent/export",
                BotRosterPath = null,
            };

            var ex = Assert.Throws<InvalidOperationException>(() => ServerApp.Build(cfg));
            Assert.That(ex!.Message, Does.Contain("bot-fill is not enabled"));
        }

        [Test]
        public void A_Checkpoint_Without_Its_Export_Refuses_To_Start()
        {
            // A checkpoint cannot be encoded for without the export's feature spec, so half a
            // configuration is a configuration error rather than a partial enablement.
            var cfg = NewConfig() with
            {
                BotCheckpointPath = "F:/nonexistent/best-model.pt",
                BotRlExportPath = null,
                BotRosterPath = null,
            };

            // Reachability is checked first, so this asserts only that it does not start silently.
            Assert.Throws<InvalidOperationException>(() => ServerApp.Build(cfg));
        }

        [Test]
        public void No_Checkpoint_Starts_Quietly_As_Greedy()
        {
            // The common case and the one that must stay boring: every existing deploy sets none of
            // these and must be unaffected.
            var cfg = NewConfig();
            Assert.DoesNotThrow(() =>
            {
                using var app = ServerApp.Build(cfg);
            });
        }

        private static ServerConfig NewConfig() => new ServerConfig
        {
            Urls = "http://127.0.0.1:0",
            CardsDir = Support.SessionHarness.CardDataDir(),
        };
    }
}
