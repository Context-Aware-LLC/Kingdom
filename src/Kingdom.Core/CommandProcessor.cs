using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Core
{
    /// <summary>
    /// The single mutation door (dev-docs/architecture/system-overview.md:26). On reject
    /// there is ZERO mutation and ZERO events. Same <c>(GameState, PlayerIntent)</c> ⇒ same mutation
    /// and same ordered event stream on any machine — the property the golden-replay test proves (R1).
    ///
    /// Owns the complete authoritative player-intent allowlist, including interactive Unearth
    /// continuation. Coin flip and the opening deal live in <see cref="MatchEngine"/>.
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

            // An interactive choice is the only legal gameplay continuation until it is resolved.
            // AutoConfirm remains available to the authoritative timer path.
            if (state.PendingChoice != null &&
                intent is not UnearthChoiceIntent && intent is not AutoConfirmIntent)
            {
                reason = RejectReason.ChoicePending;
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
                case UnearthChoiceIntent u:
                    return ValidateUnearthChoice(state, u, out reason);
                default:
                    reason = RejectReason.UnknownIntent;    // off-allowlist (dev-docs/contracts/gameplay-and-session.md:18)
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

        private bool ValidateAutoConfirm(GameState state, AutoConfirmIntent a, out RejectReason? reason)
        {
            if (!KnownPlayer(state, a.Player, out reason)) return false;
            if (state.PendingChoice != null)
            {
                PendingChoice pending = state.PendingChoice;
                if (pending.Kind != PendingChoiceKind.Unearth)
                { reason = RejectReason.NoPendingChoice; return false; }
                if (pending.Chooser != a.Player)
                { reason = RejectReason.NotChoiceOwner; return false; }
                if (pending.Options.Count == 0 || !_db.TryGet(pending.Options[0], out _))
                { reason = RejectReason.InvalidChoice; return false; }
                return true;
            }
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

        private bool ValidateUnearthChoice(GameState state, UnearthChoiceIntent choice,
            out RejectReason? reason)
        {
            if (!KnownPlayer(state, choice.Player, out reason)) return false;
            PendingChoice? pending = state.PendingChoice;
            if (pending == null || pending.Kind != PendingChoiceKind.Unearth)
            { reason = RejectReason.NoPendingChoice; return false; }
            if (pending.Chooser != choice.Player)
            { reason = RejectReason.NotChoiceOwner; return false; }
            if (pending.ChoiceSequence != choice.ChoiceSequence)
            { reason = RejectReason.StaleChoice; return false; }

            // Deliberately NOT gated on hand space: the offer already fails closed on a full hand
            // (UnearthHandler.Execute), but a LATER trigger in the same event cascade can still fill
            // the last slot before the player answers — see the fizzle in ApplyUnearthChoice below.
            // Hand space can never make an otherwise-valid choice illegal; that would leave the only
            // two intents this pending choice accepts (this one and AutoConfirm) both rejecting and
            // the match deadlocked until the intent cap trips it (measured 2026-09-05, EMBERWARD vs
            // CLOCKWRIGHT matrix soak).
            bool offered = false;
            for (int i = 0; i < pending.Options.Count; i++)
            {
                if (pending.Options[i] == choice.ChosenCardId)
                {
                    offered = true;
                    break;
                }
            }
            if (!offered || !_db.TryGet(choice.ChosenCardId, out _))
            { reason = RejectReason.InvalidChoice; return false; }

            reason = null;
            return true;
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

            Trigger playTrigger = def.Type == CardType.SPELL ? Trigger.ON_CAST : Trigger.ON_PLAY;
            if (!Targeting.ValidatePlayerChoices(state, _db, p.Player, def, playTrigger, p.Targets,
                    ps.Hand[handIdx].Id))
            { reason = RejectReason.IllegalTarget; return false; }
            return true;
        }

        // ── Execute (Validate → Apply → Emit) ────────────────────────────────────

        /// <summary>
        /// Emit-time visibility sidecar for the events of the LAST accepted Execute call,
        /// index-parallel to that call's <see cref="CommandResult.Events"/> (dev-docs/architecture/determinism-and-projection.md:24). Server-side only — feeds <c>GameStateProjector.ProjectEvent</c>; never serialized.
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
                case UnearthChoiceIntent u: ApplyUnearthChoice(state, u, buf); break;
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
                buf.Emit(GameEventKind.CardDrawn, source: card.Id);
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

            buf.Emit(GameEventKind.TurnEnded, source: state.Players[activeSeat].Hero.Id);
            state.Phase = TurnPhase.EndOfTurn;

            // EF-012: ON_TURN_END fires here — the active player still owns the turn and the phase is
            // EndOfTurn, so a turn-end effect sees the board the turn actually ended on.
            //
            // SCOPE IS BOTH SIDES, not just the active player's permanents, and since #272 that is a
            // POSITIVE ENTRY in OrderedTriggerSources.IsControllerScoped rather than the sweep's only
            // behaviour — ON_FRIENDLY_SUMMON now takes one side and this still takes two. GDD §1b step 3
            // says "fire all ON_TURN_END effects", and nothing about the name or the card grammar
            // ("Turn End:") claims a side.
            // `excluded: null` because nothing "causes" a turn to end; unlike a summon there is no
            // source card to leave out. Order is RaiseObservers' canonical active-side-first, board
            // left→right sweep, which is what makes this replay-stable.
            interp.RaiseObservers(Trigger.ON_TURN_END, excluded: null);

            // The end-of-turn cascade must FULLY settle — deaths, tolls, win-check — BEFORE the seat
            // flip and the incoming StartTurnSequence, which resets SummonedThisTurn /
            // AttacksUsedThisTurn. Draining after that reset would let it stomp an in-flight cascade.
            // Drain settles the game-end checkpoint itself, on every exit path (scheduling §3.2), so
            // there is no longer a follow-up call for this site to remember.
            interp.Drain();
            if (state.Outcome != MatchOutcome.InProgress) return;

            // Expire turn-scoped effects on the ending player's own characters (NOR-355 timing).
            // Frost costs a FULL turn, never a partial one: a freeze applied on Player A's turn holds
            // through all of Player B's turn and clears when B ends here. The NOR-367 case — a unit
            // frozen DURING its own turn by the warden's ON_DEATH — falls out of the scope arithmetic
            // rather than needing a second flag: that instance was never aged by a turn START, so it
            // still owes a turn here and survives to serve it.
            var activePs = state.Players[activeSeat];
            EffectList.ExpireControllerTurnEnd(activePs.Hero.Effects);
            foreach (var m in activePs.Board)
                EffectList.ExpireControllerTurnEnd(m.Effects);

            // A current-turn promise/allowance expires on EITHER player's turn boundary,
            // after all end effects have resolved. Frost retains its controller-turn clock.
            foreach (PlayerState player in state.Players)
            {
                player.HeroAttackThisTurn = 0;
                EffectList.ExpireCurrentTurn(player.Hero.Effects);
                foreach (CardInstance minion in player.Board)
                    EffectList.ExpireCurrentTurn(minion.Effects);
                if (player.Weapon != null) EffectList.ExpireCurrentTurn(player.Weapon.Effects);
            }

            // A spell aura ages on the SAME clock as the turn-scoped effects just above, and for the
            // same reason: it costs a FULL turn of its controller's, never a partial one. Filtering
            // to the ending player is what makes `uses: 3` mean three of YOUR turns — both seats are
            // walked at every turn boundary, so an unfiltered tick would spend a duration in a turn
            // and a half and the number on the card would be a lie.
            //
            // This is the ONLY thing that ages an aura. `uses` is decremented from the resolution
            // path (EffectInterpreter.ConsumeOngoingEffect) for every other ongoing rule, and an
            // aura never resolves — it is folded, continuously, and Trigger.AURA has no raise site —
            // so without this site a FINITE aura would be a PERMANENT one wearing a number.
            if (ExpireControllerAuras(state, activePs.Id, buf))
                AuraEngine.Recompute(state, _db);

            var nextSeat = activeSeat == 0 ? 1 : 0;
            state.ActivePlayer = state.Players[nextSeat].Id;
            StartTurnSequence(state, state.ActivePlayer, buf);
        }

        /// <summary>
        /// Ages every AURA ongoing effect controlled by <paramref name="controller"/> by one turn,
        /// dropping the ones that reach zero. Returns whether the set changed, so the caller only
        /// pays for a fold when one actually expired.
        /// </summary>
        /// <remarks>
        /// <b>Only AURA effects are aged here.</b> Every other ongoing rule ticks when it resolves,
        /// so ticking one here as well would spend two uses for one firing.
        ///
        /// <para>The removal is the whole of expiry. <c>DerivedStats.RemoveAuras</c> is
        /// provenance-blind — it strips every aura enchantment on every pass and the fold re-lays
        /// them from live sources — so an aura that is gone from this list simply stops being
        /// re-applied. There is no un-apply path to write, and deliberately none to maintain.</para>
        /// </remarks>
        private bool ExpireControllerAuras(GameState state, PlayerId controller, EventBuffer buf)
        {
            bool changed = false;
            for (int i = state.OngoingEffects.Count - 1; i >= 0; i--)
            {
                OngoingEffectInstance effect = state.OngoingEffects[i];
                if (!effect.Controller.Equals(controller)) continue;
                if (!IsAuraEffect(effect)) continue;

                if (!RuleLifetimeCapabilities.ConsumeOngoingEffect(effect.Lifetime,
                        effect.RemainingUses, out int? remainingUses))
                {
                    effect.RemainingUses = remainingUses;
                    continue;
                }
                effect.RemainingUses = remainingUses;
                buf.Emit(GameEventKind.OngoingEffectExpired, source: effect.Id);
                state.OngoingEffects.RemoveAt(i);
                changed = true;
            }
            return changed;
        }

        /// <summary>Whether a carrierless effect is the aura kind, read off its authored rule.</summary>
        private bool IsAuraEffect(OngoingEffectInstance effect)
        {
            CardDefinition definition = _db.Get(effect.SourceDefinitionId);
            foreach (RuleDefinition rule in definition.Rules)
                if (rule.Id.Value == effect.RuleId)
                    return RuleSemantics.TriggerOf(rule) == Trigger.AURA;
            return false;
        }

        private void ApplyConcede(GameState state, ConcedeIntent c, EventBuffer buf)
        {
            int seat = state.SeatOf(c.Player);
            state.Outcome = seat == 0 ? MatchOutcome.Player1Win : MatchOutcome.Player0Win;
            state.Phase = TurnPhase.GameOver;
            buf.Emit(GameEventKind.MatchEnded);
            int winnerSeat = seat == 0 ? 1 : 0;
            buf.Emit(GameEventKind.MatchWon, source: state.Players[winnerSeat].Hero.Id);
        }

        private void ApplyAutoConfirm(GameState state, AutoConfirmIntent a, EventBuffer buf)
        {
            // Timer expiry is an ordinary command (dev-docs/contracts/gameplay-and-session.md:38):
            // choose the first offered option, keep all cards in mulligan, or end the turn in Main.
            if (state.PendingChoice != null)
            {
                PendingChoice pending = state.PendingChoice;
                ApplyUnearthChoice(state,
                    new UnearthChoiceIntent(a.Player, pending.Options[0], pending.ChoiceSequence), buf);
            }
            else if (state.Phase == TurnPhase.Mulligan)
                ApplyMulligan(state, new MulliganIntent(a.Player, System.Array.Empty<InstanceId>()), buf);
            else
                ApplyEndTurn(state, new EndTurnIntent(a.Player), buf);
        }

        private void ApplyUnearthChoice(GameState state, UnearthChoiceIntent choice, EventBuffer buf)
        {
            PlayerState chooser = state.PlayerOf(choice.Player);
            PendingChoice pending = state.PendingChoice!;
            state.PendingChoice = null;

            // Fizzle, not reject: the offer already failed closed on a full hand (UnearthHandler),
            // but a later trigger in the same cascade can still fill the last slot before the player
            // answers. Nothing else can clear this pending choice, so silently minting nothing is the
            // only option that doesn't deadlock the match.
            if (chooser.Hand.Count >= MatchEngine.HandCap) return;

            CardDefinition definition = _db.Get(choice.ChosenCardId);
            var created = new CardInstance(state.NextInstanceId(), definition.Id, chooser.Id)
            {
                Zone = Zone.Hand,
            };
            chooser.Hand.Add(created);

            // The newly minted instance id is the immutable event identity. Emit after the card
            // enters hand so EventBuffer captures owner-only visibility at the point of truth.
            buf.Emit(GameEventKind.CardCreatedInHand, source: created.Id, flags: EventFlags.IsToken);
            if (pending.RemainingOffers > 1)
                UnearthHandler.Offer(state, _db, buf, pending.Chooser, pending.Source,
                    pending.RemainingOffers - 1);
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
            switch (def.Type)
            {
                case CardType.MINION:
                    bool repeatPlay = EffectList.Has(ps.Hero.Effects, EffectKind.RepeatNextMinionPlay);
                    // Consume before raising any effects: a doubled Herald arms the NEXT minion,
                    // and a vanilla minion still spends the promise. Summons never come here.
                    EffectList.Remove(ps.Hero.Effects, EffectKind.RepeatNextMinionPlay);
                    RulesEngine.EnterBoard(state, card, def, seat, p.BoardSlot, _db);
                    buf.Emit(GameEventKind.CardPlayed, source: card.Id);
                    interp.RaiseSelf(card, Trigger.ON_PLAY, p.Targets);                 // Herald
                    if (repeatPlay) interp.RaiseSelf(card, Trigger.ON_PLAY, p.Targets);
                    // SCOPE IS THE SUMMONER'S SIDE ONLY (#272, Sam, 2026-09-04) — unlike the two
                    // sweep triggers below, and by decision rather than by inheritance. The
                    // controller is named explicitly because the sweep cannot infer it: this arm
                    // could be reached off-turn and ActivePlayer would name the wrong board.
                    interp.RaiseObservers(Trigger.ON_FRIENDLY_SUMMON, excluded: card,
                        triggerSource: card.Id, observerController: card.Controller);
                    break;

                case CardType.SPELL:
                    RulesEngine.EnterGraveyard(state, ps, card);
                    buf.Emit(GameEventKind.CardPlayed, source: card.Id);
                    interp.RaiseSelf(card, Trigger.ON_CAST, p.Targets);                 // the spell's one-shot effect
                    // EF-016: ON_SPELL_CAST — the observer half of a cast, enqueued AFTER the spell's own
                    // ON_CAST so the spell resolves first and reactions see the board it left behind. Same
                    // RaiseSelf-then-RaiseObservers shape as the MINION arm above (ON_PLAY → ON_FRIENDLY_SUMMON).
                    //
                    // THE TRAP THIS PLACEMENT EXISTS TO AVOID: hero powers reach the interpreter through an
                    // carrierless HERO_POWER source context and raise Trigger.ON_CAST too (ApplyHeroPower below).
                    // Hooking generic ON_CAST would therefore fire spell-cast reactions on every hero power.
                    // Gating the raise to this branch — the only place a CardType.SPELL is cast — is what
                    // keeps "whenever a spell is cast" meaning spells. Any future cast path must decide
                    // deliberately whether it belongs here; do NOT move this into EffectInterpreter.RaiseSelf.
                    //
                    // SCOPE IS BOTH SIDES, matching ON_TURN_END (EF-012): the name carries no FRIENDLY_
                    // qualifier the way ON_FRIENDLY_SUMMON does, and the card grammar says "whenever a
                    // spell is cast". Since #272 that is an explicit `false` in
                    // OrderedTriggerSources.IsControllerScoped, not the sweep's only behaviour — the
                    // sibling trigger took the filter and this one deliberately did not.
                    // `excluded: null` because the spell is in the graveyard by now and OrderedTriggerSources
                    // only walks boards — it cannot observe its own cast whatever we pass.
                    interp.RaiseObservers(Trigger.ON_SPELL_CAST, excluded: null, triggerSource: card.Id);
                    // An ongoing observer becomes live only after the spell and the cast-time
                    // subscriber snapshot have fully resolved. In particular it cannot observe the
                    // same cast that created it, and the resolved card remains in the graveyard.
                    bool hasOngoingRules = false;
                    for (int i = 0; i < def.Rules.Count; i++)
                        if (RuleLifetimeCapabilities.TryCreateOngoingEffect(def.Type,
                                RuleSemantics.TriggerOf(def.Rules[i]), def.Rules[i].Lifetime,
                                out _, out _))
                        {
                            hasOngoingRules = true;
                            break;
                        }
                    if (hasOngoingRules)
                    {
                        interp.Drain();
                        if (state.Outcome == MatchOutcome.InProgress)
                            interp.CreateOngoingEffects(def, card.Controller);
                    }
                    break;

                case CardType.WEAPON:
                    RulesEngine.BreakWeapon(interp, seat); // destroy old weapon (rattle enqueued) before equipping new
                    RulesEngine.EnterWeapon(state, card, def, seat);
                    buf.Emit(GameEventKind.WeaponEquipped, source: ps.Hero.Id);
                    interp.RaiseSelf(card, Trigger.ON_PLAY, p.Targets);                 // weapon Herald
                    break;
            }

            // Resolve herald/cast, settle deaths, cascade tolls — AND the win check, which Drain now
            // runs on every exit path rather than only inside its loop body (scheduling §3.2).
            //
            // DISCHARGES the LATENT hazard that stood here. The gap needed a hero at <=0 health
            // BEFORE this drain, which nothing on this path produced — but any pre-drain hero-health
            // mutation would have armed it silently: a self-damage play cost, fatigue charged at cast
            // time, or ruling #3's damage-taken representation change. Adding one here is now safe,
            // because settling the match stopped being this call site's job to remember.
            interp.Drain();
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

            var hpDef = _db.Get(ps.Hero.HeroPowerId);
            if (!Targeting.ValidatePlayerChoices(state, _db, hp.Player, hpDef, Trigger.ON_CAST, hp.Targets))
            { reason = RejectReason.IllegalTarget; return false; }
            return true;
        }

        private void ApplyHeroPower(GameState state, HeroPowerIntent hp, EventBuffer buf)
        {
            int seat = state.SeatOf(hp.Player);
            var ps = state.Players[seat];

            ps.Mana.Available -= 2;
            ps.Mana.Spent += 2;
            ps.HeroPowerUsedThisTurn = true;

            buf.Emit(GameEventKind.HeroPowerUsed, source: ps.Hero.Id);

            var interp = new EffectInterpreter(state, _db, buf);
            // This ON_CAST is the SECOND raise site for the trigger — a hero power, not a spell. EF-016
            // deliberately does NOT follow it with RaiseObservers(ON_SPELL_CAST): that raise lives only in
            // ApplyPlayCard's CardType.SPELL arm, so "whenever a spell is cast" excludes hero powers.
            // If a design ruling ever makes hero powers count as spell casts, this is the line to change —
            // and HeroPowerDoesNotFireOnSpellCast in SpellCastTriggerTests is the test that must change with it.
            CardDefinition definition = _db.Get(ps.Hero.HeroPowerId);
            interp.RaiseSelf(ps.Hero.Id, ps.Id, definition, EffectSourceKind.HeroPower,
                currentCarrier: null, Trigger.ON_CAST, hp.Targets);
            // Drain checkpoints deaths and game-end on every exit (scheduling §3.2), which discharges
            // the LATENT hazard that stood here alongside ApplyPlayCard's: a hero power costing its
            // owner life would have left a lethal unsettled. That no longer depends on this site.
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
                // A weapon, or Attack gained this turn (GAIN_HERO_ATTACK), is what lets a hero swing.
                if (ps.Weapon == null && ps.HeroAttackThisTurn <= 0) { reason = RejectReason.WeaponMissing; return false; }
                if (ps.Hero.IsFrosted()) { reason = RejectReason.AttackerFrosted; return false; }
                int maxAttacks = ps.Weapon != null && ps.Weapon.Keywords.Contains(Keyword.DOUBLESTRIKE) ? 2 : 1;
                if (ps.HeroAttacksUsedThisTurn >= maxAttacks) { reason = RejectReason.NoAttacksLeft; return false; }
            }
            else
            {
                var m = attacker.Minion!;
                if (m.IsFrosted()) { reason = RejectReason.AttackerFrosted; return false; }
                if (m.CurrentAttack <= 0) { reason = RejectReason.IllegalTarget; return false; }
                bool hasFervor = m.Keywords.Contains(Keyword.FERVOR);
                bool hasSkirmish = m.Keywords.Contains(Keyword.SKIRMISH);
                if (m.SummonedThisTurn && !hasFervor && !hasSkirmish)
                { reason = RejectReason.HasSummoningSickness; return false; }
                int maxAttacks = m.Keywords.Contains(Keyword.DOUBLESTRIKE) ? 2 : 1;
                if (m.AttacksUsedThisTurn >= maxAttacks) { reason = RejectReason.NoAttacksLeft; return false; }
            }

            // Defender must exist and belong to the opponent
            if (!Characters.TryFind(state, a.Defender.Instance, out var defender))
            { reason = RejectReason.IllegalTarget; return false; }
            if (defender.Seat == seat)
            { reason = RejectReason.IllegalTarget; return false; }

            // SKIRMISH can only target minions during summoning turn
            if (attacker.IsMinion && attacker.Minion!.SummonedThisTurn
                && attacker.Minion.Keywords.Contains(Keyword.SKIRMISH) && defender.IsHero)
            { reason = RejectReason.IllegalTarget; return false; }

            // SHROUD blocks targeting (even a shrouded bulwarker hides its bulwark)
            if (defender.IsMinion && defender.Minion!.Keywords.Contains(Keyword.SHROUD))
            { reason = RejectReason.IllegalTarget; return false; }

            // BULWARK enforcement: must attack a non-shrouded bulwarker if one exists
            var foeBoard = state.Players[foe].Board;
            bool anyBulwark = false;
            for (int i = 0; i < foeBoard.Count; i++)
            {
                var fm = foeBoard[i];
                if (fm.Keywords.Contains(Keyword.BULWARK) && !fm.Keywords.Contains(Keyword.SHROUD))
                { anyBulwark = true; break; }
            }
            if (anyBulwark && (!defender.IsMinion || !defender.Minion!.Keywords.Contains(Keyword.BULWARK)))
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
                : (ps.Weapon?.CurrentAttack ?? 0) + ps.HeroAttackThisTurn;

            var attackerKws = attacker.IsMinion
                ? new HashSet<Keyword>(attacker.Minion!.Keywords)
                : ps.Weapon != null ? new HashSet<Keyword>(ps.Weapon.Keywords) : new HashSet<Keyword>();

            // attackerKws already resolves minion-vs-weapon (line above), so OVERWHELM/VANGUARD
            // read off it too rather than re-deriving from attacker.Minion — a weapon carrying
            // either now gets the same combat-resolver behavior a minion carrier does.
            bool overwhelm = attackerKws.Contains(Keyword.OVERWHELM);
            bool vanguard = attackerKws.Contains(Keyword.VANGUARD);

            // Shroud breaks the moment the attacker declares
            if (attacker.IsMinion) attacker.Minion!.Keywords.Remove(Keyword.SHROUD);

            // Increment attack budget (before damage — canonical ordering)
            if (attacker.IsMinion) attacker.Minion!.AttacksUsedThisTurn++;
            else ps.HeroAttacksUsedThisTurn++;

            buf.Emit(GameEventKind.AttackDeclared, source: attacker.Id, target: defender.Id);

            // "Whenever this attacks" effects (e.g. Grasping Thornvine's heal) resolve from the
            // attacking carrier itself, preserving its identity and runtime source state — the
            // equipped weapon on a hero attack, the minion itself on a minion attack. Empty target
            // list either way: the defender is not addressable from ON_ATTACK (Sam, 2026-09-05).
            if (attacker.IsHero && ps.Weapon != null)
                interp.RaiseSelf(ps.Weapon, Trigger.ON_ATTACK, System.Array.Empty<TargetRef>());
            else if (attacker.IsMinion)
                interp.RaiseSelf(attacker.Minion!, Trigger.ON_ATTACK, System.Array.Empty<TargetRef>());

            // Phase 1: attacker hits defender
            DamagePipeline.ApplyDamage(interp, defender, attackerAtk, attacker.Id, attackerKws);

            // OVERWHELM: carry excess overkill damage to enemy hero (minion targets only).
            // Riders (LIFEDRAIN/VENOM) attach to the primary hit only — G0 ruling R-2(c),
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
                ps.Weapon.CurrentDurability--;
                if (ps.Weapon.CurrentDurability <= 0)
                    RulesEngine.BreakWeapon(interp, seat);   // rattle enqueued; interp.Drain() below resolves it
            }

            // Settle deaths, tolls, win-check. Direct combat damage to a hero leaves the effect queue
            // empty — which is exactly the exit Drain used to leave unchecked, because the win check
            // lived only inside its loop body. Drain now checkpoints on every exit (scheduling §3.2).
            interp.Drain();
        }

        // ── Turn sequence (GDD §1b / ARCHITECTURE §5) ─────────────────────────────

        private void StartTurnSequence(GameState state, PlayerId player, EventBuffer buf)
        {
            int seat = state.SeatOf(player);
            var ps = state.Players[seat];

            state.Phase = TurnPhase.StartOfTurn;
            state.TurnNumber++;
            state.ActionResolutionsThisTurn = 0;
            // Both seats, not just the incoming one: "this turn" is the turn now starting, and
            // what a hero took during the turn that just ended belongs to that turn.
            state.Players[0].HeroDamageTakenThisTurn = 0;
            state.Players[1].HeroDamageTakenThisTurn = 0;

            // Mana: +1 max (cap 10) → apply backdraft locks → refill.
            ps.Mana.Max = System.Math.Min(ps.Mana.Max + 1, MatchEngine.MaxMana);
            int locked = ps.NextTurnBackdraftLock;
            ps.NextTurnBackdraftLock = 0;
            ps.Mana.Locked = locked;
            ps.Mana.Spent = 0;
            ps.Mana.Temporary = 0;
            ps.Mana.Available = System.Math.Max(0, ps.Mana.Max - locked);

            // Reset summoning sickness / attack budget / hero power. (Thaw itself runs at END of the
            // frozen player's turn in ApplyEndTurn — see the comment there for the FROST timing rationale.)
            //
            // Age every turn-scoped effect on the incoming player's own characters. An instance applied
            // during this player's PREVIOUS own turn (the warden case) is aged here rather than at the
            // end of the turn it landed in, which is what makes frost cost the full turn it is carried
            // into. Frost applied on the opponent's turn is aged at this same point, one turn earlier in
            // its life — so both cases reach zero exactly when they have served a full turn.
            foreach (var m in ps.Board)
            {
                m.SummonedThisTurn = false;
                m.AttacksUsedThisTurn = 0;
                EffectList.TickControllerTurnStart(m.Effects);
            }
            EffectList.TickControllerTurnStart(ps.Hero.Effects);
            ps.HeroPowerUsedThisTurn = false;
            ps.HeroAttacksUsedThisTurn = 0;

            var interp = new EffectInterpreter(state, _db, buf);
            buf.Emit(GameEventKind.TurnStarted, source: ps.Hero.Id);

            // EF-011: ON_TURN_START fires here — AFTER the mana reset above, so a start-of-turn
            // effect sees the board the turn actually starts on (sickness cleared, mana refilled),
            // and BEFORE the draw, so the cascade settles while the drawing player is still
            // alive. Draining after DrawOne would let a corpse draw fatigue.
            //
            // The incoming player is explicit event context, separate from each source controller.
            // TurnOwnerCondition then qualifies CONTROLLER, OPPONENT or ANY without encoding ownership
            // into trigger/selector names. `excluded: null` because no card causes a turn to start.
            // The shared source seam walks incoming board minions left-to-right, equipped weapon,
            // then the other side in the same category order, followed by ongoing effects.
            interp.RaiseObservers(Trigger.ON_TURN_START, excluded: null,
                context: TriggerEventContext.ForIncomingTurn(player));

            // The start-of-turn cascade must FULLY settle — deaths, tolls, win-check — BEFORE
            // DrawOne: a start-of-turn toll that kills its own controller must end the match here
            // rather than draw a card into a finished game. Drain checkpoints on every exit path
            // (scheduling §3.2), so that settlement no longer depends on this site remembering.
            interp.Drain();
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
