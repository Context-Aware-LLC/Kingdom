using System.Collections.Generic;
using System.Text.RegularExpressions;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>
    /// The shared, headless card-legality validator (C1-C21, dev-docs/pipelines/card-authoring-and-import.md:20).
    /// Runs identically at server/client load and inside the editor (dev-docs/pipelines/card-authoring-and-import.md:20) — this
    /// assembly has zero UnityEngine references, so this type is safe to run on the dedicated server.
    /// Content/art/budget lint (the E-series) is a separate, editor-only IContentLinter — never here.
    /// </summary>
    public sealed class CardValidator : ICardValidator
    {
        private static readonly Regex IdPattern = new Regex("^[a-z0-9_]+$", RegexOptions.Compiled);
        private static readonly Regex TargetBindingPattern = new Regex("^[a-z][a-z0-9_]{0,31}$", RegexOptions.Compiled);

        // The ONLY two triggers that carry player-chosen targets. Verified against every RaiseSelf
        // call site: CommandProcessor.cs:346/354/386/454 pass the player's `targets`; ON_ATTACK
        // (CommandProcessor.cs:560), ON_DAMAGED (DamagePipeline.cs:128), the weapon ON_DEATH
        // (RulesEngine.cs:110), the minion ON_DEATH (EffectInterpreter.cs:154) and every observer
        // trigger (EffectInterpreter.RaiseObservers) all pass Array.Empty.
        private static readonly HashSet<Trigger> PlayerTargetedTriggers = new HashSet<Trigger>
        {
            Trigger.ON_PLAY, Trigger.ON_CAST,
        };

        /// <summary>Whether a keyword has meaningful behavior on an equipped weapon.</summary>
        private enum WeaponKeywordReach
        {
            Live,
            Marker,
            Unsupported,
        }

        // C6 — fail-closed carrier policy. A keyword absent from this table is unsupported until
        // its weapon behavior and tests land. Marker rows are presentation labels whose behavior
        // comes from effect/trigger data; they are already legal on shipped weapons.
        private static readonly Dictionary<Keyword, WeaponKeywordReach> WeaponKeywordTable = new()
        {
            [Keyword.LIFEDRAIN] = WeaponKeywordReach.Live,
            [Keyword.VENOM] = WeaponKeywordReach.Live,
            [Keyword.FROST] = WeaponKeywordReach.Live,
            [Keyword.DOUBLESTRIKE] = WeaponKeywordReach.Live,

            [Keyword.HERALD] = WeaponKeywordReach.Marker,
            [Keyword.TOLL] = WeaponKeywordReach.Marker,
            [Keyword.SILENCE] = WeaponKeywordReach.Marker,
            [Keyword.BACKDRAFT] = WeaponKeywordReach.Marker,
            [Keyword.REINFORCE] = WeaponKeywordReach.Marker,

            [Keyword.BULWARK] = WeaponKeywordReach.Unsupported,
            [Keyword.FERVOR] = WeaponKeywordReach.Unsupported,
            [Keyword.SKIRMISH] = WeaponKeywordReach.Unsupported,
            [Keyword.AEGIS] = WeaponKeywordReach.Unsupported,
            [Keyword.SHROUD] = WeaponKeywordReach.Unsupported,
            [Keyword.ARCANE_SURGE] = WeaponKeywordReach.Unsupported,
            [Keyword.REKINDLE] = WeaponKeywordReach.Unsupported,
            [Keyword.OVERWHELM] = WeaponKeywordReach.Unsupported,
            [Keyword.VANGUARD] = WeaponKeywordReach.Unsupported,
        };

        public ValidationReport Validate(CardDefinition def, ICardDatabase db)
        {
            var issues = new List<ValidationIssue>();
            void Err(string code, string field, string message) =>
                issues.Add(new ValidationIssue(Severity.Error, code, field, message, def.Id));
            void Warn(string code, string field, string message) =>
                issues.Add(new ValidationIssue(Severity.Warning, code, field, message, def.Id));

            // C1
            if (string.IsNullOrEmpty(def.Id) || !IdPattern.IsMatch(def.Id))
                Err("C1-ID-FORMAT", "id", "id must match ^[a-z0-9_]+$.");

            // C2 (id unique across the library) is deliberately NOT checked here: a single-card
            // Validate(def, db) call can't distinguish "db already contains THIS card" from "db
            // contains a DIFFERENT card with a colliding id" — every db lookup by id returns some
            // instance regardless. Uniqueness is a batch-level property; the set-level validator
            // (Editor Tooling's CardSetValidator, dev-docs/pipelines/card-authoring-and-import.md:21) checks it by
            // scanning all ids in the loaded set for collisions before calling Validate per-card.

            // C3
            if (string.IsNullOrEmpty(def.Name) || def.Name.Length < 1 || def.Name.Length > 32)
                Err("C3-NAME-LENGTH", "name", "name must be 1-32 characters.");

            // C4 / C5 / C6 — per-type stat shape (dev-docs/specs/card-schema.md §2.1)
            switch (def.Type)
            {
                case CardType.MINION:
                    if (def.Attack is null || def.Attack < 0)
                        Err("C4-MINION-ATTACK", "attack", "MINION requires attack >= 0.");
                    if (def.Health is null || def.Health < 1)
                        Err("C4-MINION-HEALTH", "health", "MINION requires health >= 1.");
                    if (def.Durability is not null)
                        Err("C4-MINION-DURABILITY", "durability", "MINION must have durability == null.");
                    break;
                case CardType.SPELL:
                    if (def.Attack is not null || def.Health is not null || def.Durability is not null)
                        Err("C5-SPELL-STATS", "stats", "SPELL must have attack/health/durability == null.");
                    if (def.Rules.Count == 0 && !def.RequiresEngineeringPrimitive)
                        Err("C5-SPELL-EFFECTS", "rules", "SPELL must have >=1 rule or set requiresEngineeringPrimitive.");
                    break;
                case CardType.WEAPON:
                    if (def.Attack is null || def.Attack < 0)
                        Err("C6-WEAPON-ATTACK", "attack", "WEAPON requires attack >= 0.");
                    if (def.Durability is null || def.Durability < 1)
                        Err("C6-WEAPON-DURABILITY", "durability", "WEAPON requires durability >= 1.");
                    if (def.Health is not null)
                        Err("C6-WEAPON-HEALTH", "health", "WEAPON must have health == null.");
                    foreach (var keyword in def.Keywords)
                    {
                        if (!WeaponKeywordTable.TryGetValue(keyword, out var reach))
                            reach = WeaponKeywordReach.Unsupported;
                        if (reach == WeaponKeywordReach.Unsupported)
                            Err("C6-WEAPON-KEYWORD-DEAD", "keywords",
                                $"keyword '{keyword}' has no behavior on an equipped weapon.");
                    }
                    break;
            }

            // C7
            if (def.Cost < 0 || def.Cost > 25)
                Err("C7-COST-RANGE", "cost", "cost must be 0-25.");

            // C8 — numeric stat ranges (dev-docs/specs/card-schema.md §2)
            if (def.Attack is int atk && (atk < 0 || atk > 30))
                Err("C8-ATTACK-RANGE", "attack", "attack must be 0-30.");
            if (def.Health is int hp && (hp < 1 || hp > 30))
                Err("C8-HEALTH-RANGE", "health", "health must be 1-30.");
            if (def.Durability is int dur && (dur < 1 || dur > 8))
                Err("C8-DURABILITY-RANGE", "durability", "durability must be 1-8.");

            // C9-C14, C21-C23 — per-rule checks
            var priorTargetBindings = new HashSet<string>();
            var ruleIds = new HashSet<string>();
            for (int i = 0; i < def.Rules.Count; i++)
            {
                RuleDefinition rule = def.Rules[i];
                string prefix = $"rules[{i}]";
                string ruleId = rule.Id.Value ?? "";
                string actionName = ActionName(rule.Action);
                Trigger trigger = RuleSemantics.TriggerOf(rule);

                // C9 — stable, card-scoped identity. The same syntax as a card id keeps rule ids
                // portable across JSON, logs and deterministic snapshots.
                if (!IdPattern.IsMatch(ruleId))
                    Err("C9-RULE-ID-FORMAT", $"{prefix}.id", "rule id must match ^[a-z0-9_]+$.");
                if (!ruleIds.Add(ruleId))
                    Err("C9-RULE-ID-DUPLICATE", $"{prefix}.id",
                        $"rule id '{ruleId}' appears more than once on this card.");

                // C21 — the runtime enumerator and validation share this exact fail-closed relation.
                // A new source kind, trigger, or pair is inert until EffectSourceCapabilities tables it.
                bool knownSource = EffectSourceCapabilities.TryForCardType(def.Type,
                    out EffectSourceKind sourceKind);
                if (!knownSource || !EffectSourceCapabilities.Supports(sourceKind, trigger))
                {
                    if (!EffectSourceCapabilities.SupportsAny(trigger))
                    {
                        Err("C21-TRIGGER-DEAD", $"{prefix}.activation",
                            $"trigger '{trigger}' has no raise site in the engine — a card using it does nothing, silently. Author only a live trigger (see EF-011..EF-017b).");
                    }
                    else if (trigger == Trigger.ON_ATTACK)
                    {
                        Err("C21-TRIGGER-WEAPON-ONLY", $"{prefix}.activation",
                            $"trigger '{trigger}' is raised for weapons only (CommandProcessor.cs:557-560) — it is dead on a {def.Type}.");
                    }
                    else if (trigger == Trigger.ON_HEAL)
                    {
                        Err("C21-TRIGGER-MINION-ONLY", $"{prefix}.activation",
                            $"trigger '{trigger}' is raised on board minions only — it is dead on a {def.Type}.");
                    }
                    else
                    {
                        Err("C21-TRIGGER-SOURCE-UNSUPPORTED", $"{prefix}.activation",
                            $"trigger '{trigger}' has no runtime path from a {sourceKind} source.");
                    }
                }
                // ON_DEATH on a WEAPON was rejected here (C21-WEAPON-TOLL) until EF-020 gave
                // RulesEngine.BreakWeapon a raise site. It is now live on BOTH card types, so the gate is
                // gone rather than relaxed — there is no weapon-shaped exception left to express.

                // C22 — the first reusable condition seam. Turn ownership is mandatory and explicit
                // for ON_TURN_START; on every other trigger it lacks a turn-event context and would be
                // an inert authoring error, so fail closed rather than silently skipping forever.
                if (trigger == Trigger.ON_TURN_START)
                {
                    if (!(rule.Condition is TurnOwnerCondition turnOwner))
                    {
                        Err("C22-TURN-OWNER-REQUIRED", $"{prefix}.condition",
                            "ON_TURN_START requires a TURN_OWNER condition with owner CONTROLLER, OPPONENT, or ANY.");
                    }
                    else if (!System.Enum.IsDefined(typeof(TurnOwner), turnOwner.Owner))
                    {
                        Err("C22-TURN-OWNER-INVALID", $"{prefix}.condition.owner",
                            $"turn owner '{turnOwner.Owner}' is not CONTROLLER, OPPONENT, or ANY.");
                    }
                }
                else if (rule.Condition is TurnOwnerCondition)
                {
                    Err("C22-TURN-OWNER-TRIGGER", $"{prefix}.condition",
                        "TURN_OWNER is valid only on ON_TURN_START because other triggers carry no incoming-turn context.");
                }

                // C23 — only one-shot player actions and source-bound observed triggers have a
                // runtime today. The other lifetime nodes are reserved, typed, and rejected until
                // their ongoing-state slice lands.
                if (rule.Lifetime is FiniteLifetime || rule.Lifetime is PermanentLifetime)
                {
                    Err("C23-LIFETIME-UNSUPPORTED", $"{prefix}.lifetime",
                        $"lifetime '{rule.Lifetime.GetType().Name}' requires the ongoing-effect runtime slice.");
                }
                else
                {
                    bool needsOneShot = trigger == Trigger.ON_PLAY || trigger == Trigger.ON_CAST;
                    if ((needsOneShot && !(rule.Lifetime is OneShotLifetime)) ||
                        (!needsOneShot && !(rule.Lifetime is SourceBoundLifetime)))
                        Err("C23-LIFETIME-ACTIVATION", $"{prefix}.lifetime",
                            $"trigger '{trigger}' requires a {(needsOneShot ? "ONE_SHOT" : "SOURCE_BOUND")} lifetime.");
                }
                if (rule.Lifetime is FiniteLifetime finite && finite.Uses <= 0)
                    Err("C23-FINITE-USES", $"{prefix}.lifetime.uses",
                        "FINITE lifetime uses must be greater than zero.");

                // C11-C13 — payload presence is structural in the typed action classes. String ids
                // can still be empty in hand-built definitions, and referenced cards must resolve.
                string? referencedCardId = ReferencedCardId(rule.Action);
                if (referencedCardId != null)
                {
                    if (string.IsNullOrEmpty(referencedCardId))
                        Err("C11-PAYLOAD-REQUIRED", $"{prefix}.action.cardId",
                            $"{actionName} requires action.cardId.");
                    else if (db is null || !db.TryGet(referencedCardId, out var token))
                        Err("C12-TOKEN-UNRESOLVED", $"{prefix}.action.cardId",
                            $"'{referencedCardId}' does not resolve in the card database.");
                    else if (rule.Action is SummonAction && token.Collectible)
                        Warn("C13-TOKEN-COLLECTIBLE", $"{prefix}.action.cardId",
                            $"SUMMON token '{referencedCardId}' is collectible:true — tokens are usually collectible:false.");
                }

                // C14
                TargetSpec? target = RuleSemantics.TargetOf(rule.Action);
                if (target != null && !TargetSpec.IsKnownSelector(target.Selector))
                    Err("C14-SELECTOR-INVALID", $"{prefix}.action.target.selector", $"'{target.Selector}' is not a known Selector (dev-docs/specs/card-schema.md §5.3).");
                else if (target != null &&
                         !RuleActionCapabilities.HasCompatibleTarget(
                             rule.Action, target.Selector, def.Type, trigger))
                    Err("C14F-ACTION-TARGET-INCOMPATIBLE", $"{prefix}.action.target.selector",
                        $"{actionName} cannot affect any target kind produced by '{target.Selector}'.");
                // C14b — an ANY_* selector is legal ONLY where a player supplies the target. On a
                // fired trigger it is not a mistarget, it is a crash: EF-032a, where one ON_DEATH
                // ANY_FRIENDLY_MINION row took out 6 of 64 balance-matrix cells and loaded clean
                // through this very rule, because C14 checked the selector without the trigger.
                else if (target != null &&
                         (target.ChosenBy == TargetChosenBy.PLAYER ||
                          (!target.ChosenBy.HasValue && TargetSpec.DefaultsToPlayerChoice(target.Selector)))
                         && !PlayerTargetedTriggers.Contains(trigger)
                         && !def.RequiresEngineeringPrimitive)
                    Err("C14B-SELECTOR-NEEDS-PLAYER-TARGET", $"{prefix}.action.target.selector",
                        $"'{target.Selector}' is an explicit-choice selector, but {trigger} fires with no player choosing — " +
                        "the interpreter throws on it at runtime. Use an auto-resolvable selector " +
                        "(SELF / FRIENDLY_MINIONS / ENEMY_MINIONS / ALL_MINIONS / FRIENDLY_HERO / ENEMY_HERO), " +
                        "or set requiresEngineeringPrimitive to hold the row out of every pool until the primitive lands.");

                if (target?.Count is int targetCount && targetCount < -1)
                    Err("C14C-TARGET-COUNT", $"{prefix}.action.target.count",
                        "target.count must be positive or -1 for every candidate.");
                if (target?.Count == 0)
                    Err("C14C-TARGET-COUNT", $"{prefix}.action.target.count",
                        "target.count cannot be zero.");
                if (target != null)
                {
                    if (target.Binding.Length > 0 && !TargetBindingPattern.IsMatch(target.Binding))
                        Err("C14D-TARGET-BINDING", $"{prefix}.action.target.binding",
                            "target.binding must match ^[a-z][a-z0-9_]{0,31}$.");
                    if (target.DistinctFrom.Length > 0)
                    {
                        if (!TargetBindingPattern.IsMatch(target.DistinctFrom))
                            Err("C14D-TARGET-BINDING", $"{prefix}.action.target.distinctFrom",
                                "target.distinctFrom must match ^[a-z][a-z0-9_]{0,31}$.");
                        else if (!priorTargetBindings.Contains(target.DistinctFrom))
                            Err("C14E-TARGET-BINDING-ORDER", $"{prefix}.action.target.distinctFrom",
                                $"target.distinctFrom '{target.DistinctFrom}' must name a binding from an earlier rule.");
                    }
                    if (target.Binding.Length > 0) priorTargetBindings.Add(target.Binding);
                }

                if (rule.Action is GainManaAction mana &&
                    !System.Enum.IsDefined(typeof(ManaGainKind), mana.Kind))
                    Err("C15-MANA-KIND", $"{prefix}.action.kind",
                        $"mana kind '{mana.Kind}' is not implemented.");
            }

            // C18/C19: Keywords/Class/Rarity/SetId/Tribe/Type are already closed C# enums — the
            // shared CardJson parser throws on an unknown value before a CardDefinition can exist,
            // so there is nothing further to check on the POCO itself.

            // C20: pass the flag through; whether it *should* have been set is a design/eng judgment
            // call at authoring time, not something this validator can infer.
            bool requiresPrimitive = def.RequiresEngineeringPrimitive;

            bool ok = true;
            foreach (var issue in issues)
                if (issue.Severity == Severity.Error) { ok = false; break; }

            return new ValidationReport(ok, issues, requiresPrimitive);
        }

        private static string? ReferencedCardId(RuleAction action)
        {
            switch (action)
            {
                case SummonAction a: return a.CardId;
                case AddToHandAction a: return a.CardId;
                case EquipAction a: return a.CardId;
                case TransformAction a: return a.CardId;
                default: return null;
            }
        }

        private static string ActionName(RuleAction action)
        {
            switch (action)
            {
                case DamageAction _: return "DAMAGE";
                case HealAction _: return "HEAL";
                case ModifyStatsAction _: return "MODIFY_STATS";
                case ReinforceAction _: return "REINFORCE";
                case GrantKeywordAction _: return "GRANT_KEYWORD";
                case DestroyAction _: return "DESTROY";
                case ReturnToHandAction _: return "RETURN_TO_HAND";
                case SilenceAction _: return "SILENCE";
                case TransformAction _: return "TRANSFORM";
                case FrostAction _: return "FROST";
                case TakeControlAction _: return "TAKE_CONTROL";
                case DrawAction _: return "DRAW";
                case SummonAction _: return "SUMMON";
                case AddToHandAction _: return "ADD_TO_HAND";
                case EquipAction _: return "EQUIP";
                case GainManaAction _: return "GAIN_MANA";
                case GainArmorAction _: return "GAIN_ARMOR";
                case BackdraftAction _: return "BACKDRAFT";
                case UnearthAction _: return "UNEARTH";
                default:
                    throw new System.InvalidOperationException(
                        $"Rule action '{action.GetType().Name}' is absent from validator semantics.");
            }
        }

        /// <summary>
        /// C2 (id unique across the library) — a batch-level check. Callers validating a whole set
        /// (Editor Tooling's CardSetValidator, dev-docs/pipelines/card-authoring-and-import.md:21) should run this
        /// once over all loaded rows in addition to per-card <see cref="Validate"/> calls.
        /// </summary>
        public static IReadOnlyList<ValidationIssue> CheckDuplicateIds(IEnumerable<CardDefinition> all)
        {
            var seen = new Dictionary<string, int>();
            foreach (var def in all)
            {
                if (string.IsNullOrEmpty(def.Id)) continue;
                seen.TryGetValue(def.Id, out int count);
                seen[def.Id] = count + 1;
            }

            var issues = new List<ValidationIssue>();
            foreach (var def in all)
            {
                if (seen.TryGetValue(def.Id, out int count) && count > 1)
                    issues.Add(new ValidationIssue(Severity.Error, "C2-ID-DUPLICATE", "id",
                        $"id '{def.Id}' appears {count} times in the card set.", def.Id));
            }
            return issues;
        }
    }
}
