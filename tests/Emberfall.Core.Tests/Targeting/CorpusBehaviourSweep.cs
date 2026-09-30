using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Core.Serialization;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

using Engine = Emberfall.Core.Targeting;

namespace Emberfall.Core.Tests.Targeting
{
    /// <summary>
    /// Runs EVERY authored card's rules against one stocked board and reports only the ones whose
    /// behaviour a reader of the card would not expect.
    ///
    /// <para>The 20-card sample in <see cref="CardTextBehaviourAudit"/> is for reading in full; this
    /// is the sweep, and it prints nothing for a card that behaves as written. Both exist because a
    /// sample shows you what agreement looks like and a sweep shows you where it fails.</para>
    ///
    /// <para><b>Each flag is checked PER RULE, against that rule's own rendered sentence</b> — not
    /// against the card's whole text. A two-rule card that buffs friendlies and burns enemies says
    /// both "friendly" and "enemy" in one string, so a whole-card comparison flags it every time and
    /// the sweep drowns in false positives.</para>
    ///
    /// <para><b>It asserts nothing about individual cards, deliberately</b> — the grammar is not a
    /// gate and this is not one either. It fails only if a card cannot be run at all, which is a
    /// broken harness rather than a finding.</para>
    /// </summary>
    [TestFixture]
    public sealed class CorpusBehaviourSweep
    {
        [Test]
        public void Report_every_card_whose_behaviour_its_text_does_not_predict()
        {
            List<CardDefinition> cards = ShippedCards();
            var db = new JsonCardDatabase(cards.Concat(Fillers()));

            var flagged = new List<string>();
            var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int ran = 0, unrunnable = 0;

            foreach (CardDefinition card in cards.OrderBy(c => c.Id))
            {
                if (card.Rules.Count == 0) continue;
                try
                {
                    IReadOnlyList<string> findings = Inspect(db, card);
                    ran++;
                    if (findings.Count == 0) continue;
                    flagged.Add($"{card.Id}  [{card.Type}]");
                    flagged.Add($"    says: {card.Text}");
                    foreach (string f in findings)
                    {
                        flagged.Add("    " + f);
                        string code = f.Substring(0, f.IndexOf(' '));
                        tally[code] = tally.TryGetValue(code, out int n) ? n + 1 : 1;
                    }
                    flagged.Add("");
                }
                catch (Exception e)
                {
                    unrunnable++;
                    flagged.Add($"{card.Id}  <{e.GetType().Name}> {e.Message}");
                    flagged.Add("");
                }
            }

            var report = new List<string>
            {
                $"corpus sweep — {cards.Count} cards, {ran} with rules run, {unrunnable} unrunnable",
                "",
            };
            report.AddRange(flagged);
            report.Add(new string('-', 70));
            report.Add("flags raised:");
            foreach (KeyValuePair<string, int> kv in tally) report.Add($"  {kv.Value,4}  {kv.Key}");
            if (tally.Count == 0) report.Add("  (none)");

            string text = string.Join("\n", report);
            File.WriteAllText(Path.Combine(RepoPaths.RepoRoot, "corpus-behaviour-sweep.txt"), text);
            TestContext.Out.WriteLine(text);

            Assert.That(unrunnable, Is.Zero, "every authored card must at least run");

            // THE GATE. Until 2026-08-20 this test asserted only the line above: it computed every
            // finding, wrote them to a file nobody opens, and passed. "Baseline: zero flags" was a
            // statement about that file, not a thing the suite would defend — a comparator whose
            // output is ungated is not an oracle, it is a report.
            //
            // Found by mutation, which is the only way an ungated check ever announces itself: a
            // deliberately flipped selector side made this sweep print 18 SAYS-ENEMY-HITS-FRIENDLY
            // findings and still report green.
            //
            // The corpus is at zero, so zero is the bar. A card whose text and behaviour disagree
            // is either a data bug or an engine bug and both are wanted loudly; if a finding is
            // ever judged acceptable, the judgement belongs in Inspect() as a stated exception with
            // its reason, never as a tolerance here.
            Assert.That(tally, Is.Empty,
                "text and behaviour disagree on the shipped corpus — see corpus-behaviour-sweep.txt:\n" +
                string.Join("\n", flagged));
        }

        /// <summary>Every way this sweep knows how to say "the card does not do what it says".</summary>
        private static IReadOnlyList<string> Inspect(ICardDatabase db, CardDefinition card)
        {
            var findings = new List<string>();
            GameState state = Board();
            PlayerId controller = state.Players[0].Id;
            Trigger trigger = RuleSemantics.TriggerOf(card.Rules[0]);
            CardInstance carrier = Place(state, card, controller, trigger);

            var chosen = new List<TargetRef>();
            foreach (RuleDefinition rule in card.Rules)
            {
                TargetSpec? spec = RuleSemantics.TargetOf(rule.Action);
                if (spec == null) continue;

                string sentence = Sentence(rule, db);
                IReadOnlyList<TargetCandidate> resolved = Engine.ResolutionCandidates(
                    state, db, controller, spec, rule.Action, carrier.Id, carrier.Id);

                // How many this rule actually REACHES — not how many it is offered. A player: 1 spec
                // is offered the whole legal pool and touches exactly one of them, so comparing the
                // pool against the sentence's plurality would flag every targeted card in the game.
                // One expression where there were two: a count that has reached the selector's
                // maximum clamps to the candidate list anyway, which is what "all" always meant.
                int reaches = Math.Min(spec.Selection.Count, resolved.Count);

                if (resolved.Count == 0)
                    findings.Add($"NO-CANDIDATES  \"{sentence}\" — selector {spec.Selector} finds " +
                                 "nothing on a fully stocked board, so this rule can never do anything.");

                bool saysFriendly = Mentions(sentence, "friendly") || Mentions(sentence, "your");
                bool saysEnemy = Mentions(sentence, "enemy") || Mentions(sentence, "enemies");

                if (saysFriendly && !saysEnemy &&
                    resolved.Any(c => !c.Controller.Equals(controller)))
                    findings.Add($"SAYS-FRIENDLY-HITS-ENEMY  \"{sentence}\" — {spec.Selector} " +
                                 "can resolve onto the opponent's side.");

                if (saysEnemy && !saysFriendly &&
                    resolved.Any(c => c.Controller.Equals(controller)))
                    findings.Add($"SAYS-ENEMY-HITS-FRIENDLY  \"{sentence}\" — {spec.Selector} " +
                                 "can resolve onto the controller's own side.");

                if (Mentions(sentence, "minion") && !Mentions(sentence, "character") &&
                    resolved.Any(c => c.IsHero))
                    findings.Add($"SAYS-MINION-HITS-HERO  \"{sentence}\" — {spec.Selector} " +
                                 "produced a hero candidate.");

                // The arity check, and the reason the selector vocabulary is worth auditing: a
                // singular sentence that reaches more than one thing is a card whose determiner
                // lies about its own count.
                if (reaches > 1 && IsSingular(sentence))
                    findings.Add($"SAYS-ONE-REACHES-MANY  \"{sentence}\" — reads as one target but " +
                                 $"reaches {reaches}.");
                if (reaches == 1 && Mentions(sentence, "all "))
                    findings.Add($"SAYS-ALL-REACHES-ONE  \"{sentence}\" — reads as a sweep but " +
                                 "reaches exactly one.");

                if (TargetSpec.IsPlayerChosen(spec))
                    foreach (TargetCandidate pick in Engine.Candidates(
                                 state, db, controller, spec, rule.Action, carrier.Id, carrier.Id)
                             .Take(spec.Selection.Count))
                        chosen.Add(new TargetRef(pick.Instance, spec.Binding));
            }

            // The canonical digest, NOT a hand-written snapshot. A field list maintained here is a
            // second description of game state that silently omits whatever it forgets — the first
            // run of this sweep called five Backdraft cards inert because the snapshot never read
            // NextTurnBackdraftLock. GameStateSerializer's walk is held exhaustive by
            // CanonicalStateFixtures.Manifest, so borrowing it means this flag cannot miss a field.
            string before = GameStateHasher.Hash(state);
            var interpreter = new EffectInterpreter(state, db, new EventBuffer(state));
            interpreter.RaiseSelf(carrier, trigger, chosen, TurnContext(card, state, controller));
            interpreter.Drain();
            if (GameStateHasher.Hash(state) == before)
                findings.Add("NO-STATE-CHANGE  the whole card resolved and changed no canonical state.");

            return findings;
        }

        /// <summary>
        /// The turn a card's own rules say they fire on.
        /// </summary>
        /// <remarks>
        /// <b>A TURN_OWNER condition is evaluated against the INCOMING turn player, which only a real
        /// start-of-turn sequence supplies</b> — with no context it fails closed, so every
        /// ON_TURN_START card resolves to nothing and this sweep reports NO-STATE-CHANGE against the
        /// card for a gap in the harness. The corpus's first ON_TURN_START row is what surfaced it.
        ///
        /// <para>The owner is read off the rule rather than hardcoded, so an OPPONENT-owned effect is
        /// exercised on the opponent's turn — the one it actually claims to fire on. A card with no
        /// such condition gets the empty context it always had.</para>
        /// </remarks>
        private static TriggerEventContext TurnContext(CardDefinition card, GameState state,
            PlayerId controller)
        {
            foreach (RuleDefinition rule in card.Rules)
            {
                if (!(rule.Condition is TurnOwnerCondition owner)) continue;
                return TriggerEventContext.ForIncomingTurn(owner.Owner == TurnOwner.OPPONENT
                    ? state.Players[1].Id
                    : controller);
            }
            return default;
        }

        private static string Sentence(RuleDefinition rule, ICardDatabase db)
        {
            try { return CardTextGenerator.Generate(new[] { rule }, db); }
            catch (Exception) { return "<unrenderable>"; }
        }

        private static bool Mentions(string sentence, string word)
            => sentence.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Whether the sentence's determiner claims exactly one target.</summary>
        private static bool IsSingular(string sentence)
            => !Mentions(sentence, "all ") &&
               (Mentions(sentence, " a ") || Mentions(sentence, " an ") || Mentions(sentence, " the ") ||
                Mentions(sentence, "another "));

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
                // Matches EffectInterpreter, which graveyards a dying minion (:247) before raising
                // its toll (:253) — so a toll is never among its own candidates.
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

        private static string Snapshot(GameState state)
        {
            var lines = new List<string>();
            foreach (PlayerState p in state.Players)
            {
                string side = p.Id.Equals(state.Players[0].Id) ? "our:" : "their:";
                lines.Add($"{side}hero {p.Hero.CurrentHealth}/{p.Hero.Armor}/{p.Hero.IsFrosted()}");
                foreach (CardInstance m in p.Board)
                    lines.Add($"{side}{m.DefinitionId}#{m.Id} {m.CurrentAttack}/{m.CurrentHealth}" +
                              $" [{string.Join(",", m.Keywords.Select(k => k.ToString()).OrderBy(k => k, StringComparer.Ordinal))}]" +
                              $" {m.IsFrosted()} {m.IsSilenced}");
                lines.Add($"{side}wpn {(p.Weapon == null ? "-" : p.Weapon.DefinitionId + "/" + p.Weapon.CurrentDurability)}");
                lines.Add($"{side}{p.Hand.Count}/{p.Deck.Count}/{p.Graveyard.Count}"
                          + $" mana={p.Mana.Available}/{p.Mana.Max}+{p.Mana.Temporary}-{p.Mana.Locked}");
            }
            return string.Join("\n", lines);
        }

        /// <summary>The shipped rows. Internal rather than private so CardTextParserCoverage reads
        /// the same corpus through the same loader — two fixtures disagreeing about what "the
        /// shipped cards" are is the kind of drift this project keeps paying for.</summary>
        internal static List<CardDefinition> ShippedCards()
        {
            var defs = new List<CardDefinition>();
            foreach (string file in new[] { "Core.cards.json", "Emberfall.cards.json", "Rekindling.cards.json" })
                defs.AddRange(CardJson.DeserializeArray(File.ReadAllText(Path.Combine(
                    RepoPaths.RepoRoot, "unity", "Assets", "CardData", file))));
            return defs;
        }

        /// <summary>
        /// Filler tribes, cycled across a SIX-minion board so every tribe appears at least twice.
        /// Twice matters: with one of each, "all friendly Constructs" reaches exactly one and reads
        /// as a sweep that is not sweeping — a property of the board, not of the card.
        /// </summary>
        private static readonly Tribe[] Tribes = { Tribe.CONSTRUCT, Tribe.BEAST, Tribe.UNDEAD };

        /// <summary>
        /// Keywords worn by two of each side's six minions, for the same reason the fillers are
        /// tribed: a keyword FILTER matches nothing on a board of bare fillers, which reads as "this
        /// card can never do anything" and is really "this board has none of what it asked for".
        /// Two, not one, or "all friendly Shroud minions" reaches exactly one and trips
        /// SAYS-ALL-REACHES-ONE instead.
        ///
        /// <para><b>Deliberately not the whole Keyword enum.</b> These are worn during the sweep's
        /// EXECUTION half as well as its targeting half, so a blanket AEGIS would absorb the damage
        /// every damage card is being measured by and report the corpus inert. A filter naming a
        /// keyword absent from this list surfaces as NO-CANDIDATES — which is the right failure:
        /// it asks whoever authored that card to widen the board here, and to think about what the
        /// keyword does to everything else being swept while they do.</para>
        /// </summary>
        private static readonly Keyword[] WornKeywords = { Keyword.SHROUD };

        private static IEnumerable<CardDefinition> Fillers()
        {
            for (int i = 0; i < 64; i++)
            {
                foreach (Tribe tribe in Tribes)
                    yield return Filler(FixtureCard.Id($"dummy_{tribe}_{i:00}"), CardType.MINION, tribe);
                yield return Filler(FixtureCard.Id($"dummy_{i:00}"), CardType.MINION);
                yield return Filler(FixtureCard.Id($"dummy_w{i:00}"), CardType.WEAPON);
            }
        }

        private static CardDefinition Filler(int id, CardType type, Tribe? tribe = null) => new CardDefinition(
            id: id, handle: FixtureCard.Handle(id), name: FixtureCard.Handle(id), type: type, cost: 1,
            attack: 2, health: type == CardType.MINION ? 4 : (int?)null,
            durability: type == CardType.WEAPON ? 2 : (int?)null,
            @class: CardClass.NEUTRAL, rarity: Rarity.COMMON,
            keywords: Array.Empty<Keyword>(), rules: Array.Empty<RuleDefinition>(),
            flavorText: "", artId: FixtureCard.Handle(id), setId: SetId.CORE,
            collectible: false, tribe: tribe, factionId: "");

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
                player.Mana.Max = 7;
                player.Mana.Available = 5;
                for (int i = 0; i < 6; i++)
                {
                    int id = next++;
                    var worn = new HashSet<Keyword>();
                    if (i % 3 == 1) worn.Add(WornKeywords[(i / 3) % WornKeywords.Length]);
                    // Tribed on purpose: a tribe FILTER matches nothing on a board of untribed
                    // fillers, which reads as "this card can never do anything" and is really
                    // "this board has none of what it asked for".
                    player.Board.Add(new CardInstance(new InstanceId(id), FixtureCard.Id($"dummy_{Tribes[i % Tribes.Length]}_{id:00}"), player.Id)
                    {
                        Zone = Zone.Board,
                        Position = i,
                        CurrentAttack = 2,
                        MaxHealth = 4,
                        CurrentHealth = i == 0 ? 2 : 4,
                        Keywords = worn,
                    });
                }
                int wid = next++;
                player.Weapon = new CardInstance(new InstanceId(wid), FixtureCard.Id($"dummy_w{wid:00}"), player.Id)
                { Zone = Zone.Weapon, CurrentAttack = 2, CurrentDurability = 2 };
                for (int i = 0; i < 3; i++)
                {
                    int h = next++, d = next++, g = next++;
                    player.Hand.Add(new CardInstance(new InstanceId(h), FixtureCard.Id($"dummy_{h:00}"), player.Id) { Zone = Zone.Hand });
                    player.Deck.Add(new CardInstance(new InstanceId(d), FixtureCard.Id($"dummy_{d:00}"), player.Id) { Zone = Zone.Deck });
                    player.Graveyard.Add(new CardInstance(new InstanceId(g), FixtureCard.Id($"dummy_{g:00}"), player.Id) { Zone = Zone.Graveyard });
                }
            }

            return new GameState(new MatchId("m-corpus-sweep"), new DeterministicRandom(0xC0FFEEUL), p0, p1)
            {
                Phase = TurnPhase.Main,
                NextInstanceCounter = next,
            };
        }
    }
}
