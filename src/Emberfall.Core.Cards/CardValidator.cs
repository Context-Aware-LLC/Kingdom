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

        // Selector is a closed vocabulary (dev-docs/specs/card-schema.md §5.3) but carried as a plain string
        // on EffectDef today rather than a structured TargetSpec/enum — validate against the list.
        // ANY_MINION / ANY_FRIENDLY_MINION canonized in the M2 Phase 0 grammar freeze (tier a).
        private static readonly HashSet<string> ValidSelectors = new HashSet<string>
        {
            "SELF", "FRIENDLY_MINIONS", "ENEMY_MINIONS", "ALL_MINIONS", "FRIENDLY_HERO",
            "ENEMY_HERO", "ANY_CHARACTER", "ANY_FRIENDLY", "ANY_ENEMY", "ANY_MINION",
            "ANY_FRIENDLY_MINION", "ADJACENT", "TRIGGER_SOURCE",
        };

        // Schema-valid selectors the EffectInterpreter cannot resolve yet (no adjacency/trigger-source
        // plumbing). The interpreter now throws on them instead of silently mistargeting, so a row that
        // authors one must declare requiresEngineeringPrimitive — the documented gate for grammar the
        // engine hasn't grown (dev-docs/specs/card-schema.md:40). Keep in sync with EffectInterpreter.SelectorTargets.
        private static readonly HashSet<string> UnimplementedSelectors = new HashSet<string>
        {
            "ADJACENT", "TRIGGER_SOURCE",
        };

        // Verbs whose EffectDef.Amount is required (C10). Extends the tooling-doc list with the
        // verbs actually present in the Action enum/M1 data (OVERLOAD, FREEZE) that doc predates.
        private static readonly HashSet<Action> AmountRequiredActions = new HashSet<Action>
        {
            Action.DEAL_DAMAGE, Action.HEAL, Action.DRAW, Action.SUMMON, Action.BUFF,
            Action.GAIN_MANA, Action.DISCOVER, Action.ADD_TO_HAND, Action.OVERLOAD,
        };

        /// <summary>Whether a <see cref="Trigger"/> has a raise site in the engine (C21).</summary>
        private enum TriggerLiveness
        {
            /// <summary>Has a raise site in the engine; type-specific exceptions are gated separately.</summary>
            Live,
            /// <summary>Raised for weapons only (<c>ON_ATTACK</c>, CommandProcessor.cs:527-531):
            /// valid on a WEAPON, dead on a MINION/SPELL/HERO_POWER.</summary>
            WeaponOnly,
            /// <summary>Declared in the <see cref="Trigger"/> enum but raised NOWHERE in src/ today —
            /// a card on it parses, validates, loads, and does nothing forever, silently.</summary>
            Dead,
        }

        // C21 — trigger liveness. THE authoritative guard against a dead trigger; it replaces the
        // prose list in CLAUDE.md, which is exactly the kind of guard that rots. Every Trigger enum
        // member MUST appear here (a member with no row is treated as Dead — fail closed).
        //
        // HARD RULE: UPDATE THIS TABLE as each of EF-011..EF-017b lands its raise site — flip that
        // trigger's value from Dead to Live in the SAME commit that adds the raise site. The `// EF-0nn`
        // pointer on each Dead row names the task that arms it; that comment is the whole point of the
        // table (retirement triggers live in the code, not in a doc).
        private static readonly Dictionary<Trigger, TriggerLiveness> TriggerLivenessTable = new()
        {
            [Trigger.ON_PLAY] = TriggerLiveness.Live, // CommandProcessor.cs:325,348 (battlecry / weapon equip)
            [Trigger.ON_CAST] = TriggerLiveness.Live, // CommandProcessor.cs:333,411 (spell / hero power)
            [Trigger.ON_DEATH] = TriggerLiveness.Live, // EffectInterpreter.cs:153 (minion deathrattle) — weapon case gated below
            [Trigger.ON_FRIENDLY_SUMMON] = TriggerLiveness.Live, // CommandProcessor.cs:326, ActionHandlers.cs:108
            [Trigger.ON_ATTACK] = TriggerLiveness.WeaponOnly, // CommandProcessor.cs:531 (weapons only)
            [Trigger.ON_DAMAGED] = TriggerLiveness.Live, // DamagePipeline.cs:128 (minion self-trigger)
            [Trigger.ON_TURN_START] = TriggerLiveness.Dead, // EF-011
            [Trigger.ON_TURN_END] = TriggerLiveness.Dead, // EF-012
            [Trigger.ON_HEAL] = TriggerLiveness.Dead, // EF-015
            [Trigger.ON_SPELL_CAST] = TriggerLiveness.Dead, // EF-016
            [Trigger.AURA] = TriggerLiveness.Dead, // EF-017b
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
                    if (def.Effects.Count == 0 && !def.RequiresEngineeringPrimitive)
                        Err("C5-SPELL-EFFECTS", "effects", "SPELL must have >=1 effect or set requiresEngineeringPrimitive.");
                    break;
                case CardType.WEAPON:
                    if (def.Attack is null || def.Attack < 0)
                        Err("C6-WEAPON-ATTACK", "attack", "WEAPON requires attack >= 0.");
                    if (def.Durability is null || def.Durability < 1)
                        Err("C6-WEAPON-DURABILITY", "durability", "WEAPON requires durability >= 1.");
                    if (def.Health is not null)
                        Err("C6-WEAPON-HEALTH", "health", "WEAPON must have health == null.");
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

            // C9-C11, C13, C14, C17, C21 — per-effect checks
            for (int i = 0; i < def.Effects.Count; i++)
            {
                var fx = def.Effects[i];
                string prefix = $"effects[{i}]";

                // C9: Action is a known verb — guaranteed by the enum type system (CardJson throws on
                // an unrecognized string), kept as a defensive no-op check for POCOs built by hand.

                // C21 — trigger liveness (see TriggerLivenessTable). A card on a trigger with no raise
                // site parses, validates, loads and does nothing, forever, with no error at runtime;
                // this is the only place that catches it.
                if (!TriggerLivenessTable.TryGetValue(fx.Trigger, out var liveness))
                    liveness = TriggerLiveness.Dead; // fail closed: a new enum member is dead until tabled live
                switch (liveness)
                {
                    case TriggerLiveness.Dead:
                        Err("C21-TRIGGER-DEAD", $"{prefix}.trigger",
                            $"trigger '{fx.Trigger}' has no raise site in the engine — a card using it does nothing, silently. Author only a live trigger (see EF-011..EF-017b).");
                        break;
                    case TriggerLiveness.WeaponOnly when def.Type != CardType.WEAPON:
                        Err("C21-TRIGGER-WEAPON-ONLY", $"{prefix}.trigger",
                            $"trigger '{fx.Trigger}' is raised for weapons only (CommandProcessor.cs:527-531) — it is dead on a {def.Type}.");
                        break;
                }
                // ON_DEATH is live for minions but a WEAPON deathrattle never fires: RulesEngine.BreakWeapon
                // (RulesEngine.cs:90-96) emits onDeath VFX and raises no trigger. Error until EF-020 gives it a
                // raise site — flip this to allow ON_DEATH on a weapon in the SAME commit that lands EF-020.
                if (fx.Trigger == Trigger.ON_DEATH && def.Type == CardType.WEAPON)
                    Err("C21-WEAPON-DEATHRATTLE", $"{prefix}.trigger",
                        "ON_DEATH on a WEAPON never fires — RulesEngine.BreakWeapon raises no trigger (blocked on EF-020).");

                // C10
                if (AmountRequiredActions.Contains(fx.Action) && fx.Amount is null)
                    Err("C10-AMOUNT-REQUIRED", $"{prefix}.amount", $"{fx.Action} requires 'amount'.");

                // C11
                switch (fx.Action)
                {
                    case Action.SUMMON:
                    case Action.ADD_TO_HAND:
                    case Action.TRANSFORM:
                    case Action.EQUIP:
                        if (string.IsNullOrEmpty(fx.TokenId))
                            Err("C11-PAYLOAD-REQUIRED", $"{prefix}.payload.tokenId", $"{fx.Action} requires payload.tokenId.");
                        break;
                    case Action.GIVE_KEYWORD:
                        if (fx.GrantedKeyword is null)
                            Err("C11-PAYLOAD-REQUIRED", $"{prefix}.payload.keyword", "GIVE_KEYWORD requires payload.keyword.");
                        break;
                }

                // C12 / C13
                if (!string.IsNullOrEmpty(fx.TokenId))
                {
                    if (db is null || !db.TryGet(fx.TokenId!, out var token))
                        Err("C12-TOKEN-UNRESOLVED", $"{prefix}.payload.tokenId", $"'{fx.TokenId}' does not resolve in the card database.");
                    else if (fx.Action == Action.SUMMON && token.Collectible)
                        Warn("C13-TOKEN-COLLECTIBLE", $"{prefix}.payload.tokenId", $"SUMMON token '{fx.TokenId}' is collectible:true — tokens are usually collectible:false.");
                }

                // C14
                if (!string.IsNullOrEmpty(fx.Selector) && !ValidSelectors.Contains(fx.Selector!))
                    Err("C14-SELECTOR-INVALID", $"{prefix}.target.selector", $"'{fx.Selector}' is not a known Selector (dev-docs/specs/card-schema.md §5.3).");
                else if (!string.IsNullOrEmpty(fx.Selector) && UnimplementedSelectors.Contains(fx.Selector!)
                         && !def.RequiresEngineeringPrimitive)
                    Err("C14-SELECTOR-UNIMPLEMENTED", $"{prefix}.target.selector",
                        $"'{fx.Selector}' is schema-valid but the engine cannot resolve it yet (the interpreter rejects it at runtime) — set requiresEngineeringPrimitive to hold the row for the primitive.");

                // C15 — GAIN_MANA payload sanity: `empty` modifies a permanent gain only.
                if (fx.Empty && !fx.Permanent)
                    Warn("C15-EMPTY-WITHOUT-PERMANENT", $"{prefix}.payload.empty",
                        "payload.empty has no meaning without payload.permanent — temporary crystals are always filled.");
                if (fx.Permanent && fx.Action != Action.GAIN_MANA)
                    Warn("C15-PERMANENT-NON-MANA", $"{prefix}.payload.permanent",
                        $"payload.permanent is only read by GAIN_MANA; it is inert on {fx.Action}.");

                // C17: VfxOverride is already a typed VfxEvent? — enum parsing enforces the closed
                // vocabulary at load time, so no further check is representable here.
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
