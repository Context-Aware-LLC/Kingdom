using System.Collections.Generic;
using System.Linq;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

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
                System.Array.Empty<RuleDefinition>(), "", "art/" + id, SetId.CORE, true,
                tribe, "");

        private static CardDefinition Minion(string id, params RuleDefinition[] rules)
            => new CardDefinition(id, id, CardType.MINION, 1, 1, 3, null,
                CardClass.NEUTRAL, Rarity.COMMON, System.Array.Empty<Keyword>(),
                rules, "", "art/" + id, SetId.CORE, true,
                null, "");

        private static CardDefinition Spell(string id, params RuleDefinition[] rules)
            => new CardDefinition(id, id, CardType.SPELL, 1, null, null, null,
                CardClass.NEUTRAL, Rarity.COMMON, System.Array.Empty<Keyword>(), rules, "", "art/" + id, SetId.CORE, true, null, "");

        private static CardDefinition Weapon(string id, int attack = 2, int durability = 2,
            params Keyword[] keywords)
            => new CardDefinition(id, id, CardType.WEAPON, 1, attack, null, durability,
                CardClass.NEUTRAL, Rarity.COMMON, keywords, System.Array.Empty<RuleDefinition>(), "", "art/" + id, SetId.CORE, true, null, "");

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
                Zone = Zone.Weapon,
                CurrentAttack = 1,
                CurrentDurability = 1,
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
            RuleDefinition first = TestRules.Rule("first", Trigger.ON_CAST,
                new DamageAction(1, new TargetSpec("ANY_MINION", TargetChosenBy.PLAYER, binding: "first")));
            RuleDefinition second = TestRules.Rule("second", Trigger.ON_CAST,
                new DamageAction(2, new TargetSpec("ANY_MINION", TargetChosenBy.PLAYER,
                    binding: "second", distinctFrom: "first")));
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

        /// <summary>A card that asks for no target refuses one, whether it has no rules at all or
        /// only untargeted ones. This used to be accepted: a branch labelled "compatibility for
        /// pre-TargetSpec content" let an unsolicited live-character target through on any card with
        /// no <c>TargetSpec</c> anywhere. No such content exists or can be authored — a selector is
        /// required by <c>CardJson.TargetFromElement</c> and by <c>TargetedRuleAction</c>'s
        /// constructor — so the branch only widened this VULN-001 surface. These cases are the
        /// replacement contract; they fail if the permissive path ever returns.</summary>
        [Test]
        public void A_Card_That_Asks_For_No_Target_Refuses_An_Unsolicited_One()
        {
            var vanilla = Minion("vanilla");
            RuleDefinition draw = TestRules.Rule("draw", Trigger.ON_CAST, new DrawAction(1));
            var untargeted = Spell("untargeted_draw", draw);
            var db = new FakeCardDatabase().Register(vanilla).Register(untargeted);
            var state = State();
            CardInstance victim = Board(state, 1, vanilla);
            var target = new[] { new TargetRef(victim.Id) };
            PlayerId caster = state.Players[0].Id;

            Assert.Multiple(() =>
            {
                Assert.That(Targeting.ValidatePlayerChoices(state, db, caster, vanilla,
                    Trigger.ON_PLAY, target), Is.False,
                    "a minion with no rules must not accept a target");
                Assert.That(Targeting.ValidatePlayerChoices(state, db, caster, untargeted,
                    Trigger.ON_CAST, target), Is.False,
                    "a spell whose only rule is untargeted must not accept a target");

                // The accept case, so this is not merely asserting "always false".
                Assert.That(Targeting.ValidatePlayerChoices(state, db, caster, untargeted,
                    Trigger.ON_CAST, System.Array.Empty<TargetRef>()), Is.True,
                    "the same spell with no targets attached is a legal play");
            });
        }

        [Test]
        public void Player_Choice_Plan_Exposes_Binding_Order_Counts_And_Distinctness()
        {
            RuleDefinition first = TestRules.Rule("first", Trigger.ON_CAST,
                new DamageAction(1, new TargetSpec("ANY_MINION", TargetChosenBy.PLAYER, count: 2,
                    binding: "first")));
            RuleDefinition second = TestRules.Rule("second", Trigger.ON_CAST,
                new DamageAction(1, new TargetSpec("ANY_ENEMY", TargetChosenBy.PLAYER,
                    binding: "second", distinctFrom: "first")));
            var spell = Spell("choice_plan", first, second);
            var body = Minion("body");
            var db = new FakeCardDatabase().Register(spell).Register(body);
            var state = State();
            Board(state, 0, body);
            Board(state, 1, body);

            IReadOnlyList<TargetChoiceRequirement> plan = Targeting.PlayerChoicePlan(
                state, db, state.Players[0].Id, spell, Trigger.ON_CAST);

            Assert.That(plan.Select(p => (p.Binding, p.RequiredCount)),
                Is.EqualTo(new[] { ("first", 2), ("second", 1) }));
            Assert.That(plan[1].DistinctFrom, Is.EqualTo(new[] { "first" }));
            Assert.That(plan[0].Candidates, Has.Count.EqualTo(2));
            Assert.That(plan[1].Candidates.Select(c => c.Controller),
                Is.All.EqualTo(state.Players[1].Id));
        }

        [Test]
        public void Binding_Distinctness_Rejects_The_Same_Target_Without_Mutation()
        {
            RuleDefinition first = TestRules.Rule("first", Trigger.ON_CAST,
                new DamageAction(1, new TargetSpec("ANY_MINION", TargetChosenBy.PLAYER, binding: "first")));
            RuleDefinition second = TestRules.Rule("second", Trigger.ON_CAST,
                new DamageAction(1, new TargetSpec("ANY_MINION", TargetChosenBy.PLAYER,
                    binding: "second", distinctFrom: "first")));
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
        public void Action_Capability_Filters_Presentation_And_Rejects_A_NoOp_Player_Target()
        {
            var buff = Spell("buff", TestRules.Rule("buff", Trigger.ON_CAST,
                new ModifyStatsAction(1, 1,
                    new TargetSpec("ANY_FRIENDLY", TargetChosenBy.PLAYER))));
            var body = Minion("body");
            var db = new FakeCardDatabase().Register(buff).Register(body);
            var state = State();
            CardInstance minion = Board(state, 0, body);
            CardInstance card = ZoneCard(state, 0, buff, Zone.Hand);

            IReadOnlyList<TargetCandidate> candidates = Targeting.PlayerChoiceCandidates(
                state, db, state.Players[0].Id, buff, Trigger.ON_CAST);

            Assert.That(candidates.Select(c => c.Instance), Is.EqualTo(new[] { minion.Id }),
                "BUFF can affect a minion but not the friendly hero produced by ANY_FRIENDLY.");
            var rejected = new CommandProcessor(db).Execute(state, new PlayCardIntent(
                state.Players[0].Id, card.Id, 0,
                new[] { new TargetRef(state.Players[0].Hero.Id) }));
            Assert.That(rejected.Accepted, Is.False);
            Assert.That(rejected.Reason, Is.EqualTo(RejectReason.IllegalTarget));
            Assert.That(rejected.Events, Is.Empty);
            Assert.That(state.Players[0].Hand, Does.Contain(card));
        }

        [Test]
        public void Zone_Targets_Reach_Handlers_Instead_Of_Being_Dropped_As_NonCharacters()
        {
            var body = Minion("body");
            var blade = Weapon("blade");
            var recover = Spell("recover", TestRules.Rule("recover", Trigger.ON_CAST,
                new ReturnToHandAction(new TargetSpec("FRIENDLY_GRAVEYARD", TargetChosenBy.PLAYER))));
            var tutor = Spell("tutor", TestRules.Rule("tutor", Trigger.ON_CAST,
                new ReturnToHandAction(new TargetSpec("FRIENDLY_DECK", TargetChosenBy.RANDOM))));
            var disarm = Spell("disarm", TestRules.Rule("disarm", Trigger.ON_CAST,
                new ReturnToHandAction(new TargetSpec("FRIENDLY_WEAPON"))));
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
                Zone = Zone.Weapon,
                CurrentAttack = 2,
                CurrentDurability = 2,
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
            CardDefinition blade = Weapon("blade", keywords: Keyword.LIFEDRAIN);
            CardDefinition replacement = Weapon("replacement", attack: 4, durability: 3);
            var silence = Spell("silence_weapon", TestRules.Rule("silence", Trigger.ON_CAST,
                new SilenceAction(new TargetSpec("FRIENDLY_WEAPON"))));
            var destroy = Spell("destroy_weapon", TestRules.Rule("destroy", Trigger.ON_CAST,
                new DestroyAction(new TargetSpec("FRIENDLY_WEAPON"))));
            var transform = Spell("transform_weapon", TestRules.Rule("transform", Trigger.ON_CAST,
                new TransformAction(replacement.Id, new TargetSpec("FRIENDLY_WEAPON"))));
            var db = new FakeCardDatabase().Register(blade).Register(replacement)
                .Register(silence).Register(destroy).Register(transform);

            GameState silenceState = State();
            var enchanted = new CardInstance(silenceState.NextInstanceId(), blade.Id,
                silenceState.Players[0].Id)
            {
                Zone = Zone.Weapon,
                CurrentAttack = 5,
                CurrentDurability = 1,
            };
            enchanted.Keywords.Add(Keyword.LIFEDRAIN);
            enchanted.Keywords.Add(Keyword.VENOM);
            enchanted.Enchantments.Add(new Enchantment
            { Source = new InstanceId(99), AttackDelta = 3, GrantedKeyword = Keyword.VENOM });
            silenceState.Players[0].Weapon = enchanted;
            CardInstance silenceCard = ZoneCard(silenceState, 0, silence, Zone.Hand);

            Assert.That(new CommandProcessor(db).Execute(silenceState,
                new PlayCardIntent(silenceState.Players[0].Id, silenceCard.Id, 0,
                    System.Array.Empty<TargetRef>())).Accepted, Is.True);
            Assert.That(silenceState.Players[0].Weapon, Is.SameAs(enchanted));
            Assert.That((enchanted.CurrentAttack, enchanted.CurrentDurability), Is.EqualTo((2, 1)));
            Assert.That(enchanted.Enchantments, Is.Empty);
            Assert.That(enchanted.Keywords, Is.Empty, "silence removes printed as well as granted riders");
            Assert.That(enchanted.IsSilenced, Is.True);

            GameState destroyState = State();
            var doomed = new CardInstance(destroyState.NextInstanceId(), blade.Id,
                destroyState.Players[0].Id)
            { Zone = Zone.Weapon, CurrentAttack = 2, CurrentDurability = 2 };
            destroyState.Players[0].Weapon = doomed;
            CardInstance destroyCard = ZoneCard(destroyState, 0, destroy, Zone.Hand);

            Assert.That(new CommandProcessor(db).Execute(destroyState,
                new PlayCardIntent(destroyState.Players[0].Id, destroyCard.Id, 0,
                    System.Array.Empty<TargetRef>())).Accepted, Is.True);
            Assert.That(destroyState.Players[0].Weapon, Is.Null);
            Assert.That(destroyState.Players[0].Graveyard, Does.Contain(doomed));

            GameState transformState = State();
            var original = new CardInstance(transformState.NextInstanceId(), blade.Id,
                transformState.Players[0].Id)
            { Zone = Zone.Weapon, CurrentAttack = 2, CurrentDurability = 2 };
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

        [Test]
        public void FriendlyMinions_Includes_Source_Minion_Itself()
        {
            var db = new FakeCardDatabase().Register(Minion("body"));
            var state = State();
            CardInstance source = Board(state, 0, db.Get("body"));
            CardInstance ally = Board(state, 0, db.Get("body"));

            var candidates = Targeting.Candidates(state, db, state.Players[0].Id,
                new TargetSpec("FRIENDLY_MINIONS"), source: source.Id);

            Assert.That(candidates.Select(c => c.Instance),
                Is.EquivalentTo(new[] { source.Id, ally.Id }),
                "FRIENDLY_MINIONS must include the source minion (self-inclusion preserved).");
        }

        [Test]
        public void OtherFriendlyMinions_Excludes_Source_And_Retains_All_Other_Friendly_Minions()
        {
            var db = new FakeCardDatabase().Register(Minion("body"));
            var state = State();
            CardInstance source = Board(state, 0, db.Get("body"));
            CardInstance ally1 = Board(state, 0, db.Get("body"));
            CardInstance ally2 = Board(state, 0, db.Get("body"));
            Board(state, 1, db.Get("body"));

            var candidates = Targeting.Candidates(state, db, state.Players[0].Id,
                new TargetSpec("OTHER_FRIENDLY_MINIONS"), source: source.Id);

            Assert.That(candidates.Select(c => c.Instance),
                Is.EquivalentTo(new[] { ally1.Id, ally2.Id }),
                "OTHER_FRIENDLY_MINIONS must exclude the source and exclude enemies.");
        }

        [Test]
        public void OtherFriendlyMinions_Without_Source_Returns_All_Friendly_Minions()
        {
            var db = new FakeCardDatabase().Register(Minion("body"));
            var state = State();
            CardInstance a = Board(state, 0, db.Get("body"));
            CardInstance b = Board(state, 0, db.Get("body"));

            var candidates = Targeting.Candidates(state, db, state.Players[0].Id,
                new TargetSpec("OTHER_FRIENDLY_MINIONS"));

            Assert.That(candidates.Select(c => c.Instance),
                Is.EquivalentTo(new[] { a.Id, b.Id }),
                "OTHER_FRIENDLY_MINIONS with no source context returns all friendly minions.");
        }

        [Test]
        public void OtherFriendlyMinions_Herald_Buffs_Existing_Board_But_Not_Itself()
        {
            var buffRule = TestRules.Rule("on_play_buff", Trigger.ON_PLAY,
                new ModifyStatsAction(1, 1, new TargetSpec("OTHER_FRIENDLY_MINIONS")));
            var herald = Minion("herald", buffRule);
            var ally = Minion("ally");
            var db = new FakeCardDatabase().Register(herald).Register(ally);
            var state = State();
            CardInstance existingAlly = Board(state, 0, db.Get("ally"));
            CardInstance card = ZoneCard(state, 0, db.Get("herald"), Zone.Hand);

            new CommandProcessor(db).Execute(state,
                new PlayCardIntent(state.Players[0].Id, card.Id, 0,
                    System.Array.Empty<TargetRef>()));

            CardInstance played = state.Players[0].Board.First(c => c.Id == card.Id);
            Assert.That(existingAlly.CurrentAttack, Is.EqualTo(2), "Ally was buffed +1 attack.");
            Assert.That(existingAlly.CurrentHealth, Is.EqualTo(4), "Ally was buffed +1 health.");
            Assert.That(played.CurrentAttack, Is.EqualTo(1), "Herald did not buff itself.");
            Assert.That(played.CurrentHealth, Is.EqualTo(3), "Herald health unchanged.");
        }

        [Test]
        public void EnemyCharacters_Is_The_Whole_Enemy_Side_Hero_First()
        {
            // The plural of ANY_ENEMY (Sam, 2026-08-13). Hero FIRST, then minions left-to-right —
            // the reverse of how the same sweep read when it was authored as two rules, so this
            // pins the order a sweep's damage lands in rather than leaving it to AddCharacters.
            var db = new FakeCardDatabase().Register(Minion("body"));
            var state = State();
            CardInstance left = Board(state, 1, db.Get("body"));
            CardInstance right = Board(state, 1, db.Get("body"));
            Board(state, 0, db.Get("body"));   // a friendly minion the sweep must not reach

            var candidates = Targeting.ResolutionCandidates(state, db, state.Players[0].Id,
                new TargetSpec("ENEMY_CHARACTERS"));

            Assert.That(candidates.Select(c => c.Kind), Is.EqualTo(new[]
            {
                TargetCandidateKind.Hero,
                TargetCandidateKind.Minion,
                TargetCandidateKind.Minion,
            }));
            Assert.That(candidates.Skip(1).Select(c => c.Instance),
                Is.EqualTo(new[] { left.Id, right.Id }));
        }

        [Test]
        public void EnemyCharacters_Is_Automatic_Not_A_Player_Prompt()
        {
            // The whole reason the selector exists: ANY_ENEMY defaults to a player prompt, so a
            // sweep of the side was unauthorable as one rule and had to be written as two.
            Assert.That(TargetSpec.DefaultsToPlayerChoice("ENEMY_CHARACTERS"), Is.False);
            Assert.That(TargetSpec.IsPlayerChosen(new TargetSpec("ENEMY_CHARACTERS")), Is.False);
            Assert.That(TargetSpec.DefaultsToPlayerChoice("ANY_ENEMY"), Is.True);
        }

        [TestCase("FRIENDLY_MINIONS")]
        [TestCase("ALL_MINIONS")]
        public void Automatic_OnPlay_Selectors_Include_Their_Own_Newly_Entered_Body(string selector)
        {
            var buffRule = TestRules.Rule("on_play_self_buff", Trigger.ON_PLAY,
                new ModifyStatsAction(1, 1, new TargetSpec(selector)));
            var herald = Minion("herald", buffRule);
            var db = new FakeCardDatabase().Register(herald);
            var state = State();
            CardInstance card = ZoneCard(state, 0, db.Get("herald"), Zone.Hand);

            new CommandProcessor(db).Execute(state,
                new PlayCardIntent(state.Players[0].Id, card.Id, 0,
                    System.Array.Empty<TargetRef>()));

            CardInstance played = state.Players[0].Board.Single();
            Assert.That(played.CurrentAttack, Is.EqualTo(2),
                $"{selector} ON_PLAY must include the played minion itself (+1 attack).");
            Assert.That(played.CurrentHealth, Is.EqualTo(4),
                $"{selector} ON_PLAY must include the played minion itself (+1 health).");
        }

        [Test]
        public void AnyFriendly_OnPlay_Candidates_Include_The_Newly_Played_Minion()
        {
            var herald = Minion("herald");
            var db = new FakeCardDatabase().Register(herald);
            var state = State();
            CardInstance placed = Board(state, 0, db.Get("herald"));

            var candidates = Targeting.ResolutionCandidates(state, db, state.Players[0].Id,
                new TargetSpec("ANY_FRIENDLY"), source: placed.Id);

            Assert.That(candidates.Select(c => c.Instance), Does.Contain(placed.Id),
                "ANY_FRIENDLY resolution pool must include the newly entered minion.");
        }
    }
}
