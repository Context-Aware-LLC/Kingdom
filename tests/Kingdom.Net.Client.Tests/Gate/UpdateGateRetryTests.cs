using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kingdom.Net.Client.Gate;
using Kingdom.Net.Client.Tests.Auth;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Gate
{
    [TestFixture]
    [Category("Unit")]
    public sealed class UpdateGateRetryTests
    {
        private const string Base = "https://emberfall.fly.dev";
        private const string Contract = "interfaces.v6";
        private const string PassAnswer =
            "{\"contract\":\"" + Contract + "\",\"minBuildAndroid\":0,\"minBuildIos\":0}";
        private const string StaleAnswer =
            "{\"contract\":\"" + Contract + "\",\"minBuildAndroid\":99,\"minBuildIos\":99}";

        private sealed class Run
        {
            public readonly FakeAuthHttp Http = new FakeAuthHttp();
            public readonly List<int> Pauses = new List<int>();
            public readonly List<(int Attempt, int TimeoutSeconds)> Attempts = new List<(int, int)>();

            public Task<UpdateGateResult> CheckAsync() => UpdateGateRetry.CheckAsync(
                (n, timeout) =>
                {
                    Attempts.Add((n, timeout));
                    return new HttpUpdateGate(Base, Http).CheckAsync(HttpUpdateGate.Android, 10, Contract);
                },
                ms => { Pauses.Add(ms); return Task.CompletedTask; });
        }

        [Test]
        public async Task A_first_answer_is_taken_without_a_pause()
        {
            var run = new Run();
            run.Http.Reply(200, PassAnswer);

            UpdateGateResult result = await run.CheckAsync();

            Assert.That(result.Verdict, Is.EqualTo(UpdateGateVerdict.Pass));
            Assert.That(result.Attempts, Is.EqualTo(1));
            Assert.That(run.Pauses, Is.Empty);
        }

        [Test]
        public async Task A_dead_network_is_asked_three_times_with_growing_pauses_then_blocks()
        {
            var run = new Run();
            run.Http.ReplyTransportFailure().ReplyTransportFailure().ReplyTransportFailure();

            UpdateGateResult result = await run.CheckAsync();

            Assert.That(result.Verdict, Is.EqualTo(UpdateGateVerdict.Unreachable));
            Assert.That(result.Attempts, Is.EqualTo(UpdateGateRetry.MaxAttempts));
            Assert.That(run.Http.Requests.Count, Is.EqualTo(3));
            Assert.That(run.Attempts.Select(a => a.Attempt), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(run.Pauses.Count, Is.EqualTo(2));
            Assert.That(run.Pauses[1], Is.GreaterThan(run.Pauses[0]), "the pauses grow");
        }

        [Test]
        public async Task A_recovered_network_passes_and_says_how_many_asks_it_took()
        {
            var run = new Run();
            run.Http.ReplyTransportFailure().Reply(200, PassAnswer);

            UpdateGateResult result = await run.CheckAsync();

            Assert.That(result.Verdict, Is.EqualTo(UpdateGateVerdict.Pass));
            Assert.That(result.Attempts, Is.EqualTo(2));
            Assert.That(run.Pauses.Count, Is.EqualTo(1));
        }

        [Test]
        public async Task An_unreadable_answer_is_retried_like_a_dead_network()
        {
            var run = new Run();
            run.Http.Reply(503, "{}").Reply(200, PassAnswer);

            UpdateGateResult result = await run.CheckAsync();

            Assert.That(result.Verdict, Is.EqualTo(UpdateGateVerdict.Pass));
            Assert.That(result.Attempts, Is.EqualTo(2));
        }

        [Test]
        public async Task A_stale_build_is_a_real_answer_and_is_never_asked_again()
        {
            var run = new Run();
            run.Http.Reply(200, StaleAnswer).Reply(200, PassAnswer);

            UpdateGateResult result = await run.CheckAsync();

            Assert.That(result.Verdict, Is.EqualTo(UpdateGateVerdict.UpdateRequired));
            Assert.That(result.Attempts, Is.EqualTo(1));
            Assert.That(run.Http.Requests.Count, Is.EqualTo(1));
            Assert.That(run.Pauses, Is.Empty);
        }

        [Test]
        public void The_worst_case_wait_stays_inside_45_seconds_and_the_first_timeout_covers_a_cold_boot()
        {
            Assert.That(UpdateGateRetry.WorstCaseSeconds, Is.LessThanOrEqualTo(45));
            // 6.68 s measured cold start (2026-10-23); see the comment on the constants.
            Assert.That(UpdateGateRetry.TimeoutSecondsFor(1), Is.GreaterThanOrEqualTo(14));
            for (int n = 1; n <= UpdateGateRetry.MaxAttempts; n++)
                Assert.That(UpdateGateRetry.TimeoutSecondsFor(n), Is.GreaterThan(7),
                    "every attempt must outlast a cold boot, in case the first never reached Fly");
        }
    }
}
