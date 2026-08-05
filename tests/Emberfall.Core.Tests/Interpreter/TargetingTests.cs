using System.Collections.Generic;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;
using Verb = Emberfall.Core.Contracts.Action;

namespace Emberfall.Core.Tests.Interpreter
{
    [TestFixture]
    [Category("Unit")]
    public sealed class TargetingTests
    {
        private static GameState State(ulong seed = 42)
        {
            var p0 = new PlayerId("P0");
            var p1 = new PlayerId("P1");
            var s = new GameState(new MatchId("targeting"), new DeterministicRandom(seed),
                new PlayerState(p0, new HeroInstance(new InstanceId(1), p0, CardClass.NIGHTVEIL, 30)),
                new PlayerState(p1, new HeroInstance(new InstanceId(2), p1, CardClass.TEMPEST, 30)))
            {
                NextInstanceCounter = 3,
                ActivePlayer = p0,
                Phase = TurnPhase.Main,
                TurnNumber = 1,
                Mulligan0Done = true,
                Mulligan1Done = true,
            };
            s.Players[0].Mana.Max = s.Players[0].Mana.Available = 10;
            s.Players[1].Mana.Max = s.Players[1].Mana.Available = 10;
            return s;
        }

        private static CardDefinition Minion(string id, Tribe? tribe = null)
            => new CardDefinition(id, id, CardType.MINION, 1, 1, 3, null,
                CardClass.NEUTRAL, Rarity.COMMON, System.Array.Empty<Keyword>(),
                System.Array.Empty<EffectDef>(), "", "", "art/" + id, SetId.CORE, true,
                tribe, "", false);

        private static CardDefinition Spell(string id, params EffectDef[] effects)
            => new CardDefinition(id, id, CardType.SPELL, 1, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON, System.Array.Empty<Keyword>(), effects,
                "", "", "art/" + id, SetId.CORE, true, null, "", false);

        private static CardDefinition Weapon(string id, int attack = 2, int durability = 2,
            params Keyword[] keywords)
            => new CardDefinition(id, id, CardType.WEAPON, 1, attack, null, durability,
                CardClass.NEUTRAL, Rarity.COMMON, keywords, System.Array.Empty<EffectDef>(),
                "", "", "art/" + id, SetId.CORE, true, null, "", false);

        private static CardInstance Board(GameState state, int seat, CardDefinition def)
        {
            var card = new CardInstance(state.NextInstanceId(), def.Id, state.Players[seat].Id)
            {
                Zone = Zone.Board,
                CurrentAttack = def.Attack ?? 0,
                CurrentHealth = def.Health ?? 1,
                MaxHealth = def.Health ?? 1,
                Position = state.Players[seat].Board.Count,
            };
            state.Players[seat].Board.Add(card);
            return card;
        }

        private static CardInstance ZoneCard(GameState state, int seat, CardDefinition def, Zone zone)
        {
            var card = new CardInstance(state.NextInstanceId(), def.Id, state.Players[seat].Id) { Zone = zone };
            if (zone == Zone.Hand) state.Players[seat].Hand.Add(card);
            else if (zone == Zone.Deck) state.Players[seat].Deck.Add(card);
            else state.Players[seat].Graveyard.Add(card);
            return card;
        }

        [Test]
        public void Candidate_Union_Covers_Hand_Deck_Graveyard_And_Weapon()
        {
            var db = new FakeCardDatabase().Register(Minion("construct", Tribe.CONSTRUCT));
            var state = State();
            CardDefinition def = db.Get("construct");
            CardInstance hand = ZoneCard(state, 0, def, Zone.Hand);
            CardInstance deck = ZoneCard(state, 0, def, Zone.Deck);
            CardInstance grave = ZoneCard(state, 0, def, Zone.Graveyard);
            state.Players[0].Weapon = new CardInstance(
                state.NextInstanceId(), def.Id, state.Players[0].Id)
            {
                Zone = Zone.Weapon, CurrentAttack = 1, CurrentDurability = 1,
            };

            TargetCandidate handCandidate = Targeting.Candidates(state, db, state.Players[0].Id,
                new TargetSpec("FRIENDLY_HAND", filter: new TargetFilterDef(tribe: Tribe.CONSTRUCT))).Single();
            Assert.That((handCandidate.Instance, handCandidate.Kind),
                Is.EqualTo((hand.Id, TargetCandidateKind.HandCard)));
            Assert.That(Targeting.ResolutionCandidates(state, db, state.Players[0].Id,
                new TargetSpec("FRIENDLY_DECK")).Single().Instance, Is.EqualTo(deck.Id));
            Assert.That(Targeting.Candidates(state, db, state.Players[0].Id,
                new TargetSpec("FRIENDLY_GRAVEYARD")).Single().Instance, Is.EqualTo(grave.Id));
            Assert.That(Targeting.Candidates(state, db, state.Players[0].Id,
                new TargetSpec("FRIENDLY_WEAPON")).Single().Kind, Is.EqualTo(TargetCandidateKind.Weapon));
        }

        [Test]
        public void Presentation_Seam_Never_Exposes_Hidden_Opponent_Or_Deck_Identities()
        {
            var db = new FakeCardDatabase().Register(Minion("body"));
            var state = State();
            ZoneCard(state, 1, db.Get("body"), Zone.Hand);
            ZoneCard(state, 0, db.Get("body"), Zone.Deck);

            Assert.That(Targeting.Candidates(state, db, state.Players[0].Id,
                new TargetSpec("ENEMY_HAND")), Is.Empty);
            Assert.That(Targeting.Candidates(state, db, state.Players[0].Id,
                new TargetSpec("FRIENDLY_DECK")), Is.Empty);
            Assert.That(Targeting.ResolutionCandidates(state, db, state.Players[0].Id,
                new TargetSpec("FRIENDLY_DECK")), Has.Count.EqualTo(1),
                "Only the authoritative internal resolution path may inspect an ordered deck.");
        }

        [Test]
        public void Adjacent_And_TriggerSource_Use_Explicit_Context()
        {
            var db = new FakeCardDatabase().Register(Minion("body"));
            var state = State();
            CardInstance left = Board(state, 0, db.Get("body"));
            CardInstance source = Board(state, 0, db.Get("body"));
            CardInstance right = Board(state, 0, db.Get("body"));
            CardInstance trigger = ZoneCard(state, 0, db.Get("body"), Zone.Graveyard);

            Assert.That(Targeting.Candidates(state, db, state.Players[0].Id,
                    new TargetSpec("ADJACENT"), source: source.Id).Select(c => c.Instance),
                Is.EqualTo(new[] { left.Id, right.Id }));
            TargetCandidate triggerCandidate = Targeting.Candidates(state, db, state.Players[0].Id,
                new TargetSpec("TRIGGER_SOURCE"), triggerSource: trigger.Id).Single();
            Assert.That((triggerCandidate.Instance, triggerCandidate.Kind),
                Is.EqualTo((trigger.Id, TargetCandidateKind.GraveyardCard)));
        }

        [Test]
        public void Damaged_And_Tribe_Filters_Are_Enforced_By_The_Shared_Seam()
        {
            var construct = Minion("construct", Tribe.CONSTRUCT);
            var beast = Minion("beast", Tribe.BEAST);
            var db = new FakeCardDatabase().Register(construct).Register(beast);
            var state = State();
            CardInstance damagedConstruct = Board(state, 0, construct);
            damagedConstruct.CurrentHealth = 2;
            Board(state, 0, construct);
            Board(state, 0, beast).CurrentHealth = 2;

            IReadOnlyList<TargetCandidate> candidates = Targeting.Candidates(state, db,
                state.Players[0].Id, new TargetSpec("FRIENDLY_MINIONS",
                    filter: new TargetFilterDef(damaged: true, tribe: Tribe.CONSTRUCT)));

            Assert.That(candidates.Select(c => c.Instance), Is.EqualTo(new[] { damagedConstruct.Id }));
        }

        [Test]
        public void Named_Bindings_Allow_Two_Independent_Player_Targets()
        {
            var first = new EffectDef(Trigger.ON_CAST, Verb.DEAL_DAMAGE, 1, null, null, null, null,
                target: new TargetSpec("ANY_MINION", TargetChosenBy.PLAYER, binding: "first"));
            var second = new EffectDef(Trigger.ON_CAST, Verb.DEAL_DAMAGE, 2, null, null, null, null,
                target: new TargetSpec("ANY_MINION", TargetChosenBy.PLAYER,
                    binding: "second", distinctFrom: "first"));
            var spell = Spell("split_bolt", first, second);
            var body = Minion("body");
            var db = new FakeCardDatabase().Register(spell).Register(body);
            var state = State();
            CardInstance a = Board(state, 1, body);
            CardInstance b = Board(state, 1, body);
            CardInstance card = ZoneCard(state, 0, spell, Zone.Hand);

            var choices = new[] { new TargetRef(a.Id, "first"), new TargetRef(b.Id, "second") };
            var accepted = new CommandProcessor(db).Execute(state,
                new PlayCardIntent(state.Players[0].Id, card.Id, 0, choices));

            Assert.That(accepted.Accepted, Is.True);
            Assert.That(a.CurrentHealth, Is.EqualTo(2));
            Assert.That(b.CurrentHealth, Is.EqualTo(1));
        }

        [Test]
        public void Binding_Distinctness_Rejects_The_Same_Target_Without_Mutation()
        {
            var first = new EffectDef(Trigger.ON_CAST, Verb.DEAL_DAMAGE, 1, null, null, null, null,
                target: new TargetSpec("ANY_MINION", TargetChosenBy.PLAYER, binding: "first"));
            var second = new EffectDef(Trigger.ON_CAST, Verb.DEAL_DAMAGE, 1, null, null, null, null,
                target: new TargetSpec("ANY_MINION", TargetChosenBy.PLAYER,
                    binding: "second", distinctFrom: "first"));
            var spell = Spell("split_bolt", first, second);
            var body = Minion("body");
            var db = new FakeCardDatabase().Register(spell).Register(body);
            var state = State();
            CardInstance target = Board(state, 1, body);
            CardInstance card = ZoneCard(state, 0, spell, Zone.Hand);

            var rejected = new CommandProcessor(db).Execute(state, new PlayCardIntent(
                state.Players[0].Id, card.Id, 0,
                new[] { new TargetRef(target.Id, "first"), new TargetRef(target.Id, "second") }));

            Assert.That(rejected.Accepted, Is.False);
            Assert.That(rejected.Reason, Is.EqualTo(RejectReason.IllegalTarget));
            Assert.That(rejected.Events, Is.Empty);
            Assert.That(state.Players[0].Hand, Does.Contain(card));
            Assert.That(target.CurrentHealth, Is.EqualTo(3));
        }

        [Test]
        public void Zone_Targets_Reach_Handlers_Instead_Of_Being_Dropped_As_NonCharacters()
        {
            var body = Minion("body");
            var blade = Weapon("blade");
            var recover = Spell("recover", new EffectDef(Trigger.ON_CAST, Verb.RETURN_TO_HAND,
                null, null, null, null, null,
                target: new TargetSpec("FRIENDLY_GRAVEYARD", TargetChosenBy.PLAYER)));
            var tutor = Spell("tutor", new EffectDef(Trigger.ON_CAST, Verb.RETURN_TO_HAND,
                null, null, null, null, null,
                target: new TargetSpec("FRIENDLY_DECK", TargetChosenBy.RANDOM)));
            var disarm = Spell("disarm", new EffectDef(Trigger.ON_CAST, Verb.RETURN_TO_HAND,
                null, null, null, null, null, target: new TargetSpec("FRIENDLY_WEAPON")));
            var db = new FakeCardDatabase().Register(body).Register(blade)
                .Register(recover).Register(tutor).Register(disarm);

            var graveState = State();
            CardInstance grave = ZoneCard(graveState, 0, body, Zone.Graveyard);
            CardInstance recoverCard = ZoneCard(graveState, 0, recover, Zone.Hand);
            var recovered = new CommandProcessor(db).Execute(graveState, new PlayCardIntent(
                graveState.Players[0].Id, recoverCard.Id, 0, new[] { new TargetRef(grave.Id) }));
            Assert.That(recovered.Accepted, Is.True);
            Assert.That(graveState.Players[0].Hand.Single().DefinitionId, Is.EqualTo(body.Id));

            var deckState = State();
            ZoneCard(deckState, 0, body, Zone.Deck);
            CardInstance tutorCard = ZoneCard(deckState, 0, tutor, Zone.Hand);
            var tutored = new CommandProcessor(db).Execute(deckState, new PlayCardIntent(
                deckState.Players[0].Id, tutorCard.Id, 0, System.Array.Empty<TargetRef>()));
            Assert.That(tutored.Accepted, Is.True);
            Assert.That(deckState.Players[0].Deck, Is.Empty);
            Assert.That(deckState.Players[0].Hand.Single().DefinitionId, Is.EqualTo(body.Id));

            var weaponState = State();
            var equipped = new CardInstance(
                weaponState.NextInstanceId(), blade.Id, weaponState.Players[0].Id)
            {
                Zone = Zone.Weapon, CurrentAttack = 2, CurrentDurability = 2,
            };
            InstanceId publicWeaponId = equipped.Id;
            weaponState.Players[0].Weapon = equipped;
            CardInstance disarmCard = ZoneCard(weaponState, 0, disarm, Zone.Hand);
            var disarmed = new CommandProcessor(db).Execute(weaponState, new PlayCardIntent(
                weaponState.Players[0].Id, disarmCard.Id, 0, System.Array.Empty<TargetRef>()));
            Assert.That(disarmed.Accepted, Is.True);
            Assert.That(weaponState.Players[0].Weapon, Is.Null);
            Assert.That(weaponState.Players[0].Hand.Single(), Is.SameAs(equipped));
            Assert.That(equipped.DefinitionId, Is.EqualTo(blade.Id));
            Assert.That(equipped.Id, Is.Not.EqualTo(publicWeaponId),
                "A public weapon receives a fresh hidden-zone identity when bounced.");
        }

        [Test]
        public void Weapon_Targets_Reach_Destroy_Silence_And_Transform_Handlers()
        {
            CardDefinition blade = Weapon("blade", keywords: Keyword.LIFESTEAL);
            CardDefinition replacement = Weapon("replacement", attack: 4, durability: 3);
            var silence = Spell("silence_weapon", new EffectDef(Trigger.ON_CAST, Verb.SILENCE,
                null, null, null, null, null, target: new TargetSpec("FRIENDLY_WEAPON")));
            var destroy = Spell("destroy_weapon", new EffectDef(Trigger.ON_CAST, Verb.DESTROY,
                null, null, null, null, null, target: new TargetSpec("FRIENDLY_WEAPON")));
            var transform = Spell("transform_weapon", new EffectDef(Trigger.ON_CAST, Verb.TRANSFORM,
                null, null, replacement.Id, null, null, target: new TargetSpec("FRIENDLY_WEAPON")));
            var db = new FakeCardDatabase().Register(blade).Register(replacement)
                .Register(silence).Register(destroy).Register(transform);

            GameState silenceState = State();
            var enchanted = new CardInstance(silenceState.NextInstanceId(), blade.Id,
                silenceState.Players[0].Id)
            {
                Zone = Zone.Weapon, CurrentAttack = 5, CurrentDurability = 1,
            };
            enchanted.Keywords.Add(Keyword.LIFESTEAL);
            enchanted.Keywords.Add(Keyword.POISONOUS);
            enchanted.Enchantments.Add(new Enchantment
                { Source = new InstanceId(99), AttackDelta = 3, GrantedKeyword = Keyword.POISONOUS });
            silenceState.Players[0].Weapon = enchanted;
            CardInstance silenceCard = ZoneCard(silenceState, 0, silence, Zone.Hand);

            Assert.That(new CommandProcessor(db).Execute(silenceState,
                new PlayCardIntent(silenceState.Players[0].Id, silenceCard.Id, 0,
                    System.Array.Empty<TargetRef>())).Accepted, Is.True);
            Assert.That(silenceState.Players[0].Weapon, Is.SameAs(enchanted));
            Assert.That((enchanted.CurrentAttack, enchanted.CurrentDurability), Is.EqualTo((2, 1)));
            Assert.That(enchanted.Enchantments, Is.Empty);
            Assert.That(enchanted.Keywords, Is.EquivalentTo(new[] { Keyword.LIFESTEAL }));

            GameState destroyState = State();
            var doomed = new CardInstance(destroyState.NextInstanceId(), blade.Id,
                destroyState.Players[0].Id) { Zone = Zone.Weapon, CurrentAttack = 2, CurrentDurability = 2 };
            destroyState.Players[0].Weapon = doomed;
            CardInstance destroyCard = ZoneCard(destroyState, 0, destroy, Zone.Hand);

            Assert.That(new CommandProcessor(db).Execute(destroyState,
                new PlayCardIntent(destroyState.Players[0].Id, destroyCard.Id, 0,
                    System.Array.Empty<TargetRef>())).Accepted, Is.True);
            Assert.That(destroyState.Players[0].Weapon, Is.Null);
            Assert.That(destroyState.Players[0].Graveyard, Does.Contain(doomed));

            GameState transformState = State();
            var original = new CardInstance(transformState.NextInstanceId(), blade.Id,
                transformState.Players[0].Id) { Zone = Zone.Weapon, CurrentAttack = 2, CurrentDurability = 2 };
            transformState.Players[0].Weapon = original;
            CardInstance transformCard = ZoneCard(transformState, 0, transform, Zone.Hand);

            Assert.That(new CommandProcessor(db).Execute(transformState,
                new PlayCardIntent(transformState.Players[0].Id, transformCard.Id, 0,
                    System.Array.Empty<TargetRef>())).Accepted, Is.True);
            CardInstance transformed = transformState.Players[0].Weapon!;
            Assert.That(transformed.DefinitionId, Is.EqualTo(replacement.Id));
            Assert.That(transformed.Id, Is.Not.EqualTo(original.Id));
            Assert.That((transformed.CurrentAttack, transformed.CurrentDurability), Is.EqualTo((4, 3)));
        }
    }
}
