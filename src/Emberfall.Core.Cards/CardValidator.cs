using System.Collections.Generic;
using System.Text.RegularExpressions;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>
    /// The shared, headless card-legality validator (C1-C20, tooling/validation-and-import.md §3).
    /// Runs identically at server/client load and inside the editor (interfaces.md §4.3) — this
    /// assembly has zero UnityEngine references, so this type is safe to run on the dedicated server.
    /// Content/art/budget lint (the E-series) is a separate, editor-only IContentLinter — never here.
    /// </summary>
    public sealed class CardValidator : ICardValidator
    {
        private static readonly Regex IdPattern = new Regex("^[a-z0-9_]+$", RegexOptions.Compiled);

        // Selector is a closed vocabulary (design/card-schema.md:169) but carried as a plain string
        // on EffectDef today rather than a structured TargetSpec/enum — validate against the list.
        private static readonly HashSet<string> ValidSelectors = new HashSet<string>
        {
            "SELF", "FRIENDLY_MINIONS", "ENEMY_MINIONS", "ALL_MINIONS", "FRIENDLY_HERO",
            "ENEMY_HERO", "ANY_CHARACTER", "ANY_FRIENDLY", "ANY_ENEMY", "ADJACENT", "TRIGGER_SOURCE",
        };

        // Verbs whose EffectDef.Amount is required (C10). Extends the tooling-doc list with the
        // verbs actually present in the Action enum/M1 data (OVERLOAD, FREEZE) that doc predates.
        private static readonly HashSet<Action> AmountRequiredActions = new HashSet<Action>
        {
            Action.DEAL_DAMAGE, Action.HEAL, Action.DRAW, Action.SUMMON, Action.BUFF,
            Action.GAIN_MANA, Action.DISCOVER, Action.ADD_TO_HAND, Action.OVERLOAD,
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
            // (Editor Tooling's CardSetValidator, tooling/validation-and-import.md §6) checks it by
            // scanning all ids in the loaded set for collisions before calling Validate per-card.

            // C3
            if (string.IsNullOrEmpty(def.Name) || def.Name.Length < 1 || def.Name.Length > 32)
                Err("C3-NAME-LENGTH", "name", "name must be 1-32 characters.");

            // C4 / C5 / C6 — per-type stat shape (card-schema.md §2.1)
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

            // C8 — numeric stat ranges (card-schema.md §2)
            if (def.Attack is int atk && (atk < 0 || atk > 30))
                Err("C8-ATTACK-RANGE", "attack", "attack must be 0-30.");
            if (def.Health is int hp && (hp < 1 || hp > 30))
                Err("C8-HEALTH-RANGE", "health", "health must be 1-30.");
            if (def.Durability is int dur && (dur < 1 || dur > 8))
                Err("C8-DURABILITY-RANGE", "durability", "durability must be 1-8.");

            // C9-C11, C13, C14, C17 — per-effect checks
            for (int i = 0; i < def.Effects.Count; i++)
            {
                var fx = def.Effects[i];
                string prefix = $"effects[{i}]";

                // C9: Action is a known verb — guaranteed by the enum type system (CardJson throws on
                // an unrecognized string), kept as a defensive no-op check for POCOs built by hand.

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
                    Err("C14-SELECTOR-INVALID", $"{prefix}.target.selector", $"'{fx.Selector}' is not a known Selector (card-schema.md §5.3).");

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
        /// (Editor Tooling's CardSetValidator, tooling/validation-and-import.md §6) should run this
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
