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
                case AttackIntent _:
                case HeroPowerIntent _:
                case DiscoverChoiceIntent _:
                    reason = RejectReason.NotImplemented;   // Attack/HeroPower = steps 2/3; Discover = later
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

            // Target legality for any targets the intent supplies (M1 targeting model: EffectInterpreter
            // doc). Each must name a live character; an enemy STEALTH minion can't be singled out by a play.
            foreach (var tr in p.Targets)
            {
                if (!Characters.TryFind(state, tr.Instance, out var ch)) { reason = RejectReason.IllegalTarget; return false; }
                if (ch.IsMinion && ch.Seat != seat && ch.Minion!.Keywords.Contains(Keyword.STEALTH))
                { reason = RejectReason.IllegalTarget; return false; }
            }
            return true;
        }

        // ── Execute (Validate → Apply → Emit) ────────────────────────────────────

        public CommandResult Execute(GameState state, PlayerIntent intent)
        {
            if (!CanExecute(state, intent, out var reason))
                return CommandResult.Rejected(reason!.Value, state.StateVersion);

            var buf = new EventBuffer(state);

            switch (intent)
            {
                case MulliganIntent m: ApplyMulligan(state, m, buf); break;
                case EndTurnIntent e: ApplyEndTurn(state, e, buf); break;
                case ConcedeIntent c: ApplyConcede(state, c, buf); break;
                case AutoConfirmIntent a: ApplyAutoConfirm(state, a, buf); break;
                case PlayCardIntent p: ApplyPlayCard(state, p, buf); break;
                default: return CommandResult.Rejected(RejectReason.NotImplemented, state.StateVersion);
            }

            state.StateVersion++;
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
                state.Rng.Shuffle(ps.Deck);
            }

            if (seat == 0) state.Mulligan0Done = true; else state.Mulligan1Done = true;

            if (state.Mulligan0Done && state.Mulligan1Done)
                StartTurnSequence(state, state.ActivePlayer, buf);   // first player's first turn
        }

        private void ApplyEndTurn(GameState state, EndTurnIntent e, EventBuffer buf)
        {
            var activeSeat = state.SeatOf(state.ActivePlayer);
            buf.Emit(VfxEvent.onTurnEnd, source: state.Players[activeSeat].Hero.Id);

            state.Phase = TurnPhase.EndOfTurn;
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
            foreach (var m in ps.Board)
            {
                m.IsFrozen = false;
                m.SummonedThisTurn = false;
                m.AttacksUsedThisTurn = 0;
            }
            ps.HeroPowerUsedThisTurn = false;

            buf.Emit(VfxEvent.onTurnStart, source: ps.Hero.Id);

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
