using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;

namespace Emberfall.Core
{
    /// <summary>
    /// The closed set of reusable engine primitives — one <see cref="IActionHandler"/> per
    /// typed <see cref="RuleAction"/> node. There is no per-card code: a card is
    /// data that composes these verbs. The set grows ONLY through the <c>requiresEngineeringPrimitive</c>
    /// gate (dev-docs/specs/card-schema.md:40), never per-card. Built once into an immutable, order-independent registry.
    /// </summary>
    internal static class ActionHandlers
    {
        public static void Execute(in EffectContext context)
        {
            switch (context.Rule.Action)
            {
                case DamageAction _: new DealDamageHandler().Execute(context); return;
                case HealAction _: new HealHandler().Execute(context); return;
                case DrawAction _: new DrawHandler().Execute(context); return;
                case SummonAction _: new SummonHandler().Execute(context); return;
                case ModifyStatsAction _:
                case ReinforceAction _: new BuffHandler().Execute(context); return;
                case GrantKeywordAction _: new GiveKeywordHandler().Execute(context); return;
                case DestroyAction _: new DestroyHandler().Execute(context); return;
                case ReturnToHandAction _: new ReturnToHandHandler().Execute(context); return;
                case SilenceAction _: new SilenceHandler().Execute(context); return;
                case GainManaAction _: new GainManaHandler().Execute(context); return;
                case GainArmorAction _: new GainArmorHandler().Execute(context); return;
                case AddToHandAction _: new AddToHandHandler().Execute(context); return;
                case TransformAction _: new TransformHandler().Execute(context); return;
                case TakeControlAction _: new TakeControlHandler().Execute(context); return;
                case EquipAction _: new EquipHandler().Execute(context); return;
                case UnearthAction _: new UnearthHandler().Execute(context); return;
                case FrostAction _: new FrostHandler().Execute(context); return;
                case BackdraftAction _: new BackdraftHandler().Execute(context); return;
                default:
                    throw new System.InvalidOperationException(
                        $"Rule action '{context.Rule.Action.GetType().Name}' has no runtime handler.");
            }
        }

        /// <summary>The effective keyword set of an effect's source — live for a board minion or
        /// equipped weapon, printed for a spell that has already moved to the graveyard.</summary>
        internal static HashSet<Keyword> SourceKeywords(in EffectContext ctx)
        {
            if (ctx.Source.Zone == Zone.Board || ctx.Source.Zone == Zone.Weapon)
                return ctx.Source.Keywords;
            var set = new HashSet<Keyword>();
            foreach (var k in ctx.Db.Get(ctx.Source.DefinitionId).Keywords) set.Add(k);
            return set;
        }

        /// <summary>+1 arcane surge per friendly ARCANE_SURGE minion in play (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/card-system.md §4.1).</summary>
        internal static int SpellDamageBonus(GameState state, PlayerId controller)
        {
            var board = state.PlayerOf(controller).Board;
            int bonus = 0;
            for (int i = 0; i < board.Count; i++)
                if (board[i].Keywords.Contains(Keyword.ARCANE_SURGE)) bonus++;
            return bonus;
        }
    }

    // ── DEAL_DAMAGE ──────────────────────────────────────────────────────────────
    internal sealed class DealDamageHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            int amount = ((DamageAction)ctx.Rule.Action).Amount;
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
        public void Execute(in EffectContext ctx)
        {
            int amount = ((HealAction)ctx.Rule.Action).Amount;
            foreach (var t in ctx.Targets)
                if (t.IsCharacter)
                    DamagePipeline.ApplyHeal(ctx.Interpreter, t.Character, amount, ctx.Source.Id);
        }
    }

    // ── DRAW ─────────────────────────────────────────────────────────────────────
    internal sealed class DrawHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            int count = ((DrawAction)ctx.Rule.Action).Count;
            int seat = ctx.State.SeatOf(ctx.Source.Controller);
            for (int i = 0; i < count; i++)
                RulesEngine.DrawOne(ctx.State, seat, ctx.Events);
        }
    }

    // ── SUMMON ───────────────────────────────────────────────────────────────────
    internal sealed class SummonHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            var action = (SummonAction)ctx.Rule.Action;
            if (!ctx.Db.TryGet(action.CardId, out var def)) return;
            int count = action.Count;
            int seat = ctx.State.SeatOf(ctx.Source.Controller);
            for (int i = 0; i < count; i++)
            {
                var token = new CardInstance(ctx.State.NextInstanceId(), def.Id, ctx.Source.Controller);
                if (!RulesEngine.EnterBoard(ctx.State, token, def, seat, -1)) break; // board full → stop
                ctx.Events.Emit(GameEventKind.CardSummoned, source: token.Id, flags: EventFlags.IsToken);
                ctx.Interpreter.RaiseObservers(Trigger.ON_FRIENDLY_SUMMON, excluded: token,
                    triggerSource: token.Id);
            }
        }
    }

    // ── BUFF ─────────────────────────────────────────────────────────────────────
    internal sealed class BuffHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            int atk = ctx.Rule.Action is ReinforceAction ? 1 : ((ModifyStatsAction)ctx.Rule.Action).AttackDelta;
            int hp = ctx.Rule.Action is ReinforceAction ? 1 : ((ModifyStatsAction)ctx.Rule.Action).HealthDelta;
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
                ctx.Events.Emit(GameEventKind.StatsModified, source: ctx.Source.Id, target: m.Id, amount: atk != 0 ? atk : hp);
            }
        }
    }

    // ── GIVE_KEYWORD ─────────────────────────────────────────────────────────────
    internal sealed class GiveKeywordHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            Keyword kw = ((GrantKeywordAction)ctx.Rule.Action).Keyword;
            foreach (var t in ctx.Targets)
            {
                if (!t.IsMinion) continue;
                var m = t.Minion!;
                // KNOWN GAP for EF-017b: the enchantment is recorded only when the keyword was NEWLY
                // added, so a second grant of a keyword the minion already has leaves no record. Once
                // auras can grant keywords, that makes "aura leaves" strip a keyword a permanent grant
                // was also holding up. Recording unconditionally is the fix and it is HASH-CANONICAL —
                // the enchantment list is serialized — so it belongs in the batched rebaseline, not here.
                if (m.Keywords.Add(kw))
                    m.Enchantments.Add(new Enchantment { Source = ctx.Source.Id, GrantedKeyword = kw });
                if (kw == Keyword.AEGIS) m.HasAegisShield = true;
                ctx.Events.Emit(GameEventKind.KeywordApplied, source: ctx.Source.Id, target: m.Id);
            }
        }
    }

    // ── DESTROY ──────────────────────────────────────────────────────────────────
    internal sealed class DestroyHandler : IActionHandler
    {
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
                m.HasUsedRekindle = false;

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
            ctx.Events.Emit(GameEventKind.CardBounced, source: ctx.Source.Id, target: card.Id);
        }
    }

    // ── SILENCE ──────────────────────────────────────────────────────────────────
    internal sealed class SilenceHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            foreach (var t in ctx.Targets)
            {
                CardInstance? card = t.IsMinion ? t.Minion
                    : t.Kind == TargetCandidateKind.Weapon ? t.Card : null;
                if (card == null) continue;
                var def = ctx.Db.Get(card.DefinitionId);

                // Damage already on the minion is KEPT, so a buffed body falling away can leave the
                // printed one at ≤0 → DeathCheck kills it (EF-017a). A weapon retains spent durability
                // while its enchantments and granted keywords reset to the printed definition.
                DerivedStats.ResetToPrinted(card, def, preserveDamage: t.IsMinion);

                ctx.Events.Emit(GameEventKind.CardSilenced, source: ctx.Source.Id, target: card.Id);
            }
        }
    }

    // ── GAIN_MANA ────────────────────────────────────────────────────────────────
    internal sealed class GainManaHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            var action = (GainManaAction)ctx.Rule.Action;
            int amount = action.Amount;
            if (amount <= 0) return;
            var ps = ctx.State.PlayerOf(ctx.Source.Controller);

            if (action.Kind != ManaGainKind.TEMPORARY)
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
                if (action.Kind != ManaGainKind.PERMANENT_EMPTY)
                {
                    int filled = ps.Mana.Available + gained;
                    ps.Mana.Available = filled > MatchEngine.MaxMana ? MatchEngine.MaxMana : filled;
                }
                ctx.Events.Emit(GameEventKind.ManaGained, source: ctx.Source.Id, amount: gained);
                return;
            }

            // Default: this-turn temporary crystals (the Spark/Coin pattern).
            ps.Mana.Temporary += amount;
            int avail = ps.Mana.Available + amount;
            ps.Mana.Available = avail > MatchEngine.MaxMana ? MatchEngine.MaxMana : avail;
            ctx.Events.Emit(GameEventKind.ManaGained, source: ctx.Source.Id, amount: amount);
        }
    }

    // ── GAIN_ARMOR ──────────────────────────────────────────────────────────────
    internal sealed class GainArmorHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            int amount = ((GainArmorAction)ctx.Rule.Action).Amount;
            if (amount <= 0) return;
            ctx.State.PlayerOf(ctx.Source.Controller).Hero.Armor += amount;
        }
    }

    // ── ADD_TO_HAND ──────────────────────────────────────────────────────────────
    internal sealed class AddToHandHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            var action = (AddToHandAction)ctx.Rule.Action;
            int count = action.Count;
            var ps = ctx.State.PlayerOf(ctx.Source.Controller);
            for (int i = 0; i < count; i++)
            {
                if (ps.Hand.Count >= MatchEngine.HandCap) break;   // hand full → the created card fizzles
                var card = new CardInstance(ctx.State.NextInstanceId(), action.CardId, ps.Id) { Zone = Zone.Hand };
                ps.Hand.Add(card);
                ctx.Events.Emit(GameEventKind.CardCreatedInHand, source: card.Id, flags: EventFlags.IsToken);
            }
        }
    }

    // ── TRANSFORM ────────────────────────────────────────────────────────────────
    internal sealed class TransformHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            var action = (TransformAction)ctx.Rule.Action;
            if (!ctx.Db.TryGet(action.CardId, out var def)) return;
            foreach (var t in ctx.Targets)
            {
                if (t.Kind == TargetCandidateKind.Weapon)
                {
                    var owner = ctx.State.Players[t.Seat];
                    if (owner.Weapon == null || owner.Weapon.Id != t.Instance || def.Type != CardType.WEAPON) continue;
                    var weaponReplacement = new CardInstance(ctx.State.NextInstanceId(), def.Id, owner.Id);
                    RulesEngine.EnterWeapon(ctx.State, weaponReplacement, def, t.Seat);
                    ctx.Events.Emit(GameEventKind.CardTransformed, source: ctx.Source.Id,
                        target: owner.Weapon.Id);
                    continue;
                }
                if (!t.IsMinion) continue;
                var ps = ctx.State.Players[t.Seat];
                int idx = ps.Board.IndexOf(t.Minion!);
                if (idx < 0) continue;
                // Transform is NOT a death — the old minion vanishes without firing its toll.
                ps.Board.RemoveAt(idx);
                var replacement = new CardInstance(ctx.State.NextInstanceId(), def.Id, ps.Id);
                RulesEngine.EnterBoard(ctx.State, replacement, def, t.Seat, idx);
                ctx.Events.Emit(GameEventKind.CardTransformed, source: ctx.Source.Id, target: replacement.Id);
            }
        }
    }

    // ── TAKE_CONTROL ────────────────────────────────────────────────────────────
    internal sealed class TakeControlHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            int destinationSeat = ctx.State.SeatOf(ctx.Source.Controller);
            PlayerState destination = ctx.State.Players[destinationSeat];
            foreach (TargetCandidate target in ctx.Targets)
            {
                if (target.Seat == destinationSeat) continue;
                PlayerState owner = ctx.State.Players[target.Seat];
                if (target.IsMinion)
                {
                    if (destination.Board.Count >= MatchEngine.BoardCap) continue;
                    CardInstance minion = target.Minion!;
                    if (!owner.Board.Remove(minion)) continue;
                    RulesEngine.Reindex(owner.Board);
                    minion.Controller = destination.Id;
                    minion.Position = destination.Board.Count;
                    destination.Board.Add(minion);
                    continue;
                }
                if (target.Kind != TargetCandidateKind.Weapon || destination.Weapon != null ||
                    owner.Weapon == null || owner.Weapon.Id != target.Instance) continue;
                CardInstance weapon = owner.Weapon;
                owner.Weapon = null;
                weapon.Controller = destination.Id;
                destination.Weapon = weapon;
            }
        }
    }

    // ── EQUIP ────────────────────────────────────────────────────────────────────
    internal sealed class EquipHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            var action = (EquipAction)ctx.Rule.Action;
            if (!ctx.Db.TryGet(action.CardId, out var def)) return;
            var ps = ctx.State.PlayerOf(ctx.Source.Controller);
            int seat = ctx.State.SeatOf(ctx.Source.Controller);
            RulesEngine.BreakWeapon(ctx.Interpreter, seat); // destroy old weapon (rattle enqueued) before equipping new
            var weapon = new CardInstance(ctx.State.NextInstanceId(), def.Id, ps.Id);
            RulesEngine.EnterWeapon(ctx.State, weapon, def, seat);
            ctx.Events.Emit(GameEventKind.WeaponEquipped, source: ps.Hero.Id);
        }
    }

    // ── UNEARTH ─────────────────────────────────────────────────────────────────
    internal sealed class UnearthHandler : IActionHandler
    {
        // UNEARTH opens an interactive 3-card choice that pauses the command for a UnearthChoiceIntent
        // (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/game-state-and-actions.md §3.1). That pending-choice flow is a later step; until then this verb
        // is a documented no-op and the card validator (dev-docs/specs/card-schema.md:40) flags any M1 card that uses it.
        public void Execute(in EffectContext ctx) { }
    }

    // ── FROST ────────────────────────────────────────────────────────────────────
    internal sealed class FrostHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            foreach (var t in ctx.Targets)
            {
                if (t.IsMinion) t.Minion!.IsFrosted = true;
                else if (t.IsHero) t.Hero!.IsFrosted = true;
                else continue;
                ctx.Events.Emit(GameEventKind.FrostApplied, source: ctx.Source.Id, target: t.Id);
            }
        }
    }

    // ── BACKDRAFT ──────────────────────────────────────────────────────────────────
    internal sealed class BackdraftHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            int amount = ((BackdraftAction)ctx.Rule.Action).Amount;
            if (amount <= 0) return;
            var ps = ctx.State.PlayerOf(ctx.Source.Controller);
            ps.NextTurnBackdraftLock += amount;
            // Backdraft is a crystal-lock; no VFX event in M1 (the mana tray handles the visual).
        }
    }
}
