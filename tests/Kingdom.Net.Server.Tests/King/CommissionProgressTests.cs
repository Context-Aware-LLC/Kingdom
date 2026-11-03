using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
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
        private static KingSeating Seating(string commission, int? bestP0 = null, int? bestP1 = null)
            => new KingSeating(3, commission, bestP0, bestP1, "Alice", "Bobby");

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
            List<byte[]>[] Run(TelemetrySource source, out CommissionProgress?[] starts)
            {
                var h = new SessionHarness(attributed: true, source: source,
                    king: Seating(CommissionIds.WasteNot, bestP0: 9, bestP1: 4));
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
            Assert.That(botFillStarts, Is.EqualTo(ladderStarts), "the same card at match start");
            for (int seat = 0; seat < 2; seat++)
            {
                Assert.That(botFill[seat].Count, Is.EqualTo(ladder[seat].Count), $"seat {seat}: the same number of frames");
                for (int i = 0; i < ladder[seat].Count; i++)
                    Assert.That(Encoding.UTF8.GetString(botFill[seat][i]), Is.EqualTo(Encoding.UTF8.GetString(ladder[seat][i])),
                        $"seat {seat} frame {i}: a bot-filled match is byte for byte a ladder match");
            }
            Assert.That(ladder[0].Any(f => Encoding.UTF8.GetString(f).Contains("\"commission\"")), Is.True,
                "the frames compared carry the field");
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
            Assert.That(h.Conn0.Last<SnapshotMsg>().Commission!.CountsIfYouWin, Is.False,
                "the last snapshot is of a finished match");
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

        [Test]
        public void A_final_that_ties_or_misses_the_best_is_not_new()
        {
            var tie = new SessionHarness(king: Seating(CommissionIds.Technically));
            int health0 = tie.Session.Host.State.Players[0].Hero.CurrentHealth;

            var h = new SessionHarness(king: Seating(CommissionIds.Technically, bestP0: health0));
            h.ConfirmMulligans();
            Concede(h, 1);
            Assert.That(h.Conn0.Last<MatchEndMsg>().Commission!.NewBest, Is.False, "a tie goes to whoever got there first");

            var first = new SessionHarness(king: Seating(CommissionIds.Technically));
            first.ConfirmMulligans();
            Concede(first, 1);
            Assert.That(first.Conn0.Last<MatchEndMsg>().Commission!.NewBest, Is.True, "a first score is a best");
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
        public void A_lost_commission_that_needs_no_win_still_carries_its_final()
        {
            var h = new SessionHarness(king: Seating(CommissionIds.Overkill));
            h.ConfirmMulligans();
            Concede(h, 0);

            Assert.That(h.Conn0.Last<MatchEndMsg>().Commission, Is.EqualTo(new CommissionFinal(CommissionIds.Overkill, 0, false)),
                "the loser of a commission that needs no win is still told the number");
        }
    }
}
