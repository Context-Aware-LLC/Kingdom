using System.Collections.Generic;

namespace Emberfall.Core.Contracts
{
    /// <summary>
    /// How many things a zone can hold, per player.
    ///
    /// <para><b>These live in Contracts rather than beside the rules that enforce them</b> because
    /// the card layer needs them too: the maximum number of candidates a selector can ever yield is
    /// derived from its query plus these caps (<c>TargetQuery.MaxCandidates</c>), and that number is
    /// what decides whether a card asks the player to choose. <c>Emberfall.Core.Cards</c> sits BELOW
    /// <c>Emberfall.Core</c> and cannot see <c>MatchEngine</c>, so the cap either lives here or gets
    /// written down twice.</para>
    ///
    /// <para><see cref="Emberfall.Core.MatchEngine"/> forwards to these; it does not restate them.
    /// There is no deck or graveyard entry on purpose — a deck is bounded by DECKBUILDING, which is
    /// not a runtime cap, and a graveyard only grows.</para>
    /// </summary>
    public static class ZoneCaps
    {
        /// <summary>Minions one player may have on the board at once.</summary>
        public const int Board = 7;

        /// <summary>Cards one player may hold at once.</summary>
        public const int Hand = 10;

        /// <summary>Heroes a player has. Named so the derivation reads as arithmetic, not as a literal.</summary>
        public const int Hero = 1;

        /// <summary>Weapons one player may have equipped at once.
        /// RETIREMENT TRIGGER: a DUAL-WIELD system raises this, and <c>TargetQuery.IsUnique</c>
        /// stops being true for the weapon zone at the same moment. Change both together.</summary>
        public const int Weapon = 1;
    }

    /// <summary>
    /// The single authoritative truth of a match (dev-docs/architecture/system-overview.md:36). A plain serializable
    /// object graph with NO UnityEngine deps and NO floats. It mutates ONLY through
    /// <see cref="ICommandProcessor.Execute"/>. Declaration order here <b>should</b> track the
    /// canonical walk in <c>Emberfall.Core.Serialization.GameStateSerializer</c>, but the serializer
    /// is AUTHORITATIVE (A2 ruling, dev-docs/architecture/determinism-and-projection.md:13) — adding/removing a field
    /// is a hash-canonical change: update the walk + manifest, convene §7, rebaseline fixtures.
    /// </summary>
    public sealed class GameState
    {
        public MatchId Id;
        public ulong StateVersion;          // bumps every applied command (delta/recon anchor)
        public DeterministicRandom Rng;     // seeded; the ONLY randomness source (§6)
        public PlayerId ActivePlayer;
        public TurnPhase Phase;
        public int TurnNumber;
        public PlayerState[] Players;       // [0],[1] — seat order is fixed and canonical
        public int FatigueCounter0;
        public int FatigueCounter1;
        public MatchOutcome Outcome;
        public int ActionResolutionsThisTurn;   // guardrails §4 loop backstop

        /// <summary>Monotonic per-match counter behind <see cref="InstanceId"/> assignment (deterministic creation order).</summary>
        public int NextInstanceCounter;
        /// <summary>Global event sequence cursor (dev-docs/architecture/determinism-and-projection.md:5).</summary>
        public ulong NextEventSequence;
        /// <summary>Per-player mulligan completion flags, seat-indexed.</summary>
        public bool Mulligan0Done;
        public bool Mulligan1Done;

        /// <summary>
        /// Resolved spell rules that continue observing future game events. Append order is creation
        /// order; <see cref="OngoingEffectInstance.Id"/> is the stable tie-breaker used by trigger walks.
        /// </summary>
        public List<OngoingEffectInstance> OngoingEffects = new List<OngoingEffectInstance>();

        /// <summary>
        /// Authoritative state for a pending player choice (currently Unearth). Carries serializable
        /// state only, never a continuation or delegate. <see cref="GameStateProjector"/> redacts
        /// choices per field for the chooser and opponent.
        /// </summary>
        public PendingChoice? PendingChoice;

        public GameState(MatchId id, DeterministicRandom rng, PlayerState p0, PlayerState p1)
        {
            Id = id;
            Rng = rng;
            Players = new[] { p0, p1 };
            Phase = TurnPhase.Mulligan;
            Outcome = MatchOutcome.InProgress;
            ActivePlayer = p0.Id;
            TurnNumber = 0;
            // D-P1 (dev-docs/architecture/determinism-and-projection.md:23): InstanceId(0) is the reserved redaction
            // sentinel and is never minted — the counter starts at 1.
            NextInstanceCounter = 1;
        }

        public int SeatOf(PlayerId player)
        {
            if (Players[0].Id == player) return 0;
            if (Players[1].Id == player) return 1;
            throw new System.ArgumentException($"Unknown player '{player}'.", nameof(player));
        }

        public PlayerState PlayerOf(PlayerId player) => Players[SeatOf(player)];

        public InstanceId NextInstanceId() => new InstanceId(NextInstanceCounter++);
    }

    /// <summary>Runtime lifetime carried by an ongoing effect, independent of its originating spell.</summary>
    public enum OngoingEffectLifetime
    {
        ONE_SHOT = 0,
        FINITE = 1,
        PERMANENT = 2,
    }

    /// <summary>
    /// One canonical, carrierless ongoing rule created by a resolved spell. Presentation may show
    /// every public field. The authored rule id is authority-only and is stripped by projection.
    /// </summary>
    public sealed class OngoingEffectInstance
    {
        public InstanceId Id;
        public PlayerId Controller;
        /// <summary>The permanent numeric id of the card this effect came from — hash-canonical.</summary>
        public int SourceDefinitionId;
        public OngoingEffectLifetime Lifetime;
        public int? RemainingUses;

        // Canonical authority state, intentionally absent from the public presentation contract.
        // Friend assemblies may walk it for runtime lookup and authority snapshot round-trips.
        internal string RuleId = "";

        public bool IsExpired => Lifetime != OngoingEffectLifetime.PERMANENT &&
            (!RemainingUses.HasValue || RemainingUses.Value <= 0);

        public OngoingEffectInstance(InstanceId id, PlayerId controller, int sourceDefinitionId,
            OngoingEffectLifetime lifetime, int? remainingUses)
        {
            // > 0 rather than a null check: card ids start at 1, so 0 is an unset field wearing
            // a value that looks set — the failure mode a string id could not have.
            if (sourceDefinitionId <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(sourceDefinitionId),
                    sourceDefinitionId, "An ongoing effect requires a source definition id.");
            if (!System.Enum.IsDefined(typeof(OngoingEffectLifetime), lifetime))
                throw new System.ArgumentOutOfRangeException(nameof(lifetime), lifetime,
                    "Unknown ongoing-effect lifetime.");
            if (lifetime == OngoingEffectLifetime.PERMANENT && remainingUses.HasValue)
                throw new System.ArgumentException("A permanent ongoing effect has no remaining-use count.",
                    nameof(remainingUses));
            if (lifetime != OngoingEffectLifetime.PERMANENT && !remainingUses.HasValue)
                throw new System.ArgumentException("A bounded ongoing effect requires a remaining-use count.",
                    nameof(remainingUses));
            if (remainingUses < 0)
                throw new System.ArgumentOutOfRangeException(nameof(remainingUses), remainingUses,
                    "Remaining uses cannot be negative.");

            Id = id;
            Controller = controller;
            SourceDefinitionId = sourceDefinitionId;
            Lifetime = lifetime;
            RemainingUses = remainingUses;
        }

        internal static OngoingEffectInstance CreateOneShot(InstanceId id, PlayerId controller,
            int sourceDefinitionId, string ruleId)
            => Create(id, controller, sourceDefinitionId, ruleId,
                OngoingEffectLifetime.ONE_SHOT, 1);

        internal static OngoingEffectInstance CreateFinite(InstanceId id, PlayerId controller,
            int sourceDefinitionId, string ruleId, int uses)
            => Create(id, controller, sourceDefinitionId, ruleId,
                OngoingEffectLifetime.FINITE, uses);

        internal static OngoingEffectInstance CreatePermanent(InstanceId id, PlayerId controller,
            int sourceDefinitionId, string ruleId)
            => Create(id, controller, sourceDefinitionId, ruleId,
                OngoingEffectLifetime.PERMANENT, null);

        internal static OngoingEffectInstance Create(InstanceId id, PlayerId controller,
            int sourceDefinitionId, string ruleId, OngoingEffectLifetime lifetime,
            int? remainingUses)
        {
            if (string.IsNullOrWhiteSpace(ruleId))
                throw new System.ArgumentException("An authoritative ongoing effect requires a rule id.",
                    nameof(ruleId));
            var instance = new OngoingEffectInstance(id, controller, sourceDefinitionId, lifetime,
                remainingUses)
            { RuleId = ruleId };
            return instance;
        }
    }

    /// <summary>One player's mutable per-match state. Zone lists are ordered (canonical, hashable).</summary>
    public sealed class PlayerState
    {
        public PlayerId Id;
        public HeroInstance Hero;
        public List<CardInstance> Deck = new List<CardInstance>();      // ordered; top = index 0 = next draw
        public List<CardInstance> Hand = new List<CardInstance>();      // ordered; cap 10 (overdraw burns)
        public List<CardInstance> Board = new List<CardInstance>();     // ordered slots; cap 7; index = position
        public CardInstance? Weapon;                                    // nullable; the equipped card in Zone.Weapon
        public List<CardInstance> Graveyard = new List<CardInstance>(); // for recursion cards
        public ManaPool Mana = new ManaPool();
        public bool HeroPowerUsedThisTurn;
        public int HeroAttacksUsedThisTurn;
        public int NextTurnBackdraftLock; // crystals to lock at next start-of-turn

        public PlayerState(PlayerId id, HeroInstance hero)
        {
            Id = id;
            Hero = hero;
        }
    }

    /// <summary>Hero character. 30 HP; armor reserved for the future.</summary>
    public sealed class HeroInstance : IEffectHost
    {
        public InstanceId Id;
        public PlayerId Controller;
        public CardClass Class;
        public int CurrentHealth;
        public int MaxHealth;
        public int Armor;
        public int HeroPowerId;
        /// <summary>EXPERIMENT (effect-model fork) — see <see cref="EffectInstance"/>.</summary>
        public List<EffectInstance> Effects = new List<EffectInstance>();
        /// <summary>Satisfies <see cref="IEffectHost"/> over the field above without changing how any
        /// existing caller touches <c>Effects</c> — see <c>EffectHost.cs</c>. <c>IsFrosted</c> and
        /// <c>FrostThawPending</c> moved off this class to <see cref="EffectHostExtensions"/>, written
        /// once for both hosts instead of once each.</summary>
        List<EffectInstance> IEffectHost.Effects => Effects;

        public HeroInstance(InstanceId id, PlayerId controller, CardClass @class, int maxHealth)
        {
            Id = id;
            Controller = controller;
            Class = @class;
            MaxHealth = maxHealth;
            CurrentHealth = maxHealth;
        }
    }

    /// <summary>
    /// Per-match mutable card state. Definitions are immutable; instances carry the runtime state
    /// (dev-docs/architecture/determinism-and-projection.md:10). All stats are <b>ints — never floats</b>.
    /// </summary>
    public sealed class CardInstance : IEffectHost
    {
        public InstanceId Id;
        public int DefinitionId;   // → ICardDatabase.Get(...)
        public PlayerId Controller;
        public Zone Zone;
        public int CurrentAttack;
        public int CurrentHealth;
        public int MaxHealth;
        public int? CurrentDurability;
        /// <summary>Base ∪ granted keywords. The one unordered collection in state — the hasher imposes
        /// a total order over it (sorted by enum int) so it cannot perturb the hash (dev-docs/architecture/determinism-and-projection.md:13).</summary>
        public HashSet<Keyword> Keywords = new HashSet<Keyword>();
        public List<Enchantment> Enchantments = new List<Enchantment>();
        public bool HasAegisShield;
        /// <summary>EXPERIMENT (effect-model fork) — see <see cref="EffectInstance"/>.</summary>
        public List<EffectInstance> Effects = new List<EffectInstance>();
        /// <summary>Satisfies <see cref="IEffectHost"/> over the field above without changing how any
        /// existing caller touches <c>Effects</c> — see <c>EffectHost.cs</c>. <c>IsFrosted</c> and
        /// <c>FrostThawPending</c> moved off this class to <see cref="EffectHostExtensions"/>, written
        /// once for both hosts instead of once each.</summary>
        List<EffectInstance> IEffectHost.Effects => Effects;
        /// <summary>True while this live permanent has had its printed and granted abilities removed.
        /// A zone-life reset clears this flag; it is canonical because it changes future resolution.</summary>
        public bool IsSilenced;
        public bool SummonedThisTurn;
        public int AttacksUsedThisTurn;
        public bool HasUsedRekindle;
        public int Position; // board slot

        public CardInstance(InstanceId id, int definitionId, PlayerId controller)
        {
            Id = id;
            DefinitionId = definitionId;
            Controller = controller;
        }
    }

    /// <summary>
    /// An authoritative interactive choice awaiting a player. Unearth populates this record;
    /// canonical serialization preserves option order, while <c>GameStateProjector</c> exposes the
    /// complete record only to <see cref="Chooser"/> and default-denies its private fields to the
    /// opponent. Walk order is fixed as declared.
    /// </summary>
    public sealed class PendingChoice
    {
        /// <summary>UNREDACTED — the visibility key; both seats see who is choosing.</summary>
        public PlayerId Chooser;
        /// <summary>UNREDACTED — opponent sees "opponent is Unearthing"; the trigger is already public.</summary>
        public PendingChoiceKind Kind;
        /// <summary>REDACTED (default-deny) — the already-minted instance/effect that raised the choice.</summary>
        public InstanceId Source;
        /// <summary>REDACTED — the hidden information. CardDefinition ids (candidates are never
        /// minted as instances). Phase-2 canonical population order = deterministic RNG roll order
        /// of the Unearth pool (OBL-3); no consumer may re-sort.</summary>
        public List<int> Options = new List<int>();
        /// <summary>REDACTED (default-deny; OBL-5 revisit). Deterministic resume/timer correlation id.</summary>
        public ulong ChoiceSequence;
    }

    /// <summary>Kinds of mid-command pending choice. Hash-canonical — extend only via a §7
    /// tail-append when a real mechanic lands (Unearth-only per the 2026-07-15 ruling, Q1).</summary>
    public enum PendingChoiceKind
    {
        // RETIREMENT TRIGGER: adding a second member here ends this enum's carve-out in
        // WireMutationSweepTests.Mutated_Field_Survives_The_Wire — see the identical note on
        // EffectKind. Drop "PendingChoice.Kind" from that branch in the same commit.
        Unearth = 0,
    }

    /// <summary>
    /// A buff/debuff (removed by SILENCE). <see cref="IsAura"/> is explicit provenance for the
    /// continuous layer: sources can leave the board and be recomputed without deleting an
    /// ordinary effect from the same card instance.
    /// </summary>
    public sealed class Enchantment
    {
        public InstanceId Source;
        public int AttackDelta;
        public int HealthDelta;
        public Keyword? GrantedKeyword;
        public bool IsAura;
    }

    /// <summary>
    /// Mana economy (GDD §1c, hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/game-state-and-actions.md §7). All ints. The four display states the
    /// D6 tray reads map onto these fields.
    /// </summary>
    public sealed class ManaPool
    {
        public int Max;        // crystals earned (cap 10)
        public int Available;  // currently spendable
        public int Spent;      // used this turn
        public int Locked;     // backdraft-locked this turn
        public int Temporary;  // one-turn extra crystals
    }
}
