using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Achievements;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Telemetry;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Achievements
{
    /// <summary>
    /// Achievements, the Ledger of Bad Ideas. The failures worth a test are the ones a player would
    /// never report: one earned in a friend match, one missing after a match against a bot, a bot
    /// seat earning anything, and a second earning moving the date of the first.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class AchievementTests
    {
        private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

        private static readonly string[] AllFour =
            { "second_thoughts", "working_as_intended", "early_retirement", "against_advice" };

        private sealed class Harness
        {
            public Harness(InMemoryAccountStore accounts, InMemoryAchievementStore store, string playerId)
            {
                Accounts = accounts;
                Store = store;
                PlayerId = playerId;
            }

            public InMemoryAccountStore Accounts { get; }
            public InMemoryAchievementStore Store { get; }
            public string PlayerId { get; }

            public AchievementService Service(Func<AccountId, bool>? isBotAccount = null)
                => new AchievementService(Store, Accounts, isBotAccount);
        }

        private static async Task<Harness> HarnessAsync()
        {
            var accounts = new InMemoryAccountStore(new FakeTime());
            Account player = await accounts.ResolveOrCreateAsync("supabase", "subject-0");
            return new Harness(accounts, new InMemoryAchievementStore(), player.PlayerId);
        }

        /// <summary>A match that earns every achievement in the catalog.</summary>
        private static AchievementMatchFacts Everything(
            string playerId, string matchId = "match-1", TelemetrySource source = TelemetrySource.Ladder)
            => new AchievementMatchFacts(
                matchId, playerId, Now, source, SeatResult.Win, MatchEndReason.Win, TurnCount: 9,
                EnemyMinionsDestroyed: 3, MulliganReplaced: 1, HeroPowersUsed: 2);

        // ── The catalog ───────────────────────────────────────────────────────────────────

        [Test]
        public void Ids_Are_Unique_And_Lowercase()
        {
            // An id keys the earned row forever. Two entries sharing one would make the second
            // unearnable for everybody who already holds the first.
            string[] ids = AchievementCatalog.All.Select(a => a.Id).ToArray();
            Assert.That(ids, Is.Unique);
            Assert.That(ids.All(id => id == id.ToLowerInvariant()), Is.True);
        }

        [Test]
        public void A_Quiet_Loss_Earns_Nothing()
        {
            var quiet = new AchievementMatchFacts(
                "match-1", "player", Now, TelemetrySource.Ladder, SeatResult.Loss, MatchEndReason.Concede,
                TurnCount: 2, EnemyMinionsDestroyed: 0, MulliganReplaced: 0, HeroPowersUsed: 0);

            Assert.That(AchievementCatalog.All.Where(a => a.EarnedBy(quiet)), Is.Empty);
        }

        [TestCase("second_thoughts")]
        [TestCase("working_as_intended")]
        [TestCase("early_retirement")]
        [TestCase("against_advice")]
        public void Each_Gentle_Achievement_Fires_On_Its_Own_Fact(string id)
        {
            var quiet = new AchievementMatchFacts(
                "match-1", "player", Now, TelemetrySource.Ladder, SeatResult.Loss, MatchEndReason.Concede,
                TurnCount: 2, EnemyMinionsDestroyed: 0, MulliganReplaced: 0, HeroPowersUsed: 0);
            AchievementMatchFacts facts = id switch
            {
                "second_thoughts" => quiet with { MulliganReplaced = 1 },
                "working_as_intended" => quiet with { HeroPowersUsed = 1 },
                "early_retirement" => quiet with { EnemyMinionsDestroyed = 1 },
                _ => quiet with { Result = SeatResult.Win, EndReason = MatchEndReason.Win },
            };

            Assert.That(AchievementCatalog.All.Where(a => a.EarnedBy(facts)).Select(a => a.Id),
                Is.EqualTo(new[] { id }));
        }

        // ── Who earns ─────────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_Find_Game_Match_Earns_What_It_Shows()
        {
            var h = await HarnessAsync();

            IReadOnlyList<string> earned = await h.Service().ApplyAsync(Everything(h.PlayerId));

            Assert.That(earned, Is.EquivalentTo(AllFour));
        }

        [Test]
        public async Task An_Achievement_Is_Earned_Once_And_Keeps_Its_First_Match()
        {
            var h = await HarnessAsync();
            AchievementService service = h.Service();

            await service.ApplyAsync(Everything(h.PlayerId, "match-1"));
            IReadOnlyList<string> again = await service.ApplyAsync(
                Everything(h.PlayerId, "match-2") with { EndedAtUtc = Now.AddDays(1) });

            IReadOnlyList<EarnedAchievement> ledger = await service.EarnedAsync(h.PlayerId);
            Assert.Multiple(() =>
            {
                Assert.That(again, Is.Empty, "a second earning is not news");
                Assert.That(ledger, Has.Count.EqualTo(AllFour.Length));
                Assert.That(ledger.All(a => a.MatchId == "match-1" && a.EarnedAt == Now), Is.True,
                    "a later match must not move the date an achievement was earned");
            });
        }

        [TestCase(TelemetrySource.Friendly)]
        [TestCase(TelemetrySource.AiSoak)]
        public async Task A_Match_Outside_Find_Game_Earns_Nothing(TelemetrySource source)
        {
            // Sam, 2026-10-17: two friends must not be able to trade achievements.
            var h = await HarnessAsync();

            IReadOnlyList<string> earned = await h.Service().ApplyAsync(Everything(h.PlayerId, source: source));

            Assert.That(earned, Is.Empty);
            Assert.That(await h.Store.ReadAsync(h.PlayerId), Is.Empty);
        }

        [Test]
        public async Task Bot_Fill_Counts_For_The_Human_And_Never_For_The_Bot()
        {
            // A bot-filled match has to count exactly like any other, or the missing achievement
            // gives the bot away.
            var h = await HarnessAsync();
            Account bot = await h.Accounts.ResolveOrCreateAsync("bot", "roster-bot-0");
            AchievementService service = h.Service(
                account => account == AccountId.FromAuthenticatedAccount(bot.PlayerId));

            IReadOnlyList<string> human = await service.ApplyAsync(
                Everything(h.PlayerId, source: TelemetrySource.BotFill));
            IReadOnlyList<string> roster = await service.ApplyAsync(
                Everything(bot.PlayerId, source: TelemetrySource.BotFill));

            Assert.Multiple(() =>
            {
                Assert.That(human, Is.EquivalentTo(AllFour));
                Assert.That(roster, Is.Empty);
            });
        }

        [Test]
        public async Task Bot_Fill_Without_The_Roster_Earns_Nothing()
        {
            // Fail closed: without the predicate the server cannot say which seat is the bot.
            var h = await HarnessAsync();

            IReadOnlyList<string> earned = await h.Service(isBotAccount: null)
                .ApplyAsync(Everything(h.PlayerId, source: TelemetrySource.BotFill));

            Assert.That(earned, Is.Empty);
        }

        [Test]
        public async Task A_Guest_Earns_Nothing()
        {
            var h = await HarnessAsync();
            Account guest = await h.Accounts.ResolveOrCreateAsync("device_token", "token-hash");

            IReadOnlyList<string> earned = await h.Service().ApplyAsync(Everything(guest.PlayerId));

            Assert.That(earned, Is.Empty);
        }

        // ── The sink ──────────────────────────────────────────────────────────────────────

        [Test]
        public async Task The_Sink_Forwards_Every_Event_And_Credits_Both_Seats()
        {
            var h = await HarnessAsync();
            Account other = await h.Accounts.ResolveOrCreateAsync("supabase", "subject-1");
            var inner = new InMemoryTelemetrySink();
            var sink = new AchievementSink(inner, h.Service());

            AccountId p0 = AccountId.FromAuthenticatedAccount(h.PlayerId);
            AccountId p1 = AccountId.FromAuthenticatedAccount(other.PlayerId);
            var complete = new MatchCompleteEvent(
                new MatchId("match-1"), Now, MatchEndReason.Win, MatchOutcome.Player0Win, 9, TelemetrySource.Ladder,
                new SeatOutcome(p0, CardClass.EMBERWARD, "deck-0", "hash-0", SeatResult.Win, Array.Empty<int>()),
                new SeatOutcome(p1, CardClass.TIDECALLER, "deck-1", "hash-1", SeatResult.Loss, Array.Empty<int>(),
                    EnemyMinionsDestroyed: 2));

            sink.MatchComplete(complete);
            sink.CardsSeen(new CardSeenEvent(new MatchId("match-1"), Now, TelemetrySource.Ladder,
                Array.Empty<CardSeenRow>()));
            sink.MatchVoid(new MatchVoidEvent(new MatchId("match-2"), Now, 3, TelemetrySource.Ladder, p0, p1));
            await sink.EvaluateAsync(complete);
            IReadOnlyList<EarnedAchievement> winner = await h.Store.ReadAsync(h.PlayerId);
            IReadOnlyList<EarnedAchievement> loser = await h.Store.ReadAsync(other.PlayerId);

            Assert.Multiple(() =>
            {
                Assert.That(inner.Completes, Has.Count.EqualTo(1));
                Assert.That(inner.CardSeenBatches, Has.Count.EqualTo(1));
                Assert.That(inner.Voids, Has.Count.EqualTo(1));
                Assert.That(winner.Select(a => a.AchievementId), Is.EqualTo(new[] { "against_advice" }));
                Assert.That(loser.Select(a => a.AchievementId), Is.EqualTo(new[] { "early_retirement" }));
            });
        }

        // ── GET /achievements ─────────────────────────────────────────────────────────────

        private const string Subject = "supabase-subject-0001";

        private sealed class StubVerifier : IIdentityTokenVerifier
        {
            private readonly TokenVerificationResult _result;
            public StubVerifier(TokenVerificationResult result) => _result = result;
            public Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
                => Task.FromResult(_result);
        }

        private static IIdentityTokenVerifier Accepts()
            => new StubVerifier(TokenVerificationResult.Ok(
                new VerifiedIdentity(Subject, "player@example.com", EmailVerified: true)));

        [Test]
        public async Task The_Endpoint_Lists_What_Was_Earned_With_Its_Play_Games_Id()
        {
            var accounts = new InMemoryAccountStore(new FakeTime());
            Account account = await accounts.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(Subject), "player@example.com");
            var service = new AchievementService(new InMemoryAchievementStore(), accounts);
            await service.ApplyAsync(Everything(account.PlayerId));

            AchievementReadResult result = await AchievementsEndpoint.ReadAsync(
                "a.jwt.token", Accepts(), accounts, service);

            Assert.Multiple(() =>
            {
                Assert.That(result.Status, Is.EqualTo(AchievementReadStatus.Known));
                Assert.That(result.HttpStatus, Is.EqualTo(200));
                Assert.That(result.Achievements.Select(a => a.Id), Is.EquivalentTo(AllFour));
                foreach (AchievementView view in result.Achievements)
                    Assert.That(view.PlayGamesId, Is.EqualTo(AchievementCatalog.Find(view.Id)!.PlayGamesId));
            });
        }

        [Test]
        public async Task The_Endpoint_Without_A_Token_Is_Unauthenticated_Not_Empty()
        {
            var accounts = new InMemoryAccountStore(new FakeTime());
            var service = new AchievementService(new InMemoryAchievementStore(), accounts);

            AchievementReadResult result = await AchievementsEndpoint.ReadAsync(null, Accepts(), accounts, service);

            Assert.That(result.Status, Is.EqualTo(AchievementReadStatus.Unauthenticated));
            Assert.That(result.HttpStatus, Is.EqualTo(401));
        }

        [Test]
        public async Task The_Endpoint_With_No_Service_Is_Unavailable()
        {
            AchievementReadResult result = await AchievementsEndpoint.ReadAsync(
                "a.jwt.token", Accepts(), new InMemoryAccountStore(new FakeTime()), achievements: null);

            Assert.That(result.Status, Is.EqualTo(AchievementReadStatus.Unavailable));
            Assert.That(result.HttpStatus, Is.EqualTo(503));
        }
    }
}
