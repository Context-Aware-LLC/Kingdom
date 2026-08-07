#nullable disable
using System.Collections.Generic;
using Emberfall.Core;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Presentation
{
    /// <summary>What the pointer is over. Produced by <see cref="InputRouter"/>'s raycast; consumed
    /// by <see cref="DragTargetingModel"/>, which never touches a collider or a Transform.</summary>
    public enum PointerSubjectKind { None, Card, BoardSlot, Hero, HeroPower }

    public readonly struct PointerSubject
    {
        public readonly PointerSubjectKind Kind;
        public readonly InstanceId Id;         // Card: the card instance. Hero: the hero instance.
        public readonly PlayerId Owner;
        public readonly Zone Zone;       // Card only
        public readonly int SlotIndex;  // BoardSlot only

        private PointerSubject(PointerSubjectKind kind, InstanceId id, PlayerId owner, Zone zone, int slotIndex)
        {
            Kind = kind;
            Id = id;
            Owner = owner;
            Zone = zone;
            SlotIndex = slotIndex;
        }

        public static PointerSubject None => default;

        public static PointerSubject OfCard(InstanceId id, PlayerId owner, Zone zone)
            => new PointerSubject(PointerSubjectKind.Card, id, owner, zone, -1);

        public static PointerSubject OfBoardSlot(PlayerId owner, int slotIndex)
            => new PointerSubject(PointerSubjectKind.BoardSlot, default, owner, Zone.Board, slotIndex);

        public static PointerSubject OfHero(InstanceId heroId, PlayerId owner)
            => new PointerSubject(PointerSubjectKind.Hero, heroId, owner, Zone.Board, -1);

        public static PointerSubject OfHeroPower(PlayerId owner)
            => new PointerSubject(PointerSubjectKind.HeroPower, default, owner, Zone.Board, -1);
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
    /// so this errs toward submitting and letting the reject toast explain — a silently-swallowed
    /// gesture teaches the player nothing.</para>
    /// </summary>
    public sealed class DragTargetingModel
    {
        /// <summary>Awaiting a second choice: a slot placement followed by a herald target.</summary>
        public enum TargetingPhase { Direct, AwaitingEffectTarget }

        // No cancel hint. Cancelling is releasing on nothing — see Resolve, where a null subject
        // means "this drop meant nothing" — which is the same gesture with a mouse or a finger and
        // needs no instruction. The banner used to end in "— Esc cancels", which named a key that
        // does not exist on the mobile-first target and bought nothing on desktop, where Escape
        // still cancels (InputRouter's KeyCode.Escape branch) whether or not the banner says so.
        public const string AimBanner = "Drag to a target";
        public const string AttackBanner = "Drag to a target to attack";

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
                    // Our hero is an attacker when armed (engine: ValidateAttack IsHero + weapon).
                    // A weaponless swing is the validator's to reject, not ours to pre-empt.
                    if (pressed.Owner != _p0Id) return DragSource.None;
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

        public string BannerFor(DragSource source)
            => source.Kind == DragSourceKind.Attacker ? AttackBanner : AimBanner;

        // ── Release: what intent does this gesture produce? ────────────────────────

        /// <summary>
        /// The release half. Null means "this drop meant nothing" — cancel silently rather than
        /// spending a reject toast on a gesture the player clearly abandoned.
        /// </summary>
        public PlayerIntent Resolve(DragSource source, PointerSubject released)
        {
            if (State == null || !CanAct()) return null;

            // If we're awaiting an effect target (two-step herald), handle that instead.
            if (Phase == TargetingPhase.AwaitingEffectTarget)
                return ResolveEffectTarget(released);

            switch (source.Kind)
            {
                case DragSourceKind.HandCard: return ResolveHandCard(source, released);
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
            foreach (TargetChoiceRequirement requirement in Targeting.PlayerChoicePlan(
                         State, _db, _p0Id, def, trigger, source))
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

        private PlayerIntent ResolveHandCard(DragSource source, PointerSubject released)
        {
            switch (released.Kind)
            {
                case PointerSubjectKind.BoardSlot:
                    // The player picks the slot. Plan decision 13 deleted the auto-place shortcut that
                    // used to turn a click on empty space into a play at the end of the board: on touch
                    // it turned stray taps into misplays, and the slot path already carries the intent.
                    if (released.Owner != _p0Id) return null;

                    // An occupied slot is never a legal placement target, on either side of the board.
                    // Check if this slot already has a minion in it.
                    PlayerState ps = null;
                    foreach (PlayerState p in State.Players)
                        if (p.Id == released.Owner) { ps = p; break; }
                    if (ps != null && released.SlotIndex < ps.Board.Count) return null;

                    if (source.NeedsTarget)
                    {
                        // Targeted spells do not place on the board, so a slot release is a miss.
                        if (!HandCardOccupiesBoardSlot(source.Id)) return null;

                        // A targeted minion needs placement before its herald choice plan.
                        if (!BeginHandChoicePlan(source.Id, released.SlotIndex)) return null;
                        _phase = TargetingPhase.AwaitingEffectTarget;
                        return null;   // No intent yet; the player still needs to pick a target.
                    }

                    return new PlayCardIntent(_p0Id, source.Id, released.SlotIndex,
                                              System.Array.Empty<TargetRef>());

                case PointerSubjectKind.Card:
                    // Enemy board minions are always aimable; our own only when the card actually wants
                    // a chosen target, so a drop on our own board with a vanilla minion is a miss, not
                    // a spell fired at slot 0.
                    if (released.Owner == _p0Id && !source.NeedsTarget) return null;

                    // Cards that occupy a board slot must be placed before choosing an effect target.
                    if (HandCardOccupiesBoardSlot(source.Id)) return null;

                    if (!BeginHandChoicePlan(source.Id, 0)) return null;
                    return ChooseTarget(released);

                case PointerSubjectKind.Hero:
                    // Cards that occupy a board slot must be placed before choosing an effect target.
                    if (HandCardOccupiesBoardSlot(source.Id)) return null;

                    if (!BeginHandChoicePlan(source.Id, 0)) return null;
                    return ChooseTarget(released);

                default:
                    return null;
            }
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
        /// returns false and the engine's rejection is the backstop.</summary>
        public bool HandCardNeedsTarget(InstanceId cardId)
        {
            if (!TryHandDefinition(cardId, out CardDefinition def)) return false;
            Trigger trigger = def.Type == CardType.SPELL ? Trigger.ON_CAST : Trigger.ON_PLAY;
            return Targeting.RequiresPlayerChoice(def, trigger);
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

        /// <summary>Mirrors the engine's ValidateHeroPower rule. With no db wired the gesture falls
        /// through to the immediate fire and the engine's rejection is the backstop.</summary>
        public bool HeroPowerNeedsTarget()
        {
            return TryHeroPowerDefinition(out CardDefinition def) &&
                   Targeting.RequiresPlayerChoice(def, Trigger.ON_CAST);
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
