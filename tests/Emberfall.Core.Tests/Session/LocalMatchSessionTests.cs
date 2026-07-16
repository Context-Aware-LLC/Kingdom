using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.Session
{
    /// <summary>
    /// WS A4 session-seam suite (engineering/a4-session-seam-design.md §8) — including the
    /// long-deferred a3 §7.1 TEST 6 (honest AI): everything an in-process consumer can reach
    /// through <see cref="IMatchSession"/> is projected, at every step of a live match.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class LocalMatchSessionTests
    {
        private const int DeckSize = 12;

        private static (IMatchSession p0, IMatchSession p1) NewSessions(ulong seed)
        {
            var db = new FakeCardDatabase();
            var setup = new MatchSetup(
                CardClass.EMBERWARD, FakeCardDatabase.Deck("embermark", DeckSize),
                CardClass.TIDECALLER, FakeCardDatabase.Deck("tidemark", DeckSize));
            return LocalMatch.Start(db, setup, new PlayerId("P0"), new PlayerId("P1"), seed);
        }

        private static CommandResult Submit(IMatchSession s, PlayerIntent intent)
        {
            var task = s.SubmitAsync(intent);
            Assert.That(task.IsCompleted, Is.True, "Local session must resolve synchronously.");
            return task.Result;
        }

        /// <summary>Mulligan both seats through their own sessions.</summary>
        private static void MulliganBoth(IMatchSession p0, IMatchSession p1)
        {
            Assert.That(Submit(p0, new MulliganIntent(p0.Current.Players[0].Id, Array.Empty<InstanceId>())).Accepted, Is.True);
            Assert.That(Submit(p1, new MulliganIntent(p1.Current.Players[1].Id, Array.Empty<InstanceId>())).Accepted, Is.True);
        }

        private static void AssertProjectedFor(GameState s, int viewerSeat)
        {
            Assert.That(s.Rng.RawState, Is.Zero, "Live RNG cursor leaked through the session.");
            foreach (var c in s.Players[1 - viewerSeat].Hand)
            {
                Assert.That(c.Id.Value, Is.Zero, "Opponent hand id leaked.");
                Assert.That(c.DefinitionId, Is.Empty, "Opponent hand DefinitionId leaked.");
            }
            for (int seat = 0; seat < 2; seat++)
                foreach (var c in s.Players[seat].Deck)
                    Assert.That(c.DefinitionId, Is.Empty, "Deck DefinitionId leaked.");
        }

        // ── a3 §7.1 test 6 — honest AI, at every step ────────────────────────────

        [Test]
        public void Honest_AI_Sessions_Never_Expose_Hidden_Information_At_Any_Step()
        {
            var (p0, p1) = NewSessions(0xA4A4UL);
            var sessions = new[] { p0, p1 };

            // Every snapshot pushed to either session mid-match must already be projected.
            foreach (var (session, seat) in new[] { (p0, 0), (p1, 1) })
                session.OnSnapshot += st => AssertProjectedFor(st, seat);

            AssertProjectedFor(p0.Current, 0);   // initial states too
            AssertProjectedFor(p1.Current, 1);

            MulliganBoth(p0, p1);
            for (int turn = 0; turn < 6; turn++)
            {
                var active = p0.Current.ActivePlayer;
                int seat = p0.Current.SeatOf(active);
                Assert.That(Submit(sessions[seat], new EndTurnIntent(active)).Accepted, Is.True);
            }

            AssertProjectedFor(p0.Current, 0);
            AssertProjectedFor(p1.Current, 1);
        }

        [Test]
        public void Own_Hand_Is_Unredacted_In_Own_Projection()
        {
            var (p0, p1) = NewSessions(0xA4B0UL);
            Assert.That(p0.Current.Players[0].Hand, Is.Not.Empty);
            foreach (var c in p0.Current.Players[0].Hand)
                Assert.That(c.DefinitionId, Is.Not.Empty);
            foreach (var c in p1.Current.Players[1].Hand)
                Assert.That(c.DefinitionId, Is.Not.Empty);
        }

        // ── reject semantics ─────────────────────────────────────────────────────

        [Test]
        public void Reject_Resolves_Task_Without_Snapshot_Or_Version_Bump()
        {
            var (p0, p1) = NewSessions(0xA4C0UL);
            MulliganBoth(p0, p1);

            var inactive = p0.Current.ActivePlayer.Equals(p0.Current.Players[0].Id) ? p1 : p0;
            var inactiveId = inactive.Current.ActivePlayer.Equals(inactive.Current.Players[0].Id)
                ? inactive.Current.Players[1].Id : inactive.Current.Players[0].Id;

            ulong before = p0.StateVersion;
            int snapshots = 0;
            p0.OnSnapshot += _ => snapshots++;
            p1.OnSnapshot += _ => snapshots++;

            var result = Submit(inactive, new EndTurnIntent(inactiveId));

            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Reason, Is.Not.Null);
            Assert.That(result.Events, Is.Empty);
            Assert.That(snapshots, Is.Zero, "A reject must not fire OnSnapshot.");
            Assert.That(p0.StateVersion, Is.EqualTo(before));
        }

        // ── accepted-command fan-out ─────────────────────────────────────────────

        [Test]
        public void Accepted_Command_Fires_One_Snapshot_Per_Session_With_Matching_Versions()
        {
            var (p0, p1) = NewSessions(0xA4D0UL);
            MulliganBoth(p0, p1);

            int fired0 = 0, fired1 = 0;
            p0.OnSnapshot += _ => fired0++;
            p1.OnSnapshot += _ => fired1++;

            var active = p0.Current.ActivePlayer;
            int seat = p0.Current.SeatOf(active);
            var result = Submit(seat == 0 ? p0 : p1, new EndTurnIntent(active));

            Assert.That(result.Accepted, Is.True);
            Assert.That(fired0, Is.EqualTo(1));
            Assert.That(fired1, Is.EqualTo(1));
            Assert.That(p0.StateVersion, Is.EqualTo(p1.StateVersion));
            Assert.That(p0.StateVersion, Is.EqualTo(result.StateVersion));
        }

        [Test]
        public void Snapshot_Arrives_Before_Events_And_Current_Is_Post_Command()
        {
            var (p0, p1) = NewSessions(0xA4E0UL);
            MulliganBoth(p0, p1);

            var order = new List<string>();
            p0.OnSnapshot += _ => order.Add("snapshot");
            p0.OnEvents += _ => order.Add("events");
            ulong versionInsideEvents = 0;
            p0.OnEvents += _ => versionInsideEvents = p0.Current.StateVersion;

            var active = p0.Current.ActivePlayer;
            int seat = p0.Current.SeatOf(active);
            var result = Submit(seat == 0 ? p0 : p1, new EndTurnIntent(active));

            Assert.That(result.Accepted, Is.True);
            Assert.That(order.First(), Is.EqualTo("snapshot"),
                "IMatchSession contract: Current must be post-command when OnEvents fires.");
            Assert.That(versionInsideEvents, Is.EqualTo(result.StateVersion));
        }

        // ── event redaction through the seam ─────────────────────────────────────

        [Test]
        public void Turn_Start_Draw_Is_Redacted_For_The_Opponent_Session_Only()
        {
            var (p0, p1) = NewSessions(0xA4F0UL);
            MulliganBoth(p0, p1);

            var sessions = new[] { p0, p1 };
            var active = p0.Current.ActivePlayer;
            int activeSeat = p0.Current.SeatOf(active);
            int nextSeat = 1 - activeSeat;   // EndTurn hands the turn (and a draw) to this seat

            IReadOnlyList<GameEvent>? drawerBatch = null, opponentBatch = null;
            sessions[nextSeat].OnEvents += b => drawerBatch = b;
            sessions[activeSeat].OnEvents += b => opponentBatch = b;

            var result = Submit(sessions[activeSeat], new EndTurnIntent(active));
            Assert.That(result.Accepted, Is.True);

            Assert.That(drawerBatch, Is.Not.Null);
            Assert.That(opponentBatch, Is.Not.Null);
            Assert.That(drawerBatch.Count, Is.EqualTo(opponentBatch.Count), "Events are never dropped (§5.3).");

            var drawIdx = Enumerable.Range(0, drawerBatch!.Count)
                .Where(i => drawerBatch[i].Kind == VfxEvent.onDraw).ToArray();
            Assert.That(drawIdx, Is.Not.Empty, "Turn start must draw.");
            foreach (int i in drawIdx)
            {
                // onDraw carries the drawn card in Source (CommandProcessor buf.Emit site).
                Assert.That(drawerBatch[i].Source!.Value.Value, Is.Not.Zero,
                    "The drawer must see their own drawn card id.");
                Assert.That(opponentBatch![i].Source!.Value.Value, Is.Zero,
                    "The opponent must get the sentinel for a hidden draw.");
            }

            // The submitter's SubmitAsync result must carry THEIR projected batch — identical
            // to what their OnEvents delivered — not the raw authoritative one.
            Assert.That(result.Events.Count, Is.EqualTo(opponentBatch!.Count));
            for (int i = 0; i < result.Events.Count; i++)
            {
                Assert.That(result.Events[i].Sequence, Is.EqualTo(opponentBatch[i].Sequence));
                Assert.That(Equals(result.Events[i].Source, opponentBatch[i].Source),
                    "SubmitAsync result events must be the submitter's projection.");
            }
        }
    }
}
