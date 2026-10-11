using System;
using System.Collections.Generic;
using Kingdom.Core;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Training.Corpus
{
    /// <summary>
    /// Enumerates the complete, deterministic action set for one player at one state. The command
    /// validator remains authoritative: candidate generation supplies shapes, and only candidates
    /// accepted by <see cref="ICommandValidator"/> reach the returned list.
    /// </summary>
    public sealed class LegalActionEnumerator
    {
        private readonly ICardDatabase _db;
        private readonly ICommandValidator _validator;

        public LegalActionEnumerator(ICardDatabase db, ICommandValidator validator)
        {
            _db = db;
            _validator = validator;
        }

        public IReadOnlyList<PlayerIntent> Enumerate(GameState state, PlayerId player, bool includeConcede = false)
        {
            var result = new List<PlayerIntent>();
            if (state.Outcome != MatchOutcome.InProgress) return result;
            if (includeConcede) AddIfLegal(state, new ConcedeIntent(player), result);

            if (state.PendingChoice is PendingChoice pending)
            {
                if (pending.Chooser != player) return result;
                foreach (int option in pending.Options)
                    AddIfLegal(state, new UnearthChoiceIntent(player, option, pending.ChoiceSequence), result);
                return result;
            }

            if (state.Phase == TurnPhase.Mulligan)
            {
                int seat = state.SeatOf(player);
                bool complete = seat == 0 ? state.Mulligan0Done : state.Mulligan1Done;
                if (complete) return result;
                List<CardInstance> hand = state.Players[seat].Hand;
                int subsets = 1 << hand.Count;
                for (int mask = 0; mask < subsets; mask++)
                {
                    var replace = new List<InstanceId>();
                    for (int i = 0; i < hand.Count; i++)
                        if ((mask & (1 << i)) != 0) replace.Add(hand[i].Id);
                    AddIfLegal(state, new MulliganIntent(player, replace), result);
                }
                return result;
            }

            if (state.Phase != TurnPhase.Main || state.ActivePlayer != player) return result;

            PlayerState own = state.PlayerOf(player);
            PlayerState foe = state.Players[state.SeatOf(player) == 0 ? 1 : 0];

            foreach (CardInstance card in own.Hand)
            {
                CardDefinition definition = _db.Get(card.DefinitionId);
                Trigger trigger = definition.Type == CardType.SPELL ? Trigger.ON_CAST : Trigger.ON_PLAY;
                IReadOnlyList<TargetChoiceRequirement> plan =
                    Targeting.PlayerChoicePlan(state, _db, player, definition, trigger, card.Id);
                foreach (IReadOnlyList<TargetRef> targets in TargetCombinations(plan))
                {
                    int lastSlot = definition.Type == CardType.MINION ? own.Board.Count : 0;
                    for (int slot = 0; slot <= lastSlot; slot++)
                        AddIfLegal(state, new PlayCardIntent(player, card.Id, slot, targets), result);
                }
            }

            var attackers = new List<InstanceId>(own.Board.Count + 1);
            foreach (CardInstance card in own.Board) attackers.Add(card.Id);
            attackers.Add(own.Hero.Id);
            var defenders = new List<InstanceId>(foe.Board.Count + 1);
            foreach (CardInstance card in foe.Board) defenders.Add(card.Id);
            defenders.Add(foe.Hero.Id);
            foreach (InstanceId attacker in attackers)
                foreach (InstanceId defender in defenders)
                    AddIfLegal(state, new AttackIntent(player, attacker, new TargetRef(defender)), result);

            CardDefinition power = _db.Get(own.Hero.HeroPowerId);
            IReadOnlyList<TargetChoiceRequirement> powerPlan =
                Targeting.PlayerChoicePlan(state, _db, player, power, Trigger.ON_CAST, own.Hero.Id);
            foreach (IReadOnlyList<TargetRef> targets in TargetCombinations(powerPlan))
                AddIfLegal(state, new HeroPowerIntent(player, targets), result);

            AddIfLegal(state, new EndTurnIntent(player), result);
            return result;
        }

        private void AddIfLegal(GameState state, PlayerIntent intent, List<PlayerIntent> result)
        {
            if (_validator.CanExecute(state, intent, out _)) result.Add(intent);
        }

        private static IReadOnlyList<IReadOnlyList<TargetRef>> TargetCombinations(
            IReadOnlyList<TargetChoiceRequirement> plan)
        {
            var result = new List<IReadOnlyList<TargetRef>>();
            if (plan.Count == 0)
            {
                result.Add(Array.Empty<TargetRef>());
                return result;
            }

            int total = 0;
            foreach (TargetChoiceRequirement group in plan)
            {
                if (group.RequiredCount < 0 || group.RequiredCount > group.Candidates.Count)
                    return result;
                total += group.RequiredCount;
            }

            var current = new TargetRef[total];
            VisitGroup(plan, current, groupIndex: 0, targetIndex: 0, result);
            return result;
        }

        private static void VisitGroup(IReadOnlyList<TargetChoiceRequirement> plan, TargetRef[] current,
                                       int groupIndex, int targetIndex,
                                       List<IReadOnlyList<TargetRef>> result)
        {
            if (groupIndex == plan.Count)
            {
                result.Add((TargetRef[])current.Clone());
                return;
            }

            TargetChoiceRequirement group = plan[groupIndex];
            if (group.RequiredCount == 0)
            {
                VisitGroup(plan, current, groupIndex + 1, targetIndex, result);
                return;
            }

            var used = new HashSet<InstanceId>();
            VisitCandidate(plan, current, groupIndex, targetIndex, withinGroup: 0, used, result);
        }

        private static void VisitCandidate(IReadOnlyList<TargetChoiceRequirement> plan, TargetRef[] current,
                                           int groupIndex, int targetIndex, int withinGroup,
                                           HashSet<InstanceId> used,
                                           List<IReadOnlyList<TargetRef>> result)
        {
            TargetChoiceRequirement group = plan[groupIndex];
            if (withinGroup == group.RequiredCount)
            {
                VisitGroup(plan, current, groupIndex + 1, targetIndex + withinGroup, result);
                return;
            }

            foreach (TargetCandidate candidate in group.Candidates)
            {
                if (!used.Add(candidate.Instance)) continue;
                current[targetIndex + withinGroup] = new TargetRef(candidate.Instance, group.Binding);
                VisitCandidate(plan, current, groupIndex, targetIndex, withinGroup + 1, used, result);
                used.Remove(candidate.Instance);
            }
        }
    }
}
