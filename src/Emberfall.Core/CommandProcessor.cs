using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core
{
    /// <summary>
    /// The single mutation door (game-state-and-actions.md §4: Validate → Apply → Emit). On reject
    /// there is ZERO mutation and ZERO events. Same <c>(GameState, PlayerIntent)</c> ⇒ same mutation
    /// and same ordered event stream on any machine — the property the golden-replay test proves (R1).
    ///
    /// M1-foundation subset: <see cref="MulliganIntent"/>, <see cref="EndTurnIntent"/>,
    /// <see cref="ConcedeIntent"/>, <see cref="AutoConfirmIntent"/> — i.e. the match-start + turn-loop
    /// spine (coin flip and the deal live in <see cref="MatchEngine"/>). Card-play / attack / hero-power
    /// land with the effect interpreter (M1-KICKOFF.md Phase B) and are rejected as NotImplemented here.
    /// </summary>
    public sealed class CommandProcessor : ICommandProcessor, ICommandValidator
    {
        private readonly ICardDatabase _db;

        public CommandProcessor(ICardDatabase db) => _db = db;

        // ── Validate ─────────────────────────────────────────────────────────────

        public bool CanExecute(GameState state, PlayerIntent intent, out RejectReason? reason)
        {
            reason = null;

            if (state.Outcome != MatchOutcome.InProgress)
            {
                // Conceding a finished game is still meaningless; only the timeless system intent is moot.
                reason = RejectReason.GameOver;
                return false;
            }

            switch (intent)
            {
                case MulliganIntent m:
                    return ValidateMulligan(state, m, out reason);
                case EndTurnIntent e:
                    return ValidateEndTurn(state, e, out reason);
                case ConcedeIntent c:
                    return KnownPlayer(state, c.Player, out reason);
                case AutoConfirmIntent a:
                    return ValidateAutoConfirm(state, a, out reason);
                case PlayCardIntent p:
                    return ValidatePlayCard(state, p, out reason);
                case AttackIntent a:
                    return ValidateAttack(state, a, out reason);
                case HeroPowerIntent hp:
                    return ValidateHeroPower(state, hp, out reason);
                // ┌─ SECURITY TRIPWIRE — VULN-001 (LATENT). READ BEFORE DELETING THIS CASE. ─┐
                // ChosenCardId is the ONLY client-authored string in the shipping intent set
                // that becomes a card identity. This rejection is the sole reason VULN-001 is
                // latent; implementing Discover arms it. An unvalidated id enters authoritative
                // state, ships to BOTH seats, and throws in the OPPONENT's BoardController —
                // freezing their client for the rest of the match while the server stays healthy.
                // Binding rules (TryGet not Get; validate against the OFFERED set, not just the
                // database) — docs\security\known-vulnerabilities\VULN-001-*.md §4.
                // └──────────────────────────────────────────────────────────────────────────┘
                case DiscoverChoiceIntent _:
                    reason = RejectReason.NotImplemented;   // Discover = later
                    return false;
                default:
                    reason = RejectReason.UnknownIntent;    // off-allowlist (interfaces.md §4.6)
                    return false;
            }
        }

        private static bool KnownPlayer(GameState state, PlayerId p, out RejectReason? reason)
        {
            reason = null;
            if (state.Players[0].Id == p || state.Players[1].Id == p) return true;
            reason = RejectReason.UnknownIntent;
            return false;
        }

        private static bool ValidateMulligan(GameState state, MulliganIntent m, out RejectReason? reason)
        {
            if (!KnownPlayer(state, m.Player, out reason)) return false;
            if (state.Phase != TurnPhase.Mulligan) { reason = RejectReason.WrongPhase; return false; }
            int seat = state.SeatOf(m.Player);
            if (seat == 0 ? state.Mulligan0Done : state.Mulligan1Done) { reason = RejectReason.AlreadyMulliganed; return false; }

            var hand = state.Players[seat].Hand;
            foreach (var id in m.ToReplace)
            {
                if (!ContainsInstance(hand, id)) { reason = RejectReason.NotInHand; return false; }
            }
            return true;
        }

        private static bool ValidateEndTurn(GameState state, EndTurnIntent e, out RejectReason? reason)
        {
            if (!KnownPlayer(state, e.Player, out reason)) return false;
            if (state.Phase != TurnPhase.Main) { reason = RejectReason.WrongPhase; return false; }
            if (state.ActivePlayer != e.Player) { reason = RejectReason.NotYourTurn; return false; }
            return true;
        }

        private static bool ValidateAutoConfirm(GameState state, AutoConfirmIntent a, out RejectReason? reason)
        {
            if (!KnownPlayer(state, a.Player, out reason)) return false;
            int seat = state.SeatOf(a.Player);
            if (state.Phase == TurnPhase.Mulligan)
            {
                if (seat == 0 ? state.Mulligan0Done : state.Mulligan1Done) { reason = RejectReason.AlreadyMulliganed; return false; }
                return true;
            }
            if (state.Phase == TurnPhase.Main && state.ActivePlayer == a.Player) return true;
            reason = RejectReason.WrongPhase;
            return false;
        }

        private bool ValidatePlayCard(GameState state, PlayCardIntent p, out RejectReason? reason)
        {
            if (!KnownPlayer(state, p.Player, out reason)) return false;
            if (state.Phase != TurnPhase.Main) { reason = RejectReason.WrongPhase; return false; }
            if (state.ActivePlayer != p.Player) { reason = RejectReason.NotYourTurn; return false; }

            int seat = state.SeatOf(p.Player);
            var ps = state.Players[seat];

            int handIdx = IndexOfInstance(ps.Hand, p.Card);
            if (handIdx < 0) { reason = RejectReason.NotInHand; return false; }

            var def = _db.Get(ps.Hand[handIdx].DefinitionId);
            if (ps.Mana.Available < def.Cost) { reason = RejectReason.NotEnoughMana; return false; }
            if (def.Type == CardType.MINION && ps.Board.Count >= MatchEngine.BoardCap)
            { reason = RejectReason.BoardFull; return false; }

            // Explicit-choice (ANY_*) selectors require a player-chosen target — mirrors the
            // ValidateHeroPower gate. Without it a targeted play silently fell through to the verb
            // defaults (enemy face for damage, own face for heal): mana spent, wrong effect, no
            // rejection the UI could surface. The interpreter now throws in that case, so this gate
            // is also what keeps a target-less client submit from faulting the match.
            if (p.Targets.Count == 0 && RequiresChosenTarget(def))
            { reason = RejectReason.IllegalTarget; return false; }

            // Target legality for any targets the intent supplies (M1 targeting model: EffectInterpreter
            // doc). Each must name a live character; an enemy STEALTH minion can't be singled out by a
            // play; and each must satisfy the card's explicit-choice selector shape (a minion-only
            // selector can't be aimed at a hero, a friendly-only selector at an enemy).
            foreach (var tr in p.Targets)
            {
                if (!Characters.TryFind(state, tr.Instance, out var ch)) { reason = RejectReason.IllegalTarget; return false; }
                if (ch.IsMinion && ch.Seat != seat && ch.Minion!.Keywords.Contains(Keyword.STEALTH))
                { reason = RejectReason.IllegalTarget; return false; }
                if (!TargetsSatisfySelectors(def, ch, seat))
                { reason = RejectReason.IllegalTarget; return false; }
            }
            return true;
        }

        // ── Explicit-choice (ANY_*) selector legality ────────────────────────────

        private static bool IsExplicitChoice(string? selector)
            => selector != null && selector.StartsWith("ANY_");

        /// <summary>True when any play-trigger effect declares an explicit-choice (ANY_*) selector.</summary>
        private static bool RequiresChosenTarget(Cards.CardDefinition def)
        {
            foreach (var eff in def.Effects)
                if ((eff.Trigger == Trigger.ON_PLAY || eff.Trigger == Trigger.ON_CAST) && IsExplicitChoice(eff.Selector))
                    return true;
            return false;
        }

        /// <summary>
        /// A chosen target must fit the shape of every play-trigger ANY_* selector on the card (the M1
        /// targeting model shares one chosen-target list across all of a card's effects).
        /// </summary>
        private static bool TargetsSatisfySelectors(Cards.CardDefinition def, CharacterRef ch, int casterSeat)
        {
            foreach (var eff in def.Effects)
            {
                if (eff.Trigger != Trigger.ON_PLAY && eff.Trigger != Trigger.ON_CAST) continue;
                if (!IsExplicitChoice(eff.Selector)) continue;
                switch (eff.Selector)
                {
                    case "ANY_FRIENDLY": if (ch.Seat != casterSeat) return false; break;
                    case "ANY_ENEMY": if (ch.Seat == casterSeat) return false; break;
                    case "ANY_MINION": if (!ch.IsMinion) return false; break;
                    case "ANY_FRIENDLY_MINION": if (!ch.IsMinion || ch.Seat != casterSeat) return false; break;
                        // ANY_CHARACTER (and any future ANY_*) accepts every live character.
                }
            }
            return true;
        }

        // ── Execute (Validate → Apply → Emit) ────────────────────────────────────

        /// <summary>
        /// Emit-time visibility sidecar for the events of the LAST accepted Execute call,
        /// index-parallel to that call's <see cref="CommandResult.Events"/> (a3-projector-design.md
        /// §6.1). Server-side only — feeds <c>GameStateProjector.ProjectEvent</c>; never serialized.
        /// Empty after a rejected command (rejects emit nothing).
        /// </summary>
        public IReadOnlyList<Projection.EventVisibility> LastEventVisibility { get; private set; }
            = System.Array.Empty<Projection.EventVisibility>();

        public CommandResult Execute(GameState state, PlayerIntent intent)
        {
            if (!CanExecute(state, intent, out var reason))
            {
                LastEventVisibility = System.Array.Empty<Projection.EventVisibility>();
                return CommandResult.Rejected(reason!.Value, state.StateVersion);
            }

            var buf = new EventBuffer(state);

            switch (intent)
            {
                case MulliganIntent m: ApplyMulligan(state, m, buf); break;
                case EndTurnIntent e: ApplyEndTurn(state, e, buf); break;
                case ConcedeIntent c: ApplyConcede(state, c, buf); break;
                case AutoConfirmIntent a: ApplyAutoConfirm(state, a, buf); break;
                case PlayCardIntent p: ApplyPlayCard(state, p, buf); break;
                case AttackIntent ai: ApplyAttack(state, ai, buf); break;
                case HeroPowerIntent hp: ApplyHeroPower(state, hp, buf); break;
                default: return CommandResult.Rejected(RejectReason.NotImplemented, state.StateVersion);
            }

            state.StateVersion++;
            LastEventVisibility = buf.Visibility;
            return CommandResult.Ok(buf.Events, state.StateVersion);
        }

        // ── Apply handlers ───────────────────────────────────────────────────────

        private void ApplyMulligan(GameState state, MulliganIntent m, EventBuffer buf)
        {
            int seat = state.SeatOf(m.Player);
            var ps = state.Players[seat];

            // Set aside the chosen cards (NOT yet returned to deck — so replacements can't redraw them).
            var returned = new List<CardInstance>();
            foreach (var id in m.ToReplace)
            {
                int idx = IndexOfInstance(ps.Hand, id);
                if (idx < 0) continue;
                returned.Add(ps.Hand[idx]);
                ps.Hand.RemoveAt(idx);
            }

            // Draw the same count of fresh cards off the top of the deck.
            for (int i = 0; i < returned.Count && ps.Deck.Count > 0; i++)
            {
                var card = ps.Deck[0];
                ps.Deck.RemoveAt(0);
                card.Zone = Zone.Hand;
                ps.Hand.Add(card);
                buf.Emit(VfxEvent.onDraw, source: card.Id);
            }

            // Shuffle the set-aside cards back into the deck (consumes RNG deterministically). An empty
            // mulligan keeps all cards and does NOT reshuffle, so it consumes no RNG (a "keep" is a no-op).
            if (returned.Count > 0)
            {
                foreach (var card in returned)
                {
                    card.Zone = Zone.Deck;
                    ps.Deck.Add(card);
                }
                DeckOps.Shuffle(state, ps);   // shuffles AND re-mints deck ids (OBL-7)
            }

            if (seat == 0) state.Mulligan0Done = true; else state.Mulligan1Done = true;

            if (state.Mulligan0Done && state.Mulligan1Done)
                StartTurnSequence(state, state.ActivePlayer, buf);   // first player's first turn
        }

        private void ApplyEndTurn(GameState state, EndTurnIntent e, EventBuffer buf)
        {
            var activeSeat = state.SeatOf(state.ActivePlayer);
            var interp = new EffectInterpreter(state, _db, buf);

            buf.Emit(VfxEvent.onTurnEnd, source: state.Players[activeSeat].Hero.Id);
            state.Phase = TurnPhase.EndOfTurn;

            // EF-012 seam: raise Trigger.ON_TURN_END here — the active player still owns the turn and the
            // phase is EndOfTurn. Nothing is raised yet, so the Drain below is a no-op (empty queue, no
            // corpses at a settled turn boundary) and this whole block is hash-neutral until EF-012 lands.

            // The end-of-turn cascade must FULLY settle — deaths, deathrattles, win-check — BEFORE the seat
            // flip and the incoming StartTurnSequence, whose thaw loop resets SummonedThisTurn /
            // AttacksUsedThisTurn. Draining after that reset would let it stomp an in-flight cascade. CheckWin
            // is called explicitly because Drain only win-checks while its queue is non-empty (the same
            // reason ApplyAttack pairs Drain with an explicit CheckWin).
            interp.Drain();
            RulesEngine.CheckWin(state, buf);
            if (state.Outcome != MatchOutcome.InProgress) return;

            var nextSeat = activeSeat == 0 ? 1 : 0;
            state.ActivePlayer = state.Players[nextSeat].Id;
            StartTurnSequence(state, state.ActivePlayer, buf);
        }

        private void ApplyConcede(GameState state, ConcedeIntent c, EventBuffer buf)
        {
            int seat = state.SeatOf(c.Player);
            state.Outcome = seat == 0 ? MatchOutcome.Player1Win : MatchOutcome.Player0Win;
            state.Phase = TurnPhase.GameOver;
            buf.Emit(VfxEvent.onGameOver);
            int winnerSeat = seat == 0 ? 1 : 0;
            buf.Emit(VfxEvent.onGameWin, source: state.Players[winnerSeat].Hero.Id);
        }

        private void ApplyAutoConfirm(GameState state, AutoConfirmIntent a, EventBuffer buf)
        {
            // Timer expiry is an ordinary command (game-state-and-actions.md §6.2): keep all cards in
            // mulligan, or end the turn in Main.
            if (state.Phase == TurnPhase.Mulligan)
                ApplyMulligan(state, new MulliganIntent(a.Player, System.Array.Empty<InstanceId>()), buf);
            else
                ApplyEndTurn(state, new EndTurnIntent(a.Player), buf);
        }

        private void ApplyPlayCard(GameState state, PlayCardIntent p, EventBuffer buf)
        {
            int seat = state.SeatOf(p.Player);
            var ps = state.Players[seat];
            int handIdx = IndexOfInstance(ps.Hand, p.Card);
            var card = ps.Hand[handIdx];
            var def = _db.Get(card.DefinitionId);

            // Pay mana, leave hand.
            ps.Mana.Available -= def.Cost;
            ps.Mana.Spent += def.Cost;
            ps.Hand.RemoveAt(handIdx);

            var interp = new EffectInterpreter(state, _db, buf);
            var targets = ResolveTargets(state, p.Targets);

            switch (def.Type)
            {
                case CardType.MINION:
                    RulesEngine.EnterBoard(state, card, def, seat, p.BoardSlot);
                    buf.Emit(VfxEvent.onPlay, source: card.Id);
                    interp.RaiseSelf(card, Trigger.ON_PLAY, targets);                 // Battlecry
                    interp.RaiseObservers(Trigger.ON_FRIENDLY_SUMMON, excluded: card);
                    break;

                case CardType.SPELL:
                    card.Zone = Zone.Graveyard;
                    ps.Graveyard.Add(card);
                    buf.Emit(VfxEvent.onPlay, source: card.Id);
                    interp.RaiseSelf(card, Trigger.ON_CAST, targets);                 // the spell's one-shot effect
                    break;

                case CardType.WEAPON:
                    card.Zone = Zone.Graveyard;
                    ps.Graveyard.Add(card);
                    RulesEngine.BreakWeapon(state, seat, buf); // destroy old weapon before equipping new
                    ps.Weapon = new WeaponInstance
                    {
                        Id = state.NextInstanceId(),
                        DefinitionId = def.Id,
                        Attack = def.Attack ?? 0,
                        Durability = def.Durability ?? 1,
                    };
                    buf.Emit(VfxEvent.onEquip, source: ps.Hero.Id);
                    interp.RaiseSelf(card, Trigger.ON_PLAY, targets);                 // weapon Battlecry
                    break;
            }

            interp.Drain();   // resolve battlecry/cast, settle deaths + win, cascade deathrattles
        }

        private bool ValidateHeroPower(GameState state, HeroPowerIntent hp, out RejectReason? reason)
        {
            reason = null;
            if (!KnownPlayer(state, hp.Player, out reason)) return false;
            if (state.Phase != TurnPhase.Main) { reason = RejectReason.WrongPhase; return false; }
            if (state.ActivePlayer != hp.Player) { reason = RejectReason.NotYourTurn; return false; }

            int seat = state.SeatOf(hp.Player);
            var ps = state.Players[seat];

            if (ps.HeroPowerUsedThisTurn) { reason = RejectReason.HeroPowerAlreadyUsed; return false; }
            if (ps.Mana.Available < 2) { reason = RejectReason.NotEnoughMana; return false; }

            // Explicit-choice selectors (ANY_*) require a player-chosen target. Without this gate a
            // targeted hero power silently falls through to the verb defaults (enemy face for damage,
            // own face for heal) — mana spent, wrong effect, no rejection the UI could surface.
            var hpDef = _db.Get(ps.Hero.HeroPowerId);
            if (hp.Targets.Count == 0 && RequiresChosenTarget(hpDef))
            { reason = RejectReason.IllegalTarget; return false; }

            // Target legality (spells can't single out a stealthed enemy minion; TAUNT not enforced for
            // non-attacks; chosen targets must fit the power's explicit-choice selector shape).
            foreach (var tr in hp.Targets)
            {
                if (!Characters.TryFind(state, tr.Instance, out var ch)) { reason = RejectReason.IllegalTarget; return false; }
                if (ch.IsMinion && ch.Seat != seat && ch.Minion!.Keywords.Contains(Keyword.STEALTH))
                { reason = RejectReason.IllegalTarget; return false; }
                if (!TargetsSatisfySelectors(hpDef, ch, seat))
                { reason = RejectReason.IllegalTarget; return false; }
            }
            return true;
        }

        private void ApplyHeroPower(GameState state, HeroPowerIntent hp, EventBuffer buf)
        {
            int seat = state.SeatOf(hp.Player);
            var ps = state.Players[seat];

            ps.Mana.Available -= 2;
            ps.Mana.Spent += 2;
            ps.HeroPowerUsedThisTurn = true;

            // Ephemeral source for the interpreter: gives it Controller (seat) and DefinitionId (effects).
            // Never placed in a zone list; just carries identity for the effect queue.
            var src = new CardInstance(state.NextInstanceId(), ps.Hero.HeroPowerId, ps.Id)
            {
                Zone = Zone.Graveyard
            };
            // The ephemeral source never enters a zone, so emit-time zone lookup can't classify
            // it — mark it public explicitly: the hero power's act is public (design §6.1).
            buf.MarkPublic(src.Id);

            buf.Emit(VfxEvent.onHeroPower, source: ps.Hero.Id);

            var interp = new EffectInterpreter(state, _db, buf);
            var targets = ResolveTargets(state, hp.Targets);
            interp.RaiseSelf(src, Trigger.ON_CAST, targets);
            interp.Drain();
        }

        private bool ValidateAttack(GameState state, AttackIntent a, out RejectReason? reason)
        {
            reason = null;
            if (!KnownPlayer(state, a.Player, out reason)) return false;
            if (state.Phase != TurnPhase.Main) { reason = RejectReason.WrongPhase; return false; }
            if (state.ActivePlayer != a.Player) { reason = RejectReason.NotYourTurn; return false; }

            int seat = state.SeatOf(a.Player);
            int foe = seat == 0 ? 1 : 0;
            var ps = state.Players[seat];

            // Attacker must exist and be ours
            if (!Characters.TryFind(state, a.Attacker, out var attacker))
            { reason = RejectReason.IllegalTarget; return false; }
            if (attacker.Seat != seat)
            { reason = RejectReason.NotYourTurn; return false; }

            if (attacker.IsHero)
            {
                if (ps.Weapon == null) { reason = RejectReason.WeaponMissing; return false; }
                if (ps.Hero.IsFrozen) { reason = RejectReason.AttackerFrozen; return false; }
                if (ps.HeroAttacksUsedThisTurn >= 1) { reason = RejectReason.NoAttacksLeft; return false; }
            }
            else
            {
                var m = attacker.Minion!;
                if (m.IsFrozen) { reason = RejectReason.AttackerFrozen; return false; }
                if (m.CurrentAttack <= 0) { reason = RejectReason.IllegalTarget; return false; }
                bool hasCharge = m.Keywords.Contains(Keyword.CHARGE);
                bool hasRush = m.Keywords.Contains(Keyword.RUSH);
                if (m.SummonedThisTurn && !hasCharge && !hasRush)
                { reason = RejectReason.HasSummoningSickness; return false; }
                int maxAttacks = m.Keywords.Contains(Keyword.WINDFURY) ? 2 : 1;
                if (m.AttacksUsedThisTurn >= maxAttacks) { reason = RejectReason.NoAttacksLeft; return false; }
            }

            // Defender must exist and belong to the opponent
            if (!Characters.TryFind(state, a.Defender.Instance, out var defender))
            { reason = RejectReason.IllegalTarget; return false; }
            if (defender.Seat == seat)
            { reason = RejectReason.IllegalTarget; return false; }

            // RUSH can only target minions during summoning turn
            if (attacker.IsMinion && attacker.Minion!.SummonedThisTurn
                && attacker.Minion.Keywords.Contains(Keyword.RUSH) && defender.IsHero)
            { reason = RejectReason.IllegalTarget; return false; }

            // STEALTH blocks targeting (even a stealthed taunter hides its taunt)
            if (defender.IsMinion && defender.Minion!.Keywords.Contains(Keyword.STEALTH))
            { reason = RejectReason.IllegalTarget; return false; }

            // TAUNT enforcement: must attack a non-stealthed taunter if one exists
            var foeBoard = state.Players[foe].Board;
            bool anyTaunt = false;
            for (int i = 0; i < foeBoard.Count; i++)
            {
                var fm = foeBoard[i];
                if (fm.Keywords.Contains(Keyword.TAUNT) && !fm.Keywords.Contains(Keyword.STEALTH))
                { anyTaunt = true; break; }
            }
            if (anyTaunt && (!defender.IsMinion || !defender.Minion!.Keywords.Contains(Keyword.TAUNT)))
            { reason = RejectReason.IllegalTarget; return false; }

            return true;
        }

        private void ApplyAttack(GameState state, AttackIntent a, EventBuffer buf)
        {
            int seat = state.SeatOf(a.Player);
            int foe = seat == 0 ? 1 : 0;
            var ps = state.Players[seat];

            Characters.TryFind(state, a.Attacker, out var attacker);
            Characters.TryFind(state, a.Defender.Instance, out var defender);

            var interp = new EffectInterpreter(state, _db, buf);

            int attackerAtk = attacker.IsMinion
                ? attacker.Minion!.CurrentAttack
                : ps.Weapon!.Attack;

            var attackerKws = attacker.IsMinion
                ? attacker.Minion!.Keywords
                : new HashSet<Keyword>();

            bool overwhelm = attacker.IsMinion && attacker.Minion!.Keywords.Contains(Keyword.OVERWHELM);
            bool vanguard = attacker.IsMinion && attacker.Minion!.Keywords.Contains(Keyword.VANGUARD);

            // Stealth breaks the moment the attacker declares
            if (attacker.IsMinion) attacker.Minion!.Keywords.Remove(Keyword.STEALTH);

            // Increment attack budget (before damage — canonical ordering)
            if (attacker.IsMinion) attacker.Minion!.AttacksUsedThisTurn++;
            else ps.HeroAttacksUsedThisTurn++;

            buf.Emit(VfxEvent.onAttack, source: attacker.Id, target: defender.Id);

            // Weapon "whenever this attacks" effects (e.g. Grasping Thornvine's heal). WeaponInstance
            // carries no CardInstance of its own, so wrap it in a throwaway one purely for effect lookup.
            if (attacker.IsHero && ps.Weapon != null)
            {
                var weaponCard = new CardInstance(ps.Weapon.Id, ps.Weapon.DefinitionId, a.Player);
                interp.RaiseSelf(weaponCard, Trigger.ON_ATTACK, System.Array.Empty<CharacterRef>());
            }

            // Phase 1: attacker hits defender
            DamagePipeline.ApplyDamage(interp, defender, attackerAtk, attacker.Id, attackerKws);

            // OVERWHELM: carry excess overkill damage to enemy hero (minion targets only).
            // Riders (LIFESTEAL/POISONOUS) attach to the primary hit only — G0 ruling R-2(c),
            // Sam 2026-07-14: overflow carries damage, not keywords (matches the Aegis
            // no-riders precedent; keeps Lifedrain heal ≤ attack value).
            if (overwhelm && defender.IsMinion && defender.Minion!.CurrentHealth < 0)
            {
                var enemyHero = CharacterRef.OfHero(foe, state.Players[foe].Hero);
                DamagePipeline.ApplyDamage(interp, enemyHero, -defender.Minion.CurrentHealth, attacker.Id, null);
            }

            // Phase 2: retaliation — minion defenders only; heroes never retaliate
            if (defender.IsMinion)
            {
                // VANGUARD: if defender died from the hit, no retaliation
                bool retaliates = !vanguard || defender.Minion!.CurrentHealth > 0;
                if (retaliates)
                    DamagePipeline.ApplyDamage(interp, attacker, defender.Minion!.CurrentAttack,
                        defender.Id, defender.Minion.Keywords);
            }

            // Weapon durability on hero attack
            if (attacker.IsHero && ps.Weapon != null)
            {
                ps.Weapon.Durability--;
                if (ps.Weapon.Durability <= 0)
                    RulesEngine.BreakWeapon(state, seat, buf);
            }

            // Settle deaths, deathrattles, win-check.
            // CheckWin is also called here because Drain only invokes it inside the effect-queue loop —
            // direct combat damage to a hero leaves the queue empty, so the win check would be missed.
            interp.Drain();
            RulesEngine.CheckWin(state, buf);
        }

        private static IReadOnlyList<CharacterRef> ResolveTargets(GameState state, IReadOnlyList<TargetRef> refs)
        {
            if (refs == null || refs.Count == 0) return System.Array.Empty<CharacterRef>();
            var list = new List<CharacterRef>(refs.Count);
            foreach (var r in refs)
                if (Characters.TryFind(state, r.Instance, out var ch)) list.Add(ch);
            return list;
        }

        // ── Turn sequence (GDD §1b / ARCHITECTURE §5) ─────────────────────────────

        private void StartTurnSequence(GameState state, PlayerId player, EventBuffer buf)
        {
            int seat = state.SeatOf(player);
            var ps = state.Players[seat];

            state.Phase = TurnPhase.StartOfTurn;
            state.TurnNumber++;
            state.ActionResolutionsThisTurn = 0;

            // Mana: +1 max (cap 10) → apply overload locks → refill.
            ps.Mana.Max = System.Math.Min(ps.Mana.Max + 1, MatchEngine.MaxMana);
            int locked = ps.NextTurnOverloadLock;
            ps.NextTurnOverloadLock = 0;
            ps.Mana.Locked = locked;
            ps.Mana.Spent = 0;
            ps.Mana.Temporary = 0;
            ps.Mana.Available = System.Math.Max(0, ps.Mana.Max - locked);

            // Thaw + reset summoning sickness / attack budget / hero power.
            ps.Hero.IsFrozen = false;
            foreach (var m in ps.Board)
            {
                m.IsFrozen = false;
                m.SummonedThisTurn = false;
                m.AttacksUsedThisTurn = 0;
            }
            ps.HeroPowerUsedThisTurn = false;
            ps.HeroAttacksUsedThisTurn = 0;

            var interp = new EffectInterpreter(state, _db, buf);
            buf.Emit(VfxEvent.onTurnStart, source: ps.Hero.Id);

            // EF-011 seam: raise Trigger.ON_TURN_START here — AFTER the thaw/mana reset above and BEFORE the
            // draw, so a start-of-turn cascade settles before DrawOne (or a corpse would draw fatigue).
            // Nothing is raised yet, so the Drain is a no-op and this block is hash-neutral until EF-011 lands.
            interp.Drain();
            RulesEngine.CheckWin(state, buf);
            if (state.Outcome != MatchOutcome.InProgress) return;

            RulesEngine.DrawOne(state, seat, buf);
            RulesEngine.CheckWin(state, buf);

            if (state.Outcome == MatchOutcome.InProgress)
                state.Phase = TurnPhase.Main;
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        private static bool ContainsInstance(List<CardInstance> zone, InstanceId id) => IndexOfInstance(zone, id) >= 0;

        private static int IndexOfInstance(List<CardInstance> zone, InstanceId id)
        {
            for (int i = 0; i < zone.Count; i++)
                if (zone[i].Id == id) return i;
            return -1;
        }
    }
}
