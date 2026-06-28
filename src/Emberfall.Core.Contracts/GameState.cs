using System.Collections.Generic;

namespace Emberfall.Core.Contracts
{
    /// <summary>
    /// The single authoritative truth of a match (game-state-and-actions.md §2). A plain serializable
    /// object graph with NO UnityEngine deps and NO floats. It mutates ONLY through
    /// <see cref="ICommandProcessor.Execute"/>. Field <b>declaration order here is the canonical hash
    /// order</b> (golden-replay.md §4.1) — do not reorder without rebaselining every fixture.
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

        public GameState(MatchId id, DeterministicRandom rng, PlayerState p0, PlayerState p1)
        {
            Id = id;
            Rng = rng;
            Players = new[] { p0, p1 };
            Phase = TurnPhase.Mulligan;
            Outcome = MatchOutcome.InProgress;
            ActivePlayer = p0.Id;
            TurnNumber = 0;
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

    /// <summary>A buff/debuff (removed by SILENCE). Integer deltas only.</summary>
    public sealed class Enchantment
    {
        public InstanceId Source;
        public int AttackDelta;
        public int HealthDelta;
        public Keyword? GrantedKeyword;
    }

    /// <summary>Equipped weapon: attack + durability, no health.</summary>
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
