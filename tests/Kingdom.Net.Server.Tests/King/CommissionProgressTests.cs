using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Achievements;
using Kingdom.Net.Server.King;
using Kingdom.Net.Server.Telemetry;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.King
{
    /// <summary>
    /// The commission card's numbers on the wire (#421): each seat's own progress on its own
    /// snapshot, its final score on its own match end, scored by the same rule that banks it.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class CommissionProgressTests
    {
        private static KingSeating Seating(string commission, int? bestP0 = null, int? bestP1 = null,
            bool eligibleP0 = true, bool eligibleP1 = true)
            => new KingSeating(3, commission, bestP0, bestP1, "Alice", "Bobby", eligibleP0, eligibleP1);

        private static int ActiveSeat(SessionHarness h)
            => h.Session.Host.State.SeatOf(h.Session.Host.State.ActivePlayer);

        private static void EndTurn(SessionHarness h)
        {
            int seat = ActiveSeat(h);
            h.Post(seat, h.Client(seat).Intent(new EndTurnIntent(h.Session.Host.State.Players[seat].Id), h.Id));
        }

        private static void Concede(SessionHarness h, int seat)
            => h.Post(seat, h.Client(seat).Intent(new ConcedeIntent(h.Session.Host.State.Players[seat].Id), h.Id));

        private static CommissionProgress? StartProgress(SessionHarness h, int seat)
            => h.Conn(seat).Last<MatchStartMsg>().Snapshot.Commission;

        [Test]
        public void Each_seat_sees_only_its_own_score()
        {
            var h = new SessionHarness(king: Seating(CommissionIds.WasteNot, bestP0: 9, bestP1: 4));
            h.ConfirmMulligans();
            int ender = ActiveSeat(h);
            EndTurn(h);

            CommissionProgress mine = h.Conn(ender).Last<SnapshotMsg>().Commission!;
            CommissionProgress theirs = h.Conn(1 - ender).Last<SnapshotMsg>().Commission!;

            Assert.That(mine.Score, Is.GreaterThan(0), "the seat that ended its turn with mana left has a Waste Not score");
            Assert.That(theirs.Score, Is.Null, "the other seat has not qualified, and is not shown the first seat's number");
            Assert.That(mine.Best, Is.EqualTo(ender == 0 ? 9 : 4), "each seat's own best");
            Assert.That(theirs.Best, Is.EqualTo(ender == 0 ? 4 : 9));

            foreach (SnapshotMsg s in h.Conn0.MessagesOf<SnapshotMsg>())
                Assert.That(s.Commission!.Best, Is.EqualTo(9), "seat 0 is only ever sent seat 0's card");
            foreach (SnapshotMsg s in h.Conn1.MessagesOf<SnapshotMsg>())
                Assert.That(s.Commission!.Best, Is.EqualTo(4), "seat 1 is only ever sent seat 1's card");
        }

        [Test]
        public void Friendly_match_sends_no_commission_field()
        {
            var h = new SessionHarness();
            h.ConfirmMulligans();
            EndTurn(h);
            Concede(h, 0);

            Assert.That(h.Conn0.MessagesOf<MatchEndMsg>().Count(), Is.EqualTo(1), "the match ran to its end");
            foreach (FakeConnection conn in new[] { h.Conn0, h.Conn1 })
                foreach (byte[] frame in conn.Frames)
                    Assert.That(Encoding.UTF8.GetString(frame), Does.Not.Contain("\"commission\""),
                        "no King, no card: the absent key is how the client knows to hide it");
        }

        [Test]
        public void Reconnect_restores_the_score()
        {
            var h = new SessionHarness(king: Seating(CommissionIds.WasteNot, bestP0: 9, bestP1: 4));
            h.ConfirmMulligans();
            int ender = ActiveSeat(h);
            EndTurn(h);
            CommissionProgress before = h.Conn(ender).Last<SnapshotMsg>().Commission!;
            string token = h.Conn(ender).Last<MatchStartMsg>().ReconnectToken;

            h.Disconnect(ender);
            var fresh = new FakeConnection();
            h.Session.Dispatch(new SessionCommand.Reconnect(
                new ReconnectMsg(h.Session.Host.State.Players[ender].Id, token, 0), fresh));

            CommissionProgress after = fresh.Last<SnapshotMsg>().Commission!;
            Assert.That(after, Is.EqualTo(before), "the card comes back with the score it left with");
            Assert.That(after.Score, Is.GreaterThan(0));
        }

        [Test]
        public void Botfill_and_ladder_send_the_same_shape()
        {
            // Seat 1 is a human on the ladder and a bot (a Superuser, so never eligible) in bot-fill.
            // Seat 0 is the human whose wire must not give the bot away.
            List<byte[]>[] Run(TelemetrySource source, out CommissionProgress?[] starts)
            {
                var h = new SessionHarness(attributed: true, source: source,
                    king: Seating(CommissionIds.WasteNot, bestP0: 9, bestP1: 4,
                        eligibleP1: source != TelemetrySource.BotFill));
                starts = new[] { StartProgress(h, 0), StartProgress(h, 1) };
                h.ConfirmMulligans();
                EndTurn(h);
                EndTurn(h);
                Concede(h, 1);
                return new[] { NotMatchStart(h.Conn0), NotMatchStart(h.Conn1) };
            }

            List<byte[]>[] ladder = Run(TelemetrySource.Ladder, out CommissionProgress?[] ladderStarts);
            List<byte[]>[] botFill = Run(TelemetrySource.BotFill, out CommissionProgress?[] botFillStarts);

            Assert.That(ladderStarts[0], Is.Not.Null, "the field is there to compare");
            Assert.That(botFillStarts[0], Is.EqualTo(ladderStarts[0]), "the same card at match start");
            Assert.That(botFill[0].Count, Is.EqualTo(ladder[0].Count), "the human seat: the same number of frames");
            for (int i = 0; i < ladder[0].Count; i++)
                Assert.That(Encoding.UTF8.GetString(botFill[0][i]), Is.EqualTo(Encoding.UTF8.GetString(ladder[0][i])),
                    $"human seat frame {i}: a bot-filled match is byte for byte a ladder match");
            Assert.That(ladder[0].Any(f => Encoding.UTF8.GetString(f).Contains("\"commission\"")), Is.True,
                "the frames compared carry the field");
            Assert.That(botFillStarts[1], Is.Null, "the bot's own seat, a Superuser, is shown nothing");
        }

        /// <summary>Every frame but the match start, whose reconnect token is minted at random.</summary>
        private static List<byte[]> NotMatchStart(FakeConnection conn)
        {
            var kept = new List<byte[]>();
            foreach (byte[] frame in conn.Frames)
                if (ProtocolCodec.Decode(frame).Message is not MatchStartMsg) kept.Add(frame);
            return kept;
        }

        [Test]
        public void Win_only_score_is_greyed_until_the_end()
        {
            var h = new SessionHarness(king: Seating(CommissionIds.Technically));
            int health0 = h.Session.Host.State.Players[0].Hero.CurrentHealth;

            CommissionProgress first = StartProgress(h, 0)!;
            Assert.That(first.Id, Is.EqualTo(CommissionIds.Technically));
            Assert.That(first.CountsIfYouWin, Is.True, "a win-only score is greyed while the match is live");
            Assert.That(first.Score, Is.EqualTo(health0), "Technically shows its number from the first turn");
            Assert.That(first.Best, Is.Null);

            h.ConfirmMulligans();
            EndTurn(h);
            Assert.That(h.Conn0.Last<SnapshotMsg>().Commission!.CountsIfYouWin, Is.True);

            Concede(h, 1);
            CommissionProgress winner = h.Conn0.Last<SnapshotMsg>().Commission!;
            Assert.That(winner.CountsIfYouWin, Is.False, "the last snapshot is of a finished match");
            Assert.That(winner.Score, Is.EqualTo(h.Session.Host.State.Players[0].Hero.CurrentHealth),
                "the winner's number now counts, and is shown ungreyed");

            CommissionProgress loser = h.Conn1.Last<SnapshotMsg>().Commission!;
            Assert.That(loser.CountsIfYouWin, Is.False);
            Assert.That(loser.Score, Is.Null,
                "a lost win-only score counts for nothing, so the server stops showing it");
        }

        [Test]
        public void A_commission_that_needs_no_win_is_never_greyed_and_hides_a_zero()
        {
            var h = new SessionHarness(king: Seating(CommissionIds.Overkill));
            CommissionProgress first = StartProgress(h, 0)!;
            Assert.That(first.CountsIfYouWin, Is.False);
            Assert.That(first.Score, Is.Null, "no Overkill yet is not a score of 0: the card shows how it is scored");
        }

        [Test]
        public void Match_end_carries_the_final_and_new_best()
        {
            var h = new SessionHarness(king: Seating(CommissionIds.Technically, bestP0: 40, bestP1: 1));
            h.ConfirmMulligans();
            EndTurn(h);
            Concede(h, 1);

            CommissionFinal final = h.Conn0.Last<MatchEndMsg>().Commission!;
            int health0 = h.Session.Host.State.Players[0].Hero.CurrentHealth;
            Assert.That(final.Id, Is.EqualTo(CommissionIds.Technically));
            Assert.That(final.Score, Is.EqualTo(health0), "Technically scores the Health the seat won at");
            Assert.That(final.Score, Is.EqualTo(h.Conn0.Last<SnapshotMsg>().Commission!.Score),
                "the live number and the final one come from one rule");
            Assert.That(final.NewBest, Is.True, "lower is better, and it beats 40");
        }

        /// <summary>Technically: lower is better, and seat 0 wins at its full Health when seat 1
        /// concedes straight after the mulligan.</summary>
        private static CommissionFinal TechnicallyFinalAgainst(int? bestP0)
        {
            var h = new SessionHarness(king: Seating(CommissionIds.Technically, bestP0: bestP0));
            h.ConfirmMulligans();
            Concede(h, 1);
            return h.Conn0.Last<MatchEndMsg>().Commission!;
        }

        [Test]
        public void New_best_only_when_the_final_strictly_beats_the_best()
        {
            int health0 = new SessionHarness().Session.Host.State.Players[0].Hero.CurrentHealth;

            Assert.That(TechnicallyFinalAgainst(null).NewBest, Is.True, "a first score is a best");
            Assert.That(TechnicallyFinalAgainst(health0 + 1).NewBest, Is.True, "lower beats a higher best");
            Assert.That(TechnicallyFinalAgainst(health0).NewBest, Is.False, "a tie goes to whoever got there first");
            Assert.That(TechnicallyFinalAgainst(health0 - 1).NewBest, Is.False, "a final that misses the best is not new");
        }

        [Test]
        public void A_lost_win_only_commission_carries_null()
        {
            var h = new SessionHarness(king: Seating(CommissionIds.Technically, bestP0: 40, bestP1: 40));
            h.ConfirmMulligans();
            Concede(h, 1);

            Assert.That(h.Conn1.Last<MatchEndMsg>().Commission, Is.Null, "a lost win-only commission scores nothing");
            Assert.That(h.Conn0.Last<MatchEndMsg>().Commission, Is.Not.Null);
        }

        [Test]
        public void A_final_that_does_not_qualify_carries_null()
        {
            var h = new SessionHarness(king: Seating(CommissionIds.Overkill));
            h.ConfirmMulligans();
            Concede(h, 0);

            Assert.That(h.Conn0.Last<MatchEndMsg>().Commission, Is.Null, "an Overkill of 0 banks nothing, so there is no final");
            Assert.That(h.Conn1.Last<MatchEndMsg>().Commission, Is.Null, "the winner's 0 does not qualify either");
        }

        [Test]
        public void A_lost_commission_that_needs_no_win_still_carries_a_qualifying_final()
        {
            KingSeating king = Seating(CommissionIds.Overkill, bestP0: 3);
            CommissionFinal? final = king.FinalFor(0, Facts(SeatResult.Loss, overkillBest: 5));
            Assert.That(final, Is.EqualTo(new CommissionFinal(CommissionIds.Overkill, 5, true)),
                "Overkill needs no win, so a loser who qualifies is told the number");
        }

        [Test]
        public void An_ineligible_seat_is_shown_nothing_and_the_other_seat_is_unchanged()
        {
            var both = new SessionHarness(king: Seating(CommissionIds.Technically, bestP0: 40, bestP1: 40));
            var su = new SessionHarness(king: Seating(CommissionIds.Technically, bestP0: 40, bestP1: 40, eligibleP1: false));
            foreach (SessionHarness h in new[] { both, su })
            {
                h.ConfirmMulligans();
                EndTurn(h);
                Concede(h, 0);   // seat 1 wins, so a final would be owed
            }

            Assert.That(su.Conn1.MessagesOf<SnapshotMsg>().All(s => s.Commission is null), Is.True,
                "a Superuser or unattributed seat banks nothing, so it is sent no card");
            Assert.That(su.Conn1.Last<MatchStartMsg>().Snapshot.Commission, Is.Null);
            Assert.That(su.Conn1.Last<MatchEndMsg>().Commission, Is.Null, "and no final, though it won");
            Assert.That(both.Conn1.Last<MatchEndMsg>().Commission, Is.Not.Null, "the same win, eligible, has one");

            List<byte[]> withPeer = NotMatchStart(both.Conn0), withSuperuser = NotMatchStart(su.Conn0);
            Assert.That(withSuperuser.Count, Is.EqualTo(withPeer.Count));
            for (int i = 0; i < withPeer.Count; i++)
                Assert.That(Encoding.UTF8.GetString(withSuperuser[i]), Is.EqualTo(Encoding.UTF8.GetString(withPeer[i])),
                    $"frame {i}: the other seat's wire does not change with its opponent's eligibility");
        }

        [Test]
        public void An_ineligible_seat_gets_null_from_both_builders()
        {
            KingSeating king = Seating(CommissionIds.Overkill, eligibleP0: false);
            AchievementMatchFacts facts = Facts(SeatResult.Win, overkillBest: 5);
            Assert.That(king.ProgressFor(0, facts, live: true), Is.Null);
            Assert.That(king.FinalFor(0, facts), Is.Null);
            Assert.That(king.ProgressFor(1, facts, live: true), Is.Not.Null, "the eligible seat is unaffected");
            Assert.That(king.FinalFor(1, facts), Is.Not.Null);
        }

        private static AchievementMatchFacts Facts(SeatResult result, int overkillBest)
            => new AchievementMatchFacts("m", "player", DateTimeOffset.UnixEpoch, TelemetrySource.Ladder, result,
                MatchEndReason.Win, 5, 0, 0, 0, OverkillBest: overkillBest);
    }
}
