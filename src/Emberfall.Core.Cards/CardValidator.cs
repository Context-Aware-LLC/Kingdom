using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>
    /// The shared, headless card-legality validator (C1-C26, dev-docs/pipelines/card-authoring-and-import.md:20).
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
            Unsupported,
        }

        // C6 — fail-closed carrier policy. A keyword absent from this table is unsupported until
        // its weapon behavior and tests land. Action-derived markers are rejected centrally by C25
        // before this carrier-specific table is consulted.
        private static readonly Dictionary<Keyword, WeaponKeywordReach> WeaponKeywordTable = new()
        {
            [Keyword.LIFEDRAIN] = WeaponKeywordReach.Live,
            [Keyword.VENOM] = WeaponKeywordReach.Live,
            [Keyword.FROST] = WeaponKeywordReach.Live,
            [Keyword.DOUBLESTRIKE] = WeaponKeywordReach.Live,

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
                issues.Add(new ValidationIssue(Severity.Error, code, field, message, def.Id, def.Handle));
            void Warn(string code, string field, string message) =>
                issues.Add(new ValidationIssue(Severity.Warning, code, field, message, def.Id, def.Handle));

            // C1 — the FORMAT rule moved to the handle when the id became a number. It is still an
            // error rather than a warning: the handle is what a human reads in every report this
            // validator feeds, and a handle that does not look like the others is a handle nobody
            // recognises as one.
            if (string.IsNullOrEmpty(def.Handle) || !IdPattern.IsMatch(def.Handle))
                Err("C1-HANDLE-FORMAT", "handle", "handle must match ^[a-z0-9_]+$.");

            // C1B — the numeric id sits inside the band its type owns. This is the invariant the
            // whole identity model rests on, and nothing else checks it: an id of 0 is an unset
            // field wearing a valid-looking value, and an id in the wrong band silently spends a
            // number another type will later want.
            if (!InBand(def.Type, def.Id))
                Err("C1B-ID-BAND", "id",
                    $"id {def.Id} is outside the {def.Type} band {BandText(def.Type)}. " +
                    "Ids are assigned by tooling/scripts/assign_numeric_card_ids.py and are permanent.");

            // C2 (id unique across the library) is deliberately NOT checked here: a single-card
            // Validate(def, db) call can't distinguish "db already contains THIS card" from "db
            // contains a DIFFERENT card with a colliding id" — every db lookup by id returns some
            // instance regardless. Uniqueness is a batch-level property; the set-level validator
            // (Editor Tooling's CardSetValidator, dev-docs/pipelines/card-authoring-and-import.md:21) checks it by
            // scanning all ids in the loaded set for collisions before calling Validate per-card.

            // C3
            if (string.IsNullOrEmpty(def.Name) || def.Name.Length < 1 || def.Name.Length > 32)
                Err("C3-NAME-LENGTH", "name", "name must be 1-32 characters.");

            // C25 — these enum members remain solely to preserve historic numeric ordinals.
            // Runtime behavior and player-facing markers come from their typed actions.
            foreach (Keyword keyword in def.Keywords)
                if (KeywordSemantics.IsActionMarker(keyword))
                    Err("C25-ACTION-MARKER-KEYWORD", "keywords",
                        $"'{keyword}' is action-derived and cannot be authored as a carrier keyword.");

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
                    if (def.Rules.Count == 0)
                        Err("C5-SPELL-RULES", "rules", "SPELL must have >=1 rule.");
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
                        if (KeywordSemantics.IsActionMarker(keyword)) continue; // reported by C25
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

            // C9-C15, C21-C25 — per-rule checks
            var playerChoiceRuleCounts = new Dictionary<Trigger, int>();
            foreach (RuleDefinition rule in def.Rules)
            {
                TargetSpec? target = RuleSemantics.TargetOf(rule.Action);
                if (!TargetSpec.IsPlayerChosen(target)) continue;
                Trigger trigger = RuleSemantics.TriggerOf(rule);
                playerChoiceRuleCounts.TryGetValue(trigger, out int count);
                playerChoiceRuleCounts[trigger] = count + 1;
            }
            var priorTargetBindings = new HashSet<string>();
            var ruleIds = new HashSet<string>();
            // A cost is tied to the run adjacent to it, so "which cost" is answered by rule order
            // rather than by a name. That only works while a trigger charges at most one price —
            // C28-COST-SECOND is what keeps it true, and CostPaidCondition carries the retirement
            // trigger for the day it stops being.
            var costTriggers = new HashSet<Trigger>();
            var paidTriggers = new HashSet<Trigger>();
            for (int i = 0; i < def.Rules.Count; i++)
            {
                RuleDefinition rule = def.Rules[i];
                string prefix = $"rules[{i}]";
                string ruleId = rule.Id.Value ?? "";
                string actionName = ActionName(rule.Action);
                Trigger trigger = RuleSemantics.TriggerOf(rule);

                if (rule.Activation is AuraActivation && !AuraCapabilities.Supports(def.Type, rule))
                    Err("C21-AURA-UNSUPPORTED", $"{prefix}.activation",
                        "AURA currently supports only unsilenced board-minion MODIFY_STATS rules with " +
                        "ALWAYS, SOURCE_BOUND and automatic minion selectors. A tribe filter is " +
                        "allowed; a damaged filter is not, because a stat aura writes the health it " +
                        "would be selecting on.");

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
                bool ongoingSpellRule = RuleLifetimeCapabilities.IsOngoingSpellRule(def.Type, trigger);
                if (!knownSource || (!EffectSourceCapabilities.Supports(sourceKind, trigger) &&
                    !ongoingSpellRule))
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

                // C29 — the board condition. "If you control a Construct" is checked against the
                // board when the effect resolves, so it needs no event context and is legal on every
                // trigger C22 has not already claimed. What it cannot survive is naming a tribe the
                // card itself supplies: EnterBoard runs before the ON_PLAY raise, so a Construct
                // asking whether you control a Construct answers itself, holds always, and prints a
                // condition it does not have. ExcludesSource is the fix and the WORDING both — see
                // ControlsCondition — so it is pinned to the one value the carrier allows rather
                // than left as a second opinion the face and the runtime can disagree about.
                if (rule.Condition is ControlsCondition controls)
                {
                    if (controls.Tribe == Tribe.NONE)
                        Err("C29-CONTROLS-TRIBE-NONE", $"{prefix}.condition.tribe",
                            "a CONTROLS condition names a tribe to look for; NONE is the absence of one.");

                    bool sourceIsThatTribe = def.Type == CardType.MINION && def.Tribe == controls.Tribe;
                    if (sourceIsThatTribe && !controls.ExcludesSource)
                        Err("C29-CONTROLS-SELF-TRIVIAL", $"{prefix}.condition",
                            $"this card is a {controls.Tribe} and is on the board before its own " +
                            "effects resolve, so the condition would count itself and hold always — " +
                            "set other: true, which also prints it as \"another\".");
                    if (!sourceIsThatTribe && controls.ExcludesSource)
                        Err("C29-CONTROLS-OTHER-UNSPEAKABLE", $"{prefix}.condition",
                            $"other: true prints \"another {controls.Tribe}\", and this card is not " +
                            $"a {controls.Tribe} board minion for there to be another OF — drop it.");
                }

                // C28 — the cost prefix. A cost is a price paid to play the card, and every clause
                // below follows from that one sentence rather than from a policy choice.
                CostKind ruleCost = RuleSemantics.TargetOf(rule.Action)?.Cost ?? CostKind.None;
                bool playTrigger = trigger == Trigger.ON_PLAY || trigger == Trigger.ON_CAST;

                if (ruleCost != CostKind.None)
                {
                    // Paying happens when the card is PLAYED. An observer trigger has no play to
                    // refuse and no prompt to raise, so a cost there could never be charged — and
                    // ConditionEvaluator would fail its gate closed forever while the card face went
                    // on advertising a price.
                    if (!playTrigger)
                        Err("C28-COST-TRIGGER", $"{prefix}.action.target.cost",
                            $"a cost is paid when the card is played, so it is valid only on ON_PLAY " +
                            $"or ON_CAST — not on '{trigger}'.");

                    // One price per trigger, because nothing names WHICH cost a gate waits on: the
                    // tie is adjacency (Sam, 2026-08-22). A second cost would silently re-point every
                    // gate beneath it.
                    if (!costTriggers.Add(trigger))
                        Err("C28-COST-SECOND", $"{prefix}.action.target.cost",
                            $"'{trigger}' already charges a cost on this card, and a COST_PAID gate " +
                            "names no cost — it reads the one adjacent to it, so a second is ambiguous.");

                    // Sam, 2026-08-22: "the spell should be unplayable if the cost is not able to be
                    // paid." A spell leaves no body behind, so playing one whose price went unpaid is
                    // a mana and a card for nothing. The optional spelling would read as authored
                    // intent that Targeting.CanFizzle then ignores, which is worse than rejecting it.
                    if (ruleCost == CostKind.Optional &&
                        (def.Type == CardType.SPELL || def.Type == CardType.HERO_POWER))
                        Err("C28-COST-OPTIONAL-SPELL", $"{prefix}.action.target.cost",
                            $"an optional cost is not expressible on a {def.Type}: a spell with an " +
                            "unpayable cost is unplayable, never played for nothing.");
                }

                if (rule.Condition is CostPaidCondition)
                {
                    paidTriggers.Add(trigger);
                    // The gate reads the cost adjacent to it. With none, it is shut forever and the
                    // card face cannot say so — CardTextGenerator.CostScope refuses the same shape.
                    if (!costTriggers.Contains(trigger))
                        Err("C28-COST-PAID-ORPHAN", $"{prefix}.condition",
                            "COST_PAID waits on a cost charged earlier under the same trigger, and " +
                            "this card charges none — the rule could never resolve.");
                }

                // C23 — this policy is also the only path that creates and consumes a carrierless
                // observer at runtime, so validity cannot drift from execution semantics.
                if (!RuleLifetimeCapabilities.Allows(def.Type, trigger, rule.Lifetime))
                    Err("C23-LIFETIME-ACTIVATION", $"{prefix}.lifetime",
                        ongoingSpellRule
                            ? $"future spell trigger '{trigger}' requires " +
                              $"{RuleLifetimeCapabilities.RequiredLifetimeDescription(def.Type, trigger)} lifetime."
                            : $"trigger '{trigger}' requires a " +
                              $"{RuleLifetimeCapabilities.RequiredLifetimeDescription(def.Type, trigger)} lifetime.");
                if (!ongoingSpellRule && RuleLifetimeCapabilities.IsOngoingOnly(rule.Lifetime) &&
                    !RuleLifetimeCapabilities.RequiresOneShotLifetime(trigger))
                    Err("C23-LIFETIME-UNSUPPORTED", $"{prefix}.lifetime",
                        $"lifetime '{rule.Lifetime.GetType().Name}' is valid only for a future-trigger SPELL rule.");
                if (!RuleLifetimeCapabilities.HasValidFiniteUses(rule.Lifetime))
                    Err("C23-FINITE-USES", $"{prefix}.lifetime.uses",
                        "FINITE lifetime uses must be greater than zero.");

                // C11-C13 — payload presence is structural in the typed action classes. A
                // hand-built definition can still carry an unset id, which since the id became a
                // number means 0 rather than "" — a value that looks present. Referenced cards
                // must resolve.
                int? referencedCardId = ReferencedCardId(rule.Action);
                if (referencedCardId != null)
                {
                    if (referencedCardId.Value <= 0)
                        Err("C11-PAYLOAD-REQUIRED", $"{prefix}.action.cardId",
                            $"{actionName} requires action.cardId.");
                    else if (db is null || !db.TryGet(referencedCardId.Value, out var token))
                        Err("C12-TOKEN-UNRESOLVED", $"{prefix}.action.cardId",
                            $"card {referencedCardId} does not resolve in the card database.");
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
                         TargetSpec.IsPlayerChosen(target)
                         && !PlayerTargetedTriggers.Contains(trigger))
                    Err("C14B-SELECTOR-NEEDS-PLAYER-TARGET", $"{prefix}.action.target.selector",
                        $"'{target.Selector}' is an explicit-choice selector, but {trigger} fires with no player choosing — " +
                        "the interpreter throws on it at runtime. Use an auto-resolvable selector " +
                        "(SELF / ANY_FRIENDLY_MINION / OTHER_FRIENDLY_MINIONS / ANY_ENEMY_MINION / ANY_MINION / FRIENDLY_HERO / ENEMY_HERO), " +
                        "or a RANDOM target choice.");

                // C14J — cardinality, which neither C14F (kind) nor C14I (allegiance) can see: a
                // distinctFrom asks the resolver to exclude a target already chosen, and against a
                // selector naming exactly one thing it can never exclude anything. Sam, 2026-08-12
                // (#143): "there is no other weapon. There isn't another one." Rejected here, at
                // authoring, because that is where the mistake is — the alternative on the table was
                // leaving CardTextGenerator to phrase it, and every phrasing it can produce
                // ("your other weapon", "another enemy hero") asserts a second one exists.
                if (target != null && target.DistinctFrom.Length > 0 &&
                    RuleActionCapabilities.IsUniqueSelector(target.Selector))
                    Err("C14J-DISTINCT-ON-UNIQUE", $"{prefix}.action.target.distinctFrom",
                        $"'{target.Selector}' names exactly one thing, so distinctFrom " +
                        $"'{target.DistinctFrom}' can never exclude anything. Drop the distinctFrom, " +
                        "or target a selector that can produce more than one candidate.");

                // No count check: a zero or negative count has no spelling in TargetSelection, so
                // the parser refuses it and a def reaching this validator cannot be holding one.
                if (target != null)
                {
                    if (TargetSpec.IsPlayerChosen(target) && playerChoiceRuleCounts[trigger] > 1 &&
                        target.Binding.Length == 0)
                        Err("C14G-TARGET-BINDING-REQUIRED", $"{prefix}.action.target.binding",
                            "each rule in a multi-rule player choice must name its target binding explicitly.");
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
                    // C14H — a COST is a price the player pays. Only a player-chosen target can go
                    // unpaid: an automatic or RANDOM spec takes whatever exists and never asks, so
                    // the flag could not gate anything there. Fail closed rather than let a row read
                    // as a sacrifice it never enforces.
                    if (target.IsCost && !TargetSpec.IsPlayerChosen(target))
                        Err("C14H-TARGET-COST-AUTOMATIC", $"{prefix}.action.target.cost",
                            "target.cost is meaningful only on a player-chosen target — an " +
                            "automatic or RANDOM selector resolves to whatever exists and can never " +
                            "go unpaid.");
                    // C27 — a chosen count may not EXCEED the selector's derived maximum.
                    //
                    // This is the rule that keeps "every candidate" spelled exactly one way. A count
                    // at the maximum means nobody chooses; a count ABOVE it would mean the same
                    // thing while reading as a different number, so a card face would have two
                    // authored forms rendering identically and the parser could not tell them apart
                    // -- which is the ambiguity the selection collapse exists to delete (Sam,
                    // 2026-08-21).
                    //
                    // It is also the guard on the derivation itself. Nobody types the maximum: rules
                    // are derived from card text, so a count is emitted from the prose. Should a zone
                    // cap ever change, every row that had reached the old maximum surfaces HERE as a
                    // rejected card rather than as a silent change of behaviour.
                    //
                    // RANDOM is exempt on purpose. Random may repeat, so three rolls against one
                    // candidate hit it three times and a count above the maximum is meaningful.
                    if (!target.Selection.IsRandom && target.MaxCandidates.HasValue &&
                        target.Selection.Count > target.MaxCandidates.Value)
                        Err("C27-SELECTION-ABOVE-MAXIMUM", $"{prefix}.action.target.selection",
                            $"selection '{target.Selection}' asks for more than '{target.Selector}' " +
                            $"can ever yield ({target.MaxCandidates.Value}). A count AT the maximum " +
                            "already means every candidate; a count above it is a second spelling of " +
                            "the same thing.");

                    if (target.Binding.Length > 0) priorTargetBindings.Add(target.Binding);
                }

                if (rule.Action is GainManaAction mana &&
                    !System.Enum.IsDefined(typeof(ManaGainKind), mana.Kind))
                    Err("C15-MANA-KIND", $"{prefix}.action.kind",
                        $"mana kind '{mana.Kind}' is not implemented.");

                if (rule.Action is GrantKeywordAction grant &&
                    KeywordSemantics.IsActionMarker(grant.Keyword))
                    Err("C25-ACTION-MARKER-GRANT", $"{prefix}.action.keyword",
                        $"'{grant.Keyword}' is an action-derived marker and cannot be granted as runtime keyword state.");

                // C24 — interactive choices need a positive offer and a command path that is
                // already waiting for player input. Observer cascades cannot suspend and resume
                // the interpreter, so authoring Unearth there would continue past an open choice.
                if (rule.Action is UnearthAction unearth)
                {
                    if (unearth.Count <= 0)
                        Err("C24-UNEARTH-COUNT", $"{prefix}.action.count",
                            "UNEARTH count must be greater than zero.");
                    if (!PlayerTargetedTriggers.Contains(trigger))
                        Err("C24-UNEARTH-TRIGGER", $"{prefix}.action",
                            $"UNEARTH requires an ON_PLAY or ON_CAST player-command path; '{trigger}' cannot pause for input.");
                }
            }

            // C28-COST-UNBOUGHT — Sam, 2026-08-22: "no cost will be printed on a card unless it is
            // tied to the action adjacent to it." A cost with nothing gated beneath it buys nothing,
            // and RenderRuleGroups drops a prefix with an empty run — so the price would vanish from
            // the card face while the player still paid it. The one shape where the text loses an
            // effect rather than gaining one, which is why it is checked after the whole rule list.
            foreach (Trigger charged in costTriggers)
                if (!paidTriggers.Contains(charged))
                    Err("C28-COST-UNBOUGHT", "rules",
                        $"the cost charged on '{charged}' buys nothing — no rule under it waits on " +
                        "COST_PAID, so the price would be paid and never printed.");

            // C18/C19: Keywords/Class/Rarity/SetId/Tribe/Type are already closed C# enums — the
            // shared CardJson parser throws on an unknown value before a CardDefinition can exist,
            // so there is nothing further to check on the POCO itself.


            // C26 — the authored card face must still describe the rules under it. This is the
            // generator run backwards: it renders what these rules read as and compares, instead of
            // supplying the sentence and hoping. A card whose text and rules disagree is the one
            // failure the old direction could not have — it simply re-worded the card and shipped.
            //
            // Deliberately an ERROR and deliberately exact-match, and both are scaffolding. Until
            // rules are parsed FROM the sentence there is no way to tell a deliberate re-wording
            // from a stale one, so the strict reading is what keeps the pair honest — and the first
            // author who genuinely needs to re-word a card is the forcing function for the parser
            // rather than someone waiving a warning. See CardTextGenerator's retirement trigger.
            //
            // That forcing function has fired ONCE, 2026-08-18: Sam asked that "next to" be accepted
            // alongside "beside" for adjacency. No card needs it yet, so nothing changed here — but
            // the next request of this shape is answered by building the parser. Widening C26 to
            // accept a set of sentences instead of one is the wrong repair: it would keep the pair
            // honest about wording while silently giving up the property that makes a round trip an
            // oracle, which is that one rule shape renders to exactly one sentence.
            // NoPool rather than `db!`: a validator handed no pool should make a card-naming
            // sentence UNRENDERABLE — which C26 reports against the row — not dereference null.
            switch (CardTextGenerator.Check(def, db ?? CardTextGenerator.NoPool.Instance,
                        out string expectedText))
            {
                case CardTextDivergence.Missing:
                    Err("C26-TEXT-MISSING", "text",
                        $"card has rules text to print but no authored 'text'; the rules read as \"{expectedText}\".");
                    break;
                case CardTextDivergence.Orphaned:
                    Err("C26-TEXT-ORPHANED", "text",
                        $"'text' says \"{def.Text}\" but the card has no keywords or rules to say it.");
                    break;
                // Both arms below name the SECOND option, and that is not politeness. Sam,
                // 2026-08-19: "it's up to card authors to restrict their language to what is already
                // parsable, and to point out when something might not be, so that we can adapt."
                // An author who only ever reads "you got it wrong" silently re-words the card to
                // whatever the renderer already emits — which is the grammar never hearing about the
                // wording it cannot say, and the gap staying invisible for exactly as long as
                // everyone is being cooperative. The triage is per card and it is Sam's: adapt the
                // text, or adapt the grammar, usually some of both. Emberfall.Tools.CardCi prints
                // these as one row per card so a fresh corpus can be walked in one pass.
                case CardTextDivergence.Stale:
                    Err("C26-TEXT-STALE", "text",
                        $"'text' no longer describes the rules — authored \"{def.Text}\", rules read as \"{expectedText}\". " +
                        "If the authored wording is the deliberate one, this is a grammar gap to report, not a card to re-word.");
                    break;
                case CardTextDivergence.Unrenderable:
                    Err("C26-TEXT-UNRENDERABLE", "rules",
                        "rules are a shape with no phrasing, so the authored 'text' cannot be checked against them. " +
                        "This is always a grammar gap: report the card rather than re-shaping it to fit.");
                    break;
            }

            bool ok = true;
            foreach (var issue in issues)
                if (issue.Severity == Severity.Error) { ok = false; break; }

            return new ValidationReport(ok, issues);
        }

        private static int? ReferencedCardId(RuleAction action)
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
            var seen = new Dictionary<int, int>();
            var handles = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var def in all)
            {
                seen.TryGetValue(def.Id, out int count);
                seen[def.Id] = count + 1;
                if (def.Handle.Length == 0) continue;
                handles.TryGetValue(def.Handle, out int hcount);
                handles[def.Handle] = hcount + 1;
            }

            var issues = new List<ValidationIssue>();
            foreach (var def in all)
            {
                if (seen.TryGetValue(def.Id, out int count) && count > 1)
                    issues.Add(new ValidationIssue(Severity.Error, "C2-ID-DUPLICATE", "id",
                        $"id {def.Id} appears {count} times in the card set.", def.Id, def.Handle));

                // A duplicate HANDLE is a warning and not an error, and the asymmetry is the model
                // stated out loud: two rows sharing a number are the same card to everything that
                // resolves one, while two rows sharing a handle are merely two rows a human cannot
                // tell apart in a log. Promoting this to an error would make the handle a key.
                if (def.Handle.Length > 0 && handles.TryGetValue(def.Handle, out int hcount) && hcount > 1)
                    issues.Add(new ValidationIssue(Severity.Warning, "C2B-HANDLE-DUPLICATE", "handle",
                        $"handle '{def.Handle}' appears {hcount} times. Nothing resolves against a " +
                        "handle, so this breaks no lookup — it makes two rows unreadable apart.",
                        def.Id, def.Handle));
            }
            return issues;
        }

        /// <summary>
        /// The band each card type owns, and the reason a card id is never 0: <c>default(int)</c>
        /// is 0, so an id of 0 is indistinguishable from a field nobody set.
        /// </summary>
        private static bool InBand(CardType type, int id) => type switch
        {
            CardType.HERO_POWER => id >= 1 && id <= 50,
            CardType.WEAPON => id >= 51 && id <= 150,
            CardType.SPELL => id >= 151 && id <= 1000,
            CardType.MINION => id >= 1001,
            _ => id > 0,
        };

        private static string BandText(CardType type) => type switch
        {
            CardType.HERO_POWER => "1-50",
            CardType.WEAPON => "51-150",
            CardType.SPELL => "151-1000",
            CardType.MINION => "1001 and up",
            _ => "1 and up",
        };
    }
}
