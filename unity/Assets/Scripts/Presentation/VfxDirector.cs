using System;
using System.Collections;
using System.Collections.Generic;
using Kingdom.Core.Contracts;
using Kingdom.Session;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// Subscribes to the engine's semantic event stream and plays one-shot particle effects per
    /// presentation cue. Each cue maps to a prefab assigned in the Inspector;
    /// null entries are silently skipped (VFX asset not yet authored). Uses Instantiate + timed
    /// Destroy for M1; replace with ObjectPool<T> in M2 (hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §3).
    ///
    /// Persistent auras (Shield / Bulwark / Frosted tinting) are handled by CardPresenter.ApplyAuras()
    /// via ICardLiveState.Changed — no per-frame event required.
    ///
    /// Phase C step 5 scene setup: add VfxDirector MonoBehaviour to the bootstrap GameObject
    /// and assign boardAnchors + any prefabs you have in the Inspector. Null prefab = no VFX.
    /// </summary>
    public sealed class VfxDirector : MonoBehaviour
    {
        [Header("Presentation Cue → Prefab table")]
        [SerializeField] private VfxCueEntry[] table = Array.Empty<VfxCueEntry>();

        [Header("Board anchors for world-position resolution")]
        [SerializeField] private BoardAnchors boardAnchors;

        [Header("Play reveal — every played card flies out, holds up to be read, and lands")]
        [Tooltip("A cast spell resolves into the graveyard, which has no anchor and would read as " +
                 "nothing — so its fly-out collapses it into whatever it hit (or the middle of the " +
                 "board), arriving as the effect lands.")]
        [SerializeField] private RevealTiming spellBeat = new RevealTiming(0.12f, 0.70f, 0.16f);
        [Tooltip("Flies up out of the hand slot it was played from, holds up to be read, then " +
                 "floats down into its board slot — landing exactly as the board redraws with the " +
                 "real card.")]
        [SerializeField] private RevealTiming minionBeat = new RevealTiming(0.12f, 0.55f, 0.18f);
        [Tooltip("Same three-phase flight as a minion, landing in the weapon socket.")]
        [SerializeField] private RevealTiming weaponBeat = new RevealTiming(0.12f, 0.60f, 0.18f);
        [Tooltip("Height of the held card as a fraction of the SCREEN's height — the size it is " +
                 "read at. See TryHeldPose for why this is a screen fraction and not a card multiple.")]
        [SerializeField, Range(0.3f, 0.9f)] private float heldScreenHeight = 0.6f;

        [Header("Hero power cast")]
        [Tooltip("Seconds the cast effect takes to rise up the caster's portrait.")]
        [SerializeField, Range(0.1f, 1f)] private float heroCastSeconds = 0.35f;
        [Tooltip("Seconds from the first spark off the socket to the strike.")]
        [SerializeField, Range(0.1f, 1f)] private float heroPowerLeadSeconds = 0.24f;

        /// <summary>How far in front of everything the reveal card rides while it is in flight, so
        /// it never clips into the hand fan it is leaving or the board row it is joining.</summary>
        private const float RevealZBias = -0.2f;

        /// <summary>The held card sits this fraction of the way from the camera to the point it is
        /// revealed over. Card materials are opaque, so DEPTH — not sorting order — is what puts it
        /// in front; the front-most board furniture is at z -0.75, some twenty units out, and
        /// halfway clears it by ten.</summary>
        private const float HeldDepthFraction = 0.5f;

        /// <summary>Viewport margin the held card keeps from the screen edge when it is pulled
        /// toward a target near one.</summary>
        private const float HeldScreenMargin = 0.03f;

        /// <summary>A card with nowhere to land (a spell) shrinks to this and is gone as the board
        /// redraws: it reads as cast INTO its target, and never sits over the effect it caused.</summary>
        private const float CollapseScale = 0.15f;

        /// <summary>Above the hand fan (50, +20 lifted), below the inspect panel (90) and the
        /// match-end banner (100).</summary>
        private const int RevealSortOrder = 80;

        private readonly Dictionary<PresentationCue, ParticleSystem> _lookup = new();
        private GameState _state;
        private IArtProvider _artProvider;
        private BoardController _board;
        private IUiSpriteProvider _uiSprites;

        /// <summary>The bolt leaves the socket when the cast is this far up the portrait, so the
        /// two overlap instead of queueing.</summary>
        private const float BoltLeavesAtCast = 0.6f;

        /// <summary>How long a hero power entry is held for its cast. Read by
        /// NetworkedGameBootstrap when it builds the classifier delegate, like
        /// <see cref="RevealHolds"/>.</summary>
        public HeroPowerHolds HeroPowerHolds =>
            new HeroPowerHolds(heroCastSeconds,
                               heroCastSeconds * BoltLeavesAtCast + heroPowerLeadSeconds,
                               heroCastSeconds * BoltLeavesAtCast + heroPowerLeadSeconds,
                               heroCastSeconds * BoltLeavesAtCast + heroPowerLeadSeconds,
                               PortraitCastEffect.TotalSeconds(NightveilBackstabShadow.PortraitWipeSeconds)
                                   + NightveilBackstabShadow.RiseSeconds,
                               1.35f,
                               PortraitCastEffect.TotalSeconds(heroCastSeconds)
                                   + ClockwrightBlueprintEffect.GatherSeconds);

        /// <summary>The tunables MatchRunner needs to know about — how long each card type's
        /// reveal holds up the rest of the command. Read by NetworkedGameBootstrap when it builds the
        /// classifier delegate, so this stays the single source for the numbers (Sam, 2026-08-01;
        /// per-type 2026-08-18).</summary>
        public PlayRevealHolds RevealHolds =>
            new PlayRevealHolds(minionBeat.TotalHoldSeconds,
                                spellBeat.TotalHoldSeconds,
                                weaponBeat.TotalHoldSeconds,
                                new SchoolTiming(SpellSchoolEffects.EarthLeadSeconds,
                                                 SpellSchoolEffects.EarthStaggerSeconds),
                                new SchoolTiming(SpellSchoolEffects.LightningLeadSeconds,
                                                 SpellSchoolEffects.LightningStaggerSeconds),
                                new SchoolTiming(SpellSchoolEffects.FireLeadSeconds,
                                                 SpellSchoolEffects.FireStaggerSeconds),
                                new SchoolTiming(SpellSchoolEffects.ShadowLeadSeconds,
                                                 SpellSchoolEffects.ShadowStaggerSeconds),
                                new SchoolTiming(SpellSchoolEffects.ClockwrightLeadSeconds,
                                                 SpellSchoolEffects.ClockwrightStaggerSeconds),
                                new SchoolTiming(SpellSchoolEffects.WaterLeadSeconds,
                                                 SpellSchoolEffects.WaterStaggerSeconds),
                                new SchoolTiming(SpellSchoolEffects.FrostLeadSeconds,
                                                 SpellSchoolEffects.FrostStaggerSeconds),
                                new SchoolTiming(SpellSchoolEffects.GravekeepLeadSeconds,
                                                 SpellSchoolEffects.GravekeepStaggerSeconds),
                                new SchoolTiming(SpellSchoolEffects.HealLeadSeconds,
                                                 SpellSchoolEffects.HealStaggerSeconds));

        private RevealTiming TimingFor(CardType type) => type switch
        {
            CardType.SPELL  => spellBeat,
            CardType.WEAPON => weaponBeat,
            _               => minionBeat,
        };

        private void Awake()
        {
            foreach (var entry in table)
                if (entry.Prefab != null)
                    _lookup[entry.Cue] = entry.Prefab;
        }

        /// <summary>Wire what's needed to draw a spell reveal as a REAL card, not a bare texture:
        /// the art provider for its illustration and BoardController.BuildDetachedCard for the
        /// same frame/art-fit/name rig a card in hand or on the board gets. Called once by
        /// NetworkedGameBootstrap, after boardController.Initialize (BuildDetachedCard depends on it).</summary>
        public void Init(IArtProvider artProvider, BoardController board)
        {
            _artProvider = artProvider;
            _board = board;
            if (boardAnchors == null && board != null) boardAnchors = board.GetComponent<BoardAnchors>();
        }

        // Called by MatchRunner.OnStateUpdated — keeps _state current for anchor resolution.
        public void OnStateUpdated(GameState state) => _state = state;

        /// <summary>Called by MatchRunner.OnHeroPowerBeat when a hero power entry reaches the front
        /// of the queue. Positions are resolved NOW, against the pre-effect board — a minion the
        /// power kills is still standing here — and everything is timed back from
        /// <paramref name="strikeInSeconds"/>, which is when the entry releases.</summary>
        public void ShowHeroPowerBeat(HeroPowerBeat beat, float strikeInSeconds)
        {
            if (boardAnchors == null || _state == null || beat.Targets == null) return;

            // The cast, on the caster's portrait. A plate with no art for its class does nothing.
            Transform caster = _board != null ? _board.ResolveCombatVisual(beat.Hero) : null;
            HeroPlateView plate = caster != null ? caster.GetComponent<HeroPlateView>() : null;
            if (plate != null)
                plate.PlayCast(Mathf.Max(0f, strikeInSeconds - beat.HoldSeconds),
                    beat.Delivery == HeroPowerDelivery.NightveilBackstabShadow
                        ? NightveilBackstabShadow.PortraitWipeSeconds : heroCastSeconds);

            if (beat.Delivery == HeroPowerDelivery.StonewardenSummon ||
                beat.Delivery == HeroPowerDelivery.TidecallerSummon)
            {
#if UNITY_EDITOR
                _uiSprites ??= new EditorUiSpriteProvider();
#else
                _uiSprites ??= new AddressableUiSpriteProvider();
#endif
                string key = beat.Delivery == HeroPowerDelivery.StonewardenSummon
                    ? "effect_summon_stonewarden" : "effect_summon_tidecaller";
                Sprite sheet = _uiSprites.Get(key);
                if (sheet != null && beat.SummonSlot >= 0)
                {
                    boardAnchors.SetBoardCount(beat.SummonController, beat.SummonBoardCount);
                    Transform slot = boardAnchors.AnchorFor(beat.SummonController, Zone.Board, beat.SummonSlot);
                    if (slot != null)
                        StartCoroutine(SummonHeroPowerEffect.Play(transform, sheet, slot,
                            strikeInSeconds));
                }
                return;
            }

            if (beat.Delivery == HeroPowerDelivery.ClockwrightBlueprint)
            {
#if UNITY_EDITOR
                _uiSprites ??= new EditorUiSpriteProvider();
#else
                _uiSprites ??= new AddressableUiSpriteProvider();
#endif
                Sprite blueprint = _uiSprites.Get(ClockwrightBlueprintEffect.SpriteKey);
                foreach (PlayerState ps in _state.Players)
                    if (ps.Hero != null && ps.Hero.Id == beat.Hero)
                    {
                        Transform deck = boardAnchors.AnchorFor(ps.Id, Zone.Deck, 0);
                        if (blueprint != null && deck != null)
                            StartCoroutine(ClockwrightBlueprintEffect.Play(transform, blueprint,
                                deck, strikeInSeconds));
                        break;
                    }
                return;
            }

            if (beat.Delivery == HeroPowerDelivery.NightveilBackstabShadow)
            {
#if UNITY_EDITOR
                _uiSprites ??= new EditorUiSpriteProvider();
#else
                _uiSprites ??= new AddressableUiSpriteProvider();
#endif
                Sprite shadow = _uiSprites.Get(NightveilBackstabShadow.SpriteKey);
                Transform target = beat.Targets.Count > 0 && _board != null
                    ? _board.ResolveCombatVisual(beat.Targets[0]) : null;
                if (shadow != null && target != null)
                    StartCoroutine(NightveilBackstabShadow.Play(transform, shadow, target,
                        strikeInSeconds));
                return;
            }

            if (beat.Delivery == HeroPowerDelivery.GravekeepSappingBolt)
            {
                Vector3? gravekeepSocket = null;
                foreach (PlayerState ps in _state.Players)
                    if (ps.Hero != null && ps.Hero.Id == beat.Hero)
                    {
                        Transform anchor = boardAnchors.HeroPowerAnchor(ps.Id);
                        if (anchor != null) gravekeepSocket = anchor.position;
                    }
                if (gravekeepSocket == null || beat.Targets.Count == 0) return;
                Transform target = _board != null ? _board.ResolveCombatVisual(beat.Targets[0]) : null;
                Vector3? at = target != null ? target.position : ResolvePosition(beat.Targets[0]);
                if (at.HasValue)
                    StartCoroutine(GravekeepSappingStrike.Play(transform, gravekeepSocket.Value, at.Value,
                                                                strikeInSeconds, heroPowerLeadSeconds));
                return;
            }

            if (beat.Delivery == HeroPowerDelivery.EmberwardSearingBolt)
            {
                Vector3? emberwardSocket = null;
                foreach (PlayerState ps in _state.Players)
                    if (ps.Hero != null && ps.Hero.Id == beat.Hero)
                    {
                        Transform anchor = boardAnchors.HeroPowerAnchor(ps.Id);
                        if (anchor != null) emberwardSocket = anchor.position;
                    }
                if (emberwardSocket == null || beat.Targets.Count == 0) return;
                Transform target = _board != null ? _board.ResolveCombatVisual(beat.Targets[0]) : null;
                Vector3? at = target != null ? target.position : ResolvePosition(beat.Targets[0]);
                if (!at.HasValue) return;
#if UNITY_EDITOR
                _uiSprites ??= new EditorUiSpriteProvider();
#else
                _uiSprites ??= new AddressableUiSpriteProvider();
#endif
                Sprite firebolt = _uiSprites.Get(EmberwardSearingBolt.SpriteKey);
                Camera camera = Camera.main;
                if (firebolt != null && camera != null)
                    StartCoroutine(EmberwardSearingBolt.Play(transform, firebolt, camera,
                        emberwardSocket.Value, at.Value, strikeInSeconds, heroPowerLeadSeconds));
                return;
            }

            if (beat.Delivery != HeroPowerDelivery.ForkedLightning) return;

            Vector3? socket = null;
            foreach (PlayerState ps in _state.Players)
                if (ps.Hero != null && ps.Hero.Id == beat.Hero)
                {
                    Transform anchor = boardAnchors.HeroPowerAnchor(ps.Id);
                    if (anchor != null) socket = anchor.position;
                }
            if (socket == null) return;

            var hits = new List<Vector3>(beat.Targets.Count);
            foreach (InstanceId id in beat.Targets)
            {
                // The live card or plate when there is one: a board slot anchor is where a card
                // would sit, which is not always where this one is drawn.
                Transform visual = _board != null ? _board.ResolveCombatVisual(id) : null;
                Vector3? at = visual != null ? visual.position : ResolvePosition(id);
                if (at.HasValue) hits.Add(at.Value);
            }
            if (hits.Count == 0) return;

#if UNITY_EDITOR
            _uiSprites ??= new EditorUiSpriteProvider();
#else
            _uiSprites ??= new AddressableUiSpriteProvider();
#endif
            Sprite arc = _uiSprites.Get(ForkedLightning.ArcSpriteKey);
            Camera cam = Camera.main;
            if (arc == null || cam == null) return;

            StartCoroutine(ForkedLightning.Play(transform, arc, cam, socket.Value, hits,
                                                strikeInSeconds, heroPowerLeadSeconds));
        }

        /// <summary>Called by MatchRunner.OnPlayReveal, the instant a card-play entry begins its
        /// extra hold — flies the played card up to be read over its target (or centre stage, if it
        /// didn't single out anything) for that type's beat, then self-destroys.
        /// Uses _state as of THIS call (the pre-effect snapshot: MatchRunner fires this before the
        /// held entry releases), which matters for a lethal hit — the target is still on the board
        /// in this snapshot even though it won't be once the hold ends.</summary>
        public void ShowPlayReveal(PlayReveal reveal)
        {
            if (boardAnchors == null || reveal.Def == null || _board == null) return;

            RevealTiming timing = TimingFor(reveal.Type);

            // Targeted → over what it hit, which is the reveal doing double duty as "this is what
            // just happened to that". Untargeted → centre stage, between the two board rows, where
            // both seats read it equally. The caster's own hero anchor was the old fallback and is
            // the wrong place for it: off to one side, on top of the portrait, and it reads as
            // something happening TO the caster.
            Vector3? over = ResolvePosition(reveal.Target) ?? ResolveCentrePosition();
            if (over == null) return;
            Vector3 overPos = over.Value + new Vector3(0f, 0f, RevealZBias);

            if (!TryHeldPose(overPos, out Vector3 heldPos, out Quaternion heldRot, out float heldScale))
                return;

            // A real card, not a stretched texture: BuildDetachedCard gives the same frame,
            // art-cover-fit and name label a card in hand or on the board gets, through the
            // shipping CreatePresenter rig — one card face implementation, not a second one drifting
            // here. anchorGo's scale carries down to every child quad/label uniformly.
            var anchorGo = new GameObject($"PlayReveal_{reveal.Def.Name}");
            Vector3? origin = ResolveHandOrigin(reveal);
            if (origin.HasValue)
                anchorGo.transform.position = origin.Value + new Vector3(0f, 0f, RevealZBias);
            else
                anchorGo.transform.SetPositionAndRotation(heldPos, heldRot);
            anchorGo.transform.localScale = Vector3.one * (origin.HasValue ? 1f : heldScale);

            CardPresenter card = _board.BuildDetachedCard(anchorGo.transform);
            // Held up near the camera it covers most of the screen, so a collider on it would take
            // every tap aimed at the board behind it for the length of the beat.
            var collider = card.GetComponent<BoxCollider>();
            if (collider != null) Destroy(collider);
            var group = card.GetComponent<UnityEngine.Rendering.SortingGroup>();
            if (group != null) group.sortingOrder = RevealSortOrder;
            card.Bind(reveal.Def, new CardLiveState(new InstanceId(-1)));
            if (_artProvider != null && !string.IsNullOrEmpty(reveal.Def.ArtId))
                _artProvider.LoadArt(reveal.Def.ArtId, card.SetArt);

            // Nowhere to land (a spell, or a slot the scene cannot resolve) → collapse into the
            // point it was revealed over.
            Vector3? destination = ResolveDestination(reveal);
            Vector3 landPos = destination.HasValue
                ? destination.Value + new Vector3(0f, 0f, RevealZBias)
                : overPos;
            float landScale = destination.HasValue ? 1f : CollapseScale;

            StartCoroutine(FlyReveal(anchorGo.transform, origin.HasValue,
                                     heldPos, heldRot, heldScale, landPos, landScale, timing));

            if (reveal.Type == CardType.SPELL) PlaySpellSchool(reveal);
            else if (reveal.Drowns) PlayDrownWash(reveal);
        }

        /// <summary>A minion that drowns as it is played: the wash on each character it damages, as
        /// the damage lands. A drown from any other trigger (an attack, a death) shows nothing
        /// yet; only a play reveal knows its targets.</summary>
        private void PlayDrownWash(PlayReveal reveal)
        {
            Camera cam = Camera.main;
            if (cam == null || reveal.Struck.Count == 0) return;
#if UNITY_EDITOR
            _uiSprites ??= new EditorUiSpriteProvider();
#else
            _uiSprites ??= new AddressableUiSpriteProvider();
#endif
            Sprite wash = _uiSprites.Get(SpellSchoolEffects.DrownWashKey);
            foreach (InstanceId id in reveal.Struck)
            {
                Vector3? at = ResolvePosition(id);
                Transform visual = _board.ResolveCombatVisual(id);
                if (visual == null && !at.HasValue) continue;
                StartCoroutine(SpellSchoolEffects.Burst(transform, wash, cam, visual, at ?? visual.position,
                    Mathf.Max(0f, reveal.HoldSeconds - 0.15f)));
            }
        }

        /// <summary>The spell-school sequences and the healing shimmer. Targets are resolved now, against
        /// the pre-effect board, and followed while the effect runs; a minion the spell kills is
        /// still standing until the entry releases.</summary>
        private void PlaySpellSchool(PlayReveal reveal)
        {
            Camera cam = Camera.main;
            if (cam == null || _state == null) return;
#if UNITY_EDITOR
            _uiSprites ??= new EditorUiSpriteProvider();
#else
            _uiSprites ??= new AddressableUiSpriteProvider();
#endif

            Transform caster = null;
            foreach (PlayerState ps in _state.Players)
                if (ps.Id == reveal.Controller && ps.Hero != null)
                    caster = _board.ResolveCombatVisual(ps.Hero.Id);

            if (reveal.Effect == SpellEffect.Earth)
            {
                StartCoroutine(SpellSchoolEffects.EarthCaster(transform,
                    _uiSprites.Get(SpellSchoolEffects.EarthBuildupKey), cam, caster,
                    SpellSchoolEffects.BuildupStartsAt(reveal.CardSeconds)));

                Sprite rock = _uiSprites.Get(SpellSchoolEffects.EarthRockKey);
                Sprite impact = _uiSprites.Get(SpellSchoolEffects.EarthImpactKey);
                for (int i = 0; i < reveal.Struck.Count; i++)
                {
                    InstanceId id = reveal.Struck[i];
                    Vector3? at = ResolvePosition(id);
                    Transform visual = _board.ResolveCombatVisual(id);
                    if (visual == null && !at.HasValue) continue;
                    StartCoroutine(SpellSchoolEffects.EarthRock(transform, rock, impact, cam, visual,
                        at ?? visual.position, SpellSchoolEffects.RockLandsAt(reveal.CardSeconds, i)));
                }
            }

            if (reveal.Effect == SpellEffect.Lightning)
            {
                // Each spell is either a bolt from the sky or one thrown across the board, never
                // both; a chain hops from each target to the next instead of from the caster.
                LightningShape shape = SpellSchoolEffects.LightningShapeOf(reveal.Def.Id);
                Sprite arc = shape != LightningShape.FromAbove
                    ? _uiSprites.Get(SpellSchoolEffects.LightningArcKey) : null;
                Sprite strike = shape == LightningShape.FromAbove
                    ? _uiSprites.Get(SpellSchoolEffects.LightningStrikeKey) : null;
                Transform from = caster;
                for (int i = 0; i < reveal.Struck.Count; i++)
                {
                    InstanceId id = reveal.Struck[i];
                    Vector3? at = ResolvePosition(id);
                    Transform visual = _board.ResolveCombatVisual(id);
                    if (visual == null && !at.HasValue) continue;
                    StartCoroutine(SpellSchoolEffects.LightningStrike(transform, arc, strike, cam,
                        from, visual, at ?? visual.position,
                        SpellSchoolEffects.LightningStrikesAt(reveal.CardSeconds, i)));
                    if (shape == LightningShape.Chain && visual != null) from = visual;
                }
            }

            if (reveal.Effect == SpellEffect.Fire)
            {
                StartCoroutine(SpellSchoolEffects.FireCaster(transform,
                    _uiSprites.Get(SpellSchoolEffects.FireBuildupKey), cam, caster,
                    SpellSchoolEffects.FlareStartsAt(reveal.CardSeconds)));

                Sprite ball = _uiSprites.Get(SpellSchoolEffects.FireFlameKey);
                Sprite burst = _uiSprites.Get(SpellSchoolEffects.FireImpactKey);
                for (int i = 0; i < reveal.Struck.Count; i++)
                {
                    InstanceId id = reveal.Struck[i];
                    Vector3? at = ResolvePosition(id);
                    Transform visual = _board.ResolveCombatVisual(id);
                    if (visual == null && !at.HasValue) continue;
                    StartCoroutine(SpellSchoolEffects.Fireball(transform, ball, burst, cam, caster,
                        visual, at ?? visual.position,
                        SpellSchoolEffects.FireballLandsAt(reveal.CardSeconds, i)));
                }
            }

            if (reveal.Effect == SpellEffect.Shadow)
            {
                HeroPlateView plate = caster != null ? caster.GetComponent<HeroPlateView>() : null;
                if (plate != null)
                    plate.PlayShadowWipe(SpellSchoolEffects.ShadowWipeStartsAt(reveal.CardSeconds),
                                         SpellSchoolEffects.ShadowWipeSeconds);

                Sprite wisp = _uiSprites.Get(SpellSchoolEffects.ShadowTravelKey);
                Sprite ring = _uiSprites.Get(SpellSchoolEffects.ShadowImpactKey);
                for (int i = 0; i < reveal.Struck.Count; i++)
                {
                    InstanceId id = reveal.Struck[i];
                    Vector3? at = ResolvePosition(id);
                    Transform visual = _board.ResolveCombatVisual(id);
                    if (visual == null && !at.HasValue) continue;
                    StartCoroutine(SpellSchoolEffects.ShadowBolt(transform, wisp, ring, cam, caster,
                        visual, at ?? visual.position,
                        SpellSchoolEffects.ShadowLandsAt(reveal.CardSeconds, i)));
                }
            }

            if (reveal.Effect == SpellEffect.Clockwright)
            {
                StartCoroutine(SpellSchoolEffects.Gather(transform,
                    _uiSprites.Get(SpellSchoolEffects.ClockwrightCasterKey), cam, caster,
                    SpellSchoolEffects.GatherStartsAt(reveal.CardSeconds, SpellSchoolEffects.ClockwrightLeadSeconds),
                    "ClockwrightCaster"));

                Sprite parts = _uiSprites.Get(SpellSchoolEffects.ClockwrightTravelKey);
                Sprite burst = _uiSprites.Get(SpellSchoolEffects.ClockwrightTargetKey);
                for (int i = 0; i < reveal.Struck.Count; i++)
                {
                    InstanceId id = reveal.Struck[i];
                    Vector3? at = ResolvePosition(id);
                    Transform visual = _board.ResolveCombatVisual(id);
                    if (visual == null && !at.HasValue) continue;
                    StartCoroutine(SpellSchoolEffects.ClockwrightBolt(transform, parts, burst, cam, caster,
                        visual, at ?? visual.position,
                        SpellSchoolEffects.ClockwrightLandsAt(reveal.CardSeconds, i)));
                }
            }

            if (reveal.Effect == SpellEffect.Lightning)
            {
                StartCoroutine(SpellSchoolEffects.Gather(transform,
                    _uiSprites.Get(SpellSchoolEffects.LightningChargeKey), cam, caster,
                    SpellSchoolEffects.GatherStartsAt(reveal.CardSeconds, SpellSchoolEffects.LightningLeadSeconds),
                    "LightningCharge", atFeet: true));

                // The hit crackles on each damaged character as the arc or bolt reaches it.
                Sprite hit = _uiSprites.Get(SpellSchoolEffects.LightningHitKey);
                for (int i = 0; i < reveal.Struck.Count; i++)
                {
                    InstanceId id = reveal.Struck[i];
                    Vector3? at = ResolvePosition(id);
                    Transform visual = _board.ResolveCombatVisual(id);
                    if (visual == null && !at.HasValue) continue;
                    StartCoroutine(SpellSchoolEffects.Burst(transform, hit, cam, visual, at ?? visual.position,
                        SpellSchoolEffects.LightningStrikesAt(reveal.CardSeconds, i)));
                }
            }

            if (reveal.Effect == SpellEffect.Water || reveal.Effect == SpellEffect.Frost)
            {
                bool frost = reveal.Effect == SpellEffect.Frost;
                float lead = frost ? SpellSchoolEffects.FrostLeadSeconds : SpellSchoolEffects.WaterLeadSeconds;
                StartCoroutine(SpellSchoolEffects.Gather(transform,
                    _uiSprites.Get(frost ? SpellSchoolEffects.FrostCasterKey : SpellSchoolEffects.WaterCasterKey),
                    cam, caster, SpellSchoolEffects.GatherStartsAt(reveal.CardSeconds, lead),
                    frost ? "FrostCaster" : "WaterCaster"));

                Sprite missile = _uiSprites.Get(frost ? SpellSchoolEffects.FrostTravelKey : SpellSchoolEffects.WaterTravelKey);
                Sprite land = frost ? null : _uiSprites.Get(reveal.Drowns ? SpellSchoolEffects.DrownWashKey
                                                                           : SpellSchoolEffects.WaterSplashKey);
                for (int i = 0; i < reveal.Struck.Count; i++)
                {
                    InstanceId id = reveal.Struck[i];
                    Vector3? at = ResolvePosition(id);
                    Transform visual = _board.ResolveCombatVisual(id);
                    if (visual == null && !at.HasValue) continue;
                    StartCoroutine(frost
                        ? SpellSchoolEffects.FrostBolt(transform, missile, cam, caster, visual,
                            at ?? visual.position, SpellSchoolEffects.FrostLandsAt(reveal.CardSeconds, i))
                        : SpellSchoolEffects.WaterBolt(transform, missile, land, reveal.Drowns, cam, caster,
                            visual, at ?? visual.position, SpellSchoolEffects.WaterLandsAt(reveal.CardSeconds, i)));
                }
            }

            if (reveal.Effect == SpellEffect.Gravekeep)
            {
                StartCoroutine(SpellSchoolEffects.Gather(transform,
                    _uiSprites.Get(SpellSchoolEffects.GravekeepCasterKey), cam, caster,
                    SpellSchoolEffects.GatherStartsAt(reveal.CardSeconds, SpellSchoolEffects.GravekeepLeadSeconds),
                    "GravekeepCaster"));

                Sprite skull = _uiSprites.Get(SpellSchoolEffects.GravekeepTravelKey);
                Sprite soulfire = _uiSprites.Get(SpellSchoolEffects.GravekeepTargetKey);
                for (int i = 0; i < reveal.Struck.Count; i++)
                {
                    InstanceId id = reveal.Struck[i];
                    Vector3? at = ResolvePosition(id);
                    Transform visual = _board.ResolveCombatVisual(id);
                    if (visual == null && !at.HasValue) continue;
                    StartCoroutine(SpellSchoolEffects.SkullBolt(transform, skull, soulfire, cam, caster,
                        visual, at ?? visual.position,
                        SpellSchoolEffects.GravekeepLandsAt(reveal.CardSeconds, i)));
                }
            }

            // Everything else the spell acted on gets its school's target effect alone, after the
            // damaged characters, on the same stagger.
            Sprite touchedSheet = reveal.Effect switch
            {
                SpellEffect.Earth       => _uiSprites.Get(SpellSchoolEffects.EarthImpactKey),
                SpellEffect.Lightning   => _uiSprites.Get(SpellSchoolEffects.LightningHitKey),
                SpellEffect.Fire        => _uiSprites.Get(SpellSchoolEffects.FireImpactKey),
                SpellEffect.Shadow      => _uiSprites.Get(SpellSchoolEffects.ShadowImpactKey),
                SpellEffect.Clockwright => _uiSprites.Get(SpellSchoolEffects.ClockwrightTargetKey),
                SpellEffect.Water       => _uiSprites.Get(SpellSchoolEffects.WaterSplashKey),
                SpellEffect.Gravekeep   => _uiSprites.Get(SpellSchoolEffects.GravekeepTargetKey),
                _                       => null, // frost: the freeze's crystals are its target effect
            };
            for (int i = 0; touchedSheet != null && i < reveal.Touched.Count; i++)
            {
                InstanceId id = reveal.Touched[i];
                Vector3? at = ResolvePosition(id);
                Transform visual = _board.ResolveCombatVisual(id);
                if (visual == null && !at.HasValue) continue;
                int n = reveal.Struck.Count + i;
                float landsAt = reveal.Effect switch
                {
                    SpellEffect.Earth       => SpellSchoolEffects.RockLandsAt(reveal.CardSeconds, n),
                    SpellEffect.Lightning   => SpellSchoolEffects.LightningStrikesAt(reveal.CardSeconds, n),
                    SpellEffect.Fire        => SpellSchoolEffects.FireballLandsAt(reveal.CardSeconds, n),
                    SpellEffect.Shadow      => SpellSchoolEffects.ShadowLandsAt(reveal.CardSeconds, n),
                    SpellEffect.Water       => SpellSchoolEffects.WaterLandsAt(reveal.CardSeconds, n),
                    SpellEffect.Gravekeep   => SpellSchoolEffects.GravekeepLandsAt(reveal.CardSeconds, n),
                    _                       => SpellSchoolEffects.ClockwrightLandsAt(reveal.CardSeconds, n),
                };
                StartCoroutine(SpellSchoolEffects.Burst(transform, touchedSheet, cam, visual,
                    at ?? visual.position, landsAt));
            }

            Sprite shimmer = reveal.Healed.Count > 0 ? _uiSprites.Get(SpellSchoolEffects.HealShimmerKey) : null;
            Sprite sparkles = reveal.Healed.Count > 0 ? _uiSprites.Get(SpellSchoolEffects.HealTravelKey) : null;
            for (int i = 0; i < reveal.Healed.Count; i++)
            {
                InstanceId id = reveal.Healed[i];
                Vector3? at = ResolvePosition(id);
                Transform visual = _board.ResolveCombatVisual(id);
                if (visual == null && !at.HasValue) continue;
                Vector3 fallback = at ?? visual.position;
                float arrives = SpellSchoolEffects.HealArrivesAt(reveal.CardSeconds, i);
                // A spell that heals its own caster has no distance to travel; it just shimmers.
                if (caster != null && caster != visual)
                    StartCoroutine(SpellSchoolEffects.HealTravel(transform, sparkles, cam, caster,
                        visual, fallback, arrives));
                StartCoroutine(SpellSchoolEffects.HealShimmer(transform, shimmer, cam, visual,
                    fallback, Mathf.Max(0f, arrives - 0.05f)));
            }
        }

        /// <summary>
        /// Where the card is held up to be read: facing the camera, <see cref="heldScreenHeight"/>
        /// of the screen tall, over the point it was revealed over — pulled in from the edges
        /// as far as it must be to fit on screen whole.
        ///
        /// <para>Readability is a screen-space quantity, so it is specified in screen space. A
        /// multiple of the board card, held in the board plane twenty-odd units from a tilted
        /// camera, came out at about a quarter of the screen and foreshortened: bigger than a hand
        /// card, never readable (Sam, 2026-09-11).</para>
        /// </summary>
        private bool TryHeldPose(Vector3 over, out Vector3 pos, out Quaternion rot, out float scale)
        {
            pos = over;
            rot = Quaternion.identity;
            scale = 1f;

            Camera cam = Camera.main;
            if (cam == null) return false;

            Vector3 vp = cam.WorldToViewportPoint(over);
            float depth = vp.z * HeldDepthFraction;
            if (depth <= cam.nearClipPlane) return false;

            float visibleH = cam.orthographic
                ? 2f * cam.orthographicSize
                : 2f * depth * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            scale = heldScreenHeight * visibleH / BoardController.CardH;

            float halfH = heldScreenHeight * 0.5f;
            float halfW = halfH * (BoardController.CardW / BoardController.CardH) / cam.aspect;
            float x = FitInto(vp.x, halfW + HeldScreenMargin);
            float y = FitInto(vp.y, halfH + HeldScreenMargin);

            pos = cam.ViewportToWorldPoint(new Vector3(x, y, depth));
            rot = cam.transform.rotation;   // the card's face is its -z side, so this faces the camera
            return true;
        }

        private static float FitInto(float v, float inset)
            => inset >= 0.5f ? 0.5f : Mathf.Clamp(v, inset, 1f - inset);

        /// <summary>
        /// The beat itself: out of the hand, turned up to face the player and held to be read, then
        /// down into whatever slot the card is going to — arriving as the entry releases and the
        /// board redraws with the real card, which is the point.
        ///
        /// <para>Timing is not decorative here. The three phases sum to
        /// <see cref="RevealTiming.TotalHoldSeconds"/>, the same number NetworkedGameBootstrap hands the
        /// classifier as the entry's hold, so this coroutine and the playback queue finish
        /// together. A fly-in with no hand slot to leave still WAITS out its share rather than
        /// returning early — cutting it short would destroy the card before the board redraws and
        /// leave a hole where the handoff should be. Retune a phase on <see cref="RevealTiming"/>,
        /// never here.</para>
        /// </summary>
        private IEnumerator FlyReveal(Transform t, bool fliesFromHand,
                                      Vector3 heldPos, Quaternion heldRot, float heldScale,
                                      Vector3 landPos, float landScale, RevealTiming timing)
        {
            if (timing.FlyInSeconds > 0f)
            {
                if (fliesFromHand)
                {
                    Vector3 from = t.position;
                    Quaternion fromRot = t.rotation;
                    for (float e = 0f; e < timing.FlyInSeconds; e += Time.deltaTime)
                    {
                        float k = Mathf.SmoothStep(0f, 1f, e / timing.FlyInSeconds);
                        t.SetPositionAndRotation(Vector3.Lerp(from, heldPos, k),
                                                 Quaternion.Slerp(fromRot, heldRot, k));
                        t.localScale = Vector3.one * Mathf.Lerp(1f, heldScale, k);
                        yield return null;
                    }
                }
                else
                {
                    // A token, or a hand we cannot see into — nothing to fly out OF. Keep the
                    // beat's shape anyway; see the timing note above.
                    yield return new WaitForSeconds(timing.FlyInSeconds);
                }
            }

            t.SetPositionAndRotation(heldPos, heldRot);
            t.localScale = Vector3.one * heldScale;

            if (timing.HoldSeconds > 0f)
                yield return new WaitForSeconds(timing.HoldSeconds);

            for (float e = 0f; e < timing.FlyOutSeconds; e += Time.deltaTime)
            {
                float k = Mathf.SmoothStep(0f, 1f, e / timing.FlyOutSeconds);
                t.SetPositionAndRotation(Vector3.Lerp(heldPos, landPos, k),
                                         Quaternion.Slerp(heldRot, Quaternion.identity, k));
                t.localScale = Vector3.one * Mathf.Lerp(heldScale, landScale, k);
                yield return null;
            }

            // Nothing lingers. The entry releases here and the board redraws: a card that landed is
            // now sitting exactly where the real one is (any overlap is two copies of it — Sam,
            // 2026-08-19), and a card held up this large would hide the very effect it caused.
            Destroy(t.gameObject);
        }

        /// <summary>The hand slot the card flew out of.
        /// <para><c>_state</c> is the PRE-play snapshot here (MatchRunner fires the reveal before
        /// the held entry releases), which is exactly why this can be answered at all — post-play
        /// the card is in nobody's hand. The hand fan is still laid out for that same pre-play
        /// count too, so the anchor is the literal position the player was looking at.</para></summary>
        private Vector3? ResolveHandOrigin(PlayReveal reveal)
        {
            if (boardAnchors == null || _state == null) return null;

            foreach (var ps in _state.Players)
            {
                if (ps.Id != reveal.Controller) continue;

                for (int i = 0; i < ps.Hand.Count; i++)
                    if (ps.Hand[i].Id == reveal.Source)
                        return boardAnchors.AnchorFor(ps.Id, Zone.Hand, i)?.position;

                // The opponent's hand is redacted to id-0 placeholders
                // (dev-docs/architecture/determinism-and-projection.md:23), so the exact slot is
                // not knowable from this side and never will be. The middle of their fan is the
                // honest answer and reads the same: it came out of that hand, somewhere.
                if (ps.Hand.Count > 0)
                    return boardAnchors.AnchorFor(ps.Id, Zone.Hand, ps.Hand.Count / 2)?.position;

                return null;
            }
            return null;
        }

        /// <summary>Where the card is headed, or null for one that just fades where it was cast.</summary>
        private Vector3? ResolveDestination(PlayReveal reveal)
        {
            if (boardAnchors == null) return null;

            switch (reveal.Destination)
            {
                case RevealDestination.WeaponSocket:
                    return boardAnchors.WeaponAnchor(reveal.Controller)?.position;

                case RevealDestination.BoardSlot:
                {
                    Transform slot = boardAnchors.AnchorFor(reveal.Controller, Zone.Board, reveal.DestSlot);
                    if (slot == null) return null;

                    // x is re-derived for the row this minion is ABOUT to be part of; y and z come
                    // from the anchor as built (BoardRowLayout only ever touches x, for the same
                    // reason). The scene's anchors are still centred for the board WITHOUT this
                    // minion — BoardController re-centres them when the entry releases, which is
                    // after this flight lands. Reading slot.position.x straight would aim the card
                    // at where the row is about to stop being, and it would touch down half a slot
                    // off the real card it is handing over to.
                    float half = reveal.DestBoardCount > 1 ? (reveal.DestBoardCount - 1) * 0.5f : 0f;
                    Vector3 p = slot.position;
                    return new Vector3(BoardRowGeometry.XForSlot(reveal.DestSlot, half, boardAnchors.BoardSlotSpacing), p.y, p.z);
                }

                default:
                    return null;
            }
        }

        /// <summary>Centre of the play area, derived from the middle board-slot anchors. Hero
        /// fixtures are intentionally asymmetric in the Royal Council artwork, so their midpoint
        /// is no longer the battlefield centre.</summary>
        private Vector3? ResolveCentrePosition()
        {
            if (boardAnchors == null || _state == null || _state.Players.Length < 2) return null;

            Transform a = boardAnchors.AnchorFor(_state.Players[0].Id, Zone.Board, 3);
            Transform b = boardAnchors.AnchorFor(_state.Players[1].Id, Zone.Board, 3);
            if (a == null || b == null) return null;

            return (a.position + b.position) * 0.5f;
        }

        // Called by MatchRunner.OnMatchEvent — plays one effect per event in emission order.
        public void OnMatchEvent(IReadOnlyList<GameEvent> batch)
        {
            foreach (var e in batch)
                PlayEvent(e);
        }

        private void PlayEvent(GameEvent e)
        {
            PresentationCue cue = e.CueOverride ?? PresentationCueDefaults.For(e.Kind);
            if (!_lookup.TryGetValue(cue, out var prefab) || prefab == null) return;

            // Target preferred over source — effect lands where the thing is hit.
            Vector3? pos = ResolvePosition(e.Target) ?? ResolvePosition(e.Source);
            var ps = Instantiate(prefab, pos ?? Vector3.zero, Quaternion.identity);
            ps.Play();
            float autoDestroyDelay = ps.main.duration + ps.main.startLifetime.constantMax + 0.1f;
            Destroy(ps.gameObject, autoDestroyDelay);
        }

        private Vector3? ResolvePosition(InstanceId? id)
        {
            if (id == null || boardAnchors == null || _state == null) return null;
            // Redaction sentinel guard (dev-docs/architecture/determinism-and-projection.md:23): id 0 would
            // otherwise MATCH the first hidden-hand placeholder in the projected state and play
            // the effect on a random card-back. Hidden actor → no positional anchor; skip.
            if (id.Value.Value == 0) return null;
            var t = ResolveTransform(id.Value);
            return t != null ? (Vector3?)t.position : null;
        }

        private Transform ResolveTransform(InstanceId id)
        {
            foreach (var ps in _state.Players)
            {
                if (ps.Hero.Id == id)
                    return boardAnchors.HeroAnchor(ps.Id);

                for (int i = 0; i < ps.Board.Count; i++)
                    if (ps.Board[i].Id == id)
                        return boardAnchors.AnchorFor(ps.Id, Zone.Board, i);

                for (int i = 0; i < ps.Hand.Count; i++)
                    if (ps.Hand[i].Id == id)
                        return boardAnchors.AnchorFor(ps.Id, Zone.Hand, i);
            }
            return null;
        }
    }

    /// <summary>One row in the VfxDirector Inspector table.</summary>
    [Serializable]
    public struct VfxCueEntry
    {
        public PresentationCue Cue;
        public ParticleSystem Prefab;
    }

    /// <summary>
    /// One card type's play-reveal beat, in phases.
    ///
    /// <para>ALL THREE cost the match real time: the playback entry is held for their sum
    /// (<see cref="TotalHoldSeconds"/>), so none of the card's effects are visible for any of it,
    /// and the player who played the card is waiting on it. Only HOLD is time spent READING — the
    /// other two are travel, and travel that is too slow reads as the game being sluggish rather
    /// than as the card being important.</para>
    /// </summary>
    [Serializable]
    public struct RevealTiming
    {
        [Tooltip("Travel from the hand slot the card was played out of, up to the reveal position, " +
                 "turning to face the player and growing to full reveal size.")]
        [Range(0f, 3f)] public float FlyInSeconds;

        [Tooltip("Still, magnified, at the reveal position. The part that is actually READ — tune " +
                 "this one for readability and the travel phases for feel.")]
        [Range(0f, 3f)] public float HoldSeconds;

        [Tooltip("Travel down into whatever slot the card is going to (or, for a spell, collapse " +
                 "into what it hit), landing exactly as the board redraws with the real card.")]
        [Range(0f, 3f)] public float FlyOutSeconds;

        /// <summary>What the playback entry is held for: the whole flight, not just the still part.
        /// The board must not redraw until the card has landed, or the thing it is handing over to
        /// appears underneath it mid-flight.</summary>
        public float TotalHoldSeconds => FlyInSeconds + HoldSeconds + FlyOutSeconds;

        public RevealTiming(float flyInSeconds, float holdSeconds, float flyOutSeconds)
        {
            FlyInSeconds = flyInSeconds;
            HoldSeconds = holdSeconds;
            FlyOutSeconds = flyOutSeconds;
        }
    }
}
