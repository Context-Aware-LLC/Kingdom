using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Emberfall.Core.Cards;

namespace Emberfall.Editor
{
    /// <summary>
    /// Editor-owned load/save over the flat Emberfall card-row array
    /// (unity/Emberfall/Assets/Resources/cards/Emberfall.cards.json). This is the pragmatic stand-in for
    /// the spec's per-file `cards/&lt;setId&gt;/&lt;id&gt;.card.json` + ScriptedImporter convention
    /// (dev-docs/pipelines/card-authoring-and-import.md:5) — deliberately not adopted this pass since the
    /// Emberfall set already lives as one file; see the plan's "explicitly deferred" list.
    ///
    /// Reuses the shared <see cref="CardJson"/> parser for reads (no second deserializer, R1) and
    /// adds the one thing core doesn't have: a canonical writer, since CardJson has no Serialize.
    /// </summary>
    public static class CardJsonRowIO
    {
        // Migrated out of Resources/ (M2 D1) — served to the runtime via the CardData
        // Addressables group at address "cards/Emberfall.cards".
        public const string DefaultEmberfallJsonPath = "Assets/CardData/Emberfall.cards.json";

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
                if (!string.IsNullOrEmpty(def.FlavorText)) w.WriteString("flavorText", def.FlavorText);
                if (!string.IsNullOrEmpty(def.FactionId)) w.WriteString("factionId", def.FactionId);

                w.WriteStartArray("rules");
                foreach (var rule in def.Rules)
                {
                    w.WriteStartObject();
                    w.WriteString("id", rule.Id.Value);
                    WriteActivation(w, rule.Activation);
                    WriteCondition(w, rule.Condition);
                    WriteLifetime(w, rule.Lifetime);
                    WriteAction(w, rule.Action);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteActivation(Utf8JsonWriter w, RuleActivation activation)
        {
            w.WriteStartObject("activation");
            if (activation is TriggeredActivation triggered)
            {
                w.WriteString("type", "TRIGGERED");
                w.WriteString("trigger", triggered.Trigger.ToString());
            }
            else if (activation is AuraActivation)
                w.WriteString("type", "AURA");
            else
                throw Unknown("activation", activation);
            w.WriteEndObject();
        }

        private static void WriteCondition(Utf8JsonWriter w, RuleCondition condition)
        {
            w.WriteStartObject("condition");
            if (condition is AlwaysCondition)
                w.WriteString("type", "ALWAYS");
            else if (condition is TurnOwnerCondition turnOwner)
            {
                w.WriteString("type", "TURN_OWNER");
                w.WriteString("owner", turnOwner.Owner.ToString());
            }
            else
                throw Unknown("condition", condition);
            w.WriteEndObject();
        }

        private static void WriteLifetime(Utf8JsonWriter w, RuleLifetime lifetime)
        {
            w.WriteStartObject("lifetime");
            if (lifetime is SourceBoundLifetime)
                w.WriteString("type", "SOURCE_BOUND");
            else if (lifetime is OneShotLifetime)
                w.WriteString("type", "ONE_SHOT");
            else if (lifetime is FiniteLifetime finite)
            {
                w.WriteString("type", "FINITE");
                w.WriteNumber("uses", finite.Uses);
            }
            else if (lifetime is PermanentLifetime)
                w.WriteString("type", "PERMANENT");
            else
                throw Unknown("lifetime", lifetime);
            w.WriteEndObject();
        }

        private static void WriteAction(Utf8JsonWriter w, RuleAction action)
        {
            w.WriteStartObject("action");
            if (action is DamageAction damage) { Type(w, "DAMAGE"); Amount(w, damage.Amount); Target(w, damage.Target); }
            else if (action is HealAction heal) { Type(w, "HEAL"); Amount(w, heal.Amount); Target(w, heal.Target); }
            else if (action is ModifyStatsAction stats)
            {
                Type(w, "MODIFY_STATS");
                w.WriteNumber("attack", stats.AttackDelta);
                w.WriteNumber("health", stats.HealthDelta);
                Target(w, stats.Target);
            }
            else if (action is ReinforceAction reinforce) { Type(w, "REINFORCE"); Target(w, reinforce.Target); }
            else if (action is GrantKeywordAction grant) { Type(w, "GRANT_KEYWORD"); w.WriteString("keyword", grant.Keyword.ToString()); Target(w, grant.Target); }
            else if (action is DestroyAction destroy) { Type(w, "DESTROY"); Target(w, destroy.Target); }
            else if (action is ReturnToHandAction returnToHand) { Type(w, "RETURN_TO_HAND"); Target(w, returnToHand.Target); }
            else if (action is SilenceAction silence) { Type(w, "SILENCE"); Target(w, silence.Target); }
            else if (action is TransformAction transform) { Type(w, "TRANSFORM"); w.WriteString("cardId", transform.CardId); Target(w, transform.Target); }
            else if (action is FrostAction frost) { Type(w, "FROST"); Target(w, frost.Target); }
            else if (action is TakeControlAction takeControl) { Type(w, "TAKE_CONTROL"); Target(w, takeControl.Target); }
            else if (action is DrawAction draw) { Type(w, "DRAW"); Count(w, draw.Count); }
            else if (action is SummonAction summon) { Type(w, "SUMMON"); w.WriteString("cardId", summon.CardId); Count(w, summon.Count); }
            else if (action is AddToHandAction addToHand) { Type(w, "ADD_TO_HAND"); w.WriteString("cardId", addToHand.CardId); Count(w, addToHand.Count); }
            else if (action is EquipAction equip) { Type(w, "EQUIP"); w.WriteString("cardId", equip.CardId); }
            else if (action is GainManaAction mana) { Type(w, "GAIN_MANA"); Amount(w, mana.Amount); w.WriteString("manaKind", mana.Kind.ToString()); }
            else if (action is BackdraftAction backdraft) { Type(w, "BACKDRAFT"); Amount(w, backdraft.Amount); }
            else if (action is UnearthAction unearth) { Type(w, "UNEARTH"); Count(w, unearth.Count); }
            else if (action is GainArmorAction armor) { Type(w, "GAIN_ARMOR"); Amount(w, armor.Amount); }
            else throw Unknown("action", action);
            w.WriteEndObject();
        }

        private static void Type(Utf8JsonWriter w, string value) => w.WriteString("type", value);
        private static void Amount(Utf8JsonWriter w, int value) => w.WriteNumber("amount", value);
        private static void Count(Utf8JsonWriter w, int value) => w.WriteNumber("count", value);

        private static void Target(Utf8JsonWriter w, TargetSpec target)
        {
            w.WriteStartObject("target");
            w.WriteString("selector", target.Selector);
            if (target.ChosenBy is { } chosenBy) w.WriteString("chosenBy", chosenBy.ToString());
            if (target.Count is { } count) w.WriteNumber("count", count);
            if (target.Filter is { } filter)
            {
                w.WriteStartObject("filter");
                if (filter.Damaged is { } damaged) w.WriteBoolean("damaged", damaged);
                if (filter.Tribe is { } tribe) w.WriteString("tribe", tribe.ToString());
                w.WriteEndObject();
            }
            if (!string.IsNullOrEmpty(target.Binding)) w.WriteString("binding", target.Binding);
            if (!string.IsNullOrEmpty(target.DistinctFrom)) w.WriteString("distinctFrom", target.DistinctFrom);
            w.WriteEndObject();
        }

        private static System.InvalidOperationException Unknown(string family, object value)
            => new System.InvalidOperationException($"Unknown rule {family} node '{value.GetType().Name}'.");
    }
}
