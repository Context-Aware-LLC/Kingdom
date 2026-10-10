using System;

namespace Emberfall.Core.Contracts
{
    // ── Match / turn structure (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/game-state-and-actions.md §2) ────────────────────

    /// <summary>Turn-phase state machine (GDD §1b). Drives the core's small FSM.</summary>
    public enum TurnPhase { Mulligan, StartOfTurn, Main, EndOfTurn, GameOver }

    /// <summary>Terminal result of the match (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/game-state-and-actions.md §2).</summary>
    public enum MatchOutcome { InProgress, Player0Win, Player1Win, Draw }

    /// <summary>Where a card instance currently lives.</summary>
    public enum Zone { Deck, Hand, Board, Graveyard, Weapon }

    // ── Card taxonomy (dev-docs/specs/card-schema.md §2–4) ──────────────────────────────────────

    public enum CardType { MINION, SPELL, WEAPON, HERO_POWER }

    /// <summary>Alpha ships 4 classes; M2 adds 4 more (dev-docs/specs/card-schema.md §3).</summary>
    public enum CardClass { NEUTRAL, EMBERWARD, TIDECALLER, GRAVEKEEP, VERDANT, CLOCKWRIGHT, NIGHTVEIL, STONEWARDEN, TEMPEST }

    public enum Rarity { COMMON, RARE, EPIC, LEGENDARY }

    /// <summary>
    /// <b><see cref="CORE"/> is not a set a player can see or open</b> (Sam, 2026-10-10). It is the
    /// home for things that take the form of a card without being a collectible one: the hero powers
    /// and the coin. Nothing in it is collectible, so never describe it to a player as a set.
    /// </summary>
    public enum SetId { CORE, EMBERFALL, REKINDLING }

    /// <summary>
    /// Where a card row stands in its life (Sam, 2026-09-29, issue #398 — one field covering the
    /// draft state and #86's retired state). Not game state: no serializer walks it, so it moves no
    /// hash. PUBLISHED is ordinal 0 so that an absent field, and every row written before the
    /// field existed, means PUBLISHED without a migration.
    /// <list type="bullet">
    /// <item><b>PUBLISHED</b> — live: eligible for packs, deck-building, bot decks.</item>
    /// <item><b>DRAFT</b> — authored ahead of release (the next set, written straight into the
    /// card JSON). Resolvable, never in a live pool, never in a deck, never in training data.</item>
    /// <item><b>RETIRED</b> — #86: an id is permanent, so a card leaves play by retirement, not
    /// deletion. Non-collectible, undraftable, still resolvable, so an old deck, a replay or an
    /// ownership record keeps meaning one card.</item>
    /// </list>
    /// </summary>
    public enum CardLifecycle { PUBLISHED, DRAFT, RETIRED }

    /// <summary>
    /// Card tribes. HUMAN is appended rather than inserted: it arrived with main's tribe pass and
    /// appending keeps every existing ordinal where it was. Tribe is a CardDefinition field and
    /// reaches no canonical state — GameStateSerializer never walks it — so unlike Keyword this
    /// enum is not hash-canonical and carries no rebaseline cost.
    /// </summary>
    public enum Tribe
    {
        NONE, BEAST, CONSTRUCT, UNDEAD, ELEMENTAL, DRAGON, SPIRIT, HUMAN, FEY, MERFOLK, CATFOLK
    }

    /// <summary>
    /// Canonical keyword wire enum. Trigger labels such as Herald and Toll are derived from normalized
    /// rule activation and deliberately have no enum member.
    ///
    /// <para>SILENCE, BACKDRAFT, and DROWN are not carrier behavior and must never enter
    /// keyword state — SILENCE, BACKDRAFT and DROWN because typed actions derive their markers.
    /// Numeric values are explicit so a member's wire spelling cannot drift.</para>
    ///
    /// <para><b>Do not renumber in this fork.</b> The hasher sorts keyword sets by enum int and writes
    /// that int into the digest, so changing a value moves every digest holding more than one keyword.
    /// Tidying the dead slots away is scheduled POST-MIGRATION, in the main project, where the
    /// golden-replay gate can witness the rebaseline — that gate does not run here. See the migration
    /// document (Sam, 2026-08-20).</para>
    /// </summary>
    public enum Keyword
    {
        BULWARK = 0, FERVOR = 1, SKIRMISH = 2, AEGIS = 3,
        LIFEDRAIN = 6, DOUBLESTRIKE = 7, SHROUD = 8, VENOM = 9,
        ARCANE_SURGE = 10, SILENCE = 11, FROST = 12, BACKDRAFT = 13,
        REKINDLE = 14,
        OVERWHELM = 15, VANGUARD = 16, // GDD §1e v1.1 — combat resolver handles both
        REINFORCE = 17,
        DROWN = 18,
    }

    /// <summary>When an effect fires (dev-docs/specs/card-schema.md §5.1).
    ///
    /// <para>AURA is live as a continuous board-minion recompute, not an event raise. Its narrow
    /// capability relation lives in <c>AuraCapabilities</c>; all other source/action/target shapes fail
    /// closed. The markers below are the guard; `dev-docs/specs/card-schema.md` ("Hazards") repeats
    /// them for card authors.</para>
    ///
    /// <para><b>RETIREMENT TRIGGER — you are the person this comment was left for.</b> Every member
    /// below is currently LIVE for at least one source kind, so this fires for a trigger you ADD.
    /// Adding one — or adding the first `RaiseSelf`/`Raise` site for a member you marked DEAD —
    /// must, in that same change: (1) mark its liveness here, (2) table its exact source/trigger
    /// pairs in `EffectSourceCapabilities`, since omitting it is the only thing that makes
    /// `C21-TRIGGER-DEAD` reachable and that branch cannot fire today, and (3) update the
    /// "Hazards" section of `dev-docs/specs/card-schema.md`, which states that unreachability as a
    /// fact (it lived in `CLAUDE.md` until 2026-08-22).
    /// Do all three in the commit that adds it — nobody will come back for them later.</para>
    ///
    /// <para>Order is load-bearing: these are serialized by name, but do not reorder casually.</para>
    /// </summary>
    public enum Trigger
    {
        // LIVE — CommandProcessor.
        ON_PLAY,

        // LIVE on BOTH card types. Minions: EffectInterpreter.DeathCheck (the toll cascade).
        // Weapons: RulesEngine.BreakWeapon, on durability exhaustion AND on equip-replacement (EF-020).
        // A weapon's rattle ENQUEUES like a minion's, so on a replacement the NEW weapon is already
        // equipped when it resolves — an ON_DEATH that equips will break the replacement, not itself.
        // THIRD raise site, and the only one where nothing died: ActionHandlers.TriggerTollHandler
        // rings a LIVING board minion's toll for the TRIGGER_TOLL action. So an ON_DEATH rule can
        // now run with its carrier still on the board — do not read "this rule is resolving" as
        // "this minion is in the graveyard" at any site reachable from one.
        ON_DEATH,

        // LIVE ON EQUIPPED WEAPONS AND BOARD MINIONS (Sam, 2026-09-05) — CommandProcessor.ApplyAttack
        // raises it via RaiseSelf on whichever carrier is attacking, and fires it with an EMPTY
        // target list either way — so "the minion this hits" is not addressable. Dead on SPELL and
        // HERO_POWER: neither ever attacks.
        ON_ATTACK,

        // LIVE — DamagePipeline raises this for minions that actually take damage (not Aegis absorbs).
        ON_DAMAGED,

        // LIVE FOR MINIONS — DamagePipeline.ApplyHeal raises it on the character whose health was
        // actually restored (EF-015). SELF trigger, mirroring ON_DAMAGED: "whenever THIS minion is
        // healed", never "whenever anything is healed". Two silences are deliberate and permanent:
        // an overheal raises nothing (nothing was restored), and a HERO heal raises nothing (heroes
        // carry no card effects) — which is why the reentrant LIFEDRAIN heal inside ApplyDamage,
        // whose target is always a hero, fires this on nobody. Dead on SPELL/WEAPON: neither has
        // health to restore, so the minion-only classification here is not a staging step.
        ON_HEAL,

        // LIVE FOR BOARD MINIONS, EQUIPPED WEAPONS, AND ONGOING EFFECTS —
        // CommandProcessor.StartTurnSequence raises it after the thaw/mana/attack reset and before
        // the draw (EF-011 / NOR-31). Every rule declares a TURN_OWNER condition so controller,
        // opponent, and any-turn behavior are explicit. Resolved spell observers participate through
        // their canonical OngoingEffectInstance rather than through a card zone.
        ON_TURN_START,

        // LIVE — CommandProcessor.ApplyEndTurn raises it after the TurnEnded fact and before the
        // seat flip (EF-012). Fires for BOTH players' permanents, not just the active player's.
        ON_TURN_END,

        // LIVE — ActionHandlers.SummonHandler (tokens) + CommandProcessor.ApplyPlayCard's MINION arm.
        // CONTROLLER-SCOPED, and the only observer trigger that is: it fires on the SUMMONER'S
        // permanents only, so "Friendly" means what it says. That is a ruling (#272, Sam,
        // 2026-09-04) and not the shape of the sweep — it was both-sided by inheritance until then,
        // which made 1159 Cogsworth's text false and made a TRIGGER_SOURCE effect under it buff the
        // OPPONENT. Enforced in one place, OrderedTriggerSources.IsControllerScoped; both raise
        // sites must name the controller and RaiseObservers throws if one does not.
        ON_FRIENDLY_SUMMON,

        // LIVE — CommandProcessor.ApplyPlayCard raises it from the CardType.SPELL arm only, right after
        // the spell's own ON_CAST (EF-016). Observer trigger: fires on BOTH players' permanents, so it
        // means "whenever ANY spell is cast", not "whenever YOU cast a spell".
        // TRAP, still true: hero powers also raise ON_CAST through a carrierless HERO_POWER context, and this
        // trigger is deliberately NOT raised there — a hero power is not a spell cast.
        ON_SPELL_CAST,

        // LIVE — CommandProcessor, for spells AND hero powers.
        ON_CAST,

        // LIVE AS CONTINUOUS RECOMPUTATION, not as an emitted event. AuraEngine removes prior
        // aura-derived enchantments and reapplies live, unsilenced board-minion sources after board
        // topology, silence, transform, and control changes. AuraCapabilities owns the deliberately
        // narrow fail-closed source/condition/lifetime/action/target relation.
        AURA
    }

    /// <summary>Which of the controller's own characters a play's chosen target turned out to be.
    /// Read by <c>TARGET_IS_FRIENDLY</c>.</summary>
    public enum FriendlyTargetKind
    {
        MINION,
        HERO,
    }

    /// <summary>How an effect source relates to the player whose turn is beginning.</summary>
    public enum TurnOwner
    {
        CONTROLLER,
        OPPONENT,
        ANY,
    }

    /// <summary>
    /// Stable semantic facts emitted by the deterministic game engine. These describe what happened;
    /// presentation is selected separately through <see cref="PresentationCue"/>.
    /// </summary>
    public enum GameEventKind
    {
        CardPlayed,
        CardSummoned,
        AttackDeclared,
        DamageApplied,
        HealthRestored,
        CardDestroyed,
        StatsModified,
        KeywordApplied,
        CardDrawn,
        CardCreatedInHand,
        CardSilenced,
        FrostApplied,
        CardTransformed,
        CardBounced,
        ManaGained,
        WeaponEquipped,
        UnearthOffered,
        TurnStarted,
        TurnEnded,
        HeroPowerUsed,
        FatigueDamage,
        MatchWon,
        MatchEnded,
        ArmorGained,
        ControlChanged,
        OngoingEffectCreated,
        OngoingEffectTriggered,
        OngoingEffectExpired,
    }

    /// <summary>Optional presentation instruction carried independently of semantic event identity.</summary>
    public enum PresentationCue
    {
        Play,
        Summon,
        Attack,
        Damage,
        Heal,
        Death,
        Buff,
        Draw,
        Silence,
        Frost,
        Transform,
        Return,
        ManaGain,
        Equip,
        Unearth,
        TurnStart,
        TurnEnd,
        HeroPower,
        Fatigue,
        GameWin,
        GameOver,
        ArmorGain,
        ControlChange,
        OngoingEffectCreate,
        OngoingEffectTrigger,
        OngoingEffectExpire,
    }

    /// <summary>Per-event flags carried on a <see cref="GameEvent"/>. M1-guaranteed: AegisAbsorbed, Lethal, Overdraw (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §5).</summary>
    [Flags]
    public enum EventFlags
    {
        None = 0, AegisAbsorbed = 1, Lethal = 2, Overdraw = 4, IsToken = 8, Fatigue = 16
    }

    /// <summary>Why an intent was rejected. No mutation, no events on reject (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/game-state-and-actions.md §4).</summary>
    public enum RejectReason
    {
        NotYourTurn, WrongPhase, NotEnoughMana, IllegalTarget, NotInHand,
        BoardFull, HeroPowerAlreadyUsed, WeaponMissing, UnknownIntent,
        NotImplemented, AlreadyMulliganed, GameOver,
        HasSummoningSickness, NoAttacksLeft, AttackerFrosted,
        ChoicePending, NoPendingChoice, NotChoiceOwner, StaleChoice, InvalidChoice,
        /// <summary>Session-layer only (B1 policy gate, dev-docs/contracts/wire-protocol.md:37):
        /// the token bucket refused the intent. Never minted by <c>CommandProcessor</c>.</summary>
        RateLimited,
    }

    // ── Validation (dev-docs/specs/card-schema.md:63) ──────────────────────────────────────────

    /// <summary>Only <see cref="Error"/> blocks a bake; Warning/Info are advisory.</summary>
    public enum Severity { Error, Warning, Info }

    // Persistent aura state is presentation-side `AuraSet` flags on ICardLiveState
    // (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §5.1),
    // not an enum in this file.
}
