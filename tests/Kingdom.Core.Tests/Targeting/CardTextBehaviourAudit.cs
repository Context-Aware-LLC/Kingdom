using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

// The fixture's own namespace ends in Targeting, so name the engine seam explicitly.
using Engine = Kingdom.Core.Targeting;

namespace Kingdom.Core.Tests.Targeting
{
    /// <summary>
    /// Puts what a card SAYS next to what the engine DOES with it, over a representative sample of
    /// authored cards.
    ///
    /// <para><b>What this is for (Sam, 2026-08-20).</b> The grammar is not a gate and does not decide
    /// what may be authored — everything is authorable. What matters is that a shipped card's text
    /// describes an effect the engine actually produces, because a card that says something and does
    /// nothing is the failure worth catching well before release. This fixture is the instrument for
    /// that audit: it runs each card's rules against one fixed board and reports the sentence, the
    /// candidates its target spec resolved to, and the state the effect actually changed.</para>
    ///
    /// <para><b>It asserts nothing about any individual card, deliberately.</b> The output is a
    /// report to read, not a gate to pass — the point is to see what the disagreements ARE before
    /// ruling on any of them. The single assertion is that every sampled card produced a report
    /// entry, so a card that throws on the way in cannot drop out of the sample silently.</para>
    ///
    /// <para>The sample comes from <c>tooling/scripts/sample_card_text_audit.py</c>, which stratifies
    /// the corpus by (card type, action, selector, selection, trigger) and takes rarest key first.
    /// Regenerate it there rather than editing the list below, or the sample quietly stops being
    /// representative of the corpus it was drawn from.</para>
    /// </summary>
    [TestFixture]
    public sealed class CardTextBehaviourAudit
    {
        private static readonly int[] Sample =
        {
            1,        // hero_power_searing_bolt
            4,        // hero_power_quicken
            1066,     // clock_clockwork_sniper
            1075,     // night_kira_blade_of_shadows
            1058,     // tide_hollowshell_warden
            153,      // ember_scorch
            155,      // tide_frostbind
            194,      // tide_glacial_echo
            178,      // night_death_mark
            164,      // verdant_sapsurge
            173,      // clock_mass_recall
            60,       // ember_scorch_blade
            53,       // verdant_grasping_thornvine
        };

        [Test]
        public void Report_what_each_sampled_card_says_and_what_it_does()
        {
            ICardDatabase db = ShippedCards();
            var report = new List<string>();
            int reported = 0;

            foreach (int cardId in Sample)
            {
                report.Add(new string('-', 96));
                try { report.AddRange(AuditOne(db, cardId)); }
                catch (Exception e) { report.Add($"{cardId}: <{e.GetType().Name}> {e.Message}"); }
                reported++;
            }

            string text = string.Join("\n", report);
            File.WriteAllText(Path.Combine(RepoPaths.RepoRoot, "card-text-audit.txt"), text);
            TestContext.Out.WriteLine(text);

            Assert.That(reported, Is.EqualTo(Sample.Length),
                "every sampled card must produce a report entry, including the ones that fail");
        }

        private static IReadOnlyList<string> AuditOne(ICardDatabase db, int cardId)
        {
            CardDefinition card = db.Get(cardId);
            var lines = new List<string> { $"{cardId}   [{card.Type}]", $"  SAYS   : {card.Text}" };

            GameState state = Board();
            var events = new EventBuffer(state);
            var interpreter = new EffectInterpreter(state, db, events);

            PlayerId controller = state.Players[0].Id;
            Trigger trigger = card.Rules.Count > 0 ? RuleSemantics.TriggerOf(card.Rules[0]) : Trigger.ON_PLAY;
            CardInstance carrier = Place(state, card, controller, trigger);
            lines.Add($"  TRIGGER: {trigger}");

            // Resolve each rule's target spec and report it, then supply a deterministic pick for any
            // player-chosen target — the audit asks what the engine does once a legal choice exists,
            // not how a human would choose.
            var chosen = new List<TargetRef>();
            foreach (RuleDefinition rule in card.Rules)
            {
                TargetSpec? spec = RuleSemantics.TargetOf(rule.Action);
                if (spec == null)
                {
                    lines.Add($"  TARGET : rule {rule.Id.Value} ({Name(rule.Action)}) takes no target");
                    continue;
                }

                IReadOnlyList<TargetCandidate> resolved = Engine.ResolutionCandidates(
                    state, db, controller, spec, rule.Action, carrier.Id, carrier.Id);
                lines.Add($"  TARGET : {Name(rule.Action)} selector={spec.Selector} " +
                          $"selection={Term(spec.Selection)} -> {resolved.Count} candidate(s): " +
                          Names(state, resolved.Select(c => c.Instance)));

                if (!TargetSpec.IsPlayerChosen(spec)) continue;

                IReadOnlyList<TargetCandidate> offered = Engine.Candidates(
                    state, db, controller, spec, rule.Action, carrier.Id, carrier.Id);
                List<TargetRef> picks = offered.Take(spec.Selection.Count)
                    .Select(c => new TargetRef(c.Instance, spec.Binding)).ToList();
                chosen.AddRange(picks);
                lines.Add($"  CHOICE : asked for {spec.Selection.Count} of {offered.Count} offered, " +
                          $"took {picks.Count}: {Names(state, picks.Select(t => t.Instance))}");
            }

            string before = Snapshot(state);
            interpreter.RaiseSelf(carrier, trigger, chosen);
            interpreter.Drain();

            lines.Add("  DOES   :");
            lines.AddRange(Delta(before, Snapshot(state)).Select(d => "           " + d));
            return lines;
        }

        private static string Name(RuleAction action)
            => action.GetType().Name.Replace("Action", "").ToUpperInvariant();

        /// <summary>
        /// Puts the card where its type lives at the moment its trigger fires, so the effect runs
        /// from a real carrier in a real zone.
        ///
        /// <para>The <c>ON_DEATH</c> case is not a detail. <c>EffectInterpreter</c> moves a dying
        /// minion to the graveyard (<c>:247</c>) BEFORE raising its toll (<c>:253</c>), so a toll
        /// resolves with its own carrier already off the board — and therefore not among its own
        /// candidates. Staging it on the board instead adds one phantom candidate to every toll that
        /// targets friendly minions, which reads exactly like an engine finding and is not one.</para>
        /// </summary>
        private static CardInstance Place(GameState state, CardDefinition card, PlayerId controller,
            Trigger trigger)
        {
            PlayerState player = state.Players[0];
            var instance = new CardInstance(state.NextInstanceId(), card.Id, controller)
            {
                CurrentAttack = card.Attack ?? 0,
                CurrentHealth = card.Health ?? 1,
                MaxHealth = card.Health ?? 1,
                CurrentDurability = card.Durability,
            };

            switch (card.Type)
            {
                case CardType.MINION when trigger == Trigger.ON_DEATH:
                    instance.Zone = Zone.Graveyard;
                    player.Graveyard.Add(instance);
                    break;
                case CardType.MINION:
                    instance.Zone = Zone.Board;
                    instance.Position = player.Board.Count;
                    player.Board.Add(instance);
                    break;
                case CardType.WEAPON:
                    instance.Zone = Zone.Weapon;
                    player.Weapon = instance;
                    break;
                default:
                    instance.Zone = Zone.Hand;
                    player.Hand.Add(instance);
                    break;
            }
            return instance;
        }

        // The term has exactly one spelling now, and TargetSelection.ToString is where it lives —
        // a second copy here is how an audit line and a card file drift apart.
        private static string Term(TargetSelection selection) => selection.ToString();

        private static string Names(GameState state, IEnumerable<InstanceId> ids)
        {
            List<string> parts = ids.Select(id => Describe(state, id)).ToList();
            return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
        }

        private static string Describe(GameState state, InstanceId id)
        {
            foreach (PlayerState p in state.Players)
            {
                if (p.Hero.Id.Equals(id)) return Side(state, p) + "hero";
                IEnumerable<CardInstance> all = p.Board.Concat(p.Hand).Concat(p.Deck).Concat(p.Graveyard);
                if (p.Weapon != null) all = all.Concat(new[] { p.Weapon });
                CardInstance? card = all.FirstOrDefault(c => c.Id.Equals(id));
                if (card != null) return $"{Side(state, p)}{card.DefinitionId}#{card.Id}";
            }
            return "?" + id;
        }

        private static string Side(GameState state, PlayerState p)
            => p.Id.Equals(state.Players[0].Id) ? "our:" : "their:";

        /// <summary>A readable, total picture of every part of the state this audit can move.</summary>
        private static string Snapshot(GameState state)
        {
            var lines = new List<string>();
            foreach (PlayerState p in state.Players)
            {
                string side = Side(state, p);
                lines.Add($"{side}hero hp={p.Hero.CurrentHealth} armor={p.Hero.Armor} frost={p.Hero.IsFrosted()}");
                foreach (CardInstance m in p.Board)
                    lines.Add($"{side}board {m.DefinitionId}#{m.Id} {m.CurrentAttack}/{m.CurrentHealth}" +
                              $" kw=[{string.Join(",", m.Keywords.Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal))}]" +
                              $" frost={m.IsFrosted()} silenced={m.IsSilenced}");
                lines.Add($"{side}weapon " + (p.Weapon == null
                    ? "(none)"
                    : $"{p.Weapon.DefinitionId} atk={p.Weapon.CurrentAttack} dur={p.Weapon.CurrentDurability}"));
                lines.Add($"{side}hand={p.Hand.Count} deck={p.Deck.Count} grave={p.Graveyard.Count}");
            }
            return string.Join("\n", lines);
        }

        private static IReadOnlyList<string> Delta(string before, string after)
        {
            var remaining = new List<string>(before.Split('\n'));
            var changes = new List<string>();
            foreach (string line in after.Split('\n'))
                if (!remaining.Remove(line)) changes.Add("+ " + line);
            changes.AddRange(remaining.Select(line => "- " + line));
            return changes.Count == 0 ? new[] { "(no state change)" } : (IReadOnlyList<string>)changes;
        }

        private static ICardDatabase ShippedCards()
        {
            var defs = new List<CardDefinition>();
            foreach (string file in new[] { "Core.cards.json", "Emberfall.cards.json", "Rekindling.cards.json" })
                defs.AddRange(CardJson.DeserializeArray(File.ReadAllText(Path.Combine(
                    RepoPaths.RepoRoot, "unity", "Assets", "CardData", file))));

            // The board's filler minions need definitions too. Any effect that reads a TARGET's
            // definition — a sweep, a REINFORCE, a RETURN_TO_HAND — resolves it through the same
            // database, so a board of instances with no rows behind them throws on exactly the cards
            // this audit most wants to see. Rule-less by design: filler that carried rules of its own
            // would put effects in the report that no sampled card caused.
            for (int i = 0; i < 64; i++)
            {
                defs.Add(Filler(FixtureCard.Id($"dummy_{i:00}"), CardType.MINION));
                defs.Add(Filler(FixtureCard.Id($"dummy_w{i:00}"), CardType.WEAPON));
            }
            return new JsonCardDatabase(defs);
        }

        private static CardDefinition Filler(int id, CardType type) => new CardDefinition(
            id: id, handle: FixtureCard.Handle(id), name: FixtureCard.Handle(id), type: type, cost: 1,
            attack: 2, health: type == CardType.MINION ? 4 : (int?)null,
            durability: type == CardType.WEAPON ? 2 : (int?)null,
            @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
            keywords: Array.Empty<Keyword>(), rules: Array.Empty<RuleDefinition>(),
            flavorText: "", artId: FixtureCard.Handle(id), setId: SetId.CORE,
            collectible: false, tribe: null, factionId: "");

        /// <summary>
        /// One board with room for every shape in the sample: damaged heroes so a heal is visible,
        /// three minions a side with the leftmost damaged so a sweep and a single pick differ, a
        /// weapon each, and non-empty hidden zones so a return-to-hand has somewhere to move from.
        /// </summary>
        private static GameState Board()
        {
            var p0Id = new PlayerId("p0");
            var p1Id = new PlayerId("p1");
            int next = 0;

            var p0 = new PlayerState(p0Id, new HeroInstance(new InstanceId(next++), p0Id, CardClass.EMBERWARD, 30));
            var p1 = new PlayerState(p1Id, new HeroInstance(new InstanceId(next++), p1Id, CardClass.TIDECALLER, 30));
            p0.Hero.CurrentHealth = 22;
            p1.Hero.CurrentHealth = 25;

            foreach (PlayerState player in new[] { p0, p1 })
            {
                for (int i = 0; i < 3; i++)
                {
                    int id = next++;
                    player.Board.Add(new CardInstance(new InstanceId(id), FixtureCard.Id($"dummy_{id:00}"), player.Id)
                    {
                        Zone = Zone.Board,
                        Position = i,
                        CurrentAttack = 2,
                        MaxHealth = 4,
                        CurrentHealth = i == 0 ? 2 : 4,
                    });
                }

                int wid = next++;
                player.Weapon = new CardInstance(new InstanceId(wid), FixtureCard.Id($"dummy_w{wid:00}"), player.Id)
                { Zone = Zone.Weapon, CurrentAttack = 2, CurrentDurability = 2 };

                for (int i = 0; i < 2; i++)
                {
                    int h = next++, d = next++, g = next++;
                    player.Hand.Add(new CardInstance(new InstanceId(h), FixtureCard.Id($"dummy_{h:00}"), player.Id) { Zone = Zone.Hand });
                    player.Deck.Add(new CardInstance(new InstanceId(d), FixtureCard.Id($"dummy_{d:00}"), player.Id) { Zone = Zone.Deck });
                    player.Graveyard.Add(new CardInstance(new InstanceId(g), FixtureCard.Id($"dummy_{g:00}"), player.Id) { Zone = Zone.Graveyard });
                }
            }

            return new GameState(new MatchId("m-text-audit"), new DeterministicRandom(0xA0D17UL), p0, p1)
            {
                Phase = TurnPhase.Main,
                NextInstanceCounter = next,
            };
        }
    }
}
