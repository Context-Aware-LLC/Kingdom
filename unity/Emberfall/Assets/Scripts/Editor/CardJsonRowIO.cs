using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Emberfall.Core.Cards;

namespace Emberfall.Editor
{
    /// <summary>
    /// Editor-owned load/save over the flat M1 card-row array
    /// (unity/Emberfall/Assets/Resources/cards/M1.cards.json). This is the pragmatic stand-in for
    /// the spec's per-file `cards/&lt;setId&gt;/&lt;id&gt;.card.json` + ScriptedImporter convention
    /// (dev-docs/pipelines/card-authoring-and-import.md:5) — deliberately not adopted this pass since the M1 set
    /// already lives as one file; see the plan's "explicitly deferred" list.
    ///
    /// Reuses the shared <see cref="CardJson"/> parser for reads (no second deserializer, R1) and
    /// adds the one thing core doesn't have: a canonical writer, since CardJson has no Serialize.
    /// </summary>
    public static class CardJsonRowIO
    {
        // Migrated out of Resources/ (M2 D1) — served to the runtime via the CardData
        // Addressables group at address "cards/M1.cards".
        public const string DefaultM1JsonPath = "Assets/CardData/M1.cards.json";

        /// <summary>Loads every row as (parsed definition, verbatim source JSON for that row).</summary>
        public static IReadOnlyList<(CardDefinition def, string json)> LoadRows(string path)
        {
            string text = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(text);
            var list = new List<(CardDefinition, string)>();
            foreach (var el in doc.RootElement.EnumerateArray())
                list.Add((CardJson.FromElement(el), el.GetRawText()));
            return list;
        }

        /// <summary>Writes the full row list back as one formatted JSON array, stable field order.</summary>
        public static void SaveRows(string path, IReadOnlyList<CardDefinition> defs)
        {
            var sb = new StringBuilder();
            sb.Append("[\n");
            for (int i = 0; i < defs.Count; i++)
            {
                sb.Append("  ").Append(Serialize(defs[i]));
                if (i < defs.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("]\n");
            File.WriteAllText(path, sb.ToString());
        }

        /// <summary>Canonical single-row serialization (hist-docs/source-snapshots/2026-07-31-pre-reorganization/tooling/card-authoring-tool.md §6.3 "stable key order").</summary>
        public static string Serialize(CardDefinition def)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                w.WriteStartObject();
                w.WriteString("id", def.Id);
                w.WriteString("name", def.Name);
                w.WriteString("type", def.Type.ToString());
                w.WriteNumber("cost", def.Cost);
                if (def.Attack is int atk) w.WriteNumber("attack", atk);
                if (def.Health is int hp) w.WriteNumber("health", hp);
                if (def.Durability is int dur) w.WriteNumber("durability", dur);
                w.WriteString("class", def.Class.ToString());
                w.WriteString("rarity", def.Rarity.ToString());

                w.WriteStartArray("keywords");
                foreach (var kw in def.Keywords) w.WriteStringValue(kw.ToString());
                w.WriteEndArray();

                if (def.Tribe is { } tribe) w.WriteString("tribe", tribe.ToString());
                w.WriteString("setId", def.SetId.ToString());
                w.WriteBoolean("collectible", def.Collectible);
                w.WriteString("artId", def.ArtId);
                if (!string.IsNullOrEmpty(def.CardText)) w.WriteString("cardText", def.CardText);
                if (!string.IsNullOrEmpty(def.FlavorText)) w.WriteString("flavorText", def.FlavorText);
                if (!string.IsNullOrEmpty(def.FactionId)) w.WriteString("factionId", def.FactionId);
                if (def.RequiresEngineeringPrimitive) w.WriteBoolean("requiresEngineeringPrimitive", true);

                if (def.Effects.Count > 0)
                {
                    w.WriteStartArray("effects");
                    foreach (var fx in def.Effects)
                    {
                        w.WriteStartObject();
                        w.WriteString("trigger", fx.Trigger.ToString());
                        if (fx.Condition is { } condition)
                        {
                            w.WriteStartObject("condition");
                            w.WriteString("type", condition.Type.ToString());
                            if (condition.TurnOwner is { } owner) w.WriteString("value", owner.ToString());
                            w.WriteEndObject();
                        }
                        w.WriteString("action", fx.Action.ToString());
                        if (fx.Amount is int a) w.WriteNumber("amount", a);
                        if (fx.Amount2 is int a2) w.WriteNumber("amount2", a2);
                        if (fx.VfxOverride is { } vfx) w.WriteString("vfxEvent", vfx.ToString());
                        if (!string.IsNullOrEmpty(fx.TokenId) || fx.GrantedKeyword is not null
                            || fx.Permanent || fx.Empty)
                        {
                            w.WriteStartObject("payload");
                            if (!string.IsNullOrEmpty(fx.TokenId)) w.WriteString("tokenId", fx.TokenId);
                            if (fx.GrantedKeyword is { } kw) w.WriteString("keyword", kw.ToString());
                            if (fx.Permanent) w.WriteBoolean("permanent", true);
                            if (fx.Empty) w.WriteBoolean("empty", true);
                            w.WriteEndObject();
                        }
                        if (!string.IsNullOrEmpty(fx.Selector))
                        {
                            w.WriteStartObject("target");
                            w.WriteString("selector", fx.Selector);
                            w.WriteEndObject();
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }

                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }
}
