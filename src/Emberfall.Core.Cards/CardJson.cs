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
            "setId", "tribe", "collectible", "requiresEngineeringPrimitive", "artId", "cardText",
            "flavorText", "factionId", "keywords", "effects", "_comment"
        };

        private static readonly HashSet<string> EffectProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "trigger", "action", "amount", "amount2", "vfxEvent", "payload", "target", "condition"
        };

        private static readonly HashSet<string> PayloadProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "tokenId", "keyword", "permanent", "empty"
        };

        private static readonly HashSet<string> TargetProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "selector", "chosenBy", "count", "filter", "binding", "distinctFrom"
        };

        private static readonly HashSet<string> FilterProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "damaged", "tribe"
        };

        private static readonly HashSet<string> ConditionProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            "type", "value"
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
            string cardText = Str(el, "cardText") ?? "";
            string flavor = Str(el, "flavorText") ?? "";
            string faction = Str(el, "factionId") ?? "";

            var keywords = new List<Keyword>();
            if (el.TryGetProperty("keywords", out var kw) && kw.ValueKind == JsonValueKind.Array)
                foreach (var k in kw.EnumerateArray())
                    keywords.Add((Keyword)Enum.Parse(typeof(Keyword), k.GetString()!, ignoreCase: false));

            var effects = new List<EffectDef>();
            if (el.TryGetProperty("effects", out var fx) && fx.ValueKind == JsonValueKind.Array)
                foreach (var e in fx.EnumerateArray())
                    effects.Add(EffectFromElement(e));

            return new CardDefinition(id, name, type, cost, attack, health, durability, cls, rarity,
                keywords, effects, cardText, flavor, artId, setId, collectible, tribe, faction, reqPrim);
        }

        private static EffectDef EffectFromElement(JsonElement e)
        {
            RejectUnknownProperties(e, "effect", EffectProperties);

            Trigger trigger = ReqEnum<Trigger>(e, "trigger");
            Contracts.Action action = ReqEnum<Contracts.Action>(e, "action");
            int? amount = Int(e, "amount");
            int? amount2 = Int(e, "amount2");
            VfxEvent? vfx = TryEnum<VfxEvent>(e, "vfxEvent");

            string? tokenId = null;
            Keyword? grantedKeyword = null;
            bool permanent = false;
            bool empty = false;
            if (e.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                RejectUnknownProperties(payload, "payload", PayloadProperties);
                tokenId = Str(payload, "tokenId");
                grantedKeyword = TryEnum<Keyword>(payload, "keyword");
                permanent = Bool(payload, "permanent") ?? false;
                empty = Bool(payload, "empty") ?? false;
            }

            TargetSpec? target = null;
            if (e.TryGetProperty("target", out var tgt))
            {
                if (tgt.ValueKind != JsonValueKind.Object)
                    throw new FormatException("Card JSON target must be an object.");
                RejectUnknownProperties(tgt, "target", TargetProperties);
                string selector = RequiredString(tgt, "selector", "target.selector");
                TargetFilterDef? filter = null;
                if (tgt.TryGetProperty("filter", out var f))
                {
                    if (f.ValueKind != JsonValueKind.Object)
                        throw new FormatException("Card JSON target.filter must be an object.");
                    RejectUnknownProperties(f, "target.filter", FilterProperties);
                    bool? damaged = null;
                    if (f.TryGetProperty("damaged", out var damagedValue))
                    {
                        if (damagedValue.ValueKind != JsonValueKind.True &&
                            damagedValue.ValueKind != JsonValueKind.False)
                            throw new FormatException("Card JSON target.filter.damaged must be a boolean.");
                        damaged = damagedValue.GetBoolean();
                    }
                    Tribe? tribe = null;
                    if (f.TryGetProperty("tribe", out var tribeValue))
                    {
                        if (tribeValue.ValueKind != JsonValueKind.String)
                            throw new FormatException("Card JSON target.filter.tribe must be a string.");
                        tribe = (Tribe)System.Enum.Parse(typeof(Tribe), tribeValue.GetString()!, ignoreCase: false);
                    }
                    filter = new TargetFilterDef(damaged, tribe);
                }

                target = new TargetSpec(
                    selector,
                    TryEnum<TargetChosenBy>(tgt, "chosenBy"),
                    Int(tgt, "count"),
                    filter,
                    Str(tgt, "binding"),
                    Str(tgt, "distinctFrom"));
            }

            EffectConditionDef? condition = null;
            if (e.TryGetProperty("condition", out var gate) && gate.ValueKind == JsonValueKind.Object)
            {
                RejectUnknownProperties(gate, "condition", ConditionProperties);
                var type = ReqEnum<ConditionType>(gate, "type");
                TurnOwner? turnOwner = type == ConditionType.TURN_OWNER
                    ? TryEnum<TurnOwner>(gate, "value")
                    : null;
                condition = new EffectConditionDef(type, turnOwner);
            }

            return new EffectDef(trigger, action, amount, amount2, tokenId, grantedKeyword, vfx,
                permanent: permanent, empty: empty, condition: condition, target: target);
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
