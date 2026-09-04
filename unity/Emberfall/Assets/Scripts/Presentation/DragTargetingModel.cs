#nullable disable
using System.Collections.Generic;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Presentation
{
    /// <summary>What the pointer is over. Produced by <see cref="InputRouter"/>'s raycast; consumed
    /// by <see cref="DragTargetingModel"/>, which never touches a collider or a Transform.</summary>
    /// <summary>
    /// BOARD SLOTS ARE NOT A SUBJECT KIND (Sam, 2026-08-12, #139). There used to be a
    /// <c>BoardSlot</c> member here, produced by 14 invisible box colliders that a minion had to be
    /// dropped inside of. Placement is solved from the drop's x now
    /// (<see cref="BoardRowLayout.InsertIndexForX"/>), so the colliders, the component and this
    /// enum member all went with it. Nothing about the board's empty space is raycast any more.
    /// </summary>
    public enum PointerSubjectKind { None, Card, Hero, HeroPower }

    public readonly struct PointerSubject
    {
        public readonly PointerSubjectKind Kind;
        public readonly InstanceId Id;         // Card: the card instance. Hero: the hero instance.
        public readonly PlayerId Owner;
        public readonly Zone Zone;       // Card only

        private PointerSubject(PointerSubjectKind kind, InstanceId id, PlayerId owner, Zone zone)
        {
            Kind = kind;
            Id = id;
            Owner = owner;
            Zone = zone;
        }

        public static PointerSubject None => default;

        public static PointerSubject OfCard(InstanceId id, PlayerId owner, Zone zone)
            => new PointerSubject(PointerSubjectKind.Card, id, owner, zone);

        public static PointerSubject OfHero(InstanceId heroId, PlayerId owner)
            => new PointerSubject(PointerSubjectKind.Hero, heroId, owner, Zone.Board);

        public static PointerSubject OfHeroPower(PlayerId owner)
            => new PointerSubject(PointerSubjectKind.HeroPower, default, owner, Zone.Board);
    }

    /// <summary>
    /// WHERE a release happened, as opposed to <see cref="PointerSubject"/>'s what-it-was-over.
    /// Two facts, because two facts are all the release rule needs (Sam, 2026-08-12, #139):
    ///
    /// <list type="bullet">
    /// <item><b>Is it back in our hand?</b> The one and only cancel. Everything else on screen
    /// resolves — the enemy's half of the board included, which used to cancel silently.</item>
    /// <item><b>What x?</b> A minion's position on the row, and nothing else, reads this. The
    /// drop's y is deliberately not carried past this struct: "the Y-position of the drop is
    /// irrelevant as long as it's above the threshold of the hand".</item>
    /// </list>
    ///
    /// <para>The hand threshold is decided by <c>InputRouter</c> against
    /// <c>HandFanLayout.TopEdgeY</c> rather than in here. That is not tidiness: this file is
    /// compiled into <c>tests/Emberfall.Core.Tests</c> with no Unity assemblies present, so a
    /// reference to either of those types would break the headless build. The x, which this model
    /// DOES read, is resolved through <see cref="BoardRowGeometry"/>, which is Unity-free for the
    /// same reason and linked into that csproj alongside this file.</para>
    /// </summary>
    public readonly struct DropPoint
    {
        public readonly float X;
        public readonly bool OverOwnHand;

        private DropPoint(float x, bool overOwnHand)
        {
            X = x;
            OverOwnHand = overOwnHand;
        }

        /// <summary>Anywhere above the hand — the whole board, both halves, and off the edges.</summary>
        public static DropPoint Board(float x) => new DropPoint(x, false);

        /// <summary>Put back where it came from. The only gesture that means "I changed my mind".</summary>
        public static DropPoint OwnHand => new DropPoint(0f, true);
    }

    public enum DragSourceKind
    {
        None,
        /// <summary>A card in our hand, being dragged to a board slot or onto a target.</summary>
        HandCard,
        /// <summary>One of our board minions, or our weapon-armed hero, swinging at something.</summary>
        Attacker,
        /// <summary>Our hero power.</summary>
        HeroPower,
    }

    public readonly struct DragSource
    {
        public readonly DragSourceKind Kind;
        public readonly InstanceId Id;           // hand card / attacker instance; unused for HeroPower
        public readonly bool NeedsTarget;  // an explicit-choice (ANY_*) selector is in play

        public DragSource(DragSourceKind kind, InstanceId id, bool needsTarget)
        {
            Kind = kind;
            Id = id;
            NeedsTarget = needsTarget;
        }

        public static DragSource None => default;
    }

    /// <summary>
    /// The press/drag/release decision core (EF-048), deliberately free of MonoBehaviour, Transform,
    /// collider and camera. Everything that decides WHETHER a gesture is legal and WHAT intent it
    /// produces lives here; <see cref="InputRouter"/> is reduced to pointer sampling, raycasting and
    /// visuals. That split is what makes the machine testable without a scene — see
    /// <c>Emberfall.Editor.InputGestureVerify</c>, which drives every transition headlessly.
    ///
    /// <para>Legality here is a COURTESY affordance, not authority (ADR-0004, and the same doctrine
    /// as the old click machine): the target sets exist so the board can glow, and the validator plus
    /// the server still rule on every intent this produces. Where the two disagree the engine wins,
    /// so this errs toward SUBMITTING: the courtesy target set can be wrong (it is built from a
    /// mirror), and swallowing a gesture the authority would have allowed is the worse of the two
    /// errors. Hand-card legality that is fully visible in the mirror is checked here, though:
    /// an unaffordable card or one with an empty chosen-target set must cancel so InputRouter
    /// can return its dragged visual instead of waiting for a snapshot that a rejected play
    /// will never produce.</para>
    /// </summary>
    public sealed class DragTargetingModel
    {
        /// <summary>A slot is banked and a herald target is still owed.</summary>
        public enum TargetingPhase { Direct, AwaitingEffectTarget }

        // NO INSTRUCTIONAL TEXT (Sam, 2026-08-27). This model used to hand InputRouter banner
        // copy — "Drag to a target", "Tap a target" — narrating a gesture the player learns in
        // the first ten minutes. That instruction belongs once, in onboarding, and never again;
        // see decisions/emberfall-no-persistent-instructional-banners. The target glow is the
        // whole affordance. Cancelling is still releasing on nothing (see Resolve, where a null
        // subject means "this drop meant nothing"), and Escape still cancels on desktop via
        // InputRouter's KeyCode.Escape branch — neither ever needed saying out loud.

        private readonly PlayerId _p0Id;
        private readonly ICardDatabase _db;
        private readonly List<TargetChoiceRequirement> _choicePlan =
            new List<TargetChoiceRequirement>();
        private readonly List<TargetRef> _chosenTargets = new List<TargetRef>();
        private int _choiceIndex;
        private DragSourceKind _awaitingSourceKind;
        private TargetingPhase _phase;

        public DragTargetingModel(PlayerId p0Id, ICardDatabase db)
        {
            _p0Id = p0Id;
            _db = db;
        }

        /// <summary>The mirror this model judges against. Set from every snapshot.</summary>
        public GameState State { get; set; }

        /// <summary>Current targeting phase: Direct (normal) or collecting an effect's choice plan.</summary>
        public TargetingPhase Phase
        {
            get => _phase;
            set
            {
                _phase = value;
                if (value == TargetingPhase.Direct) ClearChoicePlan();
            }
        }

        /// <summary>When in AwaitingEffectTarget phase, the slot where the card will be played.</summary>
        public int AwaitingSlot { get; set; }

        /// <summary>When in AwaitingEffectTarget phase, the card being played.</summary>
        public InstanceId AwaitingCardId { get; set; }

        public string AwaitingBinding
            => _choiceIndex < _choicePlan.Count ? _choicePlan[_choiceIndex].Binding : "";

        public int AwaitingRequiredCount
            => _choiceIndex < _choicePlan.Count ? _choicePlan[_choiceIndex].RequiredCount : 0;

        public int AwaitingSelectedCount
        {
            get
            {
                string binding = AwaitingBinding;
                int count = 0;
                foreach (TargetRef target in _chosenTargets)
                    if (target.Binding == binding) count++;
                return count;
            }
        }

        // ── Phase gates ────────────────────────────────────────────────────────────

        /// <summary>
        /// Our seat is still deciding its mulligan. NOTE — deliberately NOT gated on
        /// <c>ActivePlayer</c>: the engine's mulligan is SIMULTANEOUS, so the non-active seat must be
        /// able to toss and confirm. Any turn gate added here hangs that seat with nothing logged.
        /// </summary>
        public bool IsMulliganActive()
        {
            if (State == null || State.Phase != TurnPhase.Mulligan) return false;
            int seat = State.SeatOf(_p0Id);
            return !(seat == 0 ? State.Mulligan0Done : State.Mulligan1Done);
        }

        /// <summary>Board gestures are ours to make: our Main phase, our turn.</summary>
        public bool CanAct()
            => State != null && State.Phase == TurnPhase.Main && State.ActivePlayer == _p0Id;

        public InstanceId HeroIdOf(PlayerId owner)
        {
            if (State == null) return default;
            foreach (PlayerState ps in State.Players)
                if (ps.Id == owner) return ps.Hero.Id;
            return default;
        }

        /// <summary>
        /// Whether pressing our own hero portrait starts an attack gesture at all (Sam,
        /// 2026-09-04: "only while they have an attack left of course"). Mirrors
        /// <c>CommandProcessor.ValidateAttack</c>'s hero branch exactly — weapon equipped, not
        /// frosted, attacks remaining, DOUBLESTRIKE granting a second.
        ///
        /// <para>This REFUSES a gesture, which the class doctrine above says to avoid — but the
        /// exception it names is the one that applies: every field read here is fully visible in
        /// the mirror and none of it is hidden information, exactly like hand-card affordability.
        /// The affordance is what makes it worth checking: the hero now lifts and follows the
        /// pointer when it can swing, so starting a gesture that the server is certain to reject
        /// would show the player a pick-up for an attack that cannot happen.</para>
        ///
        /// <para>RETIREMENT TRIGGER: a hero attack that does not come from a weapon (an
        /// attack-granting effect, a claws-style hero power) makes the weapon test wrong here and
        /// in <c>ValidateAttack</c> together.</para>
        /// </summary>
        public bool HeroCanAttack()
        {
            if (State == null) return false;
            PlayerState ps = null;
            foreach (PlayerState p in State.Players)
                if (p.Id == _p0Id) { ps = p; break; }
            if (ps?.Weapon == null) return false;
            if (ps.Hero.IsFrosted()) return false;
            int maxAttacks = ps.Weapon.Keywords.Contains(Keyword.DOUBLESTRIKE) ? 2 : 1;
            return ps.HeroAttacksUsedThisTurn < maxAttacks;
        }

        public PlayerId EnemyId()
        {
            if (State == null) return default;
            foreach (PlayerState ps in State.Players)
                if (ps.Id != _p0Id) return ps.Id;
            return default;
        }

        // ── Press: does this subject start a gesture? ──────────────────────────────

        /// <summary>
        /// The press half of the machine. Returns <see cref="DragSource.None"/> for anything that is
        /// not ours to move — which is also the "press did nothing, let the tap fall through" answer.
        /// </summary>
        public DragSource BeginSource(PointerSubject pressed)
        {
            if (!CanAct()) return DragSource.None;

            switch (pressed.Kind)
            {
                case PointerSubjectKind.Card:
                    if (pressed.Owner != _p0Id) return DragSource.None;
                    if (pressed.Zone == Zone.Hand)
                        return new DragSource(DragSourceKind.HandCard, pressed.Id, HandCardNeedsTarget(pressed.Id));
                    if (pressed.Zone == Zone.Board)
                        return new DragSource(DragSourceKind.Attacker, pressed.Id, true);
                    return DragSource.None;

                case PointerSubjectKind.Hero:
                    if (pressed.Owner != _p0Id) return DragSource.None;
                    if (!HeroCanAttack()) return DragSource.None;
                    return new DragSource(DragSourceKind.Attacker, HeroIdOf(_p0Id), true);

                case PointerSubjectKind.HeroPower:
                    if (pressed.Owner != _p0Id) return DragSource.None;
                    return new DragSource(DragSourceKind.HeroPower, default, HeroPowerNeedsTarget());

                default:
                    return DragSource.None;
            }
        }

        /// <summary>The set the board should glow while this gesture is armed. Null = no aim needed
        /// (a plain minion going to a slot, an untargeted hero power) and no banner.</summary>
        public HashSet<InstanceId> TargetsFor(DragSource source)
        {
            switch (source.Kind)
            {
                case DragSourceKind.HandCard: return source.NeedsTarget ? SpellTargets(source.Id) : null;
                case DragSourceKind.Attacker: return AttackTargets(source.Id);
                case DragSourceKind.HeroPower: return source.NeedsTarget ? HeroPowerTargets() : null;
                default: return null;
            }
        }

        // ── Release: what intent does this gesture produce? ────────────────────────

        /// <summary>
        /// The release half. Null means "this drop meant nothing" — cancel rather than manufacture
        /// an intent from a gesture the player clearly abandoned.
        /// </summary>
        public PlayerIntent Resolve(DragSource source, PointerSubject released, DropPoint drop)
        {
            if (State == null || !CanAct()) return null;

            // If we're awaiting an effect target (multi-step herald), handle that instead. The drop
            // point is not consulted: the card's position was already fixed by the release that
            // placed it, and this step only picks a target.
            if (Phase == TargetingPhase.AwaitingEffectTarget)
                return ResolveEffectTarget(released);

            switch (source.Kind)
            {
                case DragSourceKind.HandCard: return ResolveHandCard(source, released, drop);
                case DragSourceKind.Attacker: return ResolveAttack(source, released);
                case DragSourceKind.HeroPower: return ResolveHeroPower(source, released);
                default: return null;
            }
        }

        /// <summary>Resolve the next step in the current binding-aware choice plan.</summary>
        private PlayerIntent ResolveEffectTarget(PointerSubject released)
        {
            if (_choicePlan.Count == 0 && !BeginHandChoicePlan(AwaitingCardId, AwaitingSlot))
                return null;
            return ChooseTarget(released);
        }

        private bool BeginHandChoicePlan(InstanceId cardId, int slot)
        {
            if (!TryHandDefinition(cardId, out CardDefinition def)) return false;
            Trigger trigger = def.Type == CardType.SPELL ? Trigger.ON_CAST : Trigger.ON_PLAY;
            return BeginChoicePlan(DragSourceKind.HandCard, cardId, slot, def, trigger, cardId);
        }

        private bool BeginHeroPowerChoicePlan()
        {
            if (!TryHeroPowerDefinition(out CardDefinition def)) return false;
            return BeginChoicePlan(DragSourceKind.HeroPower, default, 0, def, Trigger.ON_CAST, null);
        }

        private bool BeginChoicePlan(DragSourceKind sourceKind, InstanceId cardId, int slot,
            CardDefinition def, Trigger trigger, InstanceId? source)
        {
            ClearChoicePlan();
            // A group the engine no longer asks for is skipped, not shown. A herald with no target
            // reports RequiredCount 0 (Targeting.RequiredPlayerCount), and an armed step with
            // nothing to pick is a dead end. HandCardCanResolve now catches that empty candidate
            // set before this method; skipping zero-count groups here keeps the plan itself valid.
            foreach (TargetChoiceRequirement requirement in Targeting.PlayerChoicePlan(
                         State, _db, _p0Id, def, trigger, source))
                if (requirement.RequiredCount > 0)
                    _choicePlan.Add(requirement);
            if (_choicePlan.Count == 0) return false;
            _choiceIndex = 0;
            _awaitingSourceKind = sourceKind;
            AwaitingCardId = cardId;
            AwaitingSlot = slot;
            return true;
        }

        private PlayerIntent ChooseTarget(PointerSubject released)
        {
            if (released.Kind != PointerSubjectKind.Card && released.Kind != PointerSubjectKind.Hero)
                return null;
            if (!AwaitingTargets().Contains(released.Id)) return null;

            TargetChoiceRequirement requirement = _choicePlan[_choiceIndex];
            _chosenTargets.Add(new TargetRef(released.Id, requirement.Binding));
            if (AwaitingSelectedCount >= requirement.RequiredCount) _choiceIndex++;

            if (_choiceIndex < _choicePlan.Count)
            {
                _phase = TargetingPhase.AwaitingEffectTarget;
                return null;
            }

            var targets = _chosenTargets.ToArray();
            DragSourceKind sourceKind = _awaitingSourceKind;
            InstanceId cardId = AwaitingCardId;
            int slot = AwaitingSlot;
            _phase = TargetingPhase.Direct;
            ClearChoicePlan();
            return sourceKind == DragSourceKind.HeroPower
                ? (PlayerIntent)new HeroPowerIntent(_p0Id, targets)
                : new PlayCardIntent(_p0Id, cardId, slot, targets);
        }

        public HashSet<InstanceId> AwaitingTargets()
        {
            var result = new HashSet<InstanceId>();
            if (_choiceIndex >= _choicePlan.Count) return result;
            TargetChoiceRequirement requirement = _choicePlan[_choiceIndex];
            var excluded = new HashSet<InstanceId>();
            foreach (TargetRef selected in _chosenTargets)
            {
                if (selected.Binding == requirement.Binding)
                    excluded.Add(selected.Instance);
                foreach (string binding in requirement.DistinctFrom)
                    if (selected.Binding == binding) excluded.Add(selected.Instance);
            }
            foreach (TargetCandidate candidate in requirement.Candidates)
                if (!excluded.Contains(candidate.Instance)) result.Add(candidate.Instance);
            return result;
        }

        private void ClearChoicePlan()
        {
            _choicePlan.Clear();
            _chosenTargets.Clear();
            _choiceIndex = 0;
            _awaitingSourceKind = DragSourceKind.None;
        }

        /// <summary>
        /// ONCE IT IS OUT OF THE HAND, LETTING GO RESOLVES IT (Sam, 2026-08-12, #139).
        ///
        /// <para>What this method used to be was a switch on what the pointer was OVER, and it is
        /// now a switch on what the CARD is. That inversion is the whole change. A drop no longer
        /// has to find a friendly empty slot collider, or our own board, or our own half of the
        /// screen: those were all ways for a deliberate gesture to end in silence, and there were
        /// three separate <c>return null</c>s that a player experienced as the game ignoring
        /// them.</para>
        ///
        /// <para>The exceptions, and there are only two:</para>
        /// <list type="number">
        /// <item>Releasing back over our own hand cancels — that is what "I changed my mind" is.</item>
        /// <item>A card that needs a chosen target and takes no board slot (a targeted spell, a
        /// targeted weapon) must land ON something aimable. It has no position to fall back on, so
        /// there is nothing for open space to mean.</item>
        /// </list>
        /// </summary>
        private PlayerIntent ResolveHandCard(DragSource source, PointerSubject released, DropPoint drop)
        {
            // Exception 1. Note this is a LINE, not the hand cards' colliders: releasing into a gap
            // between two fanned cards is still putting it back, and it would be perverse for that
            // to play the card.
            if (drop.OverOwnHand) return null;

            // A rejected play produces no state snapshot. Returning an intent for a condition the
            // local mirror can already prove false therefore strands the dragged visual at its
            // release point until some later accepted action happens to re-layout the hand.
            if (!HandCardCanResolve(source.Id)) return null;

            bool occupiesSlot = HandCardOccupiesBoardSlot(source.Id);

            // Exception 2. A targeted spell released on open space, on a slot's worth of empty
            // board, or on a character the engine will not accept, produces nothing — there is no
            // sensible target to guess and guessing one is a misplay, not a convenience.
            if (source.NeedsTarget && !occupiesSlot)
            {
                if (released.Kind != PointerSubjectKind.Card && released.Kind != PointerSubjectKind.Hero)
                    return null;
                if (!BeginHandChoicePlan(source.Id, 0)) return null;
                return ChooseTarget(released);
            }

            // Everything else: the Coin, an untargeted spell, any weapon. No position of its own,
            // so the drop point is read only for the cancel above.
            if (!occupiesSlot)
                return new PlayCardIntent(_p0Id, source.Id, 0, System.Array.Empty<TargetRef>());

            // A minion. Its position comes from the drop's x against the row as it currently
            // stands, and from nothing else — not from what was under the pointer, and not from
            // the drop's y, which is ignored the moment it clears the hand.
            int slot = BoardRowGeometry.InsertIndexForX(drop.X, FriendlyBoardCount());

            if (source.NeedsTarget)
            {
                if (!BeginHandChoicePlan(source.Id, slot)) return null;
                _phase = TargetingPhase.AwaitingEffectTarget;
                // Placement and effect targeting are deliberately separate decisions. Even when
                // the card was released over a legal character, this first drop only chooses the
                // board slot; the glowing target is then selected by click, keyboard, or re-grab.
                return null;
            }

            return new PlayCardIntent(_p0Id, source.Id, slot, System.Array.Empty<TargetRef>());
        }

        /// <summary>
        /// Local, visible playability only. The authority remains final, but mana, board capacity
        /// and an empty player-chosen target set are all present in this player's projection and
        /// can be refused early so a failed drop returns to hand immediately.
        /// </summary>
        private bool HandCardCanResolve(InstanceId cardId)
        {
            if (!TryHandDefinition(cardId, out CardDefinition def)) return true;

            PlayerState own = null;
            foreach (PlayerState ps in State.Players)
                if (ps.Id == _p0Id) { own = ps; break; }
            if (own == null) return true;

            if (own.Mana.Available < def.Cost) return false;
            if (def.Type == CardType.MINION && own.Board.Count >= MatchEngine.BoardCap) return false;

            Trigger trigger = def.Type == CardType.SPELL ? Trigger.ON_CAST : Trigger.ON_PLAY;
            foreach (TargetChoiceRequirement requirement in Targeting.PlayerChoicePlan(
                         State, _db, _p0Id, def, trigger, cardId))
                if (requirement.Candidates.Count == 0) return false;

            return true;
        }

        /// <summary>How many minions our row is currently holding — the count the drop's x is
        /// resolved against.</summary>
        private int FriendlyBoardCount()
        {
            if (State == null) return 0;
            foreach (PlayerState ps in State.Players)
                if (ps.Id == _p0Id) return ps.Board.Count;
            return 0;
        }

        private PlayerIntent ResolveAttack(DragSource source, PointerSubject released)
        {
            // Illegal picks (friendly fire, a bulwark wall in the way) are the engine's to reject; the
            // glow set above is the courtesy half. Only OWNERSHIP is filtered here, because dropping
            // on your own board during an attack is unambiguously a miss.
            switch (released.Kind)
            {
                case PointerSubjectKind.Card:
                    if (released.Zone != Zone.Board || released.Owner == _p0Id) return null;
                    return new AttackIntent(_p0Id, source.Id, new TargetRef(released.Id));

                case PointerSubjectKind.Hero:
                    if (released.Owner == _p0Id) return null;
                    return new AttackIntent(_p0Id, source.Id, new TargetRef(released.Id));

                default:
                    return null;
            }
        }

        private PlayerIntent ResolveHeroPower(DragSource source, PointerSubject released)
        {
            if (!source.NeedsTarget)
            {
                // An untargeted power fires when released back on itself — i.e. a tap. Dragged off and
                // dropped elsewhere is a cancel, which is the only way to change your mind on touch.
                return released.Kind == PointerSubjectKind.HeroPower && released.Owner == _p0Id
                    ? new HeroPowerIntent(_p0Id, System.Array.Empty<TargetRef>())
                    : null;
            }

            switch (released.Kind)
            {
                case PointerSubjectKind.Card:
                case PointerSubjectKind.Hero:
                    if (!BeginHeroPowerChoicePlan()) return null;
                    return ChooseTarget(released);
                default:
                    return null;
            }
        }

        // ── Selector reading (mirrors the engine, unchanged from the click machine) ─

        /// <summary>Mirrors the engine's ValidatePlayCard rule: a play-trigger effect with an
        /// explicit-choice (ANY_*) selector needs a player-chosen target. With no db wired this
        /// returns false and the engine's rejection is the backstop.
        ///
        /// <para>This asks what the engine will actually OWE against the board as it stands, not what
        /// the card definition mentions. A minion herald with no legal target owes no aim step;
        /// <see cref="HandCardCanResolve"/> separately cancels its drop so it returns to hand rather
        /// than arming a targeting mode the player can never complete.</para></summary>
        public bool HandCardNeedsTarget(InstanceId cardId)
        {
            if (!TryHandDefinition(cardId, out CardDefinition def)) return false;
            Trigger trigger = def.Type == CardType.SPELL ? Trigger.ON_CAST : Trigger.ON_PLAY;
            return OwesAChoice(def, trigger, cardId);
        }

        /// <summary>Does the engine owe this card at least one target it can actually be given?</summary>
        private bool OwesAChoice(CardDefinition def, Trigger trigger, InstanceId? source)
        {
            if (_db == null || State == null) return false;
            foreach (TargetChoiceRequirement requirement in Targeting.PlayerChoicePlan(
                         State, _db, _p0Id, def, trigger, source))
                if (requirement.RequiredCount > 0) return true;
            return false;
        }

        private bool HandCardOccupiesBoardSlot(InstanceId cardId)
        {
            if (_db == null || State == null) return false;
            foreach (PlayerState ps in State.Players)
            {
                if (ps.Id != _p0Id) continue;
                foreach (CardInstance ci in ps.Hand)
                {
                    if (!ci.Id.Equals(cardId)) continue;
                    return _db.TryGet(ci.DefinitionId, out CardDefinition def)
                           && def.Type == CardType.MINION;
                }
            }
            return false;
        }

        // PositionOf() lived here: it read the Position off the minion you dropped ON, which was how
        // a minion reached anywhere other than the end of the row. BoardRowLayout.InsertIndexForX
        // subsumes it — dropping on a minion is just a drop whose x happens to be that minion's, and
        // solving from x also answers for the space between two of them and for open board.

        /// <summary>Mirrors the engine's ValidateHeroPower rule. With no db wired the gesture falls
        /// through to the immediate fire and the engine's rejection is the backstop.</summary>
        public bool HeroPowerNeedsTarget()
        {
            return TryHeroPowerDefinition(out CardDefinition def) &&
                   OwesAChoice(def, Trigger.ON_CAST, null);
        }

        public HashSet<InstanceId> SpellTargets(InstanceId cardId)
        {
            var set = new HashSet<InstanceId>();
            if (_phase == TargetingPhase.AwaitingEffectTarget &&
                _awaitingSourceKind == DragSourceKind.HandCard && AwaitingCardId == cardId)
                return AwaitingTargets();
            if (!TryHandDefinition(cardId, out CardDefinition def)) return set;
            Trigger trigger = def.Type == CardType.SPELL ? Trigger.ON_CAST : Trigger.ON_PLAY;
            CollectFirstChoiceTargets(def, trigger, cardId, set);
            return set;
        }

        private HashSet<InstanceId> HeroPowerTargets()
        {
            var set = new HashSet<InstanceId>();
            if (_phase == TargetingPhase.AwaitingEffectTarget &&
                _awaitingSourceKind == DragSourceKind.HeroPower)
                return AwaitingTargets();
            if (TryHeroPowerDefinition(out CardDefinition def))
                CollectFirstChoiceTargets(def, Trigger.ON_CAST, null, set);
            return set;
        }

        // Uses the same binding-aware headless choice plan as CommandProcessor and EffectInterpreter.
        private void CollectFirstChoiceTargets(CardDefinition def, Trigger trigger,
            InstanceId? source, HashSet<InstanceId> set)
        {
            IReadOnlyList<TargetChoiceRequirement> plan = Targeting.PlayerChoicePlan(
                State, _db, _p0Id, def, trigger, source);
            if (plan.Count == 0) return;
            foreach (TargetCandidate candidate in plan[0].Candidates) set.Add(candidate.Instance);
        }

        private bool TryHandDefinition(InstanceId cardId, out CardDefinition def)
        {
            def = null;
            if (_db == null || State == null) return false;
            foreach (PlayerState ps in State.Players)
            {
                if (ps.Id != _p0Id) continue;
                foreach (CardInstance card in ps.Hand)
                    if (card.Id.Equals(cardId)) return _db.TryGet(card.DefinitionId, out def);
            }
            return false;
        }

        private bool TryHeroPowerDefinition(out CardDefinition def)
        {
            def = null;
            if (_db == null || State == null) return false;
            foreach (PlayerState ps in State.Players)
                if (ps.Id == _p0Id) return _db.TryGet(ps.Hero.HeroPowerId, out def);
            return false;
        }

        // Mirrors the defender-side half of CommandProcessor.ValidateAttack: shroud hides, a
        // non-shrouded bulwark wall restricts to bulwarks, skirmish-on-summon-turn can't go face.
        private HashSet<InstanceId> AttackTargets(InstanceId attackerId)
        {
            var set = new HashSet<InstanceId>();
            if (State == null) return set;

            bool skirmishBlocksFace = false;
            PlayerState enemy = null;
            foreach (PlayerState ps in State.Players)
            {
                if (ps.Id == _p0Id)
                {
                    foreach (CardInstance m in ps.Board)
                        if (m.Id.Equals(attackerId) && m.SummonedThisTurn
                            && m.Keywords.Contains(Keyword.SKIRMISH))
                            skirmishBlocksFace = true;
                }
                else enemy = ps;
            }
            if (enemy == null) return set;

            bool anyBulwark = false;
            foreach (CardInstance m in enemy.Board)
                if (m.Keywords.Contains(Keyword.BULWARK) && !m.Keywords.Contains(Keyword.SHROUD))
                { anyBulwark = true; break; }

            foreach (CardInstance m in enemy.Board)
            {
                if (m.Keywords.Contains(Keyword.SHROUD)) continue;
                if (anyBulwark && !m.Keywords.Contains(Keyword.BULWARK)) continue;
                set.Add(m.Id);
            }
            if (!anyBulwark && !skirmishBlocksFace)
                set.Add(enemy.Hero.Id);
            return set;
        }
    }
}
