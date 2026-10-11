using System;
using System.Linq;
using Kingdom.Core.Cards;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Core.Projection;
using Kingdom.Core.Serialization;
using Kingdom.Core.Tests.Support;
using Kingdom.Editor;
using Kingdom.Net.Protocol;
using NUnit.Framework;

namespace Kingdom.Core.Tests.CardBehavior
{
    [TestFixture]
    public sealed class QueenHeraldTests
    {
        private const int Herald = 1194, Queen = 1195;
        private const int Body = 990401, DrawBody = 990402, Bolt = 940, Destroy = 941,
            Silence = 942, Volley = 943, Summon = 944, UnearthBody = 990403;
        private ICardDatabase _db = null!;
        private CommandProcessor _processor = null!;
        private GameState _state = null!;

        [SetUp]
        public void SetUp()
        {
            var extra = new[]
            {
                Minion(Body, 2, 4),
                Minion(DrawBody, 1, 2, new DrawAction(1)),
                Minion(UnearthBody, 1, 2, new UnearthAction()),
                Spell(Bolt, new DamageAction(4, Target("ANY_CHARACTER", 1))),
                Spell(Destroy, new DestroyAction(Target("ANY_MINION", 1))),
                Spell(Silence, new SilenceAction(Target("ANY_MINION", 1))),
                Spell(Volley, new DamageAction(5, Target("ANY_MINION", 14))),
                Spell(Summon, new SummonAction(Body, 1)),
            };
            _db = new CardNameIndex(ShippedCards.Cards.Concat(extra));
            _processor = new CommandProcessor(_db);
            var p0 = new PlayerId("queen0");
            var p1 = new PlayerId("queen1");
            _state = new GameState(new MatchId("queen-herald"), new DeterministicRandom(47),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.GRAVEKEEP, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.CLOCKWRIGHT, 30)))
            {
                ActivePlayer = p0,
                Phase = TurnPhase.Main,
                TurnNumber = 3,
                NextInstanceCounter = 3,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            foreach (PlayerState player in _state.Players)
            {
                player.Mana.Max = player.Mana.Available = 10;
                for (int i = 0; i < 8; i++)
                    player.Deck.Add(new CardInstance(_state.NextInstanceId(), Body, player.Id) { Zone = Zone.Deck });
            }
        }

        [TestCase(Herald, 3, 1, 3)]
        [TestCase(Queen, 4, 2, 4)]
        public void Selected_Stats_Are_Unpublished_And_Editable(int id, int cost, int attack, int health)
        {
            CardDefinition card = _db.Get(id);
            Assert.That((card.Cost, card.Attack, card.Health), Is.EqualTo((cost, (int?)attack, (int?)health)));
            Assert.That(card.Class, Is.EqualTo(CardClass.NEUTRAL));
            Assert.That(card.Lifecycle, Is.EqualTo(CardLifecycle.DRAFT));
            Assert.That(card.PoolEligible, Is.False);
            Assert.That(_db.AllCollectible().Select(c => c.Id), Does.Not.Contain(id));
            var deck = new DeckList(CardClass.GRAVEKEEP, CardClass.CLOCKWRIGHT) { Id = "test", Name = "test" };
            deck.Entries.Add(new DeckEntry(id, 1));
            Assert.That(DeckValidator.Validate(deck, _db, AllCardsOwnedCounts.Instance).Violations.Select(v => v.Rule),
                Does.Contain(DeckRule.L7_NotPublished));
            var editable = EditableCard.FromDefinition(card);
            Assert.That(CardTextGenerator.Generate(editable.ToDefinition(), _db), Is.EqualTo(card.Text));
            CardDefinition reloaded = CardJson.DeserializeArray("[" + CardJsonRowIO.Serialize(editable.ToDefinition()) + "]").Single();
            Assert.That(CardTextGenerator.Generate(reloaded, _db), Is.EqualTo(card.Text));
            Assert.That(reloaded.Lifecycle, Is.EqualTo(CardLifecycle.DRAFT));
            Assert.That(CardTextParser.TryParse(card, new CardNameIndex(ShippedCards.Cards), out var rules, out var reason),
                Is.True, reason);
            Assert.That(rules, Has.Count.EqualTo(1));
        }

        [Test]
        public void Queen_Spares_One_Hit_And_Refreshes_On_The_Opponents_Turn()
        {
            var queen = Play(Queen);
            var first = OnBoard(Body);
            var second = OnBoard(Body);
            Play(Bolt, first.Id);
            Assert.That(first.CurrentHealth, Is.EqualTo(1));
            Assert.That(EffectList.Has(queen.Effects, EffectKind.LethalDamagePreventionSpent), Is.True);
            Play(Bolt, second.Id);
            Assert.That(second.Zone, Is.EqualTo(Zone.Graveyard));
            Execute(new EndTurnIntent(_state.ActivePlayer));
            Play(Bolt, first.Id);
            Assert.That(first.Zone, Is.EqualTo(Zone.Board));
            Assert.That(first.CurrentHealth, Is.EqualTo(1));
            Play(Bolt, first.Id);
            Assert.That(first.Zone, Is.EqualTo(Zone.Graveyard));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Queen_Does_Not_Protect_Herself_Or_The_Hero(bool hitHero)
        {
            var queen = Play(Queen);
            InstanceId target = hitHero ? _state.Players[0].Hero.Id : queen.Id;
            Play(Bolt, target);
            Assert.That(EffectList.Has(queen.Effects, EffectKind.LethalDamagePreventionSpent), Is.False);
            if (hitHero) Assert.That(_state.Players[0].Hero.CurrentHealth, Is.EqualTo(26));
            else Assert.That(queen.Zone, Is.EqualTo(Zone.Graveyard));
        }

        [Test]
        public void Silence_And_Direct_Destruction_Bypass_Protection()
        {
            var queen = Play(Queen);
            var subject = OnBoard(Body);
            Play(Destroy, subject.Id);
            Assert.That(subject.Zone, Is.EqualTo(Zone.Graveyard));
            Assert.That(EffectList.Has(queen.Effects, EffectKind.LethalDamagePreventionSpent), Is.False);
            var second = OnBoard(Body);
            Play(Silence, queen.Id);
            Play(Bolt, second.Id);
            Assert.That(second.Zone, Is.EqualTo(Zone.Graveyard));
        }

        [Test]
        public void Aegis_Does_Not_Spend_The_Pardon_And_Reduced_Damage_Is_Reported()
        {
            var queen = Play(Queen);
            var subject = OnBoard(Body);
            subject.HasAegisShield = true;
            Play(Bolt, subject.Id);
            Assert.That(subject.CurrentHealth, Is.EqualTo(4));
            Assert.That(EffectList.Has(queen.Effects, EffectKind.LethalDamagePreventionSpent), Is.False);
            var result = PlayResult(Bolt, subject.Id);
            Assert.That(result.Events.Single(e => e.Kind == GameEventKind.DamageApplied).Amount, Is.EqualTo(3));
            Assert.That(subject.CurrentHealth, Is.EqualTo(1));
        }

        [Test]
        public void Simultaneous_Volley_Uses_Queen_Until_The_Death_Checkpoint()
        {
            var queen = Play(Queen);
            var first = OnBoard(Body);
            var second = OnBoard(Body);
            Play(Volley);
            Assert.That(queen.Zone, Is.EqualTo(Zone.Graveyard));
            Assert.That(first.Zone, Is.EqualTo(Zone.Board));
            Assert.That(first.CurrentHealth, Is.EqualTo(1));
            Assert.That(second.Zone, Is.EqualTo(Zone.Graveyard));
        }

        [Test]
        public void Each_Queen_Has_One_Allowance()
        {
            OnBoard(Queen);
            OnBoard(Queen);
            var first = OnBoard(Body);
            var second = OnBoard(Body);
            Play(Bolt, first.Id);
            Play(Bolt, second.Id);
            Assert.That((first.CurrentHealth, second.CurrentHealth), Is.EqualTo((1, 1)));
            Play(Bolt, second.Id);
            Assert.That(second.Zone, Is.EqualTo(Zone.Graveyard));
        }

        [Test]
        public void Venom_After_Nonzero_Damage_Bypasses_The_Queens_Protection()
        {
            var queen = OnBoard(Queen);
            var subject = OnBoard(Body);
            var attacker = OnBoard(Body, 1);
            attacker.Keywords.Add(Keyword.VENOM);
            var interpreter = new EffectInterpreter(_state, _db, new EventBuffer(_state));
            DamagePipeline.ApplyDamage(interpreter, CharacterRef.OfMinion(0, subject), 1,
                attacker.Id, attacker.Keywords);
            interpreter.Drain();
            Assert.That(subject.Zone, Is.EqualTo(Zone.Graveyard));
            Assert.That(EffectList.Has(queen.Effects, EffectKind.LethalDamagePreventionSpent), Is.False);
        }

        [Test]
        public void Herald_Repeats_Only_The_Next_Play_Even_After_He_Dies()
        {
            var herald = Play(Herald);
            Play(Destroy, herald.Id);
            Play(Summon);
            Assert.That(EffectList.Has(_state.Players[0].Hero.Effects, EffectKind.RepeatNextMinionPlay), Is.True);
            Play(DrawBody);
            Assert.That(_state.Players[0].Hand, Has.Count.EqualTo(2));
            Assert.That(_state.Players[0].Board.Count(c => c.DefinitionId == DrawBody), Is.EqualTo(1));
            Play(DrawBody);
            Assert.That(_state.Players[0].Hand, Has.Count.EqualTo(3));
        }

        [Test]
        public void Vanilla_Minions_Consume_The_Herald_And_The_Promise_Expires()
        {
            Play(Herald);
            Play(Body);
            Play(DrawBody);
            Assert.That(_state.Players[0].Hand, Has.Count.EqualTo(1));
            Play(Herald);
            Execute(new EndTurnIntent(_state.ActivePlayer));
            Assert.That(EffectList.Has(_state.Players[0].Hero.Effects, EffectKind.RepeatNextMinionPlay), Is.False);
        }

        [Test]
        public void Repeating_A_Herald_Arms_One_Double_Play_Without_Multiplying_It()
        {
            Play(Herald);
            Play(Herald);
            Play(DrawBody);
            Assert.That(_state.Players[0].Hand, Has.Count.EqualTo(2));
        }

        [TestCase(2)]
        [TestCase(4)]
        public void Repeated_Targeted_Effects_Keep_Their_Target_And_Fizzle_If_It_Dies(int health)
        {
            int boltMinion = ShippedCards.Id("clock_clockwork_sniper");
            Play(Herald);
            var target = OnBoard(Body, 1);
            target.CurrentHealth = health;
            var bystander = OnBoard(Body, 1);
            Play(boltMinion, target.Id);
            Assert.That(target.Zone, Is.EqualTo(Zone.Graveyard));
            Assert.That(bystander.CurrentHealth, Is.EqualTo(4));
        }

        [Test]
        public void Doubled_Unearth_Offers_Two_Choices_And_Survives_A_Snapshot()
        {
            Play(Herald);
            Play(UnearthBody);
            Assert.That(_state.PendingChoice!.RemainingOffers, Is.EqualTo(2));
            Assert.That(GameStateProjector.Project(_state, _state.Players[1].Id).PendingChoice!.RemainingOffers, Is.Zero);
            _state = RoundTrip(_state);
            Choose();
            Assert.That(_state.PendingChoice!.RemainingOffers, Is.EqualTo(1));
            Choose();
            Assert.That(_state.PendingChoice, Is.Null);
            Assert.That(_state.Players[0].Hand, Has.Count.EqualTo(2));
        }

        [Test]
        public void Doubled_Unearth_Stops_At_The_Hand_Limit()
        {
            Play(Herald);
            for (int i = 0; i < 9; i++) InHand(Body);
            Play(UnearthBody);
            Choose();
            Assert.That(_state.Players[0].Hand, Has.Count.EqualTo(10));
            Assert.That(_state.PendingChoice, Is.Null);
        }

        [Test]
        public void Both_Turn_Markers_Survive_The_Wire_And_Change_The_Hash()
        {
            var queen = OnBoard(Queen);
            var subject = OnBoard(Body);
            Play(Bolt, subject.Id);
            Play(Herald);
            string before = GameStateHasher.Hash(_state);
            _state = RoundTrip(_state);
            Assert.That(GameStateHasher.Hash(_state), Is.EqualTo(before));
            Assert.That(EffectList.Has(_state.Players[0].Board.Single(c => c.Id == queen.Id).Effects,
                EffectKind.LethalDamagePreventionSpent), Is.True);
            Play(Bolt, subject.Id);
            Assert.That(_state.Players[0].Board.Any(c => c.Id == subject.Id), Is.False);
            Play(DrawBody);
            Assert.That(_state.Players[0].Hand, Has.Count.EqualTo(2));
        }

        private static TargetSpec Target(string selector, int count) => new TargetSpec(selector, TargetSelection.Of(count));
        private static CardDefinition Minion(int id, int attack, int health, RuleAction? action = null)
            => new CardDefinition(id, "test_" + id, "Test " + id, CardType.MINION, 1, attack, health, null,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(),
                action == null ? Array.Empty<RuleDefinition>() : new[] { TestRules.Rule("rule_1", Trigger.ON_PLAY, action) },
                "", "", SetId.REKINDLING, true, null, "");
        private static CardDefinition Spell(int id, RuleAction action)
            => new CardDefinition(id, "test_" + id, "Test " + id, CardType.SPELL, 0, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON, Array.Empty<Keyword>(),
                new[] { TestRules.Rule("rule_1", Trigger.ON_CAST, action) }, "", "", SetId.REKINDLING, false, null, "");
        private CardInstance InHand(int id)
        {
            PlayerState owner = _state.PlayerOf(_state.ActivePlayer);
            var card = new CardInstance(_state.NextInstanceId(), id, owner.Id) { Zone = Zone.Hand };
            owner.Hand.Add(card);
            return card;
        }
        private CardInstance OnBoard(int id, int seat = 0)
        {
            PlayerState owner = _state.Players[seat];
            CardDefinition definition = _db.Get(id);
            var card = new CardInstance(_state.NextInstanceId(), id, owner.Id)
            {
                Zone = Zone.Board,
                Position = owner.Board.Count,
                CurrentAttack = definition.Attack!.Value,
                CurrentHealth = definition.Health!.Value,
                MaxHealth = definition.Health.Value,
            };
            owner.Board.Add(card);
            return card;
        }
        private CardInstance Play(int id, params InstanceId[] targets)
        {
            var card = InHand(id);
            Execute(new PlayCardIntent(_state.ActivePlayer, card.Id, _state.PlayerOf(_state.ActivePlayer).Board.Count,
                targets.Select(t => new TargetRef(t)).ToArray()));
            return card;
        }
        private CommandResult PlayResult(int id, params InstanceId[] targets)
        {
            var card = InHand(id);
            return Execute(new PlayCardIntent(_state.ActivePlayer, card.Id, _state.PlayerOf(_state.ActivePlayer).Board.Count,
                targets.Select(t => new TargetRef(t)).ToArray()));
        }
        private CommandResult Execute(PlayerIntent intent)
        {
            CommandResult result = _processor.Execute(_state, intent);
            Assert.That(result.Accepted, Is.True, result.Reason?.ToString());
            return result;
        }
        private void Choose()
        {
            var pending = _state.PendingChoice!;
            Execute(new UnearthChoiceIntent(pending.Chooser, pending.Options[0], pending.ChoiceSequence));
        }
        private static GameState RoundTrip(GameState state)
        {
            var decoded = ProtocolCodec.Decode(ProtocolCodec.Encode(SnapshotMsg.Of(state), state.Id, 1));
            Assert.That(decoded.Ok, Is.True, decoded.Detail);
            return ((SnapshotMsg)decoded.Message!).State;
        }
    }
}
