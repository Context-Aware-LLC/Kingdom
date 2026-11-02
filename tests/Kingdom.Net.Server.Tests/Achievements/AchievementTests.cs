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

        /// <summary>Everything a won match at 1 Health can earn: not the draw, the two losses, or
        /// Technically, which needs the hero at 0.</summary>
        private static readonly string[] AllWinnable = AchievementCatalog.All
            .Select(a => a.Id)
            .Except(new[] { "mutual_respect", "the_last_idea", "own_goal", "technically" })
            .ToArray();

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

        /// <summary>A match that earns every achievement in <see cref="AllWinnable"/>.</summary>
        private static AchievementMatchFacts Everything(
            string playerId, string matchId = "match-1", TelemetrySource source = TelemetrySource.Ladder)
            => new AchievementMatchFacts(
                matchId, playerId, Now, source, SeatResult.Win, MatchEndReason.Win, TurnCount: 9,
                EnemyMinionsDestroyed: 3, MulliganReplaced: 1, HeroPowersUsed: 2, FinalHealth: 1,
                FatigueDraws: 1, CardsBurned: 1, TurnsTimedOut: 1, TurnsEndedWithTenUnspent: 1,
                SelfDamage: 15, Overkills: 1, BigMinionsBounced: 1, LegendariesLost: 1, MostBulwarks: 7,
                EnemyFellTo: FinalBlow.Toll | FinalBlow.StolenMinion | FinalBlow.HeroPower);

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

        [Test]
        public void Play_Gets_At_Least_Ten()
        {
            // Play's quality checklist: at least 10 visible achievements.
            Assert.That(AchievementCatalog.All, Has.Count.GreaterThanOrEqualTo(10));
        }

        [Test]
        public void Every_Text_The_Client_Renders_Has_A_Rule()
        {
            // The client prints AchievementTexts and the server earns AchievementCatalog. A text
            // with no rule is a Ledger page nobody can ever fill.
            Assert.That(AchievementCatalog.All.Select(a => a.Id),
                Is.EqualTo(AchievementTexts.All.Select(t => t.Id)));
        }

        [Test]
        public void The_Result_Screen_Stamps_Findings_Only_In_Find_Game()
        {
            IReadOnlyList<string> ladder = AchievementCatalog.FindingsIn(Everything("player"));
            Assert.That(ladder, Does.Contain("proceed_unwisely").And.Contain("signature_move"));
            Assert.That(ladder, Does.Not.Contain("against_advice"), "a plain win is not a bad idea");

            Assert.That(AchievementCatalog.FindingsIn(Everything("player", source: TelemetrySource.BotFill)),
                Is.EqualTo(ladder), "a bot-filled match must stamp exactly what a ladder one does");
            Assert.That(AchievementCatalog.FindingsIn(Everything("player", source: TelemetrySource.Friendly)),
                Is.Empty);
        }

        [TestCase("second_thoughts")]
        [TestCase("working_as_intended")]
        [TestCase("early_retirement")]
        [TestCase("against_advice")]
        [TestCase("out_of_ideas")]
        [TestCase("too_many_ideas")]
        [TestCase("approved_by_default")]
        [TestCase("waste_not")]
        [TestCase("mutual_respect")]
        [TestCase("self_starter")]
        [TestCase("overkill")]
        [TestCase("hubris")]
        [TestCase("gift_horse")]
        [TestCase("seven_walls")]
        [TestCase("the_last_idea")]
        [TestCase("own_goal")]
        public void Each_Achievement_Fires_On_Its_Own_Fact(string id)
        {
            var quiet = new AchievementMatchFacts(
                "match-1", "player", Now, TelemetrySource.Ladder, SeatResult.Loss, MatchEndReason.Concede,
                TurnCount: 2, EnemyMinionsDestroyed: 0, MulliganReplaced: 0, HeroPowersUsed: 0);
            AchievementMatchFacts facts = id switch
            {
                "second_thoughts" => quiet with { MulliganReplaced = 1 },
                "working_as_intended" => quiet with { HeroPowersUsed = 1 },
                "early_retirement" => quiet with { EnemyMinionsDestroyed = 1 },
                "out_of_ideas" => quiet with { FatigueDraws = 1 },
                "too_many_ideas" => quiet with { CardsBurned = 1 },
                "approved_by_default" => quiet with { TurnsTimedOut = 1 },
                "waste_not" => quiet with { TurnsEndedWithTenUnspent = 1 },
                "mutual_respect" => quiet with { Result = SeatResult.Draw },
                "self_starter" => quiet with { SelfDamage = 15 },
                "overkill" => quiet with { Overkills = 1 },
                "hubris" => quiet with { BigMinionsBounced = 1 },
                "gift_horse" => quiet with { LegendariesLost = 1 },
                "seven_walls" => quiet with { MostBulwarks = 7 },
                "the_last_idea" => quiet with { FellTo = FinalBlow.Fatigue },
                "own_goal" => quiet with { FellTo = FinalBlow.Spell | FinalBlow.OwnSide },
                _ => quiet with { Result = SeatResult.Win, EndReason = MatchEndReason.Win, FinalHealth = 30 },
            };

            Assert.That(AchievementCatalog.All.Where(a => a.EarnedBy(facts)).Select(a => a.Id),
                Is.EqualTo(new[] { id }));
        }

        [TestCase("technically")]
        [TestCase("posthumous")]
        [TestCase("return_to_sender")]
        [TestCase("signature_move")]
        public void Each_Winning_Finding_Fires_On_Its_Own_Fact_Beside_The_Win(string id)
        {
            var win = new AchievementMatchFacts(
                "match-1", "player", Now, TelemetrySource.Ladder, SeatResult.Win, MatchEndReason.Win,
                TurnCount: 9, EnemyMinionsDestroyed: 0, MulliganReplaced: 0, HeroPowersUsed: 0, FinalHealth: 30);
            AchievementMatchFacts facts = id switch
            {
                "technically" => win with { FinalHealth = -1, FellTo = FinalBlow.Other },
                "posthumous" => win with { EnemyFellTo = FinalBlow.Toll },
                "return_to_sender" => win with { EnemyFellTo = FinalBlow.StolenMinion },
                _ => win with { EnemyFellTo = FinalBlow.HeroPower },
            };

            Assert.That(AchievementCatalog.All.Where(a => a.EarnedBy(facts)).Select(a => a.Id),
                Is.EquivalentTo(new[] { "against_advice", id }));
        }

        [Test]
        public void Own_Goal_Needs_The_Spell_To_Be_Your_Own()
        {
            var loss = new AchievementMatchFacts(
                "match-1", "player", Now, TelemetrySource.Ladder, SeatResult.Loss, MatchEndReason.Win,
                TurnCount: 9, EnemyMinionsDestroyed: 0, MulliganReplaced: 0, HeroPowersUsed: 0,
                FellTo: FinalBlow.Spell);

            Assert.That(AchievementCatalog.Find("own_goal")!.EarnedBy(loss), Is.False);
        }

        [TestCase(1, true)]
        [TestCase(2, false)]
        public void Proceed_Unwisely_Is_A_Win_At_Exactly_One_Health(int health, bool earned)
        {
            var win = new AchievementMatchFacts(
                "match-1", "player", Now, TelemetrySource.Ladder, SeatResult.Win, MatchEndReason.Win,
                TurnCount: 9, EnemyMinionsDestroyed: 0, MulliganReplaced: 0, HeroPowersUsed: 0, FinalHealth: health);

            Assert.That(AchievementCatalog.Find("proceed_unwisely")!.EarnedBy(win), Is.EqualTo(earned));
            Assert.That(AchievementCatalog.Find("proceed_unwisely")!.EarnedBy(win with { Result = SeatResult.Loss }),
                Is.False, "1 Health in a loss is not proceeding");
        }

        // ── Who earns ─────────────────────────────────────────────────────────────────────

        [Test]
        public async Task A_Find_Game_Match_Earns_What_It_Shows()
        {
            var h = await HarnessAsync();

            IReadOnlyList<string> earned = await h.Service().ApplyAsync(Everything(h.PlayerId));

            Assert.That(earned, Is.EquivalentTo(AllWinnable));
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
                Assert.That(ledger, Has.Count.EqualTo(AllWinnable.Length));
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
                Assert.That(human, Is.EquivalentTo(AllWinnable));
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
        public async Task A_Seat_With_No_Account_Row_Earns_Nothing()
        {
            // A tokenless tooling seat: its player id is a device-token hash no account row carries.
            var h = await HarnessAsync();

            IReadOnlyList<string> earned = await h.Service().ApplyAsync(Everything("device-token-hash"));

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
                Assert.That(result.Achievements.Select(a => a.Id), Is.EquivalentTo(AllWinnable));
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
