using System.Collections.Generic;

namespace Emberfall.Core.Contracts
{
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
        /// The pending-choice seam (A2b, decision 3). ALWAYS null in M2 — the shape exists so
        /// Phase 2 adds interpreter machinery only, never a Contracts change. Carries serializable
        /// STATE only, never the continuation/delegate. Redaction contract for the future
        /// GameStateProjector is documented per-field on <see cref="PendingChoice"/> (§7 review
        /// 2026-07-15, hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a2b-scoped-review-2026-07-15.md — OBL-1).
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
    public sealed class HeroInstance
    {
        public InstanceId Id;
        public PlayerId Controller;
        public CardClass Class;
        public int CurrentHealth;
        public int MaxHealth;
        public int Armor;
        public string HeroPowerId = "";
        public bool IsFrosted; // set by FROST verb; cleared at start of controller's next turn

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
    public sealed class CardInstance
    {
        public InstanceId Id;
        public string DefinitionId = "";   // → ICardDatabase.Get(...)
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
        public bool IsFrosted;
        /// <summary>True while this live permanent has had its printed and granted abilities removed.
        /// A zone-life reset clears this flag; it is canonical because it changes future resolution.</summary>
        public bool IsSilenced;
        public bool SummonedThisTurn;
        public int AttacksUsedThisTurn;
        public bool HasUsedRekindle;
        public int Position; // board slot

        public CardInstance(InstanceId id, string definitionId, PlayerId controller)
        {
            Id = id;
            DefinitionId = definitionId;
            Controller = controller;
        }
    }

    /// <summary>
    /// A choice awaiting a player (A2b seam). M2: never populated. The projection redaction class
    /// per field below is fixed by the §7 ruling (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a2b-scoped-review-2026-07-15.md)
    /// and is the BINDING contract for the future GameStateProjector — it is NOT implemented here
    /// (no projection layer exists in src/ yet). Walk order is fixed as declared.
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
        public List<string> Options = new List<string>();
        /// <summary>REDACTED (default-deny; OBL-5 revisit). Deterministic resume/timer correlation id.</summary>
        public ulong ChoiceSequence;
    }

    /// <summary>Kinds of mid-command pending choice. Hash-canonical — extend only via a §7
    /// tail-append when a real mechanic lands (Unearth-only per the 2026-07-15 ruling, Q1).</summary>
    public enum PendingChoiceKind
    {
        Unearth = 0,
    }

    /// <summary>A buff/debuff (removed by SILENCE). Integer deltas only.</summary>
    public sealed class Enchantment
    {
        public InstanceId Source;
        public int AttackDelta;
        public int HealthDelta;
        public Keyword? GrantedKeyword;
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
