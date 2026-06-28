using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.GoldenReplay
{
    /// <summary>
    /// On-disk <c>(seed, intent-log, expected hash, expected event stream)</c> record (golden-replay.md §3).
    /// Self-contained and human-diffable: it pins its card snapshot, the two fixed slice decks, the seed,
    /// the intent log, and the expected outputs. <see cref="InstanceId"/>s serialize as "i&lt;n&gt;" — the
    /// deterministic counter values, themselves part of what is asserted (§3.2).
    /// </summary>
    public sealed class ReplayFixture
    {
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
        public List<PlayerIntent> Intents = new();

        public string? ExpectedStateHash;
        public List<ExpectedEvent>? ExpectedEvents;

        public string SourcePath = "";

        public static ReplayFixture Load(string path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var fx = new ReplayFixture
            {
                SourcePath = path,
                Name = root.GetProperty("name").GetString() ?? Path.GetFileNameWithoutExtension(path),
                CardSetSnapshot = root.GetProperty("cardSetSnapshot").GetString() ?? "",
                EngineContractVersion = Opt(root, "engineContractVersion") ?? "",
                MatchId = new MatchId(root.GetProperty("matchId").GetString() ?? "MATCH"),
                P0 = new PlayerId(root.GetProperty("p0").GetString() ?? "P0"),
                P1 = new PlayerId(root.GetProperty("p1").GetString() ?? "P1"),
                P0Class = ParseEnum<CardClass>(root, "p0Class"),
                P1Class = ParseEnum<CardClass>(root, "p1Class"),
                Seed = root.GetProperty("seed").GetUInt64(),
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
                default:
                    throw new NotSupportedException($"Intent tag '{tag}' not supported by the M1-foundation harness.");
            }
        }

        public static InstanceId ParseInstance(string s)
        {
            if (s.Length < 2 || s[0] != 'i')
                throw new FormatException($"Bad InstanceId '{s}' (expected 'i<n>').");
            return new InstanceId(int.Parse(s.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>One expected D7 event row (golden-replay.md §3.2). Compared field-by-field in sequence order.</summary>
    public readonly struct ExpectedEvent
    {
        public readonly ulong Seq;
        public readonly VfxEvent Kind;
        public readonly InstanceId? Src;
        public readonly InstanceId? Tgt;
        public readonly int? Amt;
        public readonly EventFlags Flags;

        public ExpectedEvent(ulong seq, VfxEvent kind, InstanceId? src, InstanceId? tgt, int? amt, EventFlags flags)
        {
            Seq = seq; Kind = kind; Src = src; Tgt = tgt; Amt = amt; Flags = flags;
        }

        public static ExpectedEvent Parse(JsonElement e)
        {
            ulong seq = e.GetProperty("seq").GetUInt64();
            var kind = (VfxEvent)Enum.Parse(typeof(VfxEvent), e.GetProperty("kind").GetString()!, false);
            InstanceId? src = InstOrNull(e, "src");
            InstanceId? tgt = InstOrNull(e, "tgt");
            int? amt = e.TryGetProperty("amt", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : (int?)null;
            EventFlags flags = e.TryGetProperty("flags", out var f) && f.ValueKind == JsonValueKind.String
                ? (EventFlags)Enum.Parse(typeof(EventFlags), f.GetString()!, false) : EventFlags.None;
            return new ExpectedEvent(seq, kind, src, tgt, amt, flags);
        }

        public static ExpectedEvent From(GameEvent e)
            => new ExpectedEvent(e.Sequence, e.Kind, e.Source, e.Target, e.Amount, e.Flags);

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
