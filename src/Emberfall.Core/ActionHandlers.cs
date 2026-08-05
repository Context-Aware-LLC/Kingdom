using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;
using Verb = Emberfall.Core.Contracts.Action;

namespace Emberfall.Core
{
    /// <summary>
    /// The closed set of reusable engine primitives — one <see cref="IActionHandler"/> per
    /// <see cref="Contracts.Action"/> verb (dev-docs/specs/card-schema.md:36). There is no per-card code: a card is
    /// data that composes these verbs. The set grows ONLY through the <c>requiresEngineeringPrimitive</c>
    /// gate (dev-docs/specs/card-schema.md:40), never per-card. Built once into an immutable, order-independent registry.
    /// </summary>
    internal static class ActionHandlers
    {
        public static readonly IReadOnlyDictionary<Verb, IActionHandler> Registry = Build();

        private static IReadOnlyDictionary<Verb, IActionHandler> Build()
        {
            var handlers = new IActionHandler[]
            {
                new DealDamageHandler(), new HealHandler(), new DrawHandler(), new SummonHandler(),
                new BuffHandler(), new GiveKeywordHandler(), new DestroyHandler(), new ReturnToHandHandler(),
                new SilenceHandler(), new GainManaHandler(), new AddToHandHandler(), new TransformHandler(),
                new EquipHandler(), new DiscoverHandler(),
                new FreezeHandler(), new OverloadHandler(),
            };
            var map = new Dictionary<Verb, IActionHandler>(handlers.Length);
            foreach (var h in handlers) map[h.Verb] = h;
            return map;
        }

        /// <summary>The effective keyword set of an effect's source — live for a board minion, printed for a spell.</summary>
        internal static HashSet<Keyword> SourceKeywords(in EffectContext ctx)
        {
            if (ctx.Source.Zone == Zone.Board) return ctx.Source.Keywords;
            var set = new HashSet<Keyword>();
            foreach (var k in ctx.Db.Get(ctx.Source.DefinitionId).Keywords) set.Add(k);
            return set;
        }

        /// <summary>+1 spell damage per friendly SPELL_DAMAGE minion in play (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/card-system.md §4.1).</summary>
        internal static int SpellDamageBonus(GameState state, PlayerId controller)
        {
            var board = state.PlayerOf(controller).Board;
            int bonus = 0;
            for (int i = 0; i < board.Count; i++)
                if (board[i].Keywords.Contains(Keyword.SPELL_DAMAGE)) bonus++;
            return bonus;
        }
    }

    // ── DEAL_DAMAGE ──────────────────────────────────────────────────────────────
    internal sealed class DealDamageHandler : IActionHandler
    {
        public Verb Verb => Verb.DEAL_DAMAGE;
        public void Execute(in EffectContext ctx)
        {
            int amount = ctx.Effect.Amount ?? 0;
            if (ctx.Db.Get(ctx.Source.DefinitionId).Type == CardType.SPELL)
                amount += ActionHandlers.SpellDamageBonus(ctx.State, ctx.Source.Controller);

            var keywords = ActionHandlers.SourceKeywords(in ctx);
            foreach (var t in ctx.Targets)
                if (t.IsCharacter)
                    DamagePipeline.ApplyDamage(ctx.Interpreter, t.Character, amount, ctx.Source.Id, keywords);
        }
    }

    // ── HEAL ─────────────────────────────────────────────────────────────────────
    internal sealed class HealHandler : IActionHandler
    {
        public Verb Verb => Verb.HEAL;
        public void Execute(in EffectContext ctx)
        {
            int amount = ctx.Effect.Amount ?? 0;
            foreach (var t in ctx.Targets)
                if (t.IsCharacter)
                    DamagePipeline.ApplyHeal(ctx.Interpreter, t.Character, amount, ctx.Source.Id);
        }
    }

    // ── DRAW ─────────────────────────────────────────────────────────────────────
    internal sealed class DrawHandler : IActionHandler
    {
        public Verb Verb => Verb.DRAW;
        public void Execute(in EffectContext ctx)
        {
            int count = ctx.Effect.Amount ?? 1;
            int seat = ctx.State.SeatOf(ctx.Source.Controller);
            for (int i = 0; i < count; i++)
                RulesEngine.DrawOne(ctx.State, seat, ctx.Events);
        }
    }

    // ── SUMMON ───────────────────────────────────────────────────────────────────
    internal sealed class SummonHandler : IActionHandler
    {
        public Verb Verb => Verb.SUMMON;
        public void Execute(in EffectContext ctx)
        {
            if (ctx.Effect.TokenId == null || !ctx.Db.TryGet(ctx.Effect.TokenId, out var def)) return;
            int count = ctx.Effect.Amount ?? 1;
            int seat = ctx.State.SeatOf(ctx.Source.Controller);
            for (int i = 0; i < count; i++)
            {
                var token = new CardInstance(ctx.State.NextInstanceId(), def.Id, ctx.Source.Controller);
                if (!RulesEngine.EnterBoard(ctx.State, token, def, seat, -1)) break; // board full → stop
                ctx.Events.Emit(ctx.Vfx(VfxEvent.onSummon), source: token.Id, flags: EventFlags.IsToken);
                ctx.Interpreter.RaiseObservers(Trigger.ON_FRIENDLY_SUMMON, excluded: token,
                    triggerSource: token.Id);
            }
        }
    }

    // ── BUFF ─────────────────────────────────────────────────────────────────────
    internal sealed class BuffHandler : IActionHandler
    {
        public Verb Verb => Verb.BUFF;
        public void Execute(in EffectContext ctx)
        {
            int atk = ctx.Effect.Amount ?? 0;
            int hp = ctx.Effect.Amount2 ?? 0;
            if (atk == 0 && hp == 0) return;
            foreach (var t in ctx.Targets)
            {
                if (!t.IsMinion) continue;
                var m = t.Minion!;
                // The enchantment is the record and the recompute reads it back (EF-017a). Before that
                // layer this baked `CurrentAttack += atk` and the enchantment was write-only, so nothing
                // could ever take the buff off again.
                m.Enchantments.Add(new Enchantment { Source = ctx.Source.Id, AttackDelta = atk, HealthDelta = hp });
                DerivedStats.Recompute(m, ctx.Db.Get(m.DefinitionId));
                ctx.Events.Emit(ctx.Vfx(VfxEvent.onBuff), source: ctx.Source.Id, target: m.Id, amount: atk != 0 ? atk : hp);
            }
        }
    }

    // ── GIVE_KEYWORD ─────────────────────────────────────────────────────────────
    internal sealed class GiveKeywordHandler : IActionHandler
    {
        public Verb Verb => Verb.GIVE_KEYWORD;
        public void Execute(in EffectContext ctx)
        {
            var kw = ctx.Effect.GrantedKeyword;
            if (kw == null) return;
            foreach (var t in ctx.Targets)
            {
                if (!t.IsMinion) continue;
                var m = t.Minion!;
                // KNOWN GAP for EF-017b: the enchantment is recorded only when the keyword was NEWLY
                // added, so a second grant of a keyword the minion already has leaves no record. Once
                // auras can grant keywords, that makes "aura leaves" strip a keyword a permanent grant
                // was also holding up. Recording unconditionally is the fix and it is HASH-CANONICAL —
                // the enchantment list is serialized — so it belongs in the batched rebaseline, not here.
                if (m.Keywords.Add(kw.Value))
                    m.Enchantments.Add(new Enchantment { Source = ctx.Source.Id, GrantedKeyword = kw });
                if (kw.Value == Keyword.DIVINE_SHIELD) m.HasAegisShield = true;
                ctx.Events.Emit(ctx.Vfx(VfxEvent.onBuff), source: ctx.Source.Id, target: m.Id);
            }
        }
    }

    // ── DESTROY ──────────────────────────────────────────────────────────────────
    internal sealed class DestroyHandler : IActionHandler
    {
        public Verb Verb => Verb.DESTROY;
        public void Execute(in EffectContext ctx)
        {
            // Mark lethal; the batch DeathCheck (minions) / WinCheck (heroes) resolves and emits.
            foreach (var t in ctx.Targets)
            {
                if (t.IsMinion) t.Minion!.CurrentHealth = 0;
                else if (t.IsHero) t.Hero!.CurrentHealth = 0;
                else if (t.Kind == TargetCandidateKind.Weapon)
                    RulesEngine.BreakWeapon(ctx.Interpreter, t.Seat);
            }
        }
    }

    // ── RETURN_TO_HAND ───────────────────────────────────────────────────────────
    internal sealed class ReturnToHandHandler : IActionHandler
    {
        public Verb Verb => Verb.RETURN_TO_HAND;
        public void Execute(in EffectContext ctx)
        {
            foreach (var t in ctx.Targets)
            {
                if (t.Kind == TargetCandidateKind.Weapon)
                {
                    var owner = ctx.State.Players[t.Seat];
                    if (owner.Weapon == null || owner.Weapon.Id != t.Instance) continue;
                    CardInstance weapon = owner.Weapon;
                    owner.Weapon = null;
                    MoveCardToHand(ctx, owner, weapon, wasPublic: true);
                    continue;
                }
                if (t.Card != null && t.Kind != TargetCandidateKind.Minion)
                {
                    var owner = ctx.State.Players[t.Seat];
                    if (t.Kind == TargetCandidateKind.HandCard) continue;
                    if (t.Kind == TargetCandidateKind.DeckCard) owner.Deck.Remove(t.Card);
                    else if (t.Kind == TargetCandidateKind.GraveyardCard) owner.Graveyard.Remove(t.Card);
                    MoveCardToHand(ctx, owner, t.Card, wasPublic: t.Kind == TargetCandidateKind.GraveyardCard);
                    continue;
                }
                if (!t.IsMinion) continue;          // heroes/weapons can't be bounced
                var m = t.Minion!;
                var ps = ctx.State.Players[t.Seat];
                int idx = ps.Board.IndexOf(m);
                if (idx < 0) continue;
                ps.Board.RemoveAt(idx);
                RulesEngine.Reindex(ps.Board);

                // Reset to printed form — board-runtime state is dropped on the bounce. The card arrives
                // in hand whole, so damage is NOT preserved across the zone change (EF-017a).
                var def = ctx.Db.Get(m.DefinitionId);
                DerivedStats.ResetToPrinted(m, def, preserveDamage: false);
                m.SummonedThisTurn = false;
                m.AttacksUsedThisTurn = 0;
                m.HasUsedReborn = false;

                MoveCardToHand(ctx, ps, m, wasPublic: true);
            }
        }

        private static void MoveCardToHand(in EffectContext ctx, PlayerState owner, CardInstance card,
            bool wasPublic)
        {
            if (owner.Hand.Count >= MatchEngine.HandCap)
            {
                card.Zone = Zone.Graveyard;
                owner.Graveyard.Add(card);
            }
            else
            {
                // A publicly-known id is retired on entry to a hidden zone. A card selected from the
                // already-hidden deck retains its identity.
                if (wasPublic) card.Id = ctx.State.NextInstanceId();
                card.Zone = Zone.Hand;
                owner.Hand.Add(card);
            }
            ctx.Events.Emit(ctx.Vfx(VfxEvent.onReturn), source: ctx.Source.Id, target: card.Id);
        }
    }

    // ── SILENCE ──────────────────────────────────────────────────────────────────
    internal sealed class SilenceHandler : IActionHandler
    {
        public Verb Verb => Verb.SILENCE;
        public void Execute(in EffectContext ctx)
        {
            foreach (var t in ctx.Targets)
            {
                if (!t.IsMinion) continue;
                var m = t.Minion!;
                var def = ctx.Db.Get(m.DefinitionId);

                // Damage already on the minion is KEPT, so a buffed body falling away can leave the
                // printed one at ≤0 → DeathCheck kills it (EF-017a).
                DerivedStats.ResetToPrinted(m, def, preserveDamage: true);

                ctx.Events.Emit(ctx.Vfx(VfxEvent.onSilence), source: ctx.Source.Id, target: m.Id);
            }
        }
    }

    // ── GAIN_MANA ────────────────────────────────────────────────────────────────
    internal sealed class GainManaHandler : IActionHandler
    {
        public Verb Verb => Verb.GAIN_MANA;
        public void Execute(in EffectContext ctx)
        {
            int amount = ctx.Effect.Amount ?? 0;
            if (amount <= 0) return;
            var ps = ctx.State.PlayerOf(ctx.Source.Controller);

            if (ctx.Effect.Permanent)
            {
                // payload.permanent (dev-docs/specs/card-schema.md §5.2): earn real crystals, capped at 10. Crystals
                // beyond the cap simply don't exist — a gain that changed nothing emits no event.
                // payload.empty ("Gain an empty Mana Crystal" — Quicken / Seedling Rite): the new
                // crystals stay unfilled this turn; without it they are also spendable immediately.
                int before = ps.Mana.Max;
                int max = before + amount;
                ps.Mana.Max = max > MatchEngine.MaxMana ? MatchEngine.MaxMana : max;
                int gained = ps.Mana.Max - before;
                if (gained <= 0) return;
                if (!ctx.Effect.Empty)
                {
                    int filled = ps.Mana.Available + gained;
                    ps.Mana.Available = filled > MatchEngine.MaxMana ? MatchEngine.MaxMana : filled;
                }
                ctx.Events.Emit(ctx.Vfx(VfxEvent.onManaGain), source: ctx.Source.Id, amount: gained);
                return;
            }

            // Default: this-turn temporary crystals (the Spark/Coin pattern).
            ps.Mana.Temporary += amount;
            int avail = ps.Mana.Available + amount;
            ps.Mana.Available = avail > MatchEngine.MaxMana ? MatchEngine.MaxMana : avail;
            ctx.Events.Emit(ctx.Vfx(VfxEvent.onManaGain), source: ctx.Source.Id, amount: amount);
        }
    }

    // ── ADD_TO_HAND ──────────────────────────────────────────────────────────────
    internal sealed class AddToHandHandler : IActionHandler
    {
        public Verb Verb => Verb.ADD_TO_HAND;
        public void Execute(in EffectContext ctx)
        {
            if (ctx.Effect.TokenId == null) return;
            int count = ctx.Effect.Amount ?? 1;
            var ps = ctx.State.PlayerOf(ctx.Source.Controller);
            for (int i = 0; i < count; i++)
            {
                if (ps.Hand.Count >= MatchEngine.HandCap) break;   // hand full → the created card fizzles
                var card = new CardInstance(ctx.State.NextInstanceId(), ctx.Effect.TokenId, ps.Id) { Zone = Zone.Hand };
                ps.Hand.Add(card);
                ctx.Events.Emit(ctx.Vfx(VfxEvent.onDraw), source: card.Id, flags: EventFlags.IsToken);
            }
        }
    }

    // ── TRANSFORM ────────────────────────────────────────────────────────────────
    internal sealed class TransformHandler : IActionHandler
    {
        public Verb Verb => Verb.TRANSFORM;
        public void Execute(in EffectContext ctx)
        {
            if (ctx.Effect.TokenId == null || !ctx.Db.TryGet(ctx.Effect.TokenId, out var def)) return;
            foreach (var t in ctx.Targets)
            {
                if (t.Kind == TargetCandidateKind.Weapon)
                {
                    var owner = ctx.State.Players[t.Seat];
                    if (owner.Weapon == null || owner.Weapon.Id != t.Instance || def.Type != CardType.WEAPON) continue;
                    var replacement = new CardInstance(ctx.State.NextInstanceId(), def.Id, owner.Id);
                    RulesEngine.EnterWeapon(ctx.State, replacement, def, t.Seat);
                    ctx.Events.Emit(ctx.Vfx(VfxEvent.onTransform), source: ctx.Source.Id,
                        target: owner.Weapon.Id);
                    continue;
                }
                if (!t.IsMinion) continue;
                var ps = ctx.State.Players[t.Seat];
                int idx = ps.Board.IndexOf(t.Minion!);
                if (idx < 0) continue;
                // Transform is NOT a death — the old minion vanishes without firing its deathrattle.
                ps.Board.RemoveAt(idx);
                var replacement = new CardInstance(ctx.State.NextInstanceId(), def.Id, ps.Id);
                RulesEngine.EnterBoard(ctx.State, replacement, def, t.Seat, idx);
                ctx.Events.Emit(ctx.Vfx(VfxEvent.onTransform), source: ctx.Source.Id, target: replacement.Id);
            }
        }
    }

    // ── EQUIP ────────────────────────────────────────────────────────────────────
    internal sealed class EquipHandler : IActionHandler
    {
        public Verb Verb => Verb.EQUIP;
        public void Execute(in EffectContext ctx)
        {
            if (ctx.Effect.TokenId == null || !ctx.Db.TryGet(ctx.Effect.TokenId, out var def)) return;
            var ps = ctx.State.PlayerOf(ctx.Source.Controller);
            int seat = ctx.State.SeatOf(ctx.Source.Controller);
            RulesEngine.BreakWeapon(ctx.Interpreter, seat); // destroy old weapon (rattle enqueued) before equipping new
            var weapon = new CardInstance(ctx.State.NextInstanceId(), def.Id, ps.Id);
            RulesEngine.EnterWeapon(ctx.State, weapon, def, seat);
            ctx.Events.Emit(ctx.Vfx(VfxEvent.onEquip), source: ps.Hero.Id);
        }
    }

    // ── DISCOVER ─────────────────────────────────────────────────────────────────
    internal sealed class DiscoverHandler : IActionHandler
    {
        public Verb Verb => Verb.DISCOVER;
        // DISCOVER opens an interactive 3-card choice that pauses the command for a DiscoverChoiceIntent
        // (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/game-state-and-actions.md §3.1). That pending-choice flow is a later step; until then this verb
        // is a documented no-op and the card validator (dev-docs/specs/card-schema.md:40) flags any M1 card that uses it.
        public void Execute(in EffectContext ctx) { }
    }

    // ── FREEZE ────────────────────────────────────────────────────────────────────
    internal sealed class FreezeHandler : IActionHandler
    {
        public Verb Verb => Verb.FREEZE;
        public void Execute(in EffectContext ctx)
        {
            foreach (var t in ctx.Targets)
            {
                if (t.IsMinion) t.Minion!.IsFrozen = true;
                else if (t.IsHero) t.Hero!.IsFrozen = true;
                else continue;
                ctx.Events.Emit(ctx.Vfx(VfxEvent.onFreeze), source: ctx.Source.Id, target: t.Id);
            }
        }
    }

    // ── OVERLOAD ──────────────────────────────────────────────────────────────────
    internal sealed class OverloadHandler : IActionHandler
    {
        public Verb Verb => Verb.OVERLOAD;
        public void Execute(in EffectContext ctx)
        {
            int amount = ctx.Effect.Amount ?? 0;
            if (amount <= 0) return;
            var ps = ctx.State.PlayerOf(ctx.Source.Controller);
            ps.NextTurnOverloadLock += amount;
            // Overload is a crystal-lock; no VFX event in M1 (the mana tray handles the visual).
        }
    }
}
