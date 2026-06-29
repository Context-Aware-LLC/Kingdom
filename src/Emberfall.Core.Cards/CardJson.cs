using System;
using System.Collections.Generic;
using System.Text.Json;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Cards
{
    /// <summary>
    /// The single shared parser for the canonical card-row JSON (card-system.md §1, §2). The Unity SO
    /// and the headless server both deserialize through THIS method, so they can never drift (R1).
    /// Tolerant of missing optional fields; strict enums (an unknown enum string throws, surfacing a
    /// bad row rather than silently mis-parsing it).
    /// </summary>
    public static class CardJson
    {
        public static CardDefinition Deserialize(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return FromElement(doc.RootElement);
        }

        /// <summary>Parse a flat array of rows (used by the pinned cardset snapshot, golden-replay.md §3.3).</summary>
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
            Trigger trigger = ReqEnum<Trigger>(e, "trigger");
            Contracts.Action action = ReqEnum<Contracts.Action>(e, "action");
            int? amount = Int(e, "amount");
            int? amount2 = Int(e, "amount2");
            VfxEvent? vfx = TryEnum<VfxEvent>(e, "vfxEvent");

            string? tokenId = null;
            Keyword? grantedKeyword = null;
            if (e.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                tokenId = Str(payload, "tokenId");
                grantedKeyword = TryEnum<Keyword>(payload, "keyword");
            }

            string? selector = null;
            if (e.TryGetProperty("target", out var tgt) && tgt.ValueKind == JsonValueKind.Object)
                selector = Str(tgt, "selector");

            return new EffectDef(trigger, action, amount, amount2, tokenId, grantedKeyword, vfx, selector);
        }

        // ── primitive readers ────────────────────────────────────────────────────
        private static string? Str(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

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
