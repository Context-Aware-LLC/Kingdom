using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Core.Cards;

namespace Emberfall.Core
{
    /// <summary>The shared engine/presentation answer for the live Arcane Surge bonus.</summary>
    public static class ArcaneSurge
    {
        public static int BonusFor(GameState state, PlayerId controller)
        {
            if (state == null) throw new System.ArgumentNullException(nameof(state));

            PlayerState player = state.PlayerOf(controller);
            var board = player.Board;
            int bonus = 0;
            for (int i = 0; i < board.Count; i++)
                if (!board[i].IsSilenced && board[i].Keywords.Contains(Keyword.ARCANE_SURGE)) bonus++;
            CardInstance? weapon = player.Weapon;
            if (weapon != null && !weapon.IsSilenced && weapon.Keywords.Contains(Keyword.ARCANE_SURGE))
                bonus++;
            return bonus;
        }
    }

    /// <summary>
    /// The closed set of reusable engine primitives — one <see cref="IActionHandler"/> per
    /// typed <see cref="RuleAction"/> node. There is no per-card code: a card is
    /// data that composes these verbs. The set grows backend-first — an engine primitive lands
    /// before any card is authored against it, never per-card, and never with the card shipping
    /// first (dev-docs/specs/card-schema.md:40). Built once into an immutable, order-independent registry.
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
                case ModifyStatsAction _: new ModifyStatsHandler().Execute(context); return;
                case GrantKeywordAction _: new GiveKeywordHandler().Execute(context); return;
                case DestroyAction _: new DestroyHandler().Execute(context); return;
                case ReturnToHandAction _: new ReturnToHandHandler().Execute(context); return;
                case ReanimateAction _: new ReanimateHandler().Execute(context); return;
                case SilenceAction _: new SilenceHandler().Execute(context); return;
                case GainManaAction _: new GainManaHandler().Execute(context); return;
                case GainArmorAction _: new GainArmorHandler().Execute(context); return;
                case AddToHandAction _: new AddToHandHandler().Execute(context); return;
                case TransformAction _: new TransformHandler().Execute(context); return;
                case TakeControlAction _: new TakeControlHandler().Execute(context); return;
                case TriggerTollAction _: new TriggerTollHandler().Execute(context); return;
                case EquipAction _: new EquipHandler().Execute(context); return;
                case UnearthAction _: new UnearthHandler().Execute(context); return;
                case FrostAction _: new FrostHandler().Execute(context); return;
                case BackdraftAction _: new BackdraftHandler().Execute(context); return;
                case DrownAction _: new DrownHandler().Execute(context); return;
                case CopyAction _: new CopyHandler().Execute(context); return;
                default:
                    throw new System.InvalidOperationException(
                        $"Rule action '{context.Rule.Action.GetType().Name}' has no runtime handler.");
            }
        }

        /// <summary>+1 arcane surge per friendly ARCANE_SURGE carrier in play: board minions and,
        /// since 2026-09-06, the equipped weapon (card-schema.md "What a weapon can and cannot
        /// do") (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/card-system.md §4.1).</summary>
        internal static int SpellDamageBonus(GameState state, PlayerId controller)
            => ArcaneSurge.BonusFor(state, controller);
    }

    // ── DEAL_DAMAGE ──────────────────────────────────────────────────────────────
    internal sealed class DealDamageHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            var damage = (DamageAction)ctx.Rule.Action;
            // Decided once, before the hit: a bolt aimed at your own hero must not charge itself.
            int amount = damage.InsteadCondition != null &&
                         ConditionEvaluator.Holds(damage.InsteadCondition, ctx.State, ctx.Db, ctx.Source, default)
                ? damage.InsteadAmount!.Value
                : damage.Amount;
            if (ctx.Source.Definition.Type == CardType.SPELL)
                amount += ActionHandlers.SpellDamageBonus(ctx.State, ctx.Source.Controller);

            foreach (var t in ctx.Targets)
                if (t.IsCharacter)
                    DamagePipeline.ApplyDamage(ctx.Interpreter, t.Character, amount, ctx.Source);
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
                    DamagePipeline.ApplyHeal(ctx.Interpreter, t.Character, amount, ctx.Source.Identity);
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
                if (!RulesEngine.EnterBoard(ctx.State, token, def, seat, -1, ctx.Db)) break; // board full → stop
                ctx.Events.Emit(GameEventKind.CardSummoned, source: token.Id, flags: EventFlags.IsToken);
                // The token's controller, not the active player: a toll or an ongoing effect can
                // summon on the OPPONENT's turn, and "friendly" is measured from the token (#272).
                ctx.Interpreter.RaiseObservers(Trigger.ON_FRIENDLY_SUMMON, excluded: token,
                    triggerSource: token.Id, observerController: token.Controller);
            }
        }
    }

    // ── REANIMATE ────────────────────────────────────────────────────────────────
    /// <summary>Moves a graveyard minion onto the effect controller's board as the same instance --
    /// its id is already public (it died in the open), so unlike <see cref="ReturnToHandHandler"/>
    /// there is no hidden zone to mint a fresh id for. Reanimation changes control when the corpse
    /// came from the opponent's graveyard. Stops rather than skips once the controller's board is
    /// full: every remaining target would fail the same <c>RulesEngine.EnterBoard</c> check.</summary>
    internal sealed class ReanimateHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            int destinationSeat = ctx.State.SeatOf(ctx.Source.Controller);
            foreach (var t in ctx.Targets)
            {
                if (t.Zone != Zone.Graveyard || t.Card == null) continue;
                if (!ctx.Db.TryGet(t.Card.DefinitionId, out CardDefinition def)) continue;
                CardInstance card = t.Card;
                if (!RulesEngine.EnterBoard(ctx.State, card, def, destinationSeat, -1, ctx.Db)) break;
                ctx.State.Players[t.Seat].Graveyard.Remove(card);
                ctx.Events.Emit(GameEventKind.CardSummoned, source: card.Id);
                ctx.Interpreter.RaiseObservers(Trigger.ON_FRIENDLY_SUMMON, excluded: card,
                    triggerSource: card.Id, observerController: card.Controller);
            }
        }
    }

    // ── MODIFY STATS ─────────────────────────────────────────────────────────────
    /// <summary>
    /// The only action that moves a minion's stats, in either direction. A negative delta is an
    /// ordinary debuff and needs no separate path; allegiance is whatever the rule's selector says.
    /// </summary>
    internal sealed class ModifyStatsHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            var buff = (ModifyStatsAction)ctx.Rule.Action;
            int atk = buff.AttackDelta;
            int hp = buff.HealthDelta;
            if (atk == 0 && hp == 0) return;
            foreach (var t in ctx.Targets)
            {
                if (!t.IsMinion) continue;
                var m = t.Minion!;
                // The enchantment IS the record; DerivedStats recomputes live stats from the list
                // (EF-017a). Never bake `CurrentAttack += atk` here — a contribution melted into the
                // total cannot be taken back off when its source leaves.
                m.Enchantments.Add(new Enchantment { Source = ctx.Source.Identity, AttackDelta = atk, HealthDelta = hp });
                DerivedStats.Recompute(m, ctx.Db.Get(m.DefinitionId));
                ctx.Events.Emit(GameEventKind.StatsModified, source: ctx.Source.Identity, target: m.Id, amount: atk != 0 ? atk : hp);
            }
        }
    }

    // ── GIVE_KEYWORD ─────────────────────────────────────────────────────────────
    internal sealed class GiveKeywordHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            Keyword kw = ((GrantKeywordAction)ctx.Rule.Action).Keyword;
            if (!KeywordSemantics.IsCarrierBehavior(kw)) return;
            foreach (var t in ctx.Targets)
            {
                if (!t.IsMinion) continue;
                var m = t.Minion!;
                // The enchantment is recorded only when the keyword was NEWLY added, so a second grant
                // of a keyword the minion already has leaves no record. That asymmetry is deliberate
                // and currently unreachable as a defect: AURA is a stats-only fold (AuraCapabilities
                // accepts ModifyStatsAction alone, so no aura can grant a keyword), and the one
                // function whose subtraction the missing record would fool — DerivedStats.RemoveBySource
                // — has no production caller. Aura teardown goes through RemoveAuras, which drops
                // entries by IsAura provenance and never touches Keywords at all.
                //
                // RETIREMENT TRIGGER: if you widen AuraCapabilities to accept GrantKeywordAction, or
                // wire RemoveBySource into a runtime path, record the enchantment unconditionally here
                // (keep the Keywords.Add guard for the set, drop it for the record). It is
                // HASH-CANONICAL — the enchantment list is serialized — so it lands in a batched
                // rebaseline, never on its own. Closed as NOR-187, 2026-08-06.
                if (m.Keywords.Add(kw))
                    m.Enchantments.Add(new Enchantment { Source = ctx.Source.Identity, GrantedKeyword = kw });
                if (kw == Keyword.AEGIS) m.HasAegisShield = true;
                ctx.Events.Emit(GameEventKind.KeywordApplied, source: ctx.Source.Identity, target: m.Id);
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
                else if (t.Zone == Zone.Weapon)
                    RulesEngine.BreakWeapon(ctx.Interpreter, t.Seat);
                else if (t.Zone == Zone.Hand && t.Card != null)
                {
                    PlayerState owner = ctx.State.Players[t.Seat];
                    if (!owner.Hand.Remove(t.Card)) continue;
                    RulesEngine.EnterGraveyard(ctx.State, owner, t.Card);
                    ctx.Events.Emit(GameEventKind.CardDestroyed, source: t.Card.Id);
                }
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
                if (t.Zone == Zone.Weapon)
                {
                    var owner = ctx.State.Players[t.Seat];
                    if (owner.Weapon == null || owner.Weapon.Id != t.Instance) continue;
                    CardInstance weapon = owner.Weapon;
                    owner.Weapon = null;
                    ResetForNewZoneLife(ctx, weapon);
                    MoveCardToHand(ctx, owner, weapon, wasPublic: true);
                    continue;
                }
                if (t.Card != null && t.Zone != Zone.Board)
                {
                    var owner = ctx.State.Players[t.Seat];
                    if (t.Zone == Zone.Hand) continue;
                    if (t.Zone == Zone.Deck) owner.Deck.Remove(t.Card);
                    else if (t.Zone == Zone.Graveyard) owner.Graveyard.Remove(t.Card);
                    MoveCardToHand(ctx, owner, t.Card, wasPublic: t.Zone == Zone.Graveyard);
                    continue;
                }
                if (!t.IsMinion) continue;          // heroes/weapons can't be bounced
                var m = t.Minion!;
                var ps = ctx.State.Players[t.Seat];
                int idx = ps.Board.IndexOf(m);
                if (idx < 0) continue;
                ps.Board.RemoveAt(idx);
                RulesEngine.Reindex(ps.Board);
                AuraEngine.Recompute(ctx.State, ctx.Db);

                // Reset to printed form — board-runtime state is dropped on the bounce. The card arrives
                // in hand whole, so damage is NOT preserved across the zone change (EF-017a).
                ResetForNewZoneLife(ctx, m);

                MoveCardToHand(ctx, ps, m, wasPublic: true);
            }
        }

        private static void ResetForNewZoneLife(in EffectContext ctx, CardInstance card)
        {
            DerivedStats.ResetToPrinted(card, ctx.Db.Get(card.DefinitionId), preserveDamage: false);
            card.SummonedThisTurn = false;
            card.AttacksUsedThisTurn = 0;
            card.HasUsedRekindle = false;
        }

        private static void MoveCardToHand(in EffectContext ctx, PlayerState owner, CardInstance card,
            bool wasPublic)
        {
            if (owner.Hand.Count >= MatchEngine.HandCap)
            {
                RulesEngine.EnterGraveyard(ctx.State, owner, card);
            }
            else
            {
                // A publicly-known id is retired on entry to a hidden zone. A card selected from the
                // already-hidden deck retains its identity.
                if (wasPublic) card.Id = ctx.State.NextInstanceId();
                card.Zone = Zone.Hand;
                owner.Hand.Add(card);
            }
            ctx.Events.Emit(GameEventKind.CardBounced, source: ctx.Source.Identity, target: card.Id);
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
                    : t.Zone == Zone.Weapon ? t.Card : null;
                if (card == null) continue;
                var def = ctx.Db.Get(card.DefinitionId);

                // Damage already on the minion is KEPT, so a buffed body falling away can leave the
                // printed one at ≤0 → DeathCheck kills it. A weapon retains spent durability: silence
                // strips abilities, it never refunds a spent resource.
                DerivedStats.ResetToSilenced(card, def, preserveDamage: t.IsMinion);

                ctx.Events.Emit(GameEventKind.CardSilenced, source: ctx.Source.Identity, target: card.Id);
                AuraEngine.Recompute(ctx.State, ctx.Db);
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
                ctx.Events.Emit(GameEventKind.ManaGained, source: ctx.Source.Identity, amount: gained);
                return;
            }

            // Default: this-turn temporary crystals (the Spark/Coin pattern).
            ps.Mana.Temporary += amount;
            int avail = ps.Mana.Available + amount;
            ps.Mana.Available = avail > MatchEngine.MaxMana ? MatchEngine.MaxMana : avail;
            ctx.Events.Emit(GameEventKind.ManaGained, source: ctx.Source.Identity, amount: amount);
        }
    }

    // ── GAIN_ARMOR ──────────────────────────────────────────────────────────────
    internal sealed class GainArmorHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            int amount = ((GainArmorAction)ctx.Rule.Action).Amount;
            if (amount <= 0) return;
            HeroInstance hero = ctx.State.PlayerOf(ctx.Source.Controller).Hero;
            hero.Armor += amount;
            ctx.Events.Emit(GameEventKind.ArmorGained, source: hero.Id, target: hero.Id,
                amount: amount);
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
                if (t.Zone == Zone.Weapon)
                {
                    var owner = ctx.State.Players[t.Seat];
                    if (owner.Weapon == null || owner.Weapon.Id != t.Instance || def.Type != CardType.WEAPON) continue;
                    var weaponReplacement = new CardInstance(ctx.State.NextInstanceId(), def.Id, owner.Id);
                    RulesEngine.EnterWeapon(ctx.State, weaponReplacement, def, t.Seat);
                    ctx.Events.Emit(GameEventKind.CardTransformed, source: ctx.Source.Identity,
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
                RulesEngine.EnterBoard(ctx.State, replacement, def, t.Seat, idx, ctx.Db);
                ctx.Events.Emit(GameEventKind.CardTransformed, source: ctx.Source.Identity, target: replacement.Id);
            }
        }
    }

    // ── TRIGGER_TOLL ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Fires a living board minion's own ON_DEATH rules, leaving it alive.
    ///
    /// <para><b>Enqueued, never executed here.</b> <c>RaiseSelf</c> puts the target's tolls on the
    /// same queue a real death would, so they drain in <see cref="EffectInterpreter.Drain"/> behind
    /// whatever is already waiting, get the same per-effect DeathCheck/WinCheck, and count against
    /// the same resolution cap. Calling the target's handlers inline instead would have run them
    /// ahead of effects queued earlier — a different board for the same cards.</para>
    ///
    /// <para><b>The target is the source of what it raises.</b> The carrier passed to
    /// <c>RaiseSelf</c> is the MINION, not the card holding this rule, so a toll reading SELF or
    /// naming its controller means the minion — the reading the card face states. Which is also why
    /// nothing here checks silence: <c>RaiseSelf</c> already refuses to form work from a silenced
    /// permanent, and that is the answer for a silenced minion whose toll is being rung by somebody
    /// else, exactly as it is when it dies.</para>
    ///
    /// <para><b>Nothing dies and nothing rekindles.</b> The minion keeps its slot, its stats and its
    /// unused REKINDLE. A toll is being RUNG, not paid.</para>
    /// </summary>
    internal sealed class TriggerTollHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            foreach (TargetCandidate target in ctx.Targets)
            {
                if (!target.IsMinion) continue;
                CardInstance minion = target.Minion!;
                ctx.Interpreter.RaiseSelf(minion, Trigger.ON_DEATH,
                    System.Array.Empty<TargetRef>(), TriggerEventContext.ForSource(minion.Id));
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
                    RulesEngine.Reindex(destination.Board);
                    AuraEngine.Recompute(ctx.State, ctx.Db);
                    ctx.Events.Emit(GameEventKind.ControlChanged, source: ctx.Source.Identity,
                        target: minion.Id);
                    continue;
                }
                if (target.Zone != Zone.Weapon || destination.Weapon != null ||
                    owner.Weapon == null || owner.Weapon.Id != target.Instance) continue;
                CardInstance weapon = owner.Weapon;
                owner.Weapon = null;
                weapon.Controller = destination.Id;
                destination.Weapon = weapon;
                AuraEngine.Recompute(ctx.State, ctx.Db);
                ctx.Events.Emit(GameEventKind.ControlChanged, source: ctx.Source.Identity,
                    target: weapon.Id);
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
        public void Execute(in EffectContext ctx)
        {
            PlayerState chooser = ctx.State.PlayerOf(ctx.Source.Controller);

            // Never overwrite an unresolved choice or open a choice that cannot be completed. An
            // empty eligible pool fails closed without consuming RNG. There is no longer an authored
            // count to be invalid — the offer size is UnearthAction.Options.
            if (ctx.State.PendingChoice != null || chooser.Hand.Count >= MatchEngine.HandCap)
                return;

            List<int> remaining = CanonicalEligiblePool(ctx.Db);
            if (remaining.Count == 0) return;

            int offerCount = System.Math.Min(UnearthAction.Options, remaining.Count);
            var pending = new PendingChoice
            {
                Chooser = chooser.Id,
                Kind = PendingChoiceKind.Unearth,
                Source = ctx.Source.Identity,
                // The offer event consumes this same cursor value immediately below. This gives
                // choices a match-wide monotonic correlation id without a second serialized cursor.
                ChoiceSequence = ctx.State.NextEventSequence,
            };

            // Sampling is without replacement. The canonical sort happens BEFORE RNG; sampled
            // order is the player-visible option order and must never be sorted afterward.
            for (int i = 0; i < offerCount; i++)
            {
                int index = ctx.State.Rng.NextInt(remaining.Count);
                pending.Options.Add(remaining[index]);
                remaining.RemoveAt(index);
            }

            ctx.State.PendingChoice = pending;
            ctx.Events.Emit(GameEventKind.UnearthOffered, source: ctx.Source.Identity);
        }

        private static List<int> CanonicalEligiblePool(ICardDatabase db)
        {
            var ids = new HashSet<int>();
            foreach (CardDefinition definition in db.AllCollectible())
            {
                // AllCollectible implementations promise PoolEligible rows, but this is a trust
                // boundary for authoritative choice state, so enforce the gate again here.
                if (definition != null && definition.PoolEligible && definition.Id > 0)
                    ids.Add(definition.Id);
            }

            // Sorted so the offered pool is the same list on both machines. The ORDER CHANGED
            // when the key did: this is numeric order now, not the ordinal order of the handles.
            var canonical = new List<int>(ids);
            canonical.Sort();
            return canonical;
        }
    }

    // ── FROST ────────────────────────────────────────────────────────────────────
    internal sealed class FrostHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            bool thaw = ((FrostAction)ctx.Rule.Action).Negate;
            foreach (var t in ctx.Targets)
            {
                IEffectHost? host = t.Host;
                if (host == null) continue;

                if (thaw)
                {
                    // The inverse: drop every Frost instance the target carries, whatever applied it
                    // and however many turns it still owed. No event — nothing in the closed
                    // GameEventKind vocabulary means "thawed", and the client reads the frost overlay
                    // off the snapshot (CardLiveState.IsFrosted), so the state change is what it sees.
                    EffectList.Remove(host.Effects, EffectKind.Frost);
                    continue;
                }

                // One turn-scoped instance. It is aged at the START of the frozen character's own turn
                // and expires at the END of a turn it entered already aged, so a unit frozen on its own
                // turn (warden's ON_DEATH) still owes a full turn — NOR-367 without the second flag.
                var frost = new EffectInstance(EffectKind.Frost, ctx.Source.Identity,
                    EffectScope.ControllerTurns, turnsRemaining: 1);
                EffectList.Apply(host.Effects, frost);
                ctx.Events.Emit(GameEventKind.FrostApplied, source: ctx.Source.Identity, target: t.Id);
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

    // ── DROWN ────────────────────────────────────────────────────────────────────
    /// <summary>Half a minion's CURRENT health rounded up, or a flat 2 to a hero. Bypasses Aegis
    /// entirely (<c>ignoresAegis: true</c>) — see <see cref="DrownAction"/>.</summary>
    internal sealed class DrownHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            foreach (var t in ctx.Targets)
            {
                if (t.IsHero)
                    DamagePipeline.ApplyDamage(ctx.Interpreter, t.Character, 2, ctx.Source, ignoresAegis: true);
                else if (t.IsMinion)
                {
                    int half = (t.Minion!.CurrentHealth + 1) / 2; // integer ceil-division for positive health
                    DamagePipeline.ApplyDamage(ctx.Interpreter, t.Character, half, ctx.Source, ignoresAegis: true);
                }
            }
        }
    }

    // ── COPY ─────────────────────────────────────────────────────────────────────
    /// <summary>See <see cref="CopyAction"/> for the shape this fills in.</summary>
    internal sealed class CopyHandler : IActionHandler
    {
        public void Execute(in EffectContext ctx)
        {
            int seat = ctx.State.SeatOf(ctx.Source.Controller);
            foreach (var t in ctx.Targets)
            {
                if (!t.IsMinion) continue;
                CardInstance original = t.Minion!;
                if (!ctx.Db.TryGet(original.DefinitionId, out CardDefinition def)) continue;

                var copy = new CardInstance(ctx.State.NextInstanceId(), original.DefinitionId, ctx.Source.Controller);
                // EnterBoard does the topology work (board-cap check, insert, reindex, first aura
                // pass) and seeds the printed form -- which the lines below then override with the
                // target's CURRENT stats and keywords, "exact copy" rather than a fresh printed one.
                if (!RulesEngine.EnterBoard(ctx.State, copy, def, seat, -1, ctx.Db)) break; // board full → stop, mirrors SummonHandler

                copy.Keywords.Clear();
                foreach (var k in original.Keywords) copy.Keywords.Add(k);
                copy.HasAegisShield = original.HasAegisShield;

                // The buffed delta becomes the copy's OWN (non-aura) enchantment rather than a raw
                // number, so a later DerivedStats.Recompute -- any board change triggers one -- folds
                // it back in instead of silently discarding it (EF-017a: stats are def + Enchantments,
                // never a stored total).
                int attackDelta = original.CurrentAttack - DerivedStats.BaseAttack(def);
                int healthDelta = original.MaxHealth - DerivedStats.BaseMaxHealth(def);
                if (attackDelta != 0 || healthDelta != 0)
                {
                    copy.Enchantments.Add(new Enchantment
                    {
                        Source = copy.Id,
                        AttackDelta = attackDelta,
                        HealthDelta = healthDelta,
                    });
                    DerivedStats.Recompute(copy, def);
                }

                ctx.Events.Emit(GameEventKind.CardSummoned, source: copy.Id, flags: EventFlags.IsToken);
                ctx.Interpreter.RaiseObservers(Trigger.ON_FRIENDLY_SUMMON, excluded: copy,
                    triggerSource: copy.Id, observerController: copy.Controller);
            }
        }
    }
}
