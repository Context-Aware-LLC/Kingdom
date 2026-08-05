using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Core
{
    /// <summary>
    /// Continuous, deterministic AURA fold. It never consumes RNG, enqueues no rule, and owns the
    /// remove-then-reapply lifecycle for every board topology or aura-eligibility change.
    /// </summary>
    internal static class AuraEngine
    {
        public static void Recompute(GameState state, ICardDatabase db)
        {
            // First remove marked provenance from every possible aura target. Board order is the
            // canonical order and no ordinary enchantment is considered, even when sources match.
            for (int seat = 0; seat < 2; seat++)
                foreach (CardInstance target in state.Players[seat].Board)
                    DerivedStats.RemoveAuras(target, db.Get(target.DefinitionId));

            // Then reapply sources in fixed seat, board, authored-rule order.
            for (int seat = 0; seat < 2; seat++)
            {
                foreach (CardInstance source in state.Players[seat].Board)
                {
                    if (source.IsSilenced || source.Zone != Zone.Board) continue;
                    CardDefinition definition = db.Get(source.DefinitionId);
                    if (definition.Type != CardType.MINION) continue;
                    foreach (RuleDefinition rule in definition.Rules)
                    {
                        if (!AuraCapabilities.Supports(definition.Type, rule)) continue;
                        Apply(state, db, source, (ModifyStatsAction)rule.Action);
                    }
                }
            }
        }

        private static void Apply(GameState state, ICardDatabase db, CardInstance source,
            ModifyStatsAction action)
        {
            foreach (CardInstance target in Targets(state, source, action.Target))
            {
                target.Enchantments.Add(new Enchantment
                {
                    Source = source.Id,
                    AttackDelta = action.AttackDelta,
                    HealthDelta = action.HealthDelta,
                    IsAura = true,
                });
                DerivedStats.Recompute(target, db.Get(target.DefinitionId));
            }
        }

        private static IEnumerable<CardInstance> Targets(GameState state, CardInstance source,
            TargetSpec target)
        {
            int sourceSeat = state.SeatOf(source.Controller);
            switch (target.Selector)
            {
                case "SELF":
                    yield return source;
                    yield break;
                case "FRIENDLY_MINIONS":
                    foreach (CardInstance card in state.Players[sourceSeat].Board) yield return card;
                    yield break;
                case "ENEMY_MINIONS":
                    foreach (CardInstance card in state.Players[1 - sourceSeat].Board) yield return card;
                    yield break;
                case "ALL_MINIONS":
                    foreach (CardInstance card in state.Players[0].Board) yield return card;
                    foreach (CardInstance card in state.Players[1].Board) yield return card;
                    yield break;
                case "ADJACENT":
                    var board = state.Players[sourceSeat].Board;
                    int index = board.IndexOf(source);
                    if (index > 0) yield return board[index - 1];
                    if (index >= 0 && index + 1 < board.Count) yield return board[index + 1];
                    yield break;
            }
        }
    }
}
