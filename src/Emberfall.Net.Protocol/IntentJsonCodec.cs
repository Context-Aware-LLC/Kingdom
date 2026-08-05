using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Protocol
{
    /// <summary>
    /// PlayerIntent ⇄ JSON, discriminated by the <c>[ReplayTag]</c> string in <c>$type</c> — never
    /// a C# type name (design §5; kickoff §2). The tag table is HAND-WRITTEN, not reflected:
    /// the codec contains zero runtime reflection, so IL2CPP stripping cannot break it (as-built
    /// hardening of design §5 finding 7 — reflection lives only in the completeness TEST, which
    /// proves this table covers every sealed PlayerIntent subtype). Field names are the golden-
    /// fixture dialect (`player`, `card`, `slot`, `targets`, `attacker`, `defender`, `toReplace`)
    /// plus `chosenCardId` (no fixture precedent — design §5 finding 8).
    /// </summary>
    internal static class IntentJsonCodec
    {
        public const string PlayCard = "play_card";
        public const string Attack = "attack";
        public const string HeroPower = "hero_power";
        public const string EndTurn = "end_turn";
        public const string Mulligan = "mulligan";
        public const string UnearthChoice = "unearth_choice";
        public const string Concede = "concede";
        public const string AutoConfirm = "auto_confirm";

        /// <summary>Every tag the codec speaks (the completeness test locks this to the
        /// [ReplayTag]-annotated subtype set).</summary>
        public static IReadOnlyList<string> KnownTags { get; } = new[]
        {
            PlayCard, Attack, HeroPower, EndTurn, Mulligan, UnearthChoice, Concede, AutoConfirm,
        };

        public static bool TryTagOf(PlayerIntent intent, out string tag)
        {
            switch (intent)
            {
                case PlayCardIntent _: tag = PlayCard; return true;
                case AttackIntent _: tag = Attack; return true;
                case HeroPowerIntent _: tag = HeroPower; return true;
                case EndTurnIntent _: tag = EndTurn; return true;
                case MulliganIntent _: tag = Mulligan; return true;
                case UnearthChoiceIntent _: tag = UnearthChoice; return true;
                case ConcedeIntent _: tag = Concede; return true;
                case AutoConfirmIntent _: tag = AutoConfirm; return true;
                default: tag = ""; return false;
            }
        }

        public static void Write(JsonWriter w, PlayerIntent intent)
        {
            if (!TryTagOf(intent, out string tag))
                throw new InvalidOperationException($"Intent type '{intent.GetType().Name}' is not in the wire tag table.");

            w.BeginObject();
            w.Name("$type"); w.Str(tag);
            w.Name("player"); w.Str(intent.Player.Value);
            switch (intent)
            {
                case PlayCardIntent pc:
                    w.Name("card"); w.Str(WirePrimitives.InstanceToWire(pc.Card));
                    w.Name("slot"); w.I32(pc.BoardSlot);
                    w.Name("targets"); WriteTargets(w, pc.Targets);
                    break;
                case AttackIntent at:
                    w.Name("attacker"); w.Str(WirePrimitives.InstanceToWire(at.Attacker));
                    w.Name("defender"); w.Str(WirePrimitives.InstanceToWire(at.Defender.Instance));
                    break;
                case HeroPowerIntent hp:
                    w.Name("targets"); WriteTargets(w, hp.Targets);
                    break;
                case MulliganIntent mu:
                    w.Name("toReplace");
                    w.BeginArray();
                    foreach (var id in mu.ToReplace) w.Str(WirePrimitives.InstanceToWire(id));
                    w.EndArray();
                    break;
                case UnearthChoiceIntent dc:
                    w.Name("chosenCardId"); w.Str(dc.ChosenCardId);
                    break;
                    // EndTurn / Concede / AutoConfirm carry only $type + player.
            }
            w.EndObject();
        }

        private static void WriteTargets(JsonWriter w, IReadOnlyList<TargetRef> targets)
        {
            w.BeginArray();
            foreach (var t in targets)
            {
                if (t.Binding.Length == 0)
                {
                    w.Str(WirePrimitives.InstanceToWire(t.Instance));
                    continue;
                }
                w.BeginObject();
                w.Name("instance"); w.Str(WirePrimitives.InstanceToWire(t.Instance));
                w.Name("binding"); w.Str(t.Binding);
                w.EndObject();
            }
            w.EndArray();
        }

        /// <summary>Decode an intent object. Unknown tag → <see cref="ProtocolError.UnknownIntentTag"/>;
        /// any shape problem → <see cref="ProtocolError.BadField"/>. Never throws.</summary>
        public static bool TryRead(JVal obj, out PlayerIntent intent, out ProtocolError error, out string detail)
        {
            intent = null!;
            error = ProtocolError.BadField;
            detail = "";

            if (obj.Kind != JKind.Object) { detail = "intent is not an object"; return false; }
            if (!obj.TryGet("$type", out var tagVal) || !tagVal.TryStr(out string tag))
            { detail = "intent missing $type"; return false; }
            if (!obj.TryGet("player", out var playerVal) || !playerVal.TryStr(out string playerStr))
            { detail = "intent missing player"; return false; }
            var player = new PlayerId(playerStr);

            switch (tag)
            {
                case EndTurn: intent = new EndTurnIntent(player); return Success(out error);
                case Concede: intent = new ConcedeIntent(player); return Success(out error);
                case AutoConfirm: intent = new AutoConfirmIntent(player); return Success(out error);

                case PlayCard:
                    {
                        if (!TryInstanceField(obj, "card", out var card, ref detail)) return false;
                        int slot = 0;
                        if (obj.TryGet("slot", out var slotVal) && !slotVal.TryI32(out slot))
                        { detail = "bad slot"; return false; }
                        if (!TryTargets(obj, out var targets, ref detail)) return false;
                        intent = new PlayCardIntent(player, card, slot, targets);
                        return Success(out error);
                    }
                case Attack:
                    {
                        if (!TryInstanceField(obj, "attacker", out var attacker, ref detail)) return false;
                        if (!TryInstanceField(obj, "defender", out var defender, ref detail)) return false;
                        intent = new AttackIntent(player, attacker, new TargetRef(defender));
                        return Success(out error);
                    }
                case HeroPower:
                    {
                        if (!TryTargets(obj, out var targets, ref detail)) return false;
                        intent = new HeroPowerIntent(player, targets);
                        return Success(out error);
                    }
                case Mulligan:
                    {
                        var toReplace = new List<InstanceId>();
                        if (obj.TryGet("toReplace", out var trVal))
                        {
                            if (trVal.Kind != JKind.Array) { detail = "toReplace is not an array"; return false; }
                            foreach (var item in trVal.Items!)
                            {
                                if (!item.TryStr(out string s) || !WirePrimitives.TryInstance(s, out var id))
                                { detail = "bad toReplace entry"; return false; }
                                toReplace.Add(id);
                            }
                        }
                        intent = new MulliganIntent(player, toReplace);
                        return Success(out error);
                    }
                case UnearthChoice:
                    {
                        if (!obj.TryGet("chosenCardId", out var ccVal) || !ccVal.TryStr(out string chosen))
                        { detail = "missing chosenCardId"; return false; }
                        intent = new UnearthChoiceIntent(player, chosen);
                        return Success(out error);
                    }
                default:
                    error = ProtocolError.UnknownIntentTag;
                    detail = $"unknown intent tag '{tag}'";
                    return false;
            }
        }

        private static bool Success(out ProtocolError error)
        {
            error = ProtocolError.None;
            return true;
        }

        private static bool TryInstanceField(JVal obj, string name, out InstanceId id, ref string detail)
        {
            id = default;
            if (!obj.TryGet(name, out var val) || !val.TryStr(out string s) || !WirePrimitives.TryInstance(s, out id))
            {
                detail = $"bad {name}";
                return false;
            }
            return true;
        }

        private static bool TryTargets(JVal obj, out List<TargetRef> targets, ref string detail)
        {
            targets = new List<TargetRef>();
            if (!obj.TryGet("targets", out var tVal)) return true; // absent = empty (fixture precedent)
            if (tVal.Kind != JKind.Array) { detail = "targets is not an array"; return false; }
            foreach (var item in tVal.Items!)
            {
                if (item.TryStr(out string s) && WirePrimitives.TryInstance(s, out var legacyId))
                {
                    targets.Add(new TargetRef(legacyId));
                    continue;
                }
                if (item.Kind != JKind.Object ||
                    !item.TryGet("instance", out var instanceVal) ||
                    !instanceVal.TryStr(out string instanceText) ||
                    !WirePrimitives.TryInstance(instanceText, out var id) ||
                    !item.TryGet("binding", out var bindingVal) ||
                    !bindingVal.TryStr(out string binding) || string.IsNullOrEmpty(binding))
                { detail = "bad target entry"; return false; }
                targets.Add(new TargetRef(id, binding));
            }
            return true;
        }
    }
}
