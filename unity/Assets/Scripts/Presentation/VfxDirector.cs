using System;
using System.Collections;
using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Session;
using UnityEngine;

namespace Emberfall.Presentation
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

        /// <summary>The tunables MatchRunner needs to know about — how long each card type's
        /// reveal holds up the rest of the command. Read by NetworkedGameBootstrap when it builds the
        /// classifier delegate, so this stays the single source for the numbers (Sam, 2026-08-01;
        /// per-type 2026-08-18).</summary>
        public PlayRevealHolds RevealHolds =>
            new PlayRevealHolds(minionBeat.TotalHoldSeconds,
                                spellBeat.TotalHoldSeconds,
                                weaponBeat.TotalHoldSeconds);

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
        }

        // Called by MatchRunner.OnStateUpdated — keeps _state current for anchor resolution.
        public void OnStateUpdated(GameState state) => _state = state;

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
                    return new Vector3(BoardRowGeometry.XForSlot(reveal.DestSlot, half), p.y, p.z);
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
