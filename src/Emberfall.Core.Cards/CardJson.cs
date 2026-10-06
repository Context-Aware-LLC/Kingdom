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
            // Exhaustive: RejectUnknownProperties throws on any property absent from this set, so a
            // row carrying a retired one fails loudly instead of being silently ignored.
            "id", "handle", "name", "type", "cost", "attack", "health", "durability", "class", "rarity",
            "setId", "tribe", "collectible", "lifecycle", "artId",
            "flavorText", "tooltip", "factionId", "keywords", "text", "rules", "soundEffects", "_comment"
        };

        private static readonly HashSet<string> SoundEffectProperties =
            new HashSet<string>(new[] { "soundId", "trigger" }, StringComparer.Ordinal);

        private static readonly HashSet<string> TargetProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "selector", "selection", "filter", "binding", "distinctFrom", "cost"
        };

        private static readonly HashSet<string> FilterProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "damaged", "tribe", "negateTribe", "maxCost", "negateCost", "keyword", "negateKeyword"
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

            // 'id' is a NUMBER on a card row and a STRING on a rule row -- two different kinds of
            // id that happen to share a property name. A row whose id still parses as a string is a
            // pre-migration row, and saying so beats a JsonException about a token type.
            int id = Int(el, "id") ?? throw new FormatException(
                el.TryGetProperty("id", out var raw) && raw.ValueKind == JsonValueKind.String
                    ? $"Card row 'id' is the string '{raw.GetString()}'. The numeric id is the key " +
                      "now and the mnemonic moved to 'handle' -- run tooling/scripts/assign_numeric_card_ids.py."
                    : "Card row missing 'id'.");
            string handle = Str(el, "handle") ?? "";
            string name = Str(el, "name") ?? handle;
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
            CardLifecycle lifecycle = TryEnum<CardLifecycle>(el, "lifecycle") ?? CardLifecycle.PUBLISHED;
            string artId = Str(el, "artId") ?? "";
            string text = Str(el, "text") ?? "";
            string flavor = Str(el, "flavorText") ?? "";
            string tooltip = Str(el, "tooltip") ?? "";
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
            }

            var soundEffects = new List<CardSoundEffect>();
            if (el.TryGetProperty("soundEffects", out var soundElements))
            {
                if (soundElements.ValueKind != JsonValueKind.Array)
                    throw new FormatException("Card JSON soundEffects must be an array.");
                foreach (JsonElement sound in soundElements.EnumerateArray())
                {
                    RejectUnknownProperties(sound, "sound effect", SoundEffectProperties);
                    soundEffects.Add(new CardSoundEffect(
                        RequiredString(sound, "soundId", "soundEffects[].soundId"),
                        ReqEnum<Trigger>(sound, "trigger")));
                }
            }
            return new CardDefinition(id, handle, name, type, cost, attack, health, durability, cls, rarity,
                keywords, rules, flavor, artId, setId, collectible, tribe, faction, tooltip, text,
                soundEffects, lifecycle);
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
                case "COST_PAID":
                    RejectUnknownProperties(element, "rule.condition", Set("type"));
                    return CostPaidCondition.Instance;
                case "CONTROLS":
                    RejectUnknownProperties(element, "rule.condition", Set("type", "tribe", "other"));
                    Tribe named = ReqEnum<Tribe>(element, "tribe");
                    if (named == Tribe.NONE)
                        throw new FormatException(
                            "rule.condition.tribe is NONE. A CONTROLS condition names a tribe to " +
                            "look for, and NONE is the absence of one.");
                    return new ControlsCondition(named, Bool(element, "other") ?? false);
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
                case "GRANT_KEYWORD":
                    Fields(element, type, "keyword", "target");
                    return new GrantKeywordAction(ReqEnum<Keyword>(element, "keyword"), TargetFromElement(element));
                case "DESTROY":
                    Fields(element, type, "target");
                    return new DestroyAction(TargetFromElement(element));
                case "RETURN_TO_HAND":
                    Fields(element, type, "target");
                    return new ReturnToHandAction(TargetFromElement(element));
                case "REANIMATE":
                    Fields(element, type, "target");
                    return new ReanimateAction(TargetFromElement(element));
                case "SILENCE":
                    Fields(element, type, "target");
                    return new SilenceAction(TargetFromElement(element));
                case "TRANSFORM":
                    Fields(element, type, "cardId", "target");
                    return new TransformAction(RequiredCardId(element, "rule.action.cardId"),
                        TargetFromElement(element));
                case "FROST":
                    Fields(element, type, "target", "negate");
                    return new FrostAction(TargetFromElement(element),
                        negate: OptionalBool(element, "negate", "rule.action.negate"));
                case "DROWN":
                    Fields(element, type, "target");
                    return new DrownAction(TargetFromElement(element));
                case "COPY":
                    Fields(element, type, "target");
                    return new CopyAction(TargetFromElement(element));
                case "TAKE_CONTROL":
                    Fields(element, type, "target");
                    return new TakeControlAction(TargetFromElement(element));
                case "TRIGGER_TOLL":
                    Fields(element, type, "target");
                    return new TriggerTollAction(TargetFromElement(element));
                case "DRAW":
                    Fields(element, type, "count");
                    return new DrawAction(Count(element));
                case "SUMMON":
                    Fields(element, type, "cardId", "count");
                    return new SummonAction(RequiredCardId(element, "rule.action.cardId"), Count(element));
                case "ADD_TO_HAND":
                    Fields(element, type, "cardId", "count");
                    return new AddToHandAction(RequiredCardId(element, "rule.action.cardId"), Count(element));
                case "EQUIP":
                    Fields(element, type, "cardId");
                    return new EquipAction(RequiredCardId(element, "rule.action.cardId"));
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
                    // No fields. `count` was removed 2026-09-04 and is now REJECTED rather than
                    // ignored, so a row still carrying one fails loudly at load instead of quietly
                    // meaning something it no longer says. Greenfield: conform the content.
                    Fields(element, type);
                    return new UnearthAction();
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
                bool negateTribe = OptionalBool(f, "negateTribe", "target.filter.negateTribe");
                int? maxCost = null;
                if (f.TryGetProperty("maxCost", out var maxCostValue))
                {
                    if (maxCostValue.ValueKind != JsonValueKind.Number ||
                        !maxCostValue.TryGetInt32(out int parsedMaxCost))
                        throw new FormatException("Card JSON target.filter.maxCost must be an integer.");
                    maxCost = parsedMaxCost;
                }
                bool negateCost = OptionalBool(f, "negateCost", "target.filter.negateCost");
                Keyword? keyword = null;
                if (f.TryGetProperty("keyword", out var keywordValue))
                {
                    if (keywordValue.ValueKind != JsonValueKind.String)
                        throw new FormatException("Card JSON target.filter.keyword must be a string.");
                    keyword = (Keyword)Enum.Parse(typeof(Keyword), keywordValue.GetString()!, ignoreCase: false);
                }
                bool negateKeyword = OptionalBool(f, "negateKeyword", "target.filter.negateKeyword");
                filter = new TargetFilterDef(damaged, tribe, negateTribe, maxCost, negateCost,
                    keyword, negateKeyword);
            }
            // A cost is spelled out rather than flagged: "required" and "optional" are two
            // different cards, and a boolean would have to name one of them by its absence.
            CostKind cost = CostKind.None;
            if (target.TryGetProperty("cost", out var costValue))
            {
                if (costValue.ValueKind != JsonValueKind.String ||
                    !Enum.TryParse(costValue.GetString(), ignoreCase: true, out cost) ||
                    cost == CostKind.None)
                    throw new FormatException(
                        "Card JSON target.cost must be \"required\" or \"optional\".");
            }
            // Read last so a target that is wrong in two ways reports its selector first; the
            // selector is what an author has to fix before the term even means anything.
            string term = RequiredString(target, "selection", "target.selection");
            if (!TargetSelection.TryParse(term, out TargetSelection selection))
                throw new FormatException($"Card JSON target.selection '{term}' is not a selection " +
                    "term. Write \"all\", \"player: N\" or \"random: N\", N being 1 or more.");
            return new TargetSpec(selector, selection, filter, Str(target, "binding"),
                Str(target, "distinctFrom"), cost);
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

        /// <summary>
        /// Reads a card id a rule names. Separate from <see cref="RequiredString"/> because the
        /// property is a NUMBER here and a string on the rule row itself — the same name, two
        /// kinds of id — and because a row still carrying a handle needs to say so rather than
        /// surface as a token-type mismatch.
        /// </summary>
        private static int RequiredCardId(JsonElement element, string field)
        {
            if (!element.TryGetProperty("cardId", out var value))
                throw new FormatException($"{field} is required.");
            if (value.ValueKind == JsonValueKind.String)
                throw new FormatException(
                    $"{field} is the string '{value.GetString()}'. A rule resolves a card by its " +
                    "number now — run tooling/scripts/assign_numeric_card_ids.py.");
            if (!value.TryGetInt32(out int id))
                throw new FormatException($"{field} is not a whole number.");
            return id;
        }

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

        /// <summary>An optional boolean that, unlike <see cref="Bool"/>, rejects a wrong-shaped value
        /// rather than silently reading it as absent — for fields like the filter negate bits, where
        /// a malformed value is an authoring mistake worth surfacing at load.</summary>
        private static bool OptionalBool(JsonElement el, string name, string path)
        {
            if (!el.TryGetProperty(name, out var v)) return false;
            if (v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False)
                throw new FormatException($"Card JSON {path} must be a boolean.");
            return v.GetBoolean();
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
