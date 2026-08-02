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

    public enum SetId { CORE, EMBERFALL, REKINDLING }

    public enum Tribe { NONE, BEAST, CONSTRUCT, UNDEAD, ELEMENTAL, DRAGON, SPIRIT }

    /// <summary>
    /// Canonical keyword enum (D3, dev-docs/specs/card-schema.md §4 v1.3). Closed set — 17 keywords, each
    /// implemented. ECHO was formally cut by M2 decision 2 (zero implementation, zero cards);
    /// this is a hash-canonical change (GameStateHasher sorts keyword sets by enum int), executed
    /// with the golden-replay gate as witness per hist-docs/source-snapshots/2026-07-31-pre-reorganization/project/M2-KICKOFF.md tier b.
    /// </summary>
    public enum Keyword
    {
        TAUNT, CHARGE, RUSH, DIVINE_SHIELD, BATTLECRY, DEATHRATTLE, LIFESTEAL,
        WINDFURY, STEALTH, POISONOUS, SPELL_DAMAGE, SILENCE, FREEZE, OVERLOAD,
        REBORN,
        OVERWHELM, VANGUARD,   // GDD §1e v1.1 — combat resolver handles both (requiresEngineeringPrimitive lifted by step 2)
        REINFORCE,             // REKINDLING set: Battlecry — Give a friendly minion +1/+1.
    }

    /// <summary>When an effect fires (dev-docs/specs/card-schema.md §5.1).
    ///
    /// <para>DECLARED IS NOT LIVE. Six of these have no raise site anywhere in the engine — a card
    /// using one parses, validates, loads and then does nothing, forever, silently, with no error.
    /// The markers below are the guard, and `CLAUDE.md` repeats them for card authors.</para>
    ///
    /// <para><b>RETIREMENT TRIGGER — you are the person this comment was left for.</b> If you are
    /// adding the first `RaiseSelf`/`Raise` site for a DEAD trigger below, that same change must:
    /// (1) flip its marker here to LIVE, (2) remove it from the NO-GO list in `CLAUDE.md`, and
    /// (3) add it to the live-trigger table in `CardValidator` (rule C21). Do all three in the
    /// commit that adds the raise site — nobody will come back for them later.</para>
    ///
    /// <para>Order is load-bearing: these are serialized by name, but do not reorder casually.</para>
    /// </summary>
    public enum Trigger
    {
        // LIVE — CommandProcessor.
        ON_PLAY,

        // LIVE on BOTH card types. Minions: EffectInterpreter.DeathCheck (the deathrattle cascade).
        // Weapons: RulesEngine.BreakWeapon, on durability exhaustion AND on equip-replacement (EF-020).
        // A weapon's rattle ENQUEUES like a minion's, so on a replacement the NEW weapon is already
        // equipped when it resolves — an ON_DEATH that equips will break the replacement, not itself.
        ON_DEATH,

        // LIVE, BUT WEAPONS ONLY. CommandProcessor gates the raise on
        // `attacker.IsHero && ps.Weapon != null`, and fires it with an EMPTY target list — so
        // "the minion this hits" is not addressable. On a MINION this trigger is dead.
        ON_ATTACK,

        // LIVE — DamagePipeline raises this for minions that actually take damage (not Aegis absorbs).
        ON_DAMAGED,

        // DEAD — no raise site. Intended site: the DamagePipeline heal path. Note it would fire
        // reentrantly inside ApplyDamage via LIFESTEAL.
        ON_HEAL,

        // DEAD — no raise site. Intended site: CommandProcessor.StartTurnSequence, before the draw.
        ON_TURN_START,

        // LIVE — CommandProcessor.ApplyEndTurn raises it after the onTurnEnd VFX emit and before the
        // seat flip (EF-012). Fires for BOTH players' permanents, not just the active player's.
        ON_TURN_END,

        // LIVE — ActionHandlers + CommandProcessor.
        ON_FRIENDLY_SUMMON,

        // LIVE — CommandProcessor.ApplyPlayCard raises it from the CardType.SPELL arm only, right after
        // the spell's own ON_CAST (EF-016). Observer trigger: fires on BOTH players' permanents, so it
        // means "whenever ANY spell is cast", not "whenever YOU cast a spell".
        // TRAP, still true: hero powers also raise ON_CAST through an ephemeral HERO_POWER card, and this
        // trigger is deliberately NOT raised there — a hero power is not a spell cast.
        ON_SPELL_CAST,

        // LIVE — CommandProcessor, for spells AND hero powers.
        ON_CAST,

        // DEAD, and not an event at all: it needs a derived-stat recompute pass, which does not
        // exist anywhere in src/. BuffHandler bakes stats destructively and the Enchantment it
        // writes is never read back, so an aura source leaving the board cannot unapply itself.
        AURA
    }

    /// <summary>The primitive verbs (dev-docs/specs/card-schema.md §5.2). Closed; grows only via the requiresEngineeringPrimitive gate.</summary>
    public enum Action
    {
        DEAL_DAMAGE, HEAL, DRAW, SUMMON, BUFF, GIVE_KEYWORD, DESTROY, RETURN_TO_HAND,
        SILENCE, DISCOVER, TRANSFORM, EQUIP, GAIN_MANA, ADD_TO_HAND,
        FREEZE,   // step 4: sets IsFrozen on the target character (minion or hero)
        OVERLOAD, // step 4: locks N crystals at the start of the controller's next turn
    }

    /// <summary>The closed D7 VFX vocabulary (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/game-state-and-actions.md §5.2). Purely observational.</summary>
    public enum VfxEvent
    {
        onPlay, onSummon, onAttack, onDamage, onHeal, onDeath, onBuff, onDebuff,
        onDraw, onSilence, onFreeze, onTransform, onReturn, onManaGain, onEquip,
        onDiscover, onTurnStart, onTurnEnd, onHeroPower, onFatigue, onGameWin, onGameOver
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
        HasSummoningSickness, NoAttacksLeft, AttackerFrozen,
        /// <summary>Session-layer only (B1 policy gate, dev-docs/contracts/wire-protocol.md:37):
        /// the token bucket refused the intent. Never minted by <c>CommandProcessor</c>.</summary>
        RateLimited,
    }

    // ── Validation (dev-docs/specs/card-schema.md:63) ──────────────────────────────────────────

    /// <summary>Only <see cref="Error"/> blocks a bake; Warning/Info are advisory.</summary>
    public enum Severity { Error, Warning, Info }

    // NOTE: the former `AuraKind` enum was deleted here (M2, decisions/ADR-0006, condition C-G0-3).
    // It was dead code — no source consumer ever bound to it. Persistent aura state is presentation-
    // side `AuraSet` flags on ICardLiveState (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §5.1), which is what shipped.
}
