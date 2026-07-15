using System;

namespace Emberfall.Core.Contracts
{
    // ── Match / turn structure (game-state-and-actions.md §2) ────────────────────

    /// <summary>Turn-phase state machine (GDD §1b). Drives the core's small FSM.</summary>
    public enum TurnPhase { Mulligan, StartOfTurn, Main, EndOfTurn, GameOver }

    /// <summary>Terminal result of the match (game-state-and-actions.md §2).</summary>
    public enum MatchOutcome { InProgress, Player0Win, Player1Win, Draw }

    /// <summary>Where a card instance currently lives.</summary>
    public enum Zone { Deck, Hand, Board, Graveyard, Weapon }

    // ── Card taxonomy (card-schema.md §2–4) ──────────────────────────────────────

    public enum CardType { MINION, SPELL, WEAPON, HERO_POWER }

    /// <summary>Alpha ships 4 classes; last two reserved for M2 but enumerated now (card-schema.md §3).</summary>
    public enum CardClass { NEUTRAL, EMBERWARD, TIDECALLER, GRAVEKEEP, VERDANT }

    public enum Rarity { COMMON, RARE, EPIC, LEGENDARY }

    public enum SetId { CORE, EMBERFALL }

    public enum Tribe { NONE, BEAST, CONSTRUCT, UNDEAD, ELEMENTAL, DRAGON, SPIRIT }

    /// <summary>
    /// Canonical keyword enum (D3, card-schema.md §4 v1.3). Closed set — 17 keywords, each
    /// implemented. ECHO was formally cut by M2 decision 2 (zero implementation, zero cards);
    /// this is a hash-canonical change (GameStateHasher sorts keyword sets by enum int), executed
    /// with the golden-replay gate as witness per M2-KICKOFF.md tier b.
    /// </summary>
    public enum Keyword
    {
        TAUNT, CHARGE, RUSH, DIVINE_SHIELD, BATTLECRY, DEATHRATTLE, LIFESTEAL,
        WINDFURY, STEALTH, POISONOUS, SPELL_DAMAGE, SILENCE, FREEZE, OVERLOAD,
        REBORN,
        OVERWHELM, VANGUARD,   // GDD §1e v1.1 — combat resolver handles both (requiresEngineeringPrimitive lifted by step 2)
    }

    /// <summary>When an effect fires (card-schema.md §5.1).</summary>
    public enum Trigger
    {
        ON_PLAY, ON_DEATH, ON_ATTACK, ON_DAMAGED, ON_HEAL, ON_TURN_START,
        ON_TURN_END, ON_FRIENDLY_SUMMON, ON_SPELL_CAST, ON_CAST, AURA
    }

    /// <summary>The primitive verbs (card-schema.md §5.2). Closed; grows only via the requiresEngineeringPrimitive gate.</summary>
    public enum Action
    {
        DEAL_DAMAGE, HEAL, DRAW, SUMMON, BUFF, GIVE_KEYWORD, DESTROY, RETURN_TO_HAND,
        SILENCE, DISCOVER, TRANSFORM, EQUIP, GAIN_MANA, ADD_TO_HAND,
        FREEZE,   // step 4: sets IsFrozen on the target character (minion or hero)
        OVERLOAD, // step 4: locks N crystals at the start of the controller's next turn
    }

    /// <summary>The closed D7 VFX vocabulary (game-state-and-actions.md §5.2). Purely observational.</summary>
    public enum VfxEvent
    {
        onPlay, onSummon, onAttack, onDamage, onHeal, onDeath, onBuff, onDebuff,
        onDraw, onSilence, onFreeze, onTransform, onReturn, onManaGain, onEquip,
        onDiscover, onTurnStart, onTurnEnd, onHeroPower, onFatigue, onGameWin, onGameOver
    }

    /// <summary>Per-event flags carried on a <see cref="GameEvent"/>. M1-guaranteed: AegisAbsorbed, Lethal, Overdraw (interfaces.md §5).</summary>
    [Flags]
    public enum EventFlags
    {
        None = 0, AegisAbsorbed = 1, Lethal = 2, Overdraw = 4, IsToken = 8, Fatigue = 16
    }

    /// <summary>Why an intent was rejected. No mutation, no events on reject (game-state-and-actions.md §4).</summary>
    public enum RejectReason
    {
        NotYourTurn, WrongPhase, NotEnoughMana, IllegalTarget, NotInHand,
        BoardFull, HeroPowerAlreadyUsed, WeaponMissing, UnknownIntent,
        NotImplemented, AlreadyMulliganed, GameOver,
        HasSummoningSickness, NoAttacksLeft, AttackerFrozen,
    }

    // ── Validation (interfaces.md §4.1) ──────────────────────────────────────────

    /// <summary>Only <see cref="Error"/> blocks a bake; Warning/Info are advisory.</summary>
    public enum Severity { Error, Warning, Info }

    // NOTE: the former `AuraKind` enum was deleted here (M2, decisions/ADR-0006, condition C-G0-3).
    // It was dead code — no source consumer ever bound to it. Persistent aura state is presentation-
    // side `AuraSet` flags on ICardLiveState (interfaces.md §5.1), which is what shipped.
}
