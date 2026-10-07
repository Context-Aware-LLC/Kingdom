using System.Collections.Generic;
using System.Text;
using Emberfall.Core.Contracts;

namespace Emberfall.Policy
{
    /// <summary>
    /// The identity of a distinct PLAY, as opposed to the identity of one listed legal action.
    ///
    /// The legal-action list spells a single decision several ways: the same card into board slot 0
    /// or slot 1, two identical copies in hand, two interchangeable tokens swinging at one target.
    /// Measured on the temperature-4 step-560 corpus, 2,195 of 4,202 "alternate action" samples —
    /// 52.24% — collapse to the same play. Exploration keyed on raw indices therefore hands more
    /// probability to whichever play happens to have the most spellings, which is not a property of
    /// the game.
    ///
    /// This key exists so <see cref="CheckpointSeatPolicy"/> can mix exploration uniformly over
    /// PLAYS. It is a heuristic, not engine-semantic ground truth, and it is versioned
    /// (<see cref="SchemaVersion"/>) so a corpus records which rule produced its groups.
    ///
    /// WIRE SPELLING IS NOT A PROBLEM HERE, BY CONSTRUCTION. A target has two JSON spellings — a
    /// bare "i2" and {"instance":"i97","binding":"target"} — and both occur in this corpus (113,490
    /// bare and 2,653 object occurrences; 381 decisions carry the same instance in both shapes).
    /// Keyed from JSON they never compare equal and the grouping silently under-merges. Keyed from
    /// the typed <see cref="TargetRef"/> the distinction cannot arise: IntentJsonCodec.WriteTargets
    /// picks the spelling purely from <c>Binding.Length == 0</c>, so shape carries no information
    /// the typed value does not already hold.
    ///
    /// BINDING IS PRESERVED, deliberately. A non-default binding names a different ROLE for the
    /// target (a sacrifice is not a target), and
    /// decisions/emberfall-mixture-behavior-probability-and-play-key-contract-20260909 forbids
    /// collapsing semantically distinct non-default bindings unless the contract declares them
    /// equivalent. It does not. NOTE: the Unity reviewer's ActionGrouping normalises a target to its
    /// instance id and drops the binding, so it merges strictly more than this does. That divergence
    /// is real and is flagged rather than silently reconciled.
    /// </summary>
    internal static class DistinctPlayKey
    {
        /// <summary>
        /// Version of the grouping rule. Any change to what merges must bump this, because a corpus
        /// records it and two corpora grouped by different rules are not comparable.
        /// </summary>
        public const int SchemaVersion = 1;

        public static string Of(PlayerIntent intent, GameState state)
        {
            var key = new StringBuilder();
            switch (intent)
            {
                case PlayCardIntent play:
                    // The board slot and the particular copy in hand are dropped: those are the two
                    // spellings that produced every alias in the first hand pass over this corpus.
                    key.Append("play_card|").Append(DefinitionOf(state, play.Card));
                    AppendTargets(key, play.Targets);
                    break;

                case AttackIntent attack:
                    {
                        // Keyed on the attacker's definition and CURRENT stats, not its instance: two
                        // identical undamaged minions hitting one target are one play, but a damaged one
                        // is not interchangeable with a healthy one and must not merge.
                        CardInstance? attacker = Find(state, attack.Attacker);
                        key.Append("attack|")
                           .Append(attacker?.DefinitionId ?? -1).Append('/')
                           .Append(attacker?.CurrentAttack ?? -1).Append('/')
                           .Append(attacker?.CurrentHealth ?? -1)
                           .Append("|->");
                        AppendTarget(key, attack.Defender);
                        break;
                    }

                case HeroPowerIntent power:
                    key.Append("hero_power");
                    AppendTargets(key, power.Targets);
                    break;

                case EndTurnIntent:
                    // end_turn is a genuine distinct choice, not a trivial filler: taking the only
                    // available play versus passing measures restraint and changes outcomes. See
                    // decisions/emberfall-normalized-entropy-vs-novel-action-analysis-20260909.
                    key.Append("end_turn");
                    break;

                case ConcedeIntent:
                    key.Append("concede");
                    break;

                case UnearthChoiceIntent unearth:
                    // ChoiceSequence is provenance and is constant across one decision's actions;
                    // ChosenCardId is the whole choice. Keying unearth by its type alone merged
                    // three offered cards into one row in the reviewer and cost two mis-graded
                    // states before it was found.
                    key.Append("unearth_choice|").Append(unearth.ChosenCardId);
                    break;

                case MulliganIntent mulligan:
                    {
                        key.Append("mulligan|");
                        var ids = new List<string>(mulligan.ToReplace.Count);
                        foreach (InstanceId id in mulligan.ToReplace) ids.Add(id.ToString());
                        ids.Sort(System.StringComparer.Ordinal); // a replacement set has no order
                        foreach (string id in ids) key.Append(id).Append(',');
                        break;
                    }

                default:
                    // EVERY OTHER TYPE KEYS ON ITS WHOLE VALUE. Merging an unrecognised action into
                    // a coarse bucket is not the conservative choice — it inflates the forced bucket
                    // and understates the choice on offer, which is the exact bias this key exists
                    // to remove. Records give a structural ToString, so a new intent shape is
                    // distinct by default rather than silently merged.
                    key.Append(intent.ToString());
                    break;
            }
            return key.ToString();
        }

        /// <summary>
        /// Which play each listed action belongs to, plays numbered in order of first appearance.
        /// The numbering is part of what a capture records: the corpus builder recomputes it from
        /// this rule and compares the arrays as written.
        /// </summary>
        public static int[] GroupsOf(IReadOnlyList<PlayerIntent> actions, GameState state)
        {
            var groups = new int[actions.Count];
            var byKey = new Dictionary<string, int>(actions.Count, System.StringComparer.Ordinal);
            for (int i = 0; i < actions.Count; i++)
            {
                string key = Of(actions[i], state);
                if (!byKey.TryGetValue(key, out int play))
                {
                    play = byKey.Count;
                    byKey.Add(key, play);
                }
                groups[i] = play;
            }
            return groups;
        }

        private static void AppendTargets(StringBuilder key, IReadOnlyList<TargetRef> targets)
        {
            key.Append("|t:");
            // Target ORDER is preserved: ReanimateHandler enters and triggers each target in
            // sequence, so two orderings of the same set are not the same play.
            foreach (TargetRef target in targets)
            {
                AppendTarget(key, target);
                key.Append(',');
            }
        }

        private static void AppendTarget(StringBuilder key, TargetRef target)
        {
            key.Append(target.Instance.ToString());
            if (target.Binding.Length != 0) key.Append('@').Append(target.Binding);
        }

        private static int DefinitionOf(GameState state, InstanceId instance)
            => Find(state, instance)?.DefinitionId ?? -1;

        private static CardInstance? Find(GameState state, InstanceId instance)
        {
            if (instance.Value == 0) return null;
            foreach (PlayerState player in state.Players)
            {
                foreach (CardInstance card in player.Hand) if (card.Id == instance) return card;
                foreach (CardInstance card in player.Board) if (card.Id == instance) return card;
                if (player.Weapon is not null && player.Weapon.Id == instance) return player.Weapon;
            }
            return null;
        }
    }
}
