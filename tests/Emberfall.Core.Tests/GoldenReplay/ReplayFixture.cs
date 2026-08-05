using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.GoldenReplay
{
    /// <summary>
    /// On-disk <c>(seed, intent-log, expected hash, expected event stream)</c> record (hist-docs/source-snapshots/2026-07-31-pre-reorganization/testing/golden-replay.md §3).
    /// Self-contained and human-diffable: it pins its card snapshot, the two fixed slice decks, the seed,
    /// the intent log, and the expected outputs. <see cref="InstanceId"/>s serialize as "i&lt;n&gt;" — the
    /// deterministic counter values, themselves part of what is asserted (§3.2).
    /// </summary>
    public sealed class ReplayFixture
    {
        /// <summary>Highest on-disk schema this loader understands (hist-docs/source-snapshots/2026-07-31-pre-reorganization/testing/golden-replay.md §3.2).</summary>
        public const int CurrentSchemaVersion = 2;

        /// <summary>
        /// The v1 implicit values — the <see cref="MatchSetup"/> C# defaults that v1 fixtures were
        /// silently baked against. Reads default to these so no existing hash moves (C-G1-7).
        /// </summary>
        public const int DefaultHeroMaxHealth = 30;
        public const string DefaultSparkTokenId = "token_spark_coin";

        public int SchemaVersion = 1;
        public string Name = "";
        public string CardSetSnapshot = "";
        public string EngineContractVersion = "";
        public MatchId MatchId;
        public PlayerId P0;
        public PlayerId P1;
        public CardClass P0Class;
        public CardClass P1Class;
        public List<string> P0Deck = new();
        public List<string> P1Deck = new();
        public ulong Seed;

        /// <summary>
        /// Hash-load-bearing <see cref="MatchSetup"/> inputs (schema v2). Both reach the digest — hero
        /// health via <c>GameStateSerializer.WalkHero</c>, the spark id via the second player's hand —
        /// so a fixture that does not record them cannot honestly claim its inputs reproduce its hash.
        /// </summary>
        public int HeroMaxHealth = DefaultHeroMaxHealth;
        public string SparkTokenId = DefaultSparkTokenId;

        public List<PlayerIntent> Intents = new();

        public string? ExpectedStateHash;
        public List<ExpectedEvent>? ExpectedEvents;

        public string SourcePath = "";

        public static ReplayFixture Load(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            int schemaVersion = root.TryGetProperty("schemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number
                ? sv.GetInt32() : 1;
            if (schemaVersion < 1 || schemaVersion > CurrentSchemaVersion)
                throw new NotSupportedException(
                    $"Fixture '{Path.GetFileName(path)}' declares schemaVersion {schemaVersion}; this harness understands " +
                    $"1..{CurrentSchemaVersion}. Update ReplayFixture before adding a newer fixture.");

            var fx = new ReplayFixture
            {
                SourcePath = path,
                SchemaVersion = schemaVersion,
                Name = root.GetProperty("name").GetString() ?? Path.GetFileNameWithoutExtension(path),
                CardSetSnapshot = root.GetProperty("cardSetSnapshot").GetString() ?? "",
                EngineContractVersion = Opt(root, "engineContractVersion") ?? "",
                MatchId = new MatchId(root.GetProperty("matchId").GetString() ?? "MATCH"),
                P0 = new PlayerId(root.GetProperty("p0").GetString() ?? "P0"),
                P1 = new PlayerId(root.GetProperty("p1").GetString() ?? "P1"),
                P0Class = ParseEnum<CardClass>(root, "p0Class"),
                P1Class = ParseEnum<CardClass>(root, "p1Class"),
                Seed = root.GetProperty("seed").GetUInt64(),

                // Default on read (C-G1-7): absent → the v1 implicit value, so v1 hashes are stable.
                HeroMaxHealth = root.TryGetProperty("heroMaxHealth", out var hp) && hp.ValueKind == JsonValueKind.Number
                    ? hp.GetInt32() : DefaultHeroMaxHealth,
                SparkTokenId = Opt(root, "sparkTokenId") ?? DefaultSparkTokenId,
            };
            foreach (var c in root.GetProperty("p0Deck").EnumerateArray()) fx.P0Deck.Add(c.GetString()!);
            foreach (var c in root.GetProperty("p1Deck").EnumerateArray()) fx.P1Deck.Add(c.GetString()!);
            foreach (var it in root.GetProperty("intents").EnumerateArray()) fx.Intents.Add(ParseIntent(it));

            if (root.TryGetProperty("expected", out var exp) && exp.ValueKind == JsonValueKind.Object)
            {
                if (exp.TryGetProperty("stateHash", out var h) && h.ValueKind == JsonValueKind.String)
                {
                    string hv = h.GetString()!;
                    if (hv.Length > 0) fx.ExpectedStateHash = hv;
                }
                if (exp.TryGetProperty("eventStream", out var es) && es.ValueKind == JsonValueKind.Array)
                {
                    fx.ExpectedEvents = new List<ExpectedEvent>();
                    foreach (var e in es.EnumerateArray()) fx.ExpectedEvents.Add(ExpectedEvent.Parse(e));
                }
            }
            return fx;
        }

        private static string? Opt(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static T ParseEnum<T>(JsonElement el, string name) where T : struct, Enum
            => (T)Enum.Parse(typeof(T), el.GetProperty(name).GetString()!, ignoreCase: false);

        private static PlayerIntent ParseIntent(JsonElement el)
        {
            string tag = el.GetProperty("$type").GetString()!;
            var player = new PlayerId(el.GetProperty("player").GetString()!);
            switch (tag)
            {
                case "mulligan":
                    var toReplace = new List<InstanceId>();
                    if (el.TryGetProperty("toReplace", out var tr) && tr.ValueKind == JsonValueKind.Array)
                        foreach (var i in tr.EnumerateArray()) toReplace.Add(ParseInstance(i.GetString()!));
                    return new MulliganIntent(player, toReplace);
                case "end_turn": return new EndTurnIntent(player);
                case "concede": return new ConcedeIntent(player);
                case "auto_confirm": return new AutoConfirmIntent(player);
                case "play_card":
                    {
                        var card = ParseInstance(el.GetProperty("card").GetString()!);
                        int slot = el.TryGetProperty("slot", out var slotEl) ? slotEl.GetInt32() : 0;
                        var targets = new List<TargetRef>();
                        if (el.TryGetProperty("targets", out var tgtsEl) && tgtsEl.ValueKind == JsonValueKind.Array)
                            foreach (var t in tgtsEl.EnumerateArray())
                                targets.Add(new TargetRef(ParseInstance(t.GetString()!)));
                        return new PlayCardIntent(player, card, slot, targets);
                    }
                case "attack":
                    {
                        var attacker = ParseInstance(el.GetProperty("attacker").GetString()!);
                        var defender = new TargetRef(ParseInstance(el.GetProperty("defender").GetString()!));
                        return new AttackIntent(player, attacker, defender);
                    }
                case "hero_power":
                    {
                        var targets = new List<TargetRef>();
                        if (el.TryGetProperty("targets", out var tgtsEl) && tgtsEl.ValueKind == JsonValueKind.Array)
                            foreach (var t in tgtsEl.EnumerateArray())
                                targets.Add(new TargetRef(ParseInstance(t.GetString()!)));
                        return new HeroPowerIntent(player, targets);
                    }
                default:
                    throw new NotSupportedException($"Intent tag '{tag}' not supported by the golden-replay harness.");
            }
        }

        public static InstanceId ParseInstance(string s)
        {
            if (s.Length < 2 || s[0] != 'i')
                throw new FormatException($"Bad InstanceId '{s}' (expected 'i<n>').");
            return new InstanceId(int.Parse(s.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>One expected D7 event row (hist-docs/source-snapshots/2026-07-31-pre-reorganization/testing/golden-replay.md §3.2). Compared field-by-field in sequence order.</summary>
    public readonly struct ExpectedEvent
    {
        public readonly ulong Seq;
        public readonly GameEventKind Kind;
        public readonly InstanceId? Src;
        public readonly InstanceId? Tgt;
        public readonly int? Amt;
        public readonly EventFlags Flags;

        public ExpectedEvent(ulong seq, GameEventKind kind, InstanceId? src, InstanceId? tgt, int? amt, EventFlags flags)
        {
            Seq = seq; Kind = kind; Src = src; Tgt = tgt; Amt = amt; Flags = flags;
        }

        public static ExpectedEvent Parse(JsonElement e)
        {
            ulong seq = e.GetProperty("seq").GetUInt64();
            var kind = ParseKind(e.GetProperty("kind").GetString()!);
            InstanceId? src = InstOrNull(e, "src");
            InstanceId? tgt = InstOrNull(e, "tgt");
            int? amt = e.TryGetProperty("amt", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : (int?)null;
            EventFlags flags = e.TryGetProperty("flags", out var f) && f.ValueKind == JsonValueKind.String
                ? (EventFlags)Enum.Parse(typeof(EventFlags), f.GetString()!, false) : EventFlags.None;
            return new ExpectedEvent(seq, kind, src, tgt, amt, flags);
        }

        public static ExpectedEvent From(GameEvent e)
            => new ExpectedEvent(e.Sequence, e.Kind, e.Source, e.Target, e.Amount, e.Flags);

        private static GameEventKind ParseKind(string value)
        {
            if (Enum.TryParse(value, false, out GameEventKind semantic)
                && Enum.IsDefined(typeof(GameEventKind), semantic))
                return semantic;

            return value switch
            {
                "onPlay" => GameEventKind.CardPlayed,
                "onSummon" => GameEventKind.CardSummoned,
                "onAttack" => GameEventKind.AttackDeclared,
                "onDamage" => GameEventKind.DamageApplied,
                "onHeal" => GameEventKind.HealthRestored,
                "onDeath" => GameEventKind.CardDestroyed,
                "onBuff" => GameEventKind.StatsModified,
                "onDraw" => GameEventKind.CardDrawn,
                "onSilence" => GameEventKind.CardSilenced,
                "onFreeze" => GameEventKind.FrostApplied,
                "onTransform" => GameEventKind.CardTransformed,
                "onReturn" => GameEventKind.CardBounced,
                "onManaGain" => GameEventKind.ManaGained,
                "onEquip" => GameEventKind.WeaponEquipped,
                "onDiscover" => GameEventKind.UnearthOffered,
                "onTurnStart" => GameEventKind.TurnStarted,
                "onTurnEnd" => GameEventKind.TurnEnded,
                "onHeroPower" => GameEventKind.HeroPowerUsed,
                "onFatigue" => GameEventKind.FatigueDamage,
                "onGameWin" => GameEventKind.MatchWon,
                "onGameOver" => GameEventKind.MatchEnded,
                _ => throw new FormatException($"Unknown event kind '{value}'."),
            };
        }

        private static InstanceId? InstOrNull(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? ReplayFixture.ParseInstance(v.GetString()!) : (InstanceId?)null;

        public bool Matches(GameEvent e)
            => Seq == e.Sequence && Kind == e.Kind && Src.Equals(e.Source)
               && Tgt.Equals(e.Target) && Amt == e.Amount && Flags == e.Flags;

        public override string ToString()
        {
            string s = Src.HasValue ? Src.Value.ToString() : "null";
            string t = Tgt.HasValue ? Tgt.Value.ToString() : "null";
            string a = Amt.HasValue ? Amt.Value.ToString(CultureInfo.InvariantCulture) : "null";
            return $"{{ seq={Seq} kind={Kind} src={s} tgt={t} amt={a} flags={Flags} }}";
        }
    }
}
