using System;
using System.Collections.Generic;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Editor
{
    /// <summary>
    /// Mutable working copy for the authoring window. Conversion is exhaustive so saving can only
    /// produce the same typed rule nodes consumed by the runtime and validator.
    /// </summary>
    public sealed class EditableCard
    {
        public string Id = "";
        public string Name = "";
        public CardType Type = CardType.MINION;
        public int Cost;
        public bool HasAttack; public int Attack;
        public bool HasHealth; public int Health;
        public bool HasDurability; public int Durability;
        public CardClass Class = CardClass.NEUTRAL;
        public Rarity Rarity = Rarity.COMMON;
        public List<Keyword> Keywords = new List<Keyword>();
        public List<EditableRule> Rules = new List<EditableRule>();
        public string CardText = "";
        public string FlavorText = "";
        public string ArtId = "";
        public SetId SetId = SetId.CORE;
        public bool Collectible = true;
        public bool HasTribe; public Tribe Tribe;
        public string FactionId = "";
        public bool RequiresEngineeringPrimitive;

        public static EditableCard FromDefinition(CardDefinition def) => new EditableCard
        {
            Id = def.Id,
            Name = def.Name,
            Type = def.Type,
            Cost = def.Cost,
            HasAttack = def.Attack.HasValue, Attack = def.Attack ?? 0,
            HasHealth = def.Health.HasValue, Health = def.Health ?? 1,
            HasDurability = def.Durability.HasValue, Durability = def.Durability ?? 1,
            Class = def.Class,
            Rarity = def.Rarity,
            Keywords = new List<Keyword>(def.Keywords),
            Rules = def.Rules.Select(EditableRule.FromDefinition).ToList(),
            CardText = def.CardText,
            FlavorText = def.FlavorText,
            ArtId = def.ArtId,
            SetId = def.SetId,
            Collectible = def.Collectible,
            HasTribe = def.Tribe.HasValue, Tribe = def.Tribe ?? default,
            FactionId = def.FactionId,
            RequiresEngineeringPrimitive = def.RequiresEngineeringPrimitive,
        };

        public CardDefinition ToDefinition() => new CardDefinition(
            id: Id, name: Name, type: Type, cost: Cost,
            attack: HasAttack ? Attack : (int?)null,
            health: HasHealth ? Health : (int?)null,
            durability: HasDurability ? Durability : (int?)null,
            @class: Class, rarity: Rarity,
            keywords: Keywords, rules: Rules.Select(rule => rule.ToDefinition()).ToList(),
            cardText: CardText, flavorText: FlavorText, artId: ArtId, setId: SetId,
            collectible: Collectible, tribe: HasTribe ? Tribe : (Tribe?)null,
            factionId: FactionId, requiresEngineeringPrimitive: RequiresEngineeringPrimitive);

        public EditableCard Clone() => FromDefinition(ToDefinition());
    }

    public enum EditableActivationKind { Triggered, Aura }
    public enum EditableConditionKind { Always, TurnOwner }
    public enum EditableLifetimeKind { SourceBound, OneShot, Finite, Permanent }
    public enum EditableRuleActionKind
    {
        Damage,
        Heal,
        ModifyStats,
        Reinforce,
        GrantKeyword,
        Destroy,
        ReturnToHand,
        Silence,
        Transform,
        Frost,
        TakeControl,
        Draw,
        Summon,
        AddToHand,
        Equip,
        GainMana,
        Backdraft,
        Unearth,
        GainArmor,
    }

    public sealed class EditableTarget
    {
        public string Selector = "ANY_CHARACTER";
        public bool HasChosenBy; public TargetChosenBy ChosenBy = TargetChosenBy.PLAYER;
        public bool HasCount; public int Count = 1;
        public bool HasDamagedFilter; public bool Damaged;
        public bool HasTribeFilter; public Tribe Tribe;
        public string Binding = "";
        public string DistinctFrom = "";

        public static EditableTarget FromDefinition(TargetSpec target) => new EditableTarget
        {
            Selector = target.Selector,
            HasChosenBy = target.ChosenBy.HasValue,
            ChosenBy = target.ChosenBy ?? TargetChosenBy.PLAYER,
            HasCount = target.Count.HasValue,
            Count = target.Count ?? 1,
            HasDamagedFilter = target.Filter?.Damaged.HasValue ?? false,
            Damaged = target.Filter?.Damaged ?? false,
            HasTribeFilter = target.Filter?.Tribe.HasValue ?? false,
            Tribe = target.Filter?.Tribe ?? default,
            Binding = target.Binding,
            DistinctFrom = target.DistinctFrom,
        };

        public TargetSpec ToDefinition()
        {
            TargetFilterDef? filter = HasDamagedFilter || HasTribeFilter
                ? new TargetFilterDef(HasDamagedFilter ? Damaged : (bool?)null,
                    HasTribeFilter ? Tribe : (Tribe?)null)
                : null;
            return new TargetSpec(Selector,
                HasChosenBy ? ChosenBy : (TargetChosenBy?)null,
                HasCount ? Count : (int?)null,
                filter,
                Binding,
                DistinctFrom);
        }
    }

    public sealed class EditableRule
    {
        public string Id = "rule_1";
        public EditableActivationKind ActivationKind = EditableActivationKind.Triggered;
        public Trigger Trigger = Trigger.ON_PLAY;
        public EditableConditionKind ConditionKind = EditableConditionKind.Always;
        public TurnOwner TurnOwnerScope = TurnOwner.CONTROLLER;
        public EditableLifetimeKind LifetimeKind = EditableLifetimeKind.OneShot;
        public int Uses = 1;
        public EditableRuleActionKind ActionKind = EditableRuleActionKind.Damage;

        public int Amount = 1;
        public int AttackDelta;
        public int HealthDelta;
        public int Count = 1;
        public string CardId = "";
        public Keyword Keyword;
        public ManaGainKind ManaKind = ManaGainKind.TEMPORARY;
        public EditableTarget Target = new EditableTarget();

        public bool IsTargeted => ActionKind == EditableRuleActionKind.Damage
            || ActionKind == EditableRuleActionKind.Heal
            || ActionKind == EditableRuleActionKind.ModifyStats
            || ActionKind == EditableRuleActionKind.Reinforce
            || ActionKind == EditableRuleActionKind.GrantKeyword
            || ActionKind == EditableRuleActionKind.Destroy
            || ActionKind == EditableRuleActionKind.ReturnToHand
            || ActionKind == EditableRuleActionKind.Silence
            || ActionKind == EditableRuleActionKind.Transform
            || ActionKind == EditableRuleActionKind.Frost
            || ActionKind == EditableRuleActionKind.TakeControl;

        public string ReferencedCardId => ActionKind == EditableRuleActionKind.Summon
            || ActionKind == EditableRuleActionKind.AddToHand
            || ActionKind == EditableRuleActionKind.Equip
            || ActionKind == EditableRuleActionKind.Transform
                ? CardId
                : "";

        public static EditableRule FromDefinition(RuleDefinition rule)
        {
            var editable = new EditableRule { Id = rule.Id.Value };
            if (rule.Activation is TriggeredActivation triggered)
            {
                editable.ActivationKind = EditableActivationKind.Triggered;
                editable.Trigger = triggered.Trigger;
            }
            else if (rule.Activation is AuraActivation)
            {
                editable.ActivationKind = EditableActivationKind.Aura;
            }
            else throw Unknown("activation", rule.Activation);

            if (rule.Condition is AlwaysCondition)
                editable.ConditionKind = EditableConditionKind.Always;
            else if (rule.Condition is TurnOwnerCondition turnOwner)
            {
                editable.ConditionKind = EditableConditionKind.TurnOwner;
                editable.TurnOwnerScope = turnOwner.Owner;
            }
            else throw Unknown("condition", rule.Condition);

            if (rule.Lifetime is SourceBoundLifetime)
                editable.LifetimeKind = EditableLifetimeKind.SourceBound;
            else if (rule.Lifetime is OneShotLifetime)
                editable.LifetimeKind = EditableLifetimeKind.OneShot;
            else if (rule.Lifetime is FiniteLifetime finite)
            {
                editable.LifetimeKind = EditableLifetimeKind.Finite;
                editable.Uses = finite.Uses;
            }
            else if (rule.Lifetime is PermanentLifetime)
                editable.LifetimeKind = EditableLifetimeKind.Permanent;
            else throw Unknown("lifetime", rule.Lifetime);

            editable.ReadAction(rule.Action);
            return editable;
        }

        public RuleDefinition ToDefinition() => new RuleDefinition(
            new RuleId(Id),
            ToActivation(),
            ToCondition(),
            ToLifetime(),
            ToAction());

        private RuleActivation ToActivation()
        {
            switch (ActivationKind)
            {
                case EditableActivationKind.Triggered: return new TriggeredActivation(Trigger);
                case EditableActivationKind.Aura: return AuraActivation.Instance;
                default: throw Unknown("editable activation", ActivationKind);
            }
        }

        private RuleCondition ToCondition()
        {
            switch (ConditionKind)
            {
                case EditableConditionKind.Always: return AlwaysCondition.Instance;
                case EditableConditionKind.TurnOwner: return new TurnOwnerCondition(TurnOwnerScope);
                default: throw Unknown("editable condition", ConditionKind);
            }
        }

        private RuleLifetime ToLifetime()
        {
            switch (LifetimeKind)
            {
                case EditableLifetimeKind.SourceBound: return SourceBoundLifetime.Instance;
                case EditableLifetimeKind.OneShot: return OneShotLifetime.Instance;
                case EditableLifetimeKind.Finite: return new FiniteLifetime(Uses);
                case EditableLifetimeKind.Permanent: return PermanentLifetime.Instance;
                default: throw Unknown("editable lifetime", LifetimeKind);
            }
        }

        private RuleAction ToAction()
        {
            TargetSpec target = Target.ToDefinition();
            switch (ActionKind)
            {
                case EditableRuleActionKind.Damage: return new DamageAction(Amount, target);
                case EditableRuleActionKind.Heal: return new HealAction(Amount, target);
                case EditableRuleActionKind.ModifyStats: return new ModifyStatsAction(AttackDelta, HealthDelta, target);
                case EditableRuleActionKind.Reinforce: return new ReinforceAction(target);
                case EditableRuleActionKind.GrantKeyword: return new GrantKeywordAction(Keyword, target);
                case EditableRuleActionKind.Destroy: return new DestroyAction(target);
                case EditableRuleActionKind.ReturnToHand: return new ReturnToHandAction(target);
                case EditableRuleActionKind.Silence: return new SilenceAction(target);
                case EditableRuleActionKind.Transform: return new TransformAction(CardId, target);
                case EditableRuleActionKind.Frost: return new FrostAction(target);
                case EditableRuleActionKind.TakeControl: return new TakeControlAction(target);
                case EditableRuleActionKind.Draw: return new DrawAction(Count);
                case EditableRuleActionKind.Summon: return new SummonAction(CardId, Count);
                case EditableRuleActionKind.AddToHand: return new AddToHandAction(CardId, Count);
                case EditableRuleActionKind.Equip: return new EquipAction(CardId);
                case EditableRuleActionKind.GainMana: return new GainManaAction(Amount, ManaKind);
                case EditableRuleActionKind.Backdraft: return new BackdraftAction(Amount);
                case EditableRuleActionKind.Unearth: return new UnearthAction(Count);
                case EditableRuleActionKind.GainArmor: return new GainArmorAction(Amount);
                default: throw Unknown("editable action", ActionKind);
            }
        }

        private void ReadAction(RuleAction action)
        {
            if (action is DamageAction damage) { ActionKind = EditableRuleActionKind.Damage; Amount = damage.Amount; ReadTarget(damage); }
            else if (action is HealAction heal) { ActionKind = EditableRuleActionKind.Heal; Amount = heal.Amount; ReadTarget(heal); }
            else if (action is ModifyStatsAction stats) { ActionKind = EditableRuleActionKind.ModifyStats; AttackDelta = stats.AttackDelta; HealthDelta = stats.HealthDelta; ReadTarget(stats); }
            else if (action is ReinforceAction reinforce) { ActionKind = EditableRuleActionKind.Reinforce; ReadTarget(reinforce); }
            else if (action is GrantKeywordAction grant) { ActionKind = EditableRuleActionKind.GrantKeyword; Keyword = grant.Keyword; ReadTarget(grant); }
            else if (action is DestroyAction destroy) { ActionKind = EditableRuleActionKind.Destroy; ReadTarget(destroy); }
            else if (action is ReturnToHandAction returnToHand) { ActionKind = EditableRuleActionKind.ReturnToHand; ReadTarget(returnToHand); }
            else if (action is SilenceAction silence) { ActionKind = EditableRuleActionKind.Silence; ReadTarget(silence); }
            else if (action is TransformAction transform) { ActionKind = EditableRuleActionKind.Transform; CardId = transform.CardId; ReadTarget(transform); }
            else if (action is FrostAction frost) { ActionKind = EditableRuleActionKind.Frost; ReadTarget(frost); }
            else if (action is TakeControlAction takeControl) { ActionKind = EditableRuleActionKind.TakeControl; ReadTarget(takeControl); }
            else if (action is DrawAction draw) { ActionKind = EditableRuleActionKind.Draw; Count = draw.Count; }
            else if (action is SummonAction summon) { ActionKind = EditableRuleActionKind.Summon; CardId = summon.CardId; Count = summon.Count; }
            else if (action is AddToHandAction addToHand) { ActionKind = EditableRuleActionKind.AddToHand; CardId = addToHand.CardId; Count = addToHand.Count; }
            else if (action is EquipAction equip) { ActionKind = EditableRuleActionKind.Equip; CardId = equip.CardId; }
            else if (action is GainManaAction mana) { ActionKind = EditableRuleActionKind.GainMana; Amount = mana.Amount; ManaKind = mana.Kind; }
            else if (action is BackdraftAction backdraft) { ActionKind = EditableRuleActionKind.Backdraft; Amount = backdraft.Amount; }
            else if (action is UnearthAction unearth) { ActionKind = EditableRuleActionKind.Unearth; Count = unearth.Count; }
            else if (action is GainArmorAction armor) { ActionKind = EditableRuleActionKind.GainArmor; Amount = armor.Amount; }
            else throw Unknown("action", action);
        }

        private void ReadTarget(TargetedRuleAction action)
            => Target = EditableTarget.FromDefinition(action.Target);

        private static InvalidOperationException Unknown(string family, object value)
            => new InvalidOperationException($"Unknown {family} node '{value.GetType().Name}'.");
    }
}
