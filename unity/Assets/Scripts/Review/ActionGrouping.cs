using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kingdom.Core.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Kingdom.Review
{
    /// <summary>
    /// Groups a frame's legal actions into the distinct PLAYS they represent, and sums the policy's
    /// probability over each.
    ///
    /// This used to be a question put to the reviewer — a "structural class" dropdown. It should
    /// never have been. Sam, looking at it: "i dont even know how to interpret these structure
    /// tags." He is right twice over: it is not a judgement about the play, and it is mechanical.
    /// Two copies of one card, or one card in slot 0 versus slot 1, are the same decision listed
    /// twice; the engine can see that, and asking a human to notice it on every screen is friction
    /// in exchange for nothing.
    ///
    /// It matters because it changes what the confidence NUMBER means. The review document's own
    /// example: 0.99477 on Storm Bolt Rider in slot 1 and 0.00523 on the same card in slot 0 is not
    /// a policy that hesitated between two plays — it is a policy that was certain, and split its
    /// certainty across two spellings of one play. Read raw, that understates its confidence and
    /// therefore understates the error when the play is wrong.
    ///
    /// GROUPED, NOT COLLAPSED. Members stay visible and the grouping is labelled, because "these
    /// two placements are equivalent" is true of most minions and false of some — adjacency, and
    /// anything that reads board position. The reviewer is the one who gets to disagree.
    /// </summary>
    public static class ActionGrouping
    {
        public static List<ActionGroup> Group(ReviewFrame frame, GameState state, ICardDatabase cards)
        {
            bool blind = frame.IsTargetAudit;
            var order = new List<string>();
            var groups = new Dictionary<string, ActionGroup>();

            for (int i = 0; i < frame.Actions.Count; i++)
            {
                ReviewAction action = frame.Actions[i];
                string key = KeyOf(action.Intent, state, out AliasKind kind);
                if (!groups.TryGetValue(key, out ActionGroup group))
                {
                    group = new ActionGroup { Alias = kind, Key = key, Label = action.Description };
                    groups[key] = group;
                    order.Add(key);
                }
                group.Members.Add(i);
                group.Probability += action.Probability;
                if (action.Chosen) group.ContainsChosen = true;
                if (action.Probability > group.TopProbability)
                {
                    group.TopProbability = action.Probability;
                    if (!blind) group.Label = action.Description;
                }
            }

            var result = new List<ActionGroup>(order.Count);
            foreach (string key in order)
            {
                ActionGroup group = groups[key];
                if (group.Members.Count < 2) group.Alias = AliasKind.None;
                result.Add(group);
            }
            if (blind)
                result.Sort((a, b) => string.CompareOrdinal(
                    BlindRank(frame, a.Key), BlindRank(frame, b.Key)));
            else
                result.Sort((a, b) => b.Probability.CompareTo(a.Probability));
            return result;
        }

        private static string BlindRank(ReviewFrame frame, string key)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(
                frame.MatchId + "\0" + frame.DecisionIndex.ToString(CultureInfo.InvariantCulture) + "\0" + key);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                var text = new StringBuilder(digest.Length * 2);
                foreach (byte value in digest) text.Append(value.ToString("x2"));
                return text.ToString();
            }
        }

        /// <summary>
        /// The identity of a PLAY, as opposed to the identity of a listed action.
        ///
        /// play_card keys on the card's DEFINITION and its targets, deliberately dropping the board
        /// slot and the particular copy in hand: those are the two spellings that produced every
        /// alias in the first hand pass. attack keys on the attacker's definition and current stats
        /// rather than its instance, so two identical undamaged minions swinging at the same target
        /// are one play — but a damaged one is not interchangeable with a healthy one, and does not
        /// merge.
        /// </summary>
        private static string KeyOf(JObject intent, GameState state, out AliasKind kind)
        {
            string type = (string)intent["$type"] ?? "?";
            var key = new StringBuilder(type);
            kind = AliasKind.None;

            switch (type)
            {
                case "play_card":
                {
                    key.Append('|').Append(DefinitionOf(state, (string)intent["card"]));
                    AppendTargets(key, intent);
                    kind = AliasKind.SameCardDifferentPlacement;
                    break;
                }
                case "attack":
                {
                    CardInstance attacker = Find(state, (string)intent["attacker"]);
                    key.Append('|').Append(attacker?.DefinitionId ?? -1)
                       .Append('/').Append(attacker?.CurrentAttack ?? -1)
                       .Append('/').Append(attacker?.CurrentHealth ?? -1)
                       .Append("|->").Append((string)intent["defender"] ?? "");
                    kind = AliasKind.InterchangeableAttacker;
                    break;
                }
                case "hero_power":
                    AppendTargets(key, intent);
                    break;
                default:
                    // EVERY OTHER TYPE KEYS ON ITS WHOLE INTENT. Keying an unrecognised action by
                    // $type alone is not the conservative choice — it MERGES actions that differ,
                    // which inflates the forced bucket and understates the choice on offer. Two
                    // real cases: unearth_choice differs only in chosenCardId (three offered cards
                    // read as one play) and mulligan differs only in toReplace. player is dropped
                    // because every action in one decision carries the same actor; end_turn and
                    // concede carry nothing else, so the type is still their whole identity.
                    AppendRemainingFields(key, intent);
                    break;
            }
            return key.ToString();
        }

        /// <summary>
        /// A TARGET HAS TWO WIRE SPELLINGS: a bare "i2", and {"instance":"i97","binding":"target"}.
        /// Both occur in this corpus. Keyed as written, the two never compare equal, so two
        /// spellings of one play stay split and the grouping silently under-merges — which is the
        /// exact failure this class exists to fix. Normalise the SHAPE to the instance id.
        ///
        /// KEEP THE BINDING, THOUGH. This used to drop it, which merged "target this minion" and
        /// "sacrifice this minion" into one play on 381 decisions in the current corpus. Sam's
        /// ruling, 2026-09-09: they are different, *by extension* — you must target a minion in
        /// order to sacrifice it, so a sacrifice is a target plus more, never the same play. That
        /// also makes this agree with DistinctPlayKey in Kingdom.Tools.NetSoak, which the
        /// exploration sampler mixes over; the two disagreeing about what a play IS would have
        /// meant the reviewer grading a grouping the sampler never used.
        /// </summary>
        private static void AppendTargets(StringBuilder key, JObject intent)
        {
            key.Append("|t:");
            if (intent["targets"] is JArray targets)
                foreach (JToken target in targets)
                    key.Append(TargetKey(target)).Append(',');
        }

        /// <summary>Every field of an intent except its type and its actor, in a stable order.</summary>
        private static void AppendRemainingFields(StringBuilder key, JObject intent)
        {
            var names = new List<string>();
            foreach (JProperty property in intent.Properties())
                if (property.Name != "$type" && property.Name != "player")
                    names.Add(property.Name);
            names.Sort(System.StringComparer.Ordinal);
            foreach (string name in names)
                key.Append('|').Append(name).Append('=').Append(intent[name].ToString(Formatting.None));
        }

        private static string TargetKey(JToken target)
        {
            if (target is JObject o)
            {
                string instance = (string)o["instance"] ?? "";
                string binding = (string)o["binding"] ?? "";
                return binding.Length == 0 ? instance : instance + "@" + binding;
            }
            // A bare spelling IS the empty binding — IntentJsonCodec.WriteTargets branches on
            // exactly Binding.Length == 0 — so it keys identically to an object that carries none.
            return (string)target ?? "";
        }

        private static int DefinitionOf(GameState state, string instanceId)
        {
            CardInstance card = Find(state, instanceId);
            return card?.DefinitionId ?? -1;
        }

        private static CardInstance Find(GameState state, string instanceId)
        {
            if (state == null || string.IsNullOrEmpty(instanceId)) return null;
            foreach (PlayerState player in state.Players)
            {
                foreach (CardInstance card in player.Hand) if (card.Id.ToString() == instanceId) return card;
                foreach (CardInstance card in player.Board) if (card.Id.ToString() == instanceId) return card;
            }
            return null;
        }
    }

    public enum AliasKind { None, SameCardDifferentPlacement, InterchangeableAttacker }

    public sealed class ActionGroup
    {
        public List<int> Members { get; } = new List<int>();
        public string Key { get; set; } = "";

        /// <summary>The policy's probability over this PLAY — the sum across its spellings. This is
        /// the number to judge certainty by; a per-action figure understates it.</summary>
        public double Probability { get; set; }

        public double TopProbability { get; set; }
        public string Label { get; set; } = "";
        public bool ContainsChosen { get; set; }
        public AliasKind Alias { get; set; }

        public bool IsAlias => Members.Count > 1;
        public bool IsConcede => string.Equals(Key, "concede", StringComparison.Ordinal);

        public string AliasNote =>
            !IsAlias ? ""
            : Alias == AliasKind.InterchangeableAttacker
                ? $"{Members.Count} interchangeable attackers"
                : $"{Members.Count} spellings of one play";

        public string Percent =>
            (Probability * 100d).ToString("0.000", CultureInfo.InvariantCulture) + "%";
    }
}
