using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Protocol
{
    /// <summary>
    /// GameState ⇄ wire JSON — the named, readable-back twin of THE canonical walk
    /// (design §6). Field order and structure mirror <c>GameStateSerializer</c> one-for-one:
    /// same seat order, same keyword sort (by enum int), same nullable conventions (JSON null
    /// stands in for the present-flag byte), same PendingChoice tail. Names are camelCase of the
    /// C# members; InstanceId as "i&lt;n&gt;", PlayerId/MatchId/DefinitionId as bare strings.
    ///
    /// This is a SECOND walk over the same shape — the drift risk is closed by test, not
    /// discipline: the A2 bridge test (hash-preserving round-trip over the fixture corpus) and
    /// the shared per-field mutation sweep both go red if a field is dropped or misread
    /// (design §6/§10). Do NOT touch GameStateSerializer or any Contracts shape from here; if a
    /// field cannot round-trip without a Contracts change, STOP — that is a §7 trigger.
    /// </summary>
    internal static class SnapshotJsonCodec
    {
        // ── Write (mirrors GameStateSerializer.Serialize order) ─────────────────

        public static void Write(JsonWriter w, GameState s)
        {
            w.BeginObject();
            w.Name("id"); w.Str(s.Id.Value);
            w.Name("stateVersion"); w.U64(s.StateVersion);
            w.Name("rngRawState"); w.U64(s.Rng.RawState);
            w.Name("activePlayer"); w.Str(s.ActivePlayer.Value);
            w.Name("phase"); w.Str(s.Phase.ToString());
            w.Name("turnNumber"); w.I32(s.TurnNumber);
            w.Name("players");
            w.BeginArray();
            WritePlayer(w, s.Players[0]);   // [0] then [1] — fixed seat order (§4.1)
            WritePlayer(w, s.Players[1]);
            w.EndArray();
            w.Name("fatigueCounter0"); w.I32(s.FatigueCounter0);
            w.Name("fatigueCounter1"); w.I32(s.FatigueCounter1);
            w.Name("outcome"); w.Str(s.Outcome.ToString());
            w.Name("actionResolutionsThisTurn"); w.I32(s.ActionResolutionsThisTurn);
            w.Name("nextInstanceCounter"); w.I32(s.NextInstanceCounter);
            w.Name("nextEventSequence"); w.U64(s.NextEventSequence);
            w.Name("mulligan0Done"); w.Bool(s.Mulligan0Done);
            w.Name("mulligan1Done"); w.Bool(s.Mulligan1Done);
            w.Name("pendingChoice");
            if (s.PendingChoice is null) w.Null();
            else WritePendingChoice(w, s.PendingChoice);
            w.EndObject();
        }

        private static void WritePlayer(JsonWriter w, PlayerState p)
        {
            w.BeginObject();
            w.Name("id"); w.Str(p.Id.Value);
            w.Name("hero"); WriteHero(w, p.Hero);
            w.Name("deck"); WriteZone(w, p.Deck);
            w.Name("hand"); WriteZone(w, p.Hand);
            w.Name("board"); WriteZone(w, p.Board);
            w.Name("weapon");
            if (p.Weapon is null) w.Null();
            else WriteCard(w, p.Weapon);
            w.Name("graveyard"); WriteZone(w, p.Graveyard);
            w.Name("mana");
            w.BeginObject();
            w.Name("max"); w.I32(p.Mana.Max);
            w.Name("available"); w.I32(p.Mana.Available);
            w.Name("spent"); w.I32(p.Mana.Spent);
            w.Name("locked"); w.I32(p.Mana.Locked);
            w.Name("temporary"); w.I32(p.Mana.Temporary);
            w.EndObject();
            w.Name("heroPowerUsedThisTurn"); w.Bool(p.HeroPowerUsedThisTurn);
            w.Name("heroAttacksUsedThisTurn"); w.I32(p.HeroAttacksUsedThisTurn);
            w.Name("nextTurnBackdraftLock"); w.I32(p.NextTurnBackdraftLock);
            w.EndObject();
        }

        private static void WriteHero(JsonWriter w, HeroInstance h)
        {
            w.BeginObject();
            w.Name("id"); w.Str(WirePrimitives.InstanceToWire(h.Id));
            w.Name("controller"); w.Str(h.Controller.Value);
            w.Name("class"); w.Str(h.Class.ToString());
            w.Name("currentHealth"); w.I32(h.CurrentHealth);
            w.Name("maxHealth"); w.I32(h.MaxHealth);
            w.Name("armor"); w.I32(h.Armor);
            w.Name("heroPowerId"); w.Str(h.HeroPowerId);
            w.Name("isFrosted"); w.Bool(h.IsFrosted);
            w.EndObject();
        }

        private static void WriteZone(JsonWriter w, List<CardInstance> zone)
        {
            w.BeginArray();
            foreach (var c in zone) WriteCard(w, c);
            w.EndArray();
        }

        private static void WriteCard(JsonWriter w, CardInstance c)
        {
            w.BeginObject();
            w.Name("id"); w.Str(WirePrimitives.InstanceToWire(c.Id));
            w.Name("definitionId"); w.Str(c.DefinitionId);
            w.Name("controller"); w.Str(c.Controller.Value);
            w.Name("zone"); w.Str(c.Zone.ToString());
            w.Name("currentAttack"); w.I32(c.CurrentAttack);
            w.Name("currentHealth"); w.I32(c.CurrentHealth);
            w.Name("maxHealth"); w.I32(c.MaxHealth);
            w.Name("currentDurability");
            if (c.CurrentDurability.HasValue) w.I32(c.CurrentDurability.Value);
            else w.Null();

            // The one unordered collection: same total order the hasher imposes (sorted by enum
            // int) so encode → decode → encode is byte-identical (§4.1 / design §6).
            var kws = new List<Keyword>(c.Keywords);
            kws.Sort((a, b) => ((int)a).CompareTo((int)b));
            w.Name("keywords");
            w.BeginArray();
            foreach (var k in kws) w.Str(k.ToString());
            w.EndArray();

            w.Name("enchantments");
            w.BeginArray();
            foreach (var e in c.Enchantments)
            {
                w.BeginObject();
                w.Name("source"); w.Str(WirePrimitives.InstanceToWire(e.Source));
                w.Name("attackDelta"); w.I32(e.AttackDelta);
                w.Name("healthDelta"); w.I32(e.HealthDelta);
                w.Name("grantedKeyword");
                if (e.GrantedKeyword.HasValue) w.Str(e.GrantedKeyword.Value.ToString());
                else w.Null();
                w.EndObject();
            }
            w.EndArray();

            w.Name("hasAegisShield"); w.Bool(c.HasAegisShield);
            w.Name("isFrosted"); w.Bool(c.IsFrosted);
            w.Name("isSilenced"); w.Bool(c.IsSilenced);
            w.Name("summonedThisTurn"); w.Bool(c.SummonedThisTurn);
            w.Name("attacksUsedThisTurn"); w.I32(c.AttacksUsedThisTurn);
            w.Name("hasUsedRekindle"); w.Bool(c.HasUsedRekindle);
            w.Name("position"); w.I32(c.Position);
            w.EndObject();
        }

        private static void WritePendingChoice(JsonWriter w, PendingChoice pc)
        {
            w.BeginObject();
            w.Name("chooser"); w.Str(pc.Chooser.Value);
            w.Name("kind"); w.Str(pc.Kind.ToString());
            w.Name("source"); w.Str(WirePrimitives.InstanceToWire(pc.Source));
            // Order-PRESERVING only — the list order is the deterministic Unearth roll order
            // (OBL-3); the codec never re-sorts it.
            w.Name("options");
            w.BeginArray();
            foreach (var o in pc.Options) w.Str(o);
            w.EndArray();
            w.Name("choiceSequence"); w.U64(pc.ChoiceSequence);
            w.EndObject();
        }

        // ── Read (rebuilds via Contracts-only public surface) ───────────────────

        public static bool TryRead(JVal obj, out GameState state, out string detail)
        {
            state = null!;
            detail = "";
            if (obj.Kind != JKind.Object) { detail = "state is not an object"; return false; }

            if (!TryStrField(obj, "id", out string idStr, ref detail)) return false;
            if (!TryU64Field(obj, "stateVersion", out ulong stateVersion, ref detail)) return false;
            if (!TryU64Field(obj, "rngRawState", out ulong rngRaw, ref detail)) return false;
            if (!TryStrField(obj, "activePlayer", out string activeStr, ref detail)) return false;
            if (!TryEnumField(obj, "phase", out TurnPhase phase, ref detail)) return false;
            if (!TryI32Field(obj, "turnNumber", out int turnNumber, ref detail)) return false;

            if (!obj.TryGet("players", out var playersVal) || playersVal.Kind != JKind.Array || playersVal.Items!.Count != 2)
            { detail = "players must be a 2-element array"; return false; }
            if (!TryReadPlayer(playersVal.Items[0], out var p0, ref detail)) return false;
            if (!TryReadPlayer(playersVal.Items[1], out var p1, ref detail)) return false;

            if (!TryI32Field(obj, "fatigueCounter0", out int fat0, ref detail)) return false;
            if (!TryI32Field(obj, "fatigueCounter1", out int fat1, ref detail)) return false;
            if (!TryEnumField(obj, "outcome", out MatchOutcome outcome, ref detail)) return false;
            if (!TryI32Field(obj, "actionResolutionsThisTurn", out int actionRes, ref detail)) return false;
            if (!TryI32Field(obj, "nextInstanceCounter", out int nextInstance, ref detail)) return false;
            if (!TryU64Field(obj, "nextEventSequence", out ulong nextEventSeq, ref detail)) return false;
            if (!TryBoolField(obj, "mulligan0Done", out bool mull0, ref detail)) return false;
            if (!TryBoolField(obj, "mulligan1Done", out bool mull1, ref detail)) return false;

            PendingChoice? pendingChoice = null;
            if (!obj.TryGet("pendingChoice", out var pcVal)) { detail = "missing pendingChoice"; return false; }
            if (!pcVal.IsNull && !TryReadPendingChoice(pcVal, out pendingChoice, ref detail)) return false;

            var rng = new DeterministicRandom(0UL);
            rng.SetRawState(rngRaw);

            state = new GameState(new MatchId(idStr), rng, p0, p1)
            {
                StateVersion = stateVersion,
                ActivePlayer = new PlayerId(activeStr),
                Phase = phase,
                TurnNumber = turnNumber,
                FatigueCounter0 = fat0,
                FatigueCounter1 = fat1,
                Outcome = outcome,
                ActionResolutionsThisTurn = actionRes,
                NextInstanceCounter = nextInstance,
                NextEventSequence = nextEventSeq,
                Mulligan0Done = mull0,
                Mulligan1Done = mull1,
                PendingChoice = pendingChoice,
            };
            return true;
        }

        private static bool TryReadPlayer(JVal obj, out PlayerState player, ref string detail)
        {
            player = null!;
            if (obj.Kind != JKind.Object) { detail = "player is not an object"; return false; }

            if (!TryStrField(obj, "id", out string idStr, ref detail)) return false;
            if (!obj.TryGet("hero", out var heroVal) || !TryReadHero(heroVal, out var hero, ref detail)) return false;

            player = new PlayerState(new PlayerId(idStr), hero);

            if (!TryReadZone(obj, "deck", player.Deck, ref detail)) return false;
            if (!TryReadZone(obj, "hand", player.Hand, ref detail)) return false;
            if (!TryReadZone(obj, "board", player.Board, ref detail)) return false;

            if (!obj.TryGet("weapon", out var wVal)) { detail = "missing weapon"; return false; }
            if (!wVal.IsNull)
            {
                if (!TryReadCard(wVal, out var weapon, ref detail)) return false;
                if (weapon.Zone != Zone.Weapon)
                {
                    detail = "weapon card must be in zone Weapon";
                    return false;
                }
                player.Weapon = weapon;
            }

            if (!TryReadZone(obj, "graveyard", player.Graveyard, ref detail)) return false;

            if (!obj.TryGet("mana", out var mVal) || mVal.Kind != JKind.Object) { detail = "missing mana"; return false; }
            if (!TryI32Field(mVal, "max", out int mMax, ref detail)) return false;
            if (!TryI32Field(mVal, "available", out int mAvail, ref detail)) return false;
            if (!TryI32Field(mVal, "spent", out int mSpent, ref detail)) return false;
            if (!TryI32Field(mVal, "locked", out int mLocked, ref detail)) return false;
            if (!TryI32Field(mVal, "temporary", out int mTemp, ref detail)) return false;
            player.Mana = new ManaPool { Max = mMax, Available = mAvail, Spent = mSpent, Locked = mLocked, Temporary = mTemp };

            if (!TryBoolField(obj, "heroPowerUsedThisTurn", out bool hpUsed, ref detail)) return false;
            if (!TryI32Field(obj, "heroAttacksUsedThisTurn", out int heroAtks, ref detail)) return false;
            if (!TryI32Field(obj, "nextTurnBackdraftLock", out int backdraft, ref detail)) return false;
            player.HeroPowerUsedThisTurn = hpUsed;
            player.HeroAttacksUsedThisTurn = heroAtks;
            player.NextTurnBackdraftLock = backdraft;
            return true;
        }

        private static bool TryReadHero(JVal obj, out HeroInstance hero, ref string detail)
        {
            hero = null!;
            if (obj.Kind != JKind.Object) { detail = "hero is not an object"; return false; }
            if (!TryInstanceField(obj, "id", out var id, ref detail)) return false;
            if (!TryStrField(obj, "controller", out string controller, ref detail)) return false;
            if (!TryEnumField(obj, "class", out CardClass cls, ref detail)) return false;
            if (!TryI32Field(obj, "currentHealth", out int curHp, ref detail)) return false;
            if (!TryI32Field(obj, "maxHealth", out int maxHp, ref detail)) return false;
            if (!TryI32Field(obj, "armor", out int armor, ref detail)) return false;
            if (!TryStrField(obj, "heroPowerId", out string hpId, ref detail)) return false;
            if (!TryBoolField(obj, "isFrosted", out bool frosted, ref detail)) return false;

            hero = new HeroInstance(id, new PlayerId(controller), cls, maxHp)
            {
                CurrentHealth = curHp,
                Armor = armor,
                HeroPowerId = hpId,
                IsFrosted = frosted,
            };
            return true;
        }

        private static bool TryReadZone(JVal obj, string name, List<CardInstance> into, ref string detail)
        {
            if (!obj.TryGet(name, out var zVal) || zVal.Kind != JKind.Array)
            { detail = $"missing zone '{name}'"; return false; }
            foreach (var item in zVal.Items!)
            {
                if (!TryReadCard(item, out var card, ref detail)) return false;
                into.Add(card);
            }
            return true;
        }

        private static bool TryReadCard(JVal obj, out CardInstance card, ref string detail)
        {
            card = null!;
            if (obj.Kind != JKind.Object) { detail = "card is not an object"; return false; }
            if (!TryInstanceField(obj, "id", out var id, ref detail)) return false;
            if (!TryStrField(obj, "definitionId", out string defId, ref detail)) return false;
            if (!TryStrField(obj, "controller", out string controller, ref detail)) return false;
            if (!TryEnumField(obj, "zone", out Zone zone, ref detail)) return false;
            if (!TryI32Field(obj, "currentAttack", out int atk, ref detail)) return false;
            if (!TryI32Field(obj, "currentHealth", out int hp, ref detail)) return false;
            if (!TryI32Field(obj, "maxHealth", out int maxHp, ref detail)) return false;

            if (!obj.TryGet("currentDurability", out var durVal)) { detail = "missing currentDurability"; return false; }
            int? durability = null;
            if (!durVal.IsNull)
            {
                if (!durVal.TryI32(out int d)) { detail = "bad currentDurability"; return false; }
                durability = d;
            }

            card = new CardInstance(id, defId, new PlayerId(controller))
            {
                Zone = zone,
                CurrentAttack = atk,
                CurrentHealth = hp,
                MaxHealth = maxHp,
                CurrentDurability = durability,
            };

            if (!obj.TryGet("keywords", out var kwVal) || kwVal.Kind != JKind.Array)
            { detail = "missing keywords"; return false; }
            foreach (var item in kwVal.Items!)
            {
                if (!item.TryStr(out string ks) || !WirePrimitives.TryEnum(ks, out Keyword k))
                { detail = "bad keyword"; return false; }
                card.Keywords.Add(k);
            }

            if (!obj.TryGet("enchantments", out var enVal) || enVal.Kind != JKind.Array)
            { detail = "missing enchantments"; return false; }
            foreach (var item in enVal.Items!)
            {
                if (item.Kind != JKind.Object) { detail = "enchantment is not an object"; return false; }
                if (!TryInstanceField(item, "source", out var src, ref detail)) return false;
                if (!TryI32Field(item, "attackDelta", out int ad, ref detail)) return false;
                if (!TryI32Field(item, "healthDelta", out int hd, ref detail)) return false;
                if (!item.TryGet("grantedKeyword", out var gkVal)) { detail = "missing grantedKeyword"; return false; }
                Keyword? granted = null;
                if (!gkVal.IsNull)
                {
                    if (!gkVal.TryStr(out string gs) || !WirePrimitives.TryEnum(gs, out Keyword gk))
                    { detail = "bad grantedKeyword"; return false; }
                    granted = gk;
                }
                card.Enchantments.Add(new Enchantment { Source = src, AttackDelta = ad, HealthDelta = hd, GrantedKeyword = granted });
            }

            if (!TryBoolField(obj, "hasAegisShield", out bool aegis, ref detail)) return false;
            if (!TryBoolField(obj, "isFrosted", out bool frosted, ref detail)) return false;
            if (!TryBoolField(obj, "isSilenced", out bool silenced, ref detail)) return false;
            if (!TryBoolField(obj, "summonedThisTurn", out bool summoned, ref detail)) return false;
            if (!TryI32Field(obj, "attacksUsedThisTurn", out int atksUsed, ref detail)) return false;
            if (!TryBoolField(obj, "hasUsedRekindle", out bool rekindle, ref detail)) return false;
            if (!TryI32Field(obj, "position", out int position, ref detail)) return false;
            card.HasAegisShield = aegis;
            card.IsFrosted = frosted;
            card.IsSilenced = silenced;
            card.SummonedThisTurn = summoned;
            card.AttacksUsedThisTurn = atksUsed;
            card.HasUsedRekindle = rekindle;
            card.Position = position;
            return true;
        }

        private static bool TryReadPendingChoice(JVal obj, out PendingChoice? pc, ref string detail)
        {
            pc = null;
            if (obj.Kind != JKind.Object) { detail = "pendingChoice is not an object"; return false; }
            if (!TryStrField(obj, "chooser", out string chooser, ref detail)) return false;
            if (!TryEnumField(obj, "kind", out PendingChoiceKind kind, ref detail)) return false;
            if (!TryInstanceField(obj, "source", out var src, ref detail)) return false;
            if (!obj.TryGet("options", out var optVal) || optVal.Kind != JKind.Array)
            { detail = "missing options"; return false; }
            if (!TryU64Field(obj, "choiceSequence", out ulong choiceSeq, ref detail)) return false;

            var result = new PendingChoice
            {
                Chooser = new PlayerId(chooser),
                Kind = kind,
                Source = src,
                ChoiceSequence = choiceSeq,
            };
            foreach (var item in optVal.Items!)
            {
                if (!item.TryStr(out string o)) { detail = "bad option"; return false; }
                result.Options.Add(o);
            }
            pc = result;
            return true;
        }

        // ── Field helpers (never throw) ──────────────────────────────────────────

        private static bool TryStrField(JVal obj, string name, out string v, ref string detail)
        {
            if (obj.TryGet(name, out var val) && val.TryStr(out v!)) return true;
            v = null!;
            detail = $"missing/bad '{name}'";
            return false;
        }

        private static bool TryI32Field(JVal obj, string name, out int v, ref string detail)
        {
            if (obj.TryGet(name, out var val) && val.TryI32(out v)) return true;
            v = 0;
            detail = $"missing/bad '{name}'";
            return false;
        }

        private static bool TryU64Field(JVal obj, string name, out ulong v, ref string detail)
        {
            if (obj.TryGet(name, out var val) && val.TryU64(out v)) return true;
            v = 0;
            detail = $"missing/bad '{name}'";
            return false;
        }

        private static bool TryBoolField(JVal obj, string name, out bool v, ref string detail)
        {
            if (obj.TryGet(name, out var val) && val.TryBool(out v)) return true;
            v = false;
            detail = $"missing/bad '{name}'";
            return false;
        }

        private static bool TryEnumField<TEnum>(JVal obj, string name, out TEnum v, ref string detail)
            where TEnum : struct, System.Enum
        {
            if (obj.TryGet(name, out var val) && val.TryStr(out string s) && WirePrimitives.TryEnum(s, out v))
                return true;
            v = default;
            detail = $"missing/bad '{name}'";
            return false;
        }

        private static bool TryInstanceField(JVal obj, string name, out InstanceId id, ref string detail)
        {
            if (obj.TryGet(name, out var val) && val.TryStr(out string s) && WirePrimitives.TryInstance(s, out id))
                return true;
            id = default;
            detail = $"missing/bad '{name}'";
            return false;
        }
    }
}
