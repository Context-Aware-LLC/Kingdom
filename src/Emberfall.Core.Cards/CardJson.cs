using System;
using System.Collections.Generic;
using System.Text.Json;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>
    /// The single shared parser for the canonical card-row JSON (dev-docs/specs/card-schema.md:5). The Unity SO
    /// and the headless server both deserialize through THIS method, so they can never drift (R1).
    /// Tolerant of missing optional fields; strict enums (an unknown enum string throws, surfacing a
    /// bad row rather than silently mis-parsing it).
    /// </summary>
    public static class CardJson
    {
        private static readonly HashSet<string> CardProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "name", "type", "cost", "attack", "health", "durability", "class", "rarity",
            "setId", "tribe", "collectible", "requiresEngineeringPrimitive", "artId",
            "flavorText", "factionId", "keywords", "rules", "_comment"
        };
        };

        private static readonly HashSet<string> TargetProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "selector", "chosenBy", "count", "filter", "binding", "distinctFrom"
        };

        private static readonly HashSet<string> FilterProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "damaged", "tribe"
        };

        private static readonly HashSet<string> RuleProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "activation", "condition", "lifetime", "action"
        };

        public static CardDefinition Deserialize(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return FromElement(doc.RootElement);
        }

        /// <summary>Parse a flat array of rows (used by the pinned cardset snapshot, dev-docs/pipelines/card-authoring-and-import.md:5).</summary>
        public static IReadOnlyList<CardDefinition> DeserializeArray(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var list = new List<CardDefinition>();
            foreach (var el in doc.RootElement.EnumerateArray())
                list.Add(FromElement(el));
            return list;
        }

        public static CardDefinition FromElement(JsonElement el)
        {
            RejectUnknownProperties(el, "card", CardProperties);

            string id = Str(el, "id") ?? throw new FormatException("Card row missing 'id'.");
            string name = Str(el, "name") ?? id;
            CardType type = ReqEnum<CardType>(el, "type");
            int cost = Int(el, "cost") ?? 0;
            int? attack = Int(el, "attack");
            int? health = Int(el, "health");
            int? durability = Int(el, "durability");
            CardClass cls = TryEnum<CardClass>(el, "class") ?? CardClass.NEUTRAL;
            Rarity rarity = TryEnum<Rarity>(el, "rarity") ?? Rarity.COMMON;
            SetId setId = TryEnum<SetId>(el, "setId") ?? SetId.CORE;
            Tribe? tribe = TryEnum<Tribe>(el, "tribe");
            bool collectible = Bool(el, "collectible") ?? true;
            bool reqPrim = Bool(el, "requiresEngineeringPrimitive") ?? false;
            string artId = Str(el, "artId") ?? "";
            string flavor = Str(el, "flavorText") ?? "";
            string faction = Str(el, "factionId") ?? "";

            var keywords = new List<Keyword>();
            if (el.TryGetProperty("keywords", out var kw) && kw.ValueKind == JsonValueKind.Array)
                foreach (var k in kw.EnumerateArray())
                    keywords.Add((Keyword)Enum.Parse(typeof(Keyword), k.GetString()!, ignoreCase: false));

            var rules = new List<RuleDefinition>();
            if (el.TryGetProperty("rules", out var ruleElements))
            {
                if (ruleElements.ValueKind != JsonValueKind.Array)
                    throw new FormatException("Card JSON rules must be an array.");
                foreach (var rule in ruleElements.EnumerateArray()) rules.Add(RuleFromElement(rule));
            return new CardDefinition(id, name, type, cost, attack, health, durability, cls, rarity,
                keywords, rules, flavor, artId, setId, collectible, tribe, faction, reqPrim);
        }

        private static RuleDefinition RuleFromElement(JsonElement element)
        {
            RejectUnknownProperties(element, "rule", RuleProperties);
            var id = new RuleId(RequiredString(element, "id", "rule.id"));
            RuleActivation activation = ActivationFromElement(
                RequiredObject(element, "activation", "rule.activation"));
            RuleCondition condition = ConditionFromElement(
                RequiredObject(element, "condition", "rule.condition"));
            RuleLifetime lifetime = LifetimeFromElement(
                RequiredObject(element, "lifetime", "rule.lifetime"));
            RuleAction action = ActionFromElement(
                RequiredObject(element, "action", "rule.action"));
            return new RuleDefinition(id, activation, condition, lifetime, action);
        }

        private static RuleActivation ActivationFromElement(JsonElement element)
        {
            string type = RequiredString(element, "type", "rule.activation.type");
            switch (type)
            {
                case "TRIGGERED":
                    RejectUnknownProperties(element, "rule.activation", Set("type", "trigger"));
                    return new TriggeredActivation(ReqEnum<Trigger>(element, "trigger"));
                case "AURA":
                    RejectUnknownProperties(element, "rule.activation", Set("type"));
                    return AuraActivation.Instance;
                default:
                    throw new FormatException($"Unknown rule activation type '{type}'.");
            }
        }

        private static RuleCondition ConditionFromElement(JsonElement element)
        {
            string type = RequiredString(element, "type", "rule.condition.type");
            switch (type)
            {
                case "ALWAYS":
                    RejectUnknownProperties(element, "rule.condition", Set("type"));
                    return AlwaysCondition.Instance;
                case "TURN_OWNER":
                    RejectUnknownProperties(element, "rule.condition", Set("type", "owner"));
                    return new TurnOwnerCondition(ReqEnum<TurnOwner>(element, "owner"));
                default:
                    throw new FormatException($"Unknown rule condition type '{type}'.");
            }
        }

        private static RuleLifetime LifetimeFromElement(JsonElement element)
        {
            string type = RequiredString(element, "type", "rule.lifetime.type");
            switch (type)
            {
                case "SOURCE_BOUND":
                    RejectUnknownProperties(element, "rule.lifetime", Set("type"));
                    return SourceBoundLifetime.Instance;
                case "ONE_SHOT":
                    RejectUnknownProperties(element, "rule.lifetime", Set("type"));
                    return OneShotLifetime.Instance;
                case "FINITE":
                    RejectUnknownProperties(element, "rule.lifetime", Set("type", "uses"));
                    return new FiniteLifetime(RequiredInt(element, "uses", "rule.lifetime.uses"));
                case "PERMANENT":
                    RejectUnknownProperties(element, "rule.lifetime", Set("type"));
                    return PermanentLifetime.Instance;
                default:
                    throw new FormatException($"Unknown rule lifetime type '{type}'.");
            }
        }

        private static RuleAction ActionFromElement(JsonElement element)
        {
            string type = RequiredString(element, "type", "rule.action.type");
            switch (type)
            {
                case "DAMAGE":
                    Fields(element, type, "amount", "target");
                    return new DamageAction(Amount(element), TargetFromElement(element));
                case "HEAL":
                    Fields(element, type, "amount", "target");
                    return new HealAction(Amount(element), TargetFromElement(element));
                case "MODIFY_STATS":
                    Fields(element, type, "attack", "health", "target");
                    return new ModifyStatsAction(
                        RequiredInt(element, "attack", "rule.action.attack"),
                        RequiredInt(element, "health", "rule.action.health"), TargetFromElement(element));
                case "REINFORCE":
                    Fields(element, type, "target");
                    return new ReinforceAction(TargetFromElement(element));
                case "GRANT_KEYWORD":
                    Fields(element, type, "keyword", "target");
                    return new GrantKeywordAction(ReqEnum<Keyword>(element, "keyword"), TargetFromElement(element));
                case "DESTROY":
                    Fields(element, type, "target");
                    return new DestroyAction(TargetFromElement(element));
                case "RETURN_TO_HAND":
                    Fields(element, type, "target");
                    return new ReturnToHandAction(TargetFromElement(element));
                case "SILENCE":
                    Fields(element, type, "target");
                    return new SilenceAction(TargetFromElement(element));
                case "TRANSFORM":
                    Fields(element, type, "cardId", "target");
                    return new TransformAction(RequiredString(element, "cardId", "rule.action.cardId"),
                        TargetFromElement(element));
                case "FROST":
                    Fields(element, type, "target");
                    return new FrostAction(TargetFromElement(element));
                case "TAKE_CONTROL":
                    Fields(element, type, "target");
                    return new TakeControlAction(TargetFromElement(element));
                case "DRAW":
                    Fields(element, type, "count");
                    return new DrawAction(Count(element));
                case "SUMMON":
                    Fields(element, type, "cardId", "count");
                    return new SummonAction(RequiredString(element, "cardId", "rule.action.cardId"), Count(element));
                case "ADD_TO_HAND":
                    Fields(element, type, "cardId", "count");
                    return new AddToHandAction(RequiredString(element, "cardId", "rule.action.cardId"), Count(element));
                case "EQUIP":
                    Fields(element, type, "cardId");
                    return new EquipAction(RequiredString(element, "cardId", "rule.action.cardId"));
                case "GAIN_MANA":
                    Fields(element, type, "amount", "manaKind");
                    return new GainManaAction(Amount(element), ReqEnum<ManaGainKind>(element, "manaKind"));
                case "BACKDRAFT":
                    Fields(element, type, "amount");
                    return new BackdraftAction(Amount(element));
                case "GAIN_ARMOR":
                    Fields(element, type, "amount");
                    return new GainArmorAction(Amount(element));
                case "UNEARTH":
                    Fields(element, type, "count");
                    return new UnearthAction(Count(element));
                default:
                    throw new FormatException($"Unknown rule action type '{type}'.");
            }
        }

        private static int Amount(JsonElement element)
            => RequiredInt(element, "amount", "rule.action.amount");

        private static int Count(JsonElement element)
            => RequiredInt(element, "count", "rule.action.count");

        private static void Fields(JsonElement element, string type, params string[] fields)
        {
            var allowed = new HashSet<string>(StringComparer.Ordinal) { "type" };
            foreach (string field in fields) allowed.Add(field);
            RejectUnknownProperties(element, $"rule.action {type}", allowed);
        }

        private static HashSet<string> Set(params string[] fields)
            => new HashSet<string>(fields, StringComparer.Ordinal);

        private static JsonElement RequiredObject(JsonElement element, string name, string path)
        {
            if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
                throw new FormatException($"Card JSON {path} must be an object.");
            return value;
        }

        private static int RequiredInt(JsonElement element, string name, string path)
        {
            if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetInt32(out int result))
                throw new FormatException($"Card JSON {path} must be an integer.");
            return result;
        }

        private static TargetSpec TargetFromElement(JsonElement owner)
        {
            JsonElement target = owner;
            if (owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("target", out JsonElement nested))
                target = nested;
            if (target.ValueKind != JsonValueKind.Object)
                throw new FormatException("Card JSON target must be an object.");
            RejectUnknownProperties(target, "target", TargetProperties);
            string selector = RequiredString(target, "selector", "target.selector");
            TargetFilterDef? filter = null;
            if (target.TryGetProperty("filter", out var f))
            {
                if (f.ValueKind != JsonValueKind.Object)
                    throw new FormatException("Card JSON target.filter must be an object.");
                RejectUnknownProperties(f, "target.filter", FilterProperties);
                bool? damaged = null;
                if (f.TryGetProperty("damaged", out var damagedValue))
                {
                    if (damagedValue.ValueKind != JsonValueKind.True && damagedValue.ValueKind != JsonValueKind.False)
                        throw new FormatException("Card JSON target.filter.damaged must be a boolean.");
                    damaged = damagedValue.GetBoolean();
                }
                Tribe? tribe = null;
                if (f.TryGetProperty("tribe", out var tribeValue))
                {
                    if (tribeValue.ValueKind != JsonValueKind.String)
                        throw new FormatException("Card JSON target.filter.tribe must be a string.");
                    tribe = (Tribe)Enum.Parse(typeof(Tribe), tribeValue.GetString()!, ignoreCase: false);
                }
                filter = new TargetFilterDef(damaged, tribe);
            }
            return new TargetSpec(selector, TryEnum<TargetChosenBy>(target, "chosenBy"),
                Int(target, "count"), filter, Str(target, "binding"), Str(target, "distinctFrom"));
        }

        private static void RejectUnknownProperties(JsonElement el, string schemaLevel, HashSet<string> allowed)
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new FormatException($"Card JSON {schemaLevel} must be an object.");

            foreach (var property in el.EnumerateObject())
            {
                if (!allowed.Contains(property.Name))
                    throw new FormatException($"Unknown card JSON property '{property.Name}' in {schemaLevel}.");
            }
        }

        // ── primitive readers ────────────────────────────────────────────────────
        private static string? Str(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string RequiredString(JsonElement el, string name, string path)
        {
            if (!el.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
                throw new FormatException($"Card JSON {path} must be a string.");
            return value.GetString()!;
        }

        private static int? Int(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null;

        private static bool? Bool(JsonElement el, string name)
        {
            if (!el.TryGetProperty(name, out var v)) return null;
            return v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : (bool?)null;
        }

        private static T ReqEnum<T>(JsonElement el, string name) where T : struct, System.Enum
        {
            string? s = Str(el, name) ?? throw new FormatException($"Missing required enum '{name}'.");
            return (T)System.Enum.Parse(typeof(T), s, ignoreCase: false);
        }

        private static T? TryEnum<T>(JsonElement el, string name) where T : struct, System.Enum
        {
            string? s = Str(el, name);
            if (string.IsNullOrEmpty(s)) return null;
            return (T)System.Enum.Parse(typeof(T), s, ignoreCase: false);
        }
    }
}
