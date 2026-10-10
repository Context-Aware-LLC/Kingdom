using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server.Identity;
using Emberfall.Net.Server.Quests;
using Emberfall.Net.Server.Telemetry;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Quests
{
    /// <summary>
    /// Daily quests (dev-docs/specs/daily-quests.md). The failure modes worth a test here are the
    /// ones that are invisible until a player is owed money: a quest paying twice, a replayed match
    /// inflating progress, and an assignment that moves under a player who has already made
    /// progress against it.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class QuestProgressTests
    {
        private sealed class StubGoldRewardPolicy : IGoldRewardPolicy
        {
            public long MatchGold => 10;
            public long FirstWinGold => 20;
            public long DailyQuestGold(string questId) => 60;
        }

        /// <summary>An <see cref="ICardDatabase"/> over an explicit id→class map.</summary>
        private sealed class ClassCardDatabase : ICardDatabase
        {
            private readonly Dictionary<int, CardDefinition> _byId = new Dictionary<int, CardDefinition>();

            public ClassCardDatabase(params (int Id, CardClass Class)[] rows)
            {
                foreach ((int id, CardClass @class) in rows)
                    _byId[id] = new CardDefinition(
                        id, "c" + id, "Card " + id, CardType.MINION, 1, 1, 1, null,
                        @class, Rarity.COMMON, Array.Empty<Keyword>(), Array.Empty<RuleDefinition>(),
                        "", "art/c" + id, SetId.CORE, true, null, "");
            }

            public bool TryGet(int cardId, out CardDefinition def) => _byId.TryGetValue(cardId, out def!);
            public CardDefinition Get(int cardId) => _byId[cardId];
            public IReadOnlyCollection<CardDefinition> AllCollectible() => _byId.Values.ToList();
        }

        private static readonly DateOnly Day = new DateOnly(2026, 9, 9);

        // ── Assignment ───────────────────────────────────────────────────────────

        [Test]
        public void A_Players_Quest_For_A_Day_Is_The_Same_Every_Time_It_Is_Asked()
        {
            QuestDefinition first = QuestCatalog.ForDay("player-1", Day);
            QuestDefinition again = QuestCatalog.ForDay("player-1", Day);

            // The whole no-scheduler design rests on this. If derivation were not stable, a player's
            // quest would change under them mid-day and their progress rows would orphan.
            Assert.That(again.Id, Is.EqualTo(first.Id));
            Assert.That(again.Goal, Is.EqualTo(first.Goal));
        }

        [Test]
        public void Two_Players_On_One_Day_Do_Not_All_Get_The_Same_Quest()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < 60; i++) ids.Add(QuestCatalog.ForDay("player-" + i, Day).Id);

            // Not a distribution assertion — just that the seed is a function of the player and not
            // of the day alone, which a hash over the date only would silently be.
            Assert.That(ids.Count, Is.GreaterThan(1));
        }

        [Test]
        public void One_Player_Is_Not_Given_The_Same_Quest_Every_Day()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < 60; i++) ids.Add(QuestCatalog.ForDay("player-1", Day.AddDays(i)).Id);

            Assert.That(ids.Count, Is.GreaterThan(1));
        }

        [Test]
        public void The_Active_Set_Is_Three_Days_Ending_Today()
        {
            IReadOnlyList<(DateOnly Day, QuestDefinition Quest)> active =
                QuestCatalog.Active("player-1", Day);

            Assert.That(active, Has.Count.EqualTo(QuestCatalog.ActiveDays));
            Assert.That(active[0].Day, Is.EqualTo(Day));
            Assert.That(active[QuestCatalog.ActiveDays - 1].Day, Is.EqualTo(Day.AddDays(-(QuestCatalog.ActiveDays - 1))));
        }

        // ── Facts from the match-end events ──────────────────────────────────────

        [Test]
        public void Only_Cards_That_Were_PLAYED_Count()
        {
            var db = new ClassCardDatabase((1, CardClass.EMBERWARD), (2, CardClass.EMBERWARD), (3, CardClass.NEUTRAL));
            AccountId p0 = AccountId.FromAuthenticatedAccount("player-0");
            AccountId p1 = AccountId.FromAuthenticatedAccount("player-1");

            IReadOnlyList<QuestMatchFacts> facts = QuestMatchFacts.From(
                Complete(p0, p1, CardClass.EMBERWARD, MatchOutcome.Player0Win, turns: 8),
                new CardSeenEvent(new MatchId("match-1"), Now, TelemetrySource.Ladder, new[]
                {
                    Row(p0, CardClass.EMBERWARD, true, cardId: 1, firstPlayedTurn: 2),
                    Row(p0, CardClass.EMBERWARD, true, cardId: 2, firstPlayedTurn: 5),
                    Row(p0, CardClass.EMBERWARD, true, cardId: 3, firstPlayedTurn: null),  // never left the deck
                }),
                db);

            QuestMatchFacts seat = facts.Single(f => f.PlayerId == "player-0");

            Assert.Multiple(() =>
            {
                Assert.That(seat.DistinctCardsPlayed, Is.EqualTo(2));
                Assert.That(seat.DistinctCardsPlayedOf(CardClass.EMBERWARD), Is.EqualTo(2));
                Assert.That(seat.DistinctCardsPlayedOf(CardClass.NEUTRAL), Is.Zero, "an unplayed card was counted");
                Assert.That(seat.Won, Is.True);
                Assert.That(seat.TurnCount, Is.EqualTo(8));
                Assert.That(seat.EnemyMinionsDestroyed, Is.EqualTo(4), "the seat's own count, not the opponent's");
            });
        }

        [Test]
        public void Both_Seats_Come_Back_And_Only_One_Of_Them_Won()
        {
            var db = new ClassCardDatabase((1, CardClass.EMBERWARD));
            AccountId p0 = AccountId.FromAuthenticatedAccount("player-0");
            AccountId p1 = AccountId.FromAuthenticatedAccount("player-1");

            IReadOnlyList<QuestMatchFacts> facts = QuestMatchFacts.From(
                Complete(p0, p1, CardClass.EMBERWARD, MatchOutcome.Player1Win, turns: 12),
                new CardSeenEvent(new MatchId("match-1"), Now, TelemetrySource.Ladder,
                    Array.Empty<CardSeenRow>()),
                db);

            Assert.That(facts, Has.Count.EqualTo(2));
            Assert.That(facts.Single(f => f.PlayerId == "player-0").Won, Is.False);
            Assert.That(facts.Single(f => f.PlayerId == "player-1").Won, Is.True);
        }

        [Test]
        public void Events_For_Two_Different_Matches_Yield_Nothing()
        {
            var db = new ClassCardDatabase((1, CardClass.EMBERWARD));
            AccountId p0 = AccountId.FromAuthenticatedAccount("player-0");
            AccountId p1 = AccountId.FromAuthenticatedAccount("player-1");

            IReadOnlyList<QuestMatchFacts> facts = QuestMatchFacts.From(
                Complete(p0, p1, CardClass.EMBERWARD, MatchOutcome.Player0Win, turns: 8),
                new CardSeenEvent(new MatchId("a-different-match"), Now, TelemetrySource.Ladder,
                    Array.Empty<CardSeenRow>()),
                db);

            Assert.That(facts, Is.Empty);
        }

        // ── Progress and payment ─────────────────────────────────────────────────

        [Test]
        public async Task A_Quest_Pays_When_It_Completes_And_Not_Before()
        {
            var h = await HarnessAsync();
            QuestDefinition quest = QuestCatalog.ForDay(h.PlayerId, Day);

            for (int i = 0; i < quest.Goal - 1; i++)
                await h.Service.ApplyAsync(FactsFor(h.PlayerId, quest, "match-" + i));

            long beforeCompletion = await h.Ledger.BalanceAsync(h.PlayerId, EconomyItems.Gold);
            await h.Service.ApplyAsync(FactsFor(h.PlayerId, quest, "match-final"));
            long afterCompletion = await h.Ledger.BalanceAsync(h.PlayerId, EconomyItems.Gold);

            Assert.Multiple(() =>
            {
                Assert.That(beforeCompletion, Is.Zero, "an unfinished quest paid");
                Assert.That(afterCompletion, Is.EqualTo(60));
            });
        }

        [Test]
        public async Task Replaying_The_Match_That_Completed_A_Quest_Does_Not_Pay_Again()
        {
            var h = await HarnessAsync();
            QuestDefinition quest = QuestCatalog.ForDay(h.PlayerId, Day);

            for (int i = 0; i < quest.Goal; i++)
                await h.Service.ApplyAsync(FactsFor(h.PlayerId, quest, "match-" + i));
            long once = await h.Ledger.BalanceAsync(h.PlayerId, EconomyItems.Gold);

            for (int i = 0; i < quest.Goal; i++)
                await h.Service.ApplyAsync(FactsFor(h.PlayerId, quest, "match-" + i));
            long replayed = await h.Ledger.BalanceAsync(h.PlayerId, EconomyItems.Gold);

            Assert.That(replayed, Is.EqualTo(once));
        }

        [Test]
        public async Task A_Completed_Quest_Credited_On_A_LATER_Day_Does_Not_Pay_Again()
        {
            var h = await HarnessAsync();
            QuestDefinition quest = QuestCatalog.ForDay(h.PlayerId, Day);

            for (int i = 0; i < quest.Goal; i++)
                await h.Service.ApplyAsync(FactsFor(h.PlayerId, quest, "match-" + i));

            // The trap this exists for: the faucet keys its transaction id on the CURRENT UTC day,
            // so a stored quest that completed yesterday and takes another credit today would mint
            // a fresh key and pay a second time. Payment is on the transition, not on being done.
            await h.Service.ApplyAsync(FactsFor(h.PlayerId, quest, "tomorrow-match", Day.AddDays(1)));

            // Counted on the LEDGER ROW, not on the balance: tomorrow's match also credits
            // TOMORROW's quest, and that one completing and paying is correct. Only this quest-day
            // is under test.
            string thisQuestDay = quest.Id + "@2026-09-09";
            int paymentsForThisQuestDay = (await h.Ledger.HistoryAsync(h.PlayerId))
                .Count(g => g.Source == "daily_quest" && g.ExternalTxnId.Contains(thisQuestDay, StringComparison.Ordinal));

            Assert.That(paymentsForThisQuestDay, Is.EqualTo(1),
                "the quest paid more than once across a day boundary");
        }

        [Test]
        public async Task A_Guest_Holds_No_Quests_And_Is_Paid_Nothing()
        {
            var accounts = new InMemoryAccountStore(new FakeTime());
            Account guest = await accounts.ResolveOrCreateAsync("device_token", "token-hash");
            var ledger = new InMemoryEntitlementLedger();
            var progress = new InMemoryQuestProgressStore();
            var service = new QuestProgressService(
                progress, accounts, new GoldFaucet(ledger, new FakeTime(), new StubGoldRewardPolicy()));

            QuestDefinition quest = QuestCatalog.ForDay(guest.PlayerId, Day);
            for (int i = 0; i < quest.Goal; i++)
                await service.ApplyAsync(FactsFor(guest.PlayerId, quest, "match-" + i));

            Assert.That(await ledger.BalanceAsync(guest.PlayerId, EconomyItems.Gold), Is.Zero);
        }

        [Test]
        public async Task The_View_Reports_Progress_And_Never_Overshoots_The_Goal()
        {
            var h = await HarnessAsync();
            QuestDefinition quest = QuestCatalog.ForDay(h.PlayerId, Day);

            // Credit far past the goal — a card quest legitimately overshoots in the match that
            // completes it, and a bar reading 19/15 looks like a bug to the person holding it.
            for (int i = 0; i < quest.Goal + 5; i++)
                await h.Service.ApplyAsync(FactsFor(h.PlayerId, quest, "match-" + i));

            IReadOnlyList<QuestView> view = await h.Service.ViewAsync(h.PlayerId, Day);
            QuestView today = view.Single(v => v.Day == Day);

            Assert.Multiple(() =>
            {
                Assert.That(today.Id, Is.EqualTo(quest.Id));
                Assert.That(today.Progress, Is.EqualTo(quest.Goal));
                Assert.That(today.Complete, Is.True);
            });
        }

        [Test]
        public async Task An_Untouched_Quest_Reports_Zero_Rather_Than_Vanishing()
        {
            var h = await HarnessAsync();

            IReadOnlyList<QuestView> view = await h.Service.ViewAsync(h.PlayerId, Day);

            Assert.That(view, Has.Count.EqualTo(QuestCatalog.ActiveDays));
            Assert.That(view.All(v => v.Progress == 0 && !v.Complete), Is.True);
        }

        // ── The sink that pairs the two match-end events ─────────────────────────

        [Test]
        public async Task The_Sink_Forwards_Every_Event_To_The_Sink_It_Wraps()
        {
            var h = await HarnessAsync();
            var inner = new InMemoryTelemetrySink();
            var sink = new QuestProgressSink(inner, h.Service, new ClassCardDatabase());

            AccountId p0 = AccountId.FromAuthenticatedAccount(h.PlayerId);
            AccountId p1 = AccountId.FromAuthenticatedAccount("other");
            sink.MatchComplete(Complete(p0, p1, CardClass.EMBERWARD, MatchOutcome.Player0Win, 8));
            sink.CardsSeen(new CardSeenEvent(new MatchId("match-1"), Now, TelemetrySource.Ladder,
                Array.Empty<CardSeenRow>()));
            sink.MatchVoid(new MatchVoidEvent(new MatchId("match-2"), Now, 3, TelemetrySource.Ladder, p0, p1));

            // Quest progress is a SECOND reader of events that already exist. If decorating could
            // drop one, it would silently corrupt the balance sample the whole C4 bar rests on.
            Assert.Multiple(() =>
            {
                Assert.That(inner.Completes, Has.Count.EqualTo(1));
                Assert.That(inner.CardSeenBatches, Has.Count.EqualTo(1));
                Assert.That(inner.Voids, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public async Task A_Paired_Match_Credits_Both_Seats()
        {
            // Both seats are players who hold "play-three" today — the one quest ANY finished match
            // advances, win or lose. Picked by derivation rather than assumed, so the test asserts
            // that the sink reached both seats and not that a coin landed the right way up.
            var accounts = new InMemoryAccountStore(new FakeTime());
            Account winner = await PlayerHoldingAsync(accounts, "play-three", 0);
            Account loser = await PlayerHoldingAsync(accounts, "play-three", 1);

            var ledger = new InMemoryEntitlementLedger();
            var service = new QuestProgressService(
                new InMemoryQuestProgressStore(), accounts,
                new GoldFaucet(ledger, new FakeTime(), new StubGoldRewardPolicy()));
            var sink = new QuestProgressSink(
                NullTelemetrySink.Instance, service, new ClassCardDatabase((1, CardClass.EMBERWARD)));

            AccountId p0 = AccountId.FromAuthenticatedAccount(winner.PlayerId);
            AccountId p1 = AccountId.FromAuthenticatedAccount(loser.PlayerId);

            await sink.EvaluateAsync(
                Complete(p0, p1, CardClass.EMBERWARD, MatchOutcome.Player0Win, 8),
                new CardSeenEvent(new MatchId("match-1"), Now, TelemetrySource.Ladder, new[]
                {
                    Row(p0, CardClass.EMBERWARD, true, 1, 2),
                    Row(p1, CardClass.EMBERWARD, false, 1, 3),
                }));

            QuestView first = (await service.ViewAsync(winner.PlayerId, Day)).Single(v => v.Day == Day);
            QuestView second = (await service.ViewAsync(loser.PlayerId, Day)).Single(v => v.Day == Day);

            Assert.Multiple(() =>
            {
                Assert.That(first.Progress, Is.EqualTo(1), "the winner's quest did not advance");
                Assert.That(second.Progress, Is.EqualTo(1), "the loser's quest did not advance");
            });
        }

        [Test]
        public async Task Bot_fill_credits_the_human_and_never_the_roster_bot()
        {
            var accounts = new InMemoryAccountStore(new FakeTime());
            Account human = await PlayerHoldingAsync(accounts, "play-three", 0);
            Account bot = await PlayerHoldingAsync(accounts, "play-three", 1);
            var progress = new InMemoryQuestProgressStore();
            var service = new QuestProgressService(
                progress,
                accounts,
                new GoldFaucet(new InMemoryEntitlementLedger(), new FakeTime(), new StubGoldRewardPolicy()),
                account => account == AccountId.FromAuthenticatedAccount(bot.PlayerId));
            var sink = new QuestProgressSink(
                NullTelemetrySink.Instance, service, new ClassCardDatabase());

            AccountId p0 = AccountId.FromAuthenticatedAccount(human.PlayerId);
            AccountId p1 = AccountId.FromAuthenticatedAccount(bot.PlayerId);
            await sink.EvaluateAsync(
                Complete(
                    p0,
                    p1,
                    CardClass.EMBERWARD,
                    MatchOutcome.Player0Win,
                    8,
                    TelemetrySource.BotFill),
                new CardSeenEvent(
                    new MatchId("match-1"),
                    Now,
                    TelemetrySource.BotFill,
                    Array.Empty<CardSeenRow>()));

            QuestView humanQuest = (await service.ViewAsync(human.PlayerId, Day))
                .Single(v => v.Day == Day);
            QuestView botQuest = (await service.ViewAsync(bot.PlayerId, Day))
                .Single(v => v.Day == Day);

            Assert.Multiple(() =>
            {
                Assert.That(humanQuest.Progress, Is.EqualTo(1));
                Assert.That(botQuest.Progress, Is.Zero);
            });
        }

        [Test]
        public async Task A_Card_Sample_With_No_Match_Complete_Behind_It_Advances_Nothing()
        {
            var accounts = new InMemoryAccountStore(new FakeTime());
            Account player = await PlayerHoldingAsync(accounts, "play-three", 0);
            var service = new QuestProgressService(
                new InMemoryQuestProgressStore(), accounts,
                new GoldFaucet(new InMemoryEntitlementLedger(), new FakeTime(), new StubGoldRewardPolicy()));
            var sink = new QuestProgressSink(
                NullTelemetrySink.Instance, service, new ClassCardDatabase((1, CardClass.EMBERWARD)));

            AccountId p0 = AccountId.FromAuthenticatedAccount(player.PlayerId);

            // Half the facts is not a smaller quest update, it is a wrong one — the turn count lives
            // in the event that never arrived.
            sink.CardsSeen(new CardSeenEvent(new MatchId("orphan"), Now, TelemetrySource.Ladder, new[]
            {
                Row(p0, CardClass.EMBERWARD, true, 1, 2),
            }));

            QuestView today = (await service.ViewAsync(player.PlayerId, Day)).Single(v => v.Day == Day);
            Assert.That(today.Progress, Is.Zero);
        }

        /// <summary>
        /// An account whose derived player holds <paramref name="questId"/> on <see cref="Day"/>.
        /// Scanning for one keeps a test that needs a particular quest honest — the alternative is
        /// asserting on whatever the hash happened to produce.
        /// </summary>
        private static async Task<Account> PlayerHoldingAsync(
            InMemoryAccountStore accounts, string questId, int skip)
        {
            for (int i = 0; i < 5000; i++)
            {
                Account candidate = await accounts.ResolveOrCreateAsync("supabase", "quest-subject-" + i);
                if (QuestCatalog.ForDay(candidate.PlayerId, Day).Id != questId) continue;
                if (skip-- > 0) continue;
                return candidate;
            }
            throw new InvalidOperationException("No subject derives quest '" + questId + "' on " + Day + ".");
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static DateTimeOffset Now => new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

        private sealed record Harness(
            QuestProgressService Service,
            InMemoryEntitlementLedger Ledger,
            string PlayerId,
            string OtherPlayerId);

        private static async Task<Harness> HarnessAsync()
        {
            var accounts = new InMemoryAccountStore(new FakeTime());
            Account player = await accounts.ResolveOrCreateAsync("supabase", "subject-0");
            Account other = await accounts.ResolveOrCreateAsync("supabase", "subject-1");
            var ledger = new InMemoryEntitlementLedger();
            var service = new QuestProgressService(
                new InMemoryQuestProgressStore(),
                accounts,
                new GoldFaucet(ledger, new FakeTime(), new StubGoldRewardPolicy()));
            return new Harness(service, ledger, player.PlayerId, other.PlayerId);
        }

        /// <summary>
        /// Facts shaped so they credit <paramref name="quest"/> whatever it turned out to be — a win
        /// as the quest's class, past turn 15, with plenty of cards of every class played. The tests
        /// above are about progress and payment, not about which predicate fired.
        /// </summary>
        private static QuestMatchFacts FactsFor(
            string playerId, QuestDefinition quest, string matchId, DateOnly? day = null)
        {
            CardClass @class = ClassNamedIn(quest.Id);
            var byClass = new Dictionary<CardClass, int>();
            foreach (CardClass c in Enum.GetValues<CardClass>()) byClass[c] = quest.Goal;

            DateOnly on = day ?? Day;
            return new QuestMatchFacts(
                matchId,
                playerId,
                new DateTimeOffset(on.Year, on.Month, on.Day, 12, 0, 0, TimeSpan.Zero),
                TelemetrySource.Ladder,
                @class,
                SeatResult.Win,
                // The one axis where two templates want opposite things: quick-win wants <= 10 and
                // long-game wants > 15. Bound to whichever quest is in hand so these tests are about
                // progress and payment rather than about which predicate happened to be drawn.
                TurnCount: quest.Id == "quick-win" ? 8 : 16,
                DistinctCardsPlayed: quest.Goal,
                EnemyMinionsDestroyed: quest.Goal,
                DistinctCardsPlayedByClass: byClass);
        }

        private static CardClass ClassNamedIn(string questId)
        {
            foreach (CardClass c in Enum.GetValues<CardClass>())
                if (questId.EndsWith(c.ToString().ToLowerInvariant(), StringComparison.Ordinal)) return c;
            return CardClass.EMBERWARD;
        }

        private static MatchCompleteEvent Complete(
            AccountId p0,
            AccountId p1,
            CardClass @class,
            MatchOutcome outcome,
            int turns,
            TelemetrySource source = TelemetrySource.Ladder)
            => new MatchCompleteEvent(
                new MatchId("match-1"), Now, MatchEndReason.Win, outcome, turns, source,
                new SeatOutcome(p0, @class, "deck-0", "hash-0",
                    outcome == MatchOutcome.Player0Win ? SeatResult.Win : SeatResult.Loss,
                    Array.Empty<int>(), EnemyMinionsDestroyed: 4),
                new SeatOutcome(p1, @class, "deck-1", "hash-1",
                    outcome == MatchOutcome.Player1Win ? SeatResult.Win : SeatResult.Loss,
                    Array.Empty<int>(), EnemyMinionsDestroyed: 1));

        private static CardSeenRow Row(
            AccountId account, CardClass @class, bool won, int cardId, int? firstPlayedTurn)
            => new CardSeenRow(account, @class, won, cardId, true, true, false, firstPlayedTurn);
    }
}
