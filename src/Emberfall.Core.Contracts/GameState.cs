using System.Collections.Generic;

namespace Emberfall.Core.Contracts
{
    /// <summary>
    /// The single authoritative truth of a match (game-state-and-actions.md §2). A plain serializable
    /// object graph with NO UnityEngine deps and NO floats. It mutates ONLY through
    /// <see cref="ICommandProcessor.Execute"/>. Declaration order here <b>should</b> track the
    /// canonical walk in <c>Emberfall.Core.Serialization.GameStateSerializer</c>, but the serializer
    /// is AUTHORITATIVE (A2 ruling, engineering/a2-serializer-design.md §2) — adding/removing a field
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
        /// <summary>Global event sequence cursor (game-state-and-actions.md §5.2).</summary>
        public ulong NextEventSequence;
        /// <summary>Per-player mulligan completion flags, seat-indexed.</summary>
        public bool Mulligan0Done;
        public bool Mulligan1Done;

        /// <summary>
        /// The pending-choice seam (A2b, decision 3). ALWAYS null in M2 — the shape exists so
        /// Phase 2 adds interpreter machinery only, never a Contracts change. Carries serializable
        /// STATE only, never the continuation/delegate. Redaction contract for the future
        /// GameStateProjector is documented per-field on <see cref="PendingChoice"/> (§7 review
        /// 2026-07-15, engineering/a2b-scoped-review-2026-07-15.md — OBL-1).
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
            // D-P1 (state-sync-and-security.md §5.2.1): InstanceId(0) is the reserved redaction
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
        public WeaponInstance? Weapon;                                  // nullable; durability
        public List<CardInstance> Graveyard = new List<CardInstance>(); // for recursion cards
        public ManaPool Mana = new ManaPool();
        public bool HeroPowerUsedThisTurn;
        public int HeroAttacksUsedThisTurn;
        public int NextTurnOverloadLock; // crystals to lock at next start-of-turn

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
        public bool IsFrozen; // set by FREEZE verb; cleared at start of controller's next turn

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
    /// (game-state-and-actions.md §1, principle 3). All stats are <b>ints — never floats</b>.
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
        /// a total order over it (sorted by enum int) so it cannot perturb the hash (golden-replay.md §4.1).</summary>
        public HashSet<Keyword> Keywords = new HashSet<Keyword>();
        public List<Enchantment> Enchantments = new List<Enchantment>();
        public bool HasAegisShield;
        public bool IsFrozen;
        public bool SummonedThisTurn;
        public int AttacksUsedThisTurn;
        public bool HasUsedReborn;
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
    /// per field below is fixed by the §7 ruling (engineering/a2b-scoped-review-2026-07-15.md)
    /// and is the BINDING contract for the future GameStateProjector — it is NOT implemented here
    /// (no projection layer exists in src/ yet). Walk order is fixed as declared.
    /// </summary>
    public sealed class PendingChoice
    {
        /// <summary>UNREDACTED — the visibility key; both seats see who is choosing.</summary>
        public PlayerId Chooser;
        /// <summary>UNREDACTED — opponent sees "opponent is Discovering"; the trigger is already public.</summary>
        public PendingChoiceKind Kind;
        /// <summary>REDACTED (default-deny) — the already-minted instance/effect that raised the choice.</summary>
        public InstanceId Source;
        /// <summary>REDACTED — the hidden information. CardDefinition ids (candidates are never
        /// minted as instances). Phase-2 canonical population order = deterministic RNG roll order
        /// of the Discover pool (OBL-3); no consumer may re-sort.</summary>
        public List<string> Options = new List<string>();
        /// <summary>REDACTED (default-deny; OBL-5 revisit). Deterministic resume/timer correlation id.</summary>
        public ulong ChoiceSequence;
    }

    /// <summary>Kinds of mid-command pending choice. Hash-canonical — extend only via a §7
    /// tail-append when a real mechanic lands (Discover-only per the 2026-07-15 ruling, Q1).</summary>
    public enum PendingChoiceKind
    {
        Discover = 0,
    }

    /// <summary>A buff/debuff (removed by SILENCE). Integer deltas only.</summary>
    public sealed class Enchantment
    {
        public InstanceId Source;
        public int AttackDelta;
        public int HealthDelta;
        public Keyword? GrantedKeyword;
    }

    /// <summary>Equipped weapon: attack + durability, no health.</summary>
    /// <summary>An equipped weapon. Stats only — there is deliberately NO keyword field, which is
    /// why no weapon can carry Lifedrain, Venom or Frost as an on-hit rider: a hero attacking hands
    /// DamagePipeline a freshly allocated EMPTY keyword set, and there is nothing here to fill it
    /// from. CLAUDE.md documents this as a constraint on card authoring.
    ///
    /// <para><b>RETIREMENT TRIGGER — you are the person this comment was left for.</b> Adding a
    /// field to this type is a HASH-CANONICAL change, not a small patch, because WeaponInstance is
    /// game state. The same commit must touch all of: GameStateSerializer (the authoritative field
    /// order, WalkWeapon), CanonicalStateFixtures (BOTH the Manifest dict and a Mutations entry),
    /// SnapshotJsonCodec (the A5 wire walk), CanonicalSerializationTests.PinnedSeedDigest, the three
    /// golden fixtures, and the CONTENT of their paired .rebaseline.md files — the harness will not
    /// rewrite those for you, and tools/ci/rebaseline-guard.sh fails the diff if you skip them.
    /// It must also update CLAUDE.md's "What a weapon can and cannot do", which becomes false.</para>
    /// </summary>
    public sealed class WeaponInstance
    {
        public InstanceId Id;
        public string DefinitionId = "";
        public int Attack;
        public int Durability;
    }

    /// <summary>
    /// Mana economy (GDD §1c, game-state-and-actions.md §7). All ints. The four display states the
    /// D6 tray reads map onto these fields.
    /// </summary>
    public sealed class ManaPool
    {
        public int Max;        // crystals earned (cap 10)
        public int Available;  // currently spendable
        public int Spent;      // used this turn
        public int Locked;     // overload-locked this turn
        public int Temporary;  // one-turn extra crystals
    }
}
