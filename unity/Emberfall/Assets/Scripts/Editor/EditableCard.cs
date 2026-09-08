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
        public int Id;
        public string Handle = "";
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
        public List<EditableSoundEffect> SoundEffects = new List<EditableSoundEffect>();
        public string Text = "";
        public string FlavorText = "";
        public string Tooltip = "";
        public string ArtId = "";
        public SetId SetId = SetId.CORE;
        public bool Collectible = true;
        public bool HasTribe; public Tribe Tribe;
        public string FactionId = "";

        public static EditableCard FromDefinition(CardDefinition def) => new EditableCard
        {
            Id = def.Id,
            Handle = def.Handle,
            Name = def.Name,
            Type = def.Type,
            Cost = def.Cost,
            HasAttack = def.Attack.HasValue,
            Attack = def.Attack ?? 0,
            HasHealth = def.Health.HasValue,
            Health = def.Health ?? 1,
            HasDurability = def.Durability.HasValue,
            Durability = def.Durability ?? 1,
            Class = def.Class,
            Rarity = def.Rarity,
            Keywords = new List<Keyword>(def.Keywords),
            Rules = def.Rules.Select(EditableRule.FromDefinition).ToList(),
            SoundEffects = def.SoundEffects.Select(EditableSoundEffect.FromDefinition).ToList(),
            Text = def.Text,
            FlavorText = def.FlavorText,
            Tooltip = def.Tooltip,
            ArtId = def.ArtId,
            SetId = def.SetId,
            Collectible = def.Collectible,
            HasTribe = def.Tribe.HasValue,
            Tribe = def.Tribe ?? default,
            FactionId = def.FactionId,
        };

        public CardDefinition ToDefinition() => new CardDefinition(
            id: Id, handle: Handle, name: Name, type: Type, cost: Cost,
            attack: HasAttack ? Attack : (int?)null,
            health: HasHealth ? Health : (int?)null,
            durability: HasDurability ? Durability : (int?)null,
            @class: Class, rarity: Rarity,
            keywords: Keywords, rules: Rules.Select(rule => rule.ToDefinition()).ToList(),
            flavorText: FlavorText, artId: ArtId, setId: SetId,
            collectible: Collectible, tribe: HasTribe ? Tribe : (Tribe?)null,
            factionId: FactionId, tooltip: Tooltip, text: Text,
            soundEffects: SoundEffects.Select(sound => sound.ToDefinition()).ToList());

        public EditableCard Clone() => FromDefinition(ToDefinition());
    }

    public enum EditableActivationKind { Triggered, Aura }

    public sealed class EditableSoundEffect
    {
        public string SoundId = "";
        public Trigger Trigger = Trigger.ON_PLAY;

        public static EditableSoundEffect FromDefinition(CardSoundEffect sound) =>
            new EditableSoundEffect { SoundId = sound.SoundId, Trigger = sound.Trigger };

        public CardSoundEffect ToDefinition() => new CardSoundEffect(SoundId, Trigger);
    }

    public enum EditableConditionKind { Always, TurnOwner, CostPaid, Controls }
    public enum EditableLifetimeKind { SourceBound, OneShot, Finite, Permanent }
    public enum EditableRuleActionKind
    {
        Damage,
        Heal,
        ModifyStats,
        GrantKeyword,
        Destroy,
        ReturnToHand,
        Reanimate,
        Silence,
        Transform,
        Frost,
        Drown,
        Copy,
        TakeControl,
        TriggerToll,
        Draw,
        Summon,
        AddToHand,
        Equip,
        GainMana,
        Backdraft,
        Unearth,
        GainArmor,
    }

    /// <summary>
    /// One axis a target can be narrowed by, as the authoring window holds it.
    ///
    /// <para>The window edits a LIST of these rather than a fixed row of "has x" toggles, because
    /// the toggles stopped scaling at two and <c>TargetFilterDef</c> is open-ended. Adding an axis
    /// is now a member here, an arm in <c>EditableFilter.Apply</c>, an arm in
    /// <c>EditableFilter.From</c>, and a value field in the window's switch — no new widget, no new
    /// pair of parallel fields.</para>
    /// </summary>
    public enum EditableFilterKind
    {
        Damaged,
        Tribe,
        MaxCost,
        Keyword,
    }

    /// <summary>One authored filter clause: which axis, and the value it carries.</summary>
    /// <remarks>
    /// Every axis keeps its own field rather than sharing one boxed value, so switching the
    /// dropdown back and forth does not destroy what the other arms held. Only the arm the Kind
    /// names is read.
    ///
    /// <para><b>Negate has no field of its own for <c>Damaged</c></b> — that axis is already a
    /// boolean, so the window edits it directly ("damaged" vs. "undamaged") instead of doubling up
    /// a negate bit that would only ever restate the same choice (Sam, 2026-09-05).</para>
    /// </remarks>
    public sealed class EditableFilter
    {
        public EditableFilterKind Kind = EditableFilterKind.Damaged;
        public bool Damaged = true;
        public Tribe Tribe = Tribe.CONSTRUCT;
        public bool NegateTribe;
        public int MaxCost = 1;
        public bool NegateCost;
        public Keyword Keyword = Keyword.SHROUD;
        public bool NegateKeyword;

        /// <summary>Unfolds a definition-side filter into one clause per axis it carries.</summary>
        public static List<EditableFilter> From(TargetFilterDef? filter)
        {
            var clauses = new List<EditableFilter>();
            if (filter == null) return clauses;
            if (filter.Damaged is { } damaged)
                clauses.Add(new EditableFilter { Kind = EditableFilterKind.Damaged, Damaged = damaged });
            if (filter.Tribe is { } tribe)
                clauses.Add(new EditableFilter
                { Kind = EditableFilterKind.Tribe, Tribe = tribe, NegateTribe = filter.NegateTribe });
            if (filter.MaxCost is { } maxCost)
                clauses.Add(new EditableFilter
                { Kind = EditableFilterKind.MaxCost, MaxCost = maxCost, NegateCost = filter.NegateCost });
            if (filter.Keyword is { } keyword)
                clauses.Add(new EditableFilter
                { Kind = EditableFilterKind.Keyword, Keyword = keyword, NegateKeyword = filter.NegateKeyword });
            return clauses;
        }

        /// <summary>
        /// Folds the clauses back into the one definition-side filter, or null when there are none.
        /// </summary>
        /// <remarks>
        /// A repeated axis is not an error to report here — the later clause simply wins, which is
        /// what the single-valued target shape can express. The window is what keeps a second one
        /// from being added.
        /// </remarks>
        public static TargetFilterDef? Fold(List<EditableFilter> clauses)
        {
            if (clauses == null || clauses.Count == 0) return null;
            bool? damaged = null; Tribe? tribe = null; bool negateTribe = false;
            int? maxCost = null; bool negateCost = false;
            Keyword? keyword = null; bool negateKeyword = false;
            foreach (EditableFilter clause in clauses)
                switch (clause.Kind)
                {
                    case EditableFilterKind.Damaged: damaged = clause.Damaged; break;
                    case EditableFilterKind.Tribe: tribe = clause.Tribe; negateTribe = clause.NegateTribe; break;
                    case EditableFilterKind.MaxCost: maxCost = clause.MaxCost; negateCost = clause.NegateCost; break;
                    case EditableFilterKind.Keyword:
                        keyword = clause.Keyword; negateKeyword = clause.NegateKeyword; break;
                }
            return new TargetFilterDef(damaged, tribe, negateTribe, maxCost, negateCost, keyword, negateKeyword);
        }
    }

    public sealed class EditableTarget
    {
        public string Selector = "ANY_CHARACTER";
        // One term, always written — so there is no "has a selection" toggle to leave off. The
        // count IS the term: "every candidate" is the selector's own maximum rather than a third
        // mode, so there is no longer a box that means nothing under one of the modes.
        public int SelectionCount = 1;
        public bool RandomSelection;
        public List<EditableFilter> Filters = new List<EditableFilter>();
        public string Binding = "";
        public string DistinctFrom = "";

        /// <summary>
        /// Whether this pick is the card's PRICE, and whether the card is playable without paying
        /// it. The one thing an author states that no sentence can be read to recover — see
        /// <see cref="CostKind"/>.
        ///
        /// <para><b>This field going missing is what made the bug worth naming.</b> Until
        /// 2026-08-22 <see cref="ToDefinition"/> simply did not pass the flag, so opening any of the
        /// seven cost cards in this window and saving it silently deleted the cost — the card kept
        /// its sentence and quietly became playable with nothing to sacrifice. A field the round
        /// trip drops is invisible in every direction except the one nobody looks at.</para>
        /// </summary>
        public CostKind Cost = CostKind.None;

        public static EditableTarget FromDefinition(TargetSpec target) => new EditableTarget
        {
            Selector = target.Selector,
            SelectionCount = target.Selection.Count,
            RandomSelection = target.Selection.IsRandom,
            Filters = EditableFilter.From(target.Filter),
            Binding = target.Binding,
            DistinctFrom = target.DistinctFrom,
            Cost = target.Cost,
        };

        public TargetSpec ToDefinition()
        {
            TargetFilterDef? filter = EditableFilter.Fold(Filters);
            TargetSelection selection = RandomSelection
                ? TargetSelection.RandomOf(SelectionCount)
                : TargetSelection.Of(SelectionCount);
            return new TargetSpec(Selector, selection, filter, Binding, DistinctFrom, Cost);
        }
    }

    public sealed class EditableRule
    {
        public string Id = "rule_1";
        public EditableActivationKind ActivationKind = EditableActivationKind.Triggered;
        public Trigger Trigger = Trigger.ON_PLAY;
        public EditableConditionKind ConditionKind = EditableConditionKind.Always;
        public TurnOwner TurnOwnerScope = TurnOwner.CONTROLLER;
        public Tribe ControlsTribe = Tribe.CONSTRUCT;
        public bool ControlsExcludesSource;
        public EditableLifetimeKind LifetimeKind = EditableLifetimeKind.OneShot;
        public int Uses = 1;
        public EditableRuleActionKind ActionKind = EditableRuleActionKind.Damage;

        public int Amount = 1;
        public int AttackDelta;
        public int HealthDelta;
        public int Count = 1;
        public int CardId;
        public Keyword Keyword;
        public ManaGainKind ManaKind = ManaGainKind.TEMPORARY;
        public EditableTarget Target = new EditableTarget();

        public bool IsTargeted => ActionKind == EditableRuleActionKind.Damage
            || ActionKind == EditableRuleActionKind.Heal
            || ActionKind == EditableRuleActionKind.ModifyStats
            || ActionKind == EditableRuleActionKind.GrantKeyword
            || ActionKind == EditableRuleActionKind.Destroy
            || ActionKind == EditableRuleActionKind.ReturnToHand
            || ActionKind == EditableRuleActionKind.Reanimate
            || ActionKind == EditableRuleActionKind.Silence
            || ActionKind == EditableRuleActionKind.Transform
            || ActionKind == EditableRuleActionKind.Frost
            || ActionKind == EditableRuleActionKind.Drown
            || ActionKind == EditableRuleActionKind.Copy
            || ActionKind == EditableRuleActionKind.TakeControl
            || ActionKind == EditableRuleActionKind.TriggerToll;

        public int ReferencedCardId => ActionKind == EditableRuleActionKind.Summon
            || ActionKind == EditableRuleActionKind.AddToHand
            || ActionKind == EditableRuleActionKind.Equip
            || ActionKind == EditableRuleActionKind.Transform
                ? CardId
                : 0;

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
            else if (rule.Condition is CostPaidCondition)
                editable.ConditionKind = EditableConditionKind.CostPaid;
            else if (rule.Condition is ControlsCondition controls)
            {
                editable.ConditionKind = EditableConditionKind.Controls;
                editable.ControlsTribe = controls.Tribe;
                editable.ControlsExcludesSource = controls.ExcludesSource;
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
                case EditableConditionKind.CostPaid: return CostPaidCondition.Instance;
                case EditableConditionKind.Controls:
                    return new ControlsCondition(ControlsTribe, ControlsExcludesSource);
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
                case EditableRuleActionKind.GrantKeyword: return new GrantKeywordAction(Keyword, target);
                case EditableRuleActionKind.Destroy: return new DestroyAction(target);
                case EditableRuleActionKind.ReturnToHand: return new ReturnToHandAction(target);
                case EditableRuleActionKind.Reanimate: return new ReanimateAction(target);
                case EditableRuleActionKind.Silence: return new SilenceAction(target);
                case EditableRuleActionKind.Transform: return new TransformAction(CardId, target);
                case EditableRuleActionKind.Frost: return new FrostAction(target);
                case EditableRuleActionKind.Drown: return new DrownAction(target);
                case EditableRuleActionKind.Copy: return new CopyAction(target);
                case EditableRuleActionKind.TakeControl: return new TakeControlAction(target);
                case EditableRuleActionKind.TriggerToll: return new TriggerTollAction(target);
                case EditableRuleActionKind.Draw: return new DrawAction(Count);
                case EditableRuleActionKind.Summon: return new SummonAction(CardId, Count);
                case EditableRuleActionKind.AddToHand: return new AddToHandAction(CardId, Count);
                case EditableRuleActionKind.Equip: return new EquipAction(CardId);
                case EditableRuleActionKind.GainMana: return new GainManaAction(Amount, ManaKind);
                case EditableRuleActionKind.Backdraft: return new BackdraftAction(Amount);
                // Takes nothing: the offer size is UnearthAction.Options, so the authoring form has
                // no Count field to read for this kind (see DrawActionFields).
                case EditableRuleActionKind.Unearth: return new UnearthAction();
                case EditableRuleActionKind.GainArmor: return new GainArmorAction(Amount);
                default: throw Unknown("editable action", ActionKind);
            }
        }

        private void ReadAction(RuleAction action)
        {
            if (action is DamageAction damage) { ActionKind = EditableRuleActionKind.Damage; Amount = damage.Amount; ReadTarget(damage); }
            else if (action is HealAction heal) { ActionKind = EditableRuleActionKind.Heal; Amount = heal.Amount; ReadTarget(heal); }
            else if (action is ModifyStatsAction stats) { ActionKind = EditableRuleActionKind.ModifyStats; AttackDelta = stats.AttackDelta; HealthDelta = stats.HealthDelta; ReadTarget(stats); }
            else if (action is GrantKeywordAction grant) { ActionKind = EditableRuleActionKind.GrantKeyword; Keyword = grant.Keyword; ReadTarget(grant); }
            else if (action is DestroyAction destroy) { ActionKind = EditableRuleActionKind.Destroy; ReadTarget(destroy); }
            else if (action is ReturnToHandAction returnToHand) { ActionKind = EditableRuleActionKind.ReturnToHand; ReadTarget(returnToHand); }
            else if (action is ReanimateAction reanimate) { ActionKind = EditableRuleActionKind.Reanimate; ReadTarget(reanimate); }
            else if (action is SilenceAction silence) { ActionKind = EditableRuleActionKind.Silence; ReadTarget(silence); }
            else if (action is TransformAction transform) { ActionKind = EditableRuleActionKind.Transform; CardId = transform.CardId; ReadTarget(transform); }
            else if (action is FrostAction frost) { ActionKind = EditableRuleActionKind.Frost; ReadTarget(frost); }
            else if (action is DrownAction drownAction) { ActionKind = EditableRuleActionKind.Drown; ReadTarget(drownAction); }
            else if (action is CopyAction copyAction) { ActionKind = EditableRuleActionKind.Copy; ReadTarget(copyAction); }
            else if (action is TakeControlAction takeControl) { ActionKind = EditableRuleActionKind.TakeControl; ReadTarget(takeControl); }
            else if (action is TriggerTollAction triggerToll) { ActionKind = EditableRuleActionKind.TriggerToll; ReadTarget(triggerToll); }
            else if (action is DrawAction draw) { ActionKind = EditableRuleActionKind.Draw; Count = draw.Count; }
            else if (action is SummonAction summon) { ActionKind = EditableRuleActionKind.Summon; CardId = summon.CardId; Count = summon.Count; }
            else if (action is AddToHandAction addToHand) { ActionKind = EditableRuleActionKind.AddToHand; CardId = addToHand.CardId; Count = addToHand.Count; }
            else if (action is EquipAction equip) { ActionKind = EditableRuleActionKind.Equip; CardId = equip.CardId; }
            else if (action is GainManaAction mana) { ActionKind = EditableRuleActionKind.GainMana; Amount = mana.Amount; ManaKind = mana.Kind; }
            else if (action is BackdraftAction backdraft) { ActionKind = EditableRuleActionKind.Backdraft; Amount = backdraft.Amount; }
            else if (action is UnearthAction) { ActionKind = EditableRuleActionKind.Unearth; }
            else if (action is GainArmorAction armor) { ActionKind = EditableRuleActionKind.GainArmor; Amount = armor.Amount; }
            else throw Unknown("action", action);
        }

        private void ReadTarget(TargetedRuleAction action)
            => Target = EditableTarget.FromDefinition(action.Target);

        private static InvalidOperationException Unknown(string family, object value)
            => new InvalidOperationException($"Unknown {family} node '{value.GetType().Name}'.");
    }
}
