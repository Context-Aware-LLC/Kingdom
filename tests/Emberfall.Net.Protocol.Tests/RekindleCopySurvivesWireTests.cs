using System;
using NUnit.Framework;
using Emberfall.Core;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;
using Emberfall.Core.Projection;
using Emberfall.Core.Tests.Support;
using Emberfall.Net.Protocol;

namespace Emberfall.Net.Protocol.Tests
{
    /// <summary>
    /// Bisect for the live "Throneless King REKINDLE copy vanishes on the client" bug
    /// (project memory <c>live-test-and-rekindle-2026-07-20</c>). The engine is proven to PRODUCE the
    /// 1-HP copy on a combat death (<c>CombatTests.REKINDLE_Triggers_On_Combat_Death</c>). This walks
    /// that copy through the two server-side wire layers the client depends on — per-viewer
    /// <see cref="GameStateProjector"/> redaction, then the <see cref="SnapshotJsonCodec"/> JSON
    /// round-trip via <see cref="ProtocolCodec"/> — and asserts it arrives intact for BOTH the owner
    /// and the opponent. Green here narrows the drop to the Unity render layer; red would name the
    /// culprit outright.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class RekindleCopySurvivesWireTests
    {
        private static CardDefinition King()
            => new CardDefinition(FixtureCard.Id("neutral_throneless_king"), "neutral_throneless_king", "Throneless King", CardType.MINION, 6, 6, 6, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                new[] { Keyword.BULWARK, Keyword.REKINDLE }, Array.Empty<RuleDefinition>(),
                "", "art/neutral_throneless_king", SetId.CORE, true, null, "");

        private static CardDefinition Bruiser()
            => new CardDefinition(FixtureCard.Id("bruiser"), "bruiser", "Bruiser", CardType.MINION, 6, 6, 8, null,
                CardClass.NEUTRAL, Rarity.COMMON,
                Array.Empty<Keyword>(), Array.Empty<RuleDefinition>(),
                "", "art/bruiser", SetId.CORE, true, null, "");

        private static CardInstance PutOnBoard(GameState s, int seat, CardDefinition def)
        {
            var ps = s.Players[seat];
            var ci = new CardInstance(s.NextInstanceId(), def.Id, ps.Id)
            {
                Zone = Zone.Board,
                CurrentAttack = def.Attack ?? 0,
                CurrentHealth = def.Health ?? 1,
                MaxHealth = def.Health ?? 1,
                Position = ps.Board.Count,
            };
            foreach (var k in def.Keywords) ci.Keywords.Add(k);
            ps.Board.Add(ci);
            return ci;
        }

        [Test]
        public void Rekindle_Copy_From_A_Combat_Death_Survives_Projection_And_The_Codec()
        {
            var db = new FakeCardDatabase().Register(King()).Register(Bruiser());
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var s = new GameState(new MatchId("M"),
                new DeterministicRandom(42UL),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.EMBERWARD, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.TIDECALLER, 30)))
            {
                NextInstanceCounter = 3,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            s.ActivePlayer = p0;
            s.Players[0].Mana.Max = s.Players[0].Mana.Available = 10;
            s.Players[1].Mana.Max = s.Players[1].Mana.Available = 10;

            var attacker = PutOnBoard(s, 0, Bruiser());   // 6/8 — survives retaliation
            var king = PutOnBoard(s, 1, King());          // 6/6 BULWARK+REKINDLE — dies, rekindles

            var r = new CommandProcessor(db).Execute(s,
                new AttackIntent(p0, attacker.Id, new TargetRef(king.Id)));
            Assert.That(r.Accepted, Is.True, "attack resolved");

            // Server truth: the original king is gone; a fresh-id 1-HP copy stands in its slot.
            Assert.That(s.Players[1].Board, Has.Count.EqualTo(1), "copy is on the authoritative board");
            var authCopy = s.Players[1].Board[0];
            Assert.That(authCopy.Id.Value, Is.Not.EqualTo(king.Id.Value), "copy has a fresh instance id");
            Assert.That(authCopy.CurrentHealth, Is.EqualTo(1));
            Assert.That(authCopy.DefinitionId, Is.EqualTo(FixtureCard.Id("neutral_throneless_king")));

            // Both viewers must receive the copy: seat 1 owns it, seat 0 is the opponent watching it.
            foreach (var viewer in new[] { p0, p1 })
            {
                var projected = GameStateProjector.Project(s, viewer);

                var bytes = ProtocolCodec.Encode(SnapshotMsg.Of(projected), projected.Id, seq: 1);
                var decoded = ProtocolCodec.Decode(bytes);
                Assert.That(decoded.Ok, Is.True, $"[{viewer}] decode failed: {decoded.Error} {decoded.Detail}");
                var wireState = ((SnapshotMsg)decoded.Message!).State;

                var board = wireState.Players[1].Board;
                Assert.That(board, Has.Count.EqualTo(1),
                    $"REKINDLE copy missing from seat-1 board as seen by {viewer} — dropped in projection or codec");
                var copy = board[0];
                Assert.That(copy.Id.Value, Is.EqualTo(authCopy.Id.Value), $"[{viewer}] copy kept its instance id over the wire");
                Assert.That(copy.DefinitionId, Is.EqualTo(FixtureCard.Id("neutral_throneless_king")), $"[{viewer}] copy is still a King");
                Assert.That(copy.CurrentHealth, Is.EqualTo(1), $"[{viewer}] copy is at 1 HP");
                Assert.That(copy.Keywords, Does.Contain(Keyword.BULWARK), $"[{viewer}] copy kept BULWARK");
                Assert.That(copy.HasUsedRekindle, Is.True, $"[{viewer}] copy is marked rekindle-consumed");
            }
        }
    }
}
