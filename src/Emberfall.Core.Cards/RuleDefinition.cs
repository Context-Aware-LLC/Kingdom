using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>A stable, card-scoped identity for one authored rule.</summary>
    public readonly struct RuleId : IEquatable<RuleId>
    {
        public string Value { get; }

        public RuleId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("A rule id cannot be empty.", nameof(value));
            Value = value;
        }

        public bool Equals(RuleId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is RuleId other && Equals(other);
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
        public override string ToString() => Value;
        public static bool operator ==(RuleId left, RuleId right) => left.Equals(right);
        public static bool operator !=(RuleId left, RuleId right) => !left.Equals(right);
    }

    /// <summary>
    /// One normalized rule. Activation, condition, lifetime and action are independent closed
    /// hierarchies, so an action never carries fields belonging to another action.
    /// </summary>
    public sealed class RuleDefinition
    {
        public RuleId Id { get; }
        public RuleActivation Activation { get; }
        public RuleCondition Condition { get; }
        public RuleLifetime Lifetime { get; }
        public RuleAction Action { get; }

        public RuleDefinition(RuleId id, RuleActivation activation, RuleCondition condition,
            RuleLifetime lifetime, RuleAction action)
        {
            Id = id;
            Activation = activation ?? throw new ArgumentNullException(nameof(activation));
            Condition = condition ?? throw new ArgumentNullException(nameof(condition));
            Lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            Action = action ?? throw new ArgumentNullException(nameof(action));
        }
    }

    public abstract class RuleActivation { private protected RuleActivation() { } }

    public sealed class TriggeredActivation : RuleActivation
    {
        public Trigger Trigger { get; }
        public TriggeredActivation(Trigger trigger) => Trigger = trigger;
    }

    public sealed class AuraActivation : RuleActivation
    {
        public static readonly AuraActivation Instance = new AuraActivation();
        private AuraActivation() { }
    }

    public abstract class RuleCondition { private protected RuleCondition() { } }

    public sealed class AlwaysCondition : RuleCondition
    {
        public static readonly AlwaysCondition Instance = new AlwaysCondition();
        private AlwaysCondition() { }
    }

    public sealed class TurnOwnerCondition : RuleCondition
    {
        public TurnOwner Owner { get; }
        public TurnOwnerCondition(TurnOwner owner) => Owner = owner;
    }

    /// <summary>
    /// Holds while the controller has a minion of <see cref="Tribe"/> on the board — the
    /// "If you control a Construct, …" gate.
    ///
    /// <para><b><see cref="ExcludesSource"/> is redundant with the card, and is authored anyway.</b>
    /// A minion is already on the board when its own <c>ON_PLAY</c> resolves (<c>EnterBoard</c> runs
    /// before <c>RaiseSelf</c>), so a Construct asking whether you control a Construct would answer
    /// itself and print a condition it never has. Excluding the source fixes the behaviour and
    /// changes the WORDING — "another Construct" — and
    /// <see cref="CardTextGenerator.DescribeConditionPrefix"/> is handed a condition and nothing
    /// else, because the parser reads a clause back by re-rendering candidates. So the flag that
    /// picks the word has to live on the condition rather than be derived from the carrier at print
    /// time. <c>CardValidator</c> C29 pins it to the one value the card allows, which is what keeps
    /// a redundant field from becoming a second, disagreeing opinion.</para>
    ///
    /// <para>Not authorable on <c>ON_TURN_START</c>, which C22 reserves for <c>TURN_OWNER</c>, nor
    /// under an <c>AURA</c>, which <c>AuraCapabilities</c> restricts to <c>ALWAYS</c>.</para>
    /// </summary>
    public sealed class ControlsCondition : RuleCondition
    {
        public Tribe Tribe { get; }

        /// <summary>Whether the rule's own source is left out of the count.</summary>
        public bool ExcludesSource { get; }

        public ControlsCondition(Tribe tribe, bool excludesSource)
        {
            if (tribe == Tribe.NONE)
                throw new ArgumentOutOfRangeException(nameof(tribe),
                    "A CONTROLS condition names a tribe to look for; NONE is the absence of one.");
            Tribe = tribe;
            ExcludesSource = excludesSource;
        }
    }

    /// <summary>
    /// Holds only when this run's COST was paid — the gate a cost prefix puts over the effects it
    /// buys.
    ///
    /// <para><b>Why a cost needs a condition at all</b> (Sam, 2026-08-22). A flag on the cost's own
    /// target says whether the pick must be payable; it can never reach the SIBLING rule the cost
    /// pays for, and suppressing that sibling is the entire point of an unpaid cost. So the price
    /// and the payoff are two rules, and this is the wire between them.</para>
    ///
    /// <para><b>It names no cost, because the tie is not authored</b> (Sam, 2026-08-22: "no cost
    /// will be printed on a card unless it is tied to the action adjacent to it"). A cost always
    /// buys the run beneath it, so the cost this reads is simply the one earlier in the same
    /// trigger's rule order. That is why there is no binding here to keep in step with anything.</para>
    ///
    /// <para><b>Inert on a REQUIRED cost, and deliberately still written.</b> A required cost is
    /// paid whenever the card was playable at all, so the gate never closes; it earns its keep on an
    /// OPTIONAL one and on a MINION or WEAPON whose optional cost found nothing to pay with. Writing
    /// it either way keeps the row saying what the engine will do rather than what it usually does.</para>
    ///
    /// <para>RETIREMENT TRIGGER: this reads ONE cost per trigger, which <c>CardValidator</c> C28
    /// enforces. A card wanting two costs needs this to name which one it waits on — at which point
    /// adjacency stops being sufficient and a binding comes back.</para>
    /// </summary>
    public sealed class CostPaidCondition : RuleCondition
    {
        public static readonly CostPaidCondition Instance = new CostPaidCondition();
        private CostPaidCondition() { }
    }

    public abstract class RuleLifetime { private protected RuleLifetime() { } }

    public sealed class SourceBoundLifetime : RuleLifetime
    {
        public static readonly SourceBoundLifetime Instance = new SourceBoundLifetime();
        private SourceBoundLifetime() { }
    }

    public sealed class OneShotLifetime : RuleLifetime
    {
        public static readonly OneShotLifetime Instance = new OneShotLifetime();
        private OneShotLifetime() { }
    }

    public sealed class FiniteLifetime : RuleLifetime
    {
        public int Uses { get; }
        public FiniteLifetime(int uses) => Uses = uses;
    }

    public sealed class PermanentLifetime : RuleLifetime
    {
        public static readonly PermanentLifetime Instance = new PermanentLifetime();
        private PermanentLifetime() { }
    }

    public abstract class RuleAction
    {
        private protected RuleAction() { }

        /// <summary>
        /// Guards a card id an action names. <b>This replaces a null check, and it is not the same
        /// guard.</b> A string id could only be absent; a numeric one has a value that LOOKS
        /// present and resolves to nothing — <c>default(int)</c> is 0, and 0 is below every band,
        /// so an unset field would otherwise travel as far as the database lookup before failing
        /// as "no card 0", naming neither the rule nor the row it came from.
        /// </summary>
        private protected static int Require(int cardId)
            => cardId > 0
                ? cardId
                : throw new ArgumentOutOfRangeException(nameof(cardId),
                    $"A rule action names card {cardId}. Card ids start at 1, so this is an unset " +
                    "field rather than a card.");
    }

    public abstract class TargetedRuleAction : RuleAction
    {
        public TargetSpec Target { get; }

        private protected TargetedRuleAction(TargetSpec target)
            => Target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public sealed class DamageAction : TargetedRuleAction
    {
        public int Amount { get; }
        public DamageAction(int amount, TargetSpec target) : base(target) => Amount = amount;
    }

    public sealed class HealAction : TargetedRuleAction
    {
        public int Amount { get; }
        public HealAction(int amount, TargetSpec target) : base(target) => Amount = amount;
    }

    public sealed class ModifyStatsAction : TargetedRuleAction
    {
        public int AttackDelta { get; }
        public int HealthDelta { get; }
        public ModifyStatsAction(int attackDelta, int healthDelta, TargetSpec target) : base(target)
        {
            AttackDelta = attackDelta;
            HealthDelta = healthDelta;
        }
    }

    public sealed class GrantKeywordAction : TargetedRuleAction
    {
        public Keyword Keyword { get; }
        public GrantKeywordAction(Keyword keyword, TargetSpec target) : base(target) => Keyword = keyword;
    }

    public sealed class DestroyAction : TargetedRuleAction
    {
        public DestroyAction(TargetSpec target) : base(target) { }
    }

    public sealed class ReturnToHandAction : TargetedRuleAction
    {
        public ReturnToHandAction(TargetSpec target) : base(target) { }
    }

    public sealed class SilenceAction : TargetedRuleAction
    {
        public SilenceAction(TargetSpec target) : base(target) { }
    }

    public sealed class TransformAction : TargetedRuleAction
    {
        public int CardId { get; }
        public TransformAction(int cardId, TargetSpec target) : base(target)
            => CardId = Require(cardId);
    }

    public sealed class FrostAction : TargetedRuleAction
    {
        public FrostAction(TargetSpec target) : base(target) { }
    }

    /// <summary>Transfers a minion or equipped weapon to the rule controller.</summary>
    public sealed class TakeControlAction : TargetedRuleAction
    {
        public TakeControlAction(TargetSpec target) : base(target) { }
    }

    public sealed class DrawAction : RuleAction
    {
        public int Count { get; }
        public DrawAction(int count) => Count = count;
    }

    public sealed class SummonAction : RuleAction
    {
        public int CardId { get; }
        public int Count { get; }
        public SummonAction(int cardId, int count)
        {
            CardId = Require(cardId);
            Count = count;
        }
    }

    public sealed class AddToHandAction : RuleAction
    {
        public int CardId { get; }
        public int Count { get; }
        public AddToHandAction(int cardId, int count)
        {
            CardId = Require(cardId);
            Count = count;
        }
    }

    public sealed class EquipAction : RuleAction
    {
        public int CardId { get; }
        public EquipAction(int cardId)
            => CardId = Require(cardId);
    }

    public enum ManaGainKind { TEMPORARY, PERMANENT_FILLED, PERMANENT_EMPTY }

    public sealed class GainManaAction : RuleAction
    {
        public int Amount { get; }
        public ManaGainKind Kind { get; }
        public GainManaAction(int amount, ManaGainKind kind)
        {
            Amount = amount;
            Kind = kind;
        }
    }

    public sealed class BackdraftAction : RuleAction
    {
        public int Amount { get; }
        public BackdraftAction(int amount) => Amount = amount;
    }

    public sealed class GainArmorAction : RuleAction
    {
        public int Amount { get; }
        public GainArmorAction(int amount) => Amount = amount;
    }

    public sealed class UnearthAction : RuleAction
    {
        public int Count { get; }
        public UnearthAction(int count) => Count = count;
    }

    /// <summary>Exhaustive semantics shared by validation, runtime targeting and presentation.</summary>
    public static class RuleSemantics
    {
        public static Trigger TriggerOf(RuleDefinition rule)
        {
            if (rule.Activation is TriggeredActivation triggered) return triggered.Trigger;
            if (rule.Activation is AuraActivation) return Trigger.AURA;
            throw new InvalidOperationException(
                $"Unknown activation node '{rule.Activation.GetType().Name}'.");
        }

        public static bool ActivatesOn(RuleDefinition rule, Trigger trigger)
            => TriggerOf(rule) == trigger;

        public static TargetSpec? TargetOf(RuleAction action)
            => action is TargetedRuleAction targeted ? targeted.Target : null;
    }

    public static class RuleActionCapabilities
    {
        public static bool ConsumesTargets(RuleAction action) => action is TargetedRuleAction;

        /// <summary>
        /// Whether an action accepts a candidate in <paramref name="zone"/> of
        /// <paramref name="type"/>. A null type is the hero, and only the hero -- see
        /// <see cref="TargetTypes.Hero"/>.
        ///
        /// <para>The two axes are checked independently, which is the point of unfusing them: this
        /// asks "is that zone allowed AND is that type allowed", where the fused predicate could
        /// only ask "is that NAME allowed" -- and so could not be told about a type outside the zone
        /// its name was minted for.</para>
        /// </summary>
        public static bool Allows(RuleAction action, Zone zone, CardType? type)
        {
            TargetShape allowed = AllowedShape(action);
            TargetTypes typeBit = type.HasValue ? TargetAxes.OfCard(type.Value) : TargetTypes.Hero;
            return (allowed.Zones & TargetAxes.OfZone(zone)) != 0 && (allowed.Types & typeBit) != 0;
        }

        public static bool HasCompatibleTarget(RuleAction action, string selector,
            CardType sourceType, Trigger trigger)
        {
            if (!ConsumesTargets(action)) return false;
            TargetShape selected = selector == "SELF"
                ? SourceShape(sourceType, trigger)
                : selector == "TRIGGER_SOURCE"
                    ? TriggerSourceShape(sourceType, trigger)
                    : SelectorShape(selector);
            return AllowedShape(action).Intersects(selected);
        }

        /// <summary>
        /// Whether a selector names exactly ONE thing, so there is never a second one to tell it
        /// apart from. The CARDINALITY axis — read off the query, where it is a consequence of
        /// side and zone rather than a fourth list of names. See <see cref="TargetQuery.IsUnique"/>,
        /// which carries the ruling and the retirement trigger.
        ///
        /// <para>WHAT IT IS FOR. A <c>distinctFrom</c> asks the resolver to exclude a target already
        /// chosen. Against a unique selector it can never exclude anything, so a card authored that
        /// way is asking for something the game cannot express. Sam ruled it out on 2026-08-12
        /// (issue #143): "there is no other weapon. There isn't another one." Every phrasing the
        /// renderer could offer — "your other weapon", "another enemy hero" — asserts a second one
        /// exists, so the wording has no right answer and the data shape is what gets rejected
        /// instead.</para>
        ///
        /// <para><c>TRIGGER_SOURCE</c> is here for the same reason and not because #143 listed it:
        /// whatever fired the trigger is one entity, so "another the trigger source" is the identical
        /// nonsense with a different noun. Leaving it out would have shipped the rule with a hole in
        /// exactly the shape it exists to close.</para>
        ///
        /// <para>RETIREMENT TRIGGER: every entry here is a claim that the game has at most one of
        /// that thing. A DUAL-WIELD system retires <c>FRIENDLY_WEAPON</c> and <c>ENEMY_WEAPON</c>
        /// from this list, and the card text generator's matching throw with them — at which point
        /// "your other weapon" stops being nonsense and becomes the correct phrasing, so
        /// <c>CardTextGenerator.Another</c> needs its <c>"your "</c> branch and the
        /// <c>"your other "</c> determiner back. A second hero does the same for the hero pair.</para>
        /// </summary>
        public static bool IsUniqueSelector(string selector)
            => TargetQueries.TryGet(selector, out TargetQuery query) && query.IsUnique;

        /// <summary>
        /// A rectangle in (zone x type) space: the set of candidates a selector produces, or the set
        /// an action accepts. <b>Every relation below is a rectangle</b> -- no member needed a
        /// per-cell exception, which is the evidence that these two axes are genuinely independent
        /// rather than a decomposition imposed on them after the fact.
        /// </summary>
        public readonly struct TargetShape
        {
            public TargetZones Zones { get; }
            public TargetTypes Types { get; }

            public TargetShape(TargetZones zones, TargetTypes types)
            {
                Zones = zones;
                Types = types;
            }

            public static TargetShape None => new TargetShape(TargetZones.None, TargetTypes.None);

            /// <summary>Non-empty overlap. Both axes must meet: two rectangles that share a zone but
            /// no type describe disjoint candidate sets.</summary>
            public bool Intersects(TargetShape other)
                => (Zones & other.Zones) != 0 && (Types & other.Types) != 0;
        }

        public static TargetShape AllowedShape(RuleAction action)
        {
            switch (action)
            {
                case DamageAction _:
                case HealAction _:
                case FrostAction _:
                    return new TargetShape(TargetZones.Board, TargetTypes.Hero | TargetTypes.Minion);
                case ModifyStatsAction _:
                case GrantKeywordAction _:
                    return new TargetShape(TargetZones.Board, TargetTypes.Minion);
                // The hand is type-agnostic here and always was: the fused `HandCard` named a zone
                // and said nothing about type, so destroying a spell in hand was already legal.
                // Unfusing changes nothing on this side. What it opens is the QUERY side, where a
                // hand selector can eventually say "and only weapons".
                case DestroyAction _:
                    return new TargetShape(TargetZones.Board | TargetZones.Weapon | TargetZones.Hand,
                        TargetTypes.Any);
                case ReturnToHandAction _:
                    return new TargetShape(
                        TargetZones.Board | TargetZones.Weapon | TargetZones.Deck | TargetZones.Graveyard,
                        TargetTypes.AnyCard);
                case SilenceAction _:
                case TransformAction _:
                case TakeControlAction _:
                    return new TargetShape(TargetZones.Board | TargetZones.Weapon,
                        TargetTypes.Minion | TargetTypes.Weapon);
                case DrawAction _:
                case SummonAction _:
                case AddToHandAction _:
                case EquipAction _:
                case GainManaAction _:
                case GainArmorAction _:
                case BackdraftAction _:
                case UnearthAction _:
                    return TargetShape.None;
                default:
                    throw new InvalidOperationException(
                        $"Rule action '{action.GetType().Name}' is absent from the target capability relation.");
            }
        }

        /// <summary>
        /// A selector's (zone x type) rectangle, projected off its query. This was the SECOND of
        /// three enumerations of the same 26 names — a switch that restated, per name, what the
        /// criteria already say. It cannot disagree with the resolver any more because it is not a
        /// second statement of anything.
        /// </summary>
        private static TargetShape SelectorShape(string selector)
            => TargetQueries.TryGet(selector, out TargetQuery query)
                ? new TargetShape(query.Zones, query.Types)
                : TargetShape.None;

        private static TargetShape SourceShape(CardType sourceType, Trigger trigger)
        {
            // A rattle resolves from the graveyard, whatever the carrier was.
            if (trigger == Trigger.ON_DEATH)
                return new TargetShape(TargetZones.Graveyard, TargetTypes.AnyCard);
            switch (sourceType)
            {
                case CardType.MINION:
                    return new TargetShape(TargetZones.Board, TargetTypes.Minion);
                case CardType.WEAPON:
                    return new TargetShape(TargetZones.Weapon, TargetTypes.Weapon);
                case CardType.SPELL:
                    return new TargetShape(TargetZones.Graveyard, TargetTypes.AnyCard);
                default:
                    return TargetShape.None;
            }
        }

        private static TargetShape TriggerSourceShape(CardType sourceType, Trigger trigger)
        {
            switch (trigger)
            {
                case Trigger.ON_FRIENDLY_SUMMON:
                case Trigger.ON_DAMAGED:
                case Trigger.ON_HEAL:
                    return new TargetShape(TargetZones.Board, TargetTypes.Minion);
                case Trigger.ON_SPELL_CAST:
                case Trigger.ON_DEATH:
                    return new TargetShape(TargetZones.Graveyard, TargetTypes.AnyCard);
                case Trigger.ON_ATTACK:
                    return new TargetShape(TargetZones.Weapon, TargetTypes.Weapon);
                case Trigger.ON_PLAY:
                case Trigger.ON_CAST:
                    return SourceShape(sourceType, trigger);
                default:
                    return TargetShape.None;
            }
        }
    }

}
