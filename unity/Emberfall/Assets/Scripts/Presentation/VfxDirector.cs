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
    /// Phase C step 5 scene setup: add VfxDirector MonoBehaviour to the GameBootstrap GameObject
    /// and assign boardAnchors + any prefabs you have in the Inspector. Null prefab = no VFX.
    /// </summary>
    public sealed class VfxDirector : MonoBehaviour
    {
        [Header("Presentation Cue → Prefab table")]
        [SerializeField] private VfxCueEntry[] table = Array.Empty<VfxCueEntry>();

        [Header("Board anchors for world-position resolution")]
        [SerializeField] private BoardAnchors boardAnchors;

        [Header("Play reveal — every played card flies out, holds, and lands")]
        [Tooltip("A cast spell resolves into the graveyard, which has no anchor and would read as " +
                 "nothing — so it fades where it was cast instead of flying anywhere. That also " +
                 "makes it the ONLY type that may linger: nothing takes its place, so nothing doubles.")]
        [SerializeField] private RevealTiming spellReveal = new RevealTiming(0.20f, 0.90f, 0f, 0.55f);
        [Tooltip("Flies up out of the hand slot it was played from, holds centre stage, then floats " +
                 "down into its board slot — landing exactly as the board redraws with the real " +
                 "card. Linger MUST stay 0: it is the overlap that put two of the same minion on " +
                 "screen at once (Sam, 2026-08-19).")]
        [SerializeField] private RevealTiming minionReveal = new RevealTiming(0.18f, 0.55f, 0.28f, 0f);
        [Tooltip("Same three-phase flight as a minion, landing in the weapon socket. Linger 0 for " +
                 "the same reason — the socket fills the instant the entry releases.")]
        [SerializeField] private RevealTiming weaponReveal = new RevealTiming(0.18f, 0.70f, 0.28f, 0f);
        [Tooltip("Reveal card size at the top of the beat, as a multiple of the in-hand/board size. " +
                 "The flight scales between 1 and this, so the card leaves and lands at the size a " +
                 "real card is and is only magnified while it is being read.")]
        [SerializeField, Range(0.5f, 2f)] private float playRevealScale = 1.2f;

        /// <summary>How far in front of everything the reveal card rides while it is in flight, so
        /// it never clips into the hand fan it is leaving or the board row it is joining.</summary>
        private const float RevealZBias = -0.2f;

        private readonly Dictionary<PresentationCue, ParticleSystem> _lookup = new();
        private GameState _state;
        private IArtProvider _artProvider;
        private BoardController _board;

        /// <summary>The tunables MatchRunner needs to know about — how long each card type's
        /// reveal holds up the rest of the command. Read by GameBootstrap when it builds the
        /// classifier delegate, so this stays the single source for the numbers (Sam, 2026-08-01;
        /// per-type 2026-08-18).</summary>
        public PlayRevealHolds RevealHolds =>
            new PlayRevealHolds(minionReveal.TotalHoldSeconds,
                                spellReveal.TotalHoldSeconds,
                                weaponReveal.TotalHoldSeconds);

        private RevealTiming TimingFor(CardType type) => type switch
        {
            CardType.SPELL  => spellReveal,
            CardType.WEAPON => weaponReveal,
            _               => minionReveal,
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
        /// GameBootstrap, after boardController.Initialize (BuildDetachedCard depends on it).</summary>
        public void Init(IArtProvider artProvider, BoardController board)
        {
            _artProvider = artProvider;
            _board = board;
        }

        // Called by MatchRunner.OnStateUpdated — keeps _state current for anchor resolution.
        public void OnStateUpdated(GameState state) => _state = state;

        /// <summary>Called by MatchRunner.OnPlayReveal, the instant a card-play entry begins its
        /// extra hold — shows the played card over its target (or centre stage, if it didn't
        /// single out anything) for that type's hold + linger, then self-destroys.
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
            Vector3? pos = ResolvePosition(reveal.Target) ?? ResolveCentrePosition();
            if (pos == null) return;

            // A real card, not a stretched texture: BuildDetachedCard gives the same frame,
            // art-cover-fit and name label a card in hand or on the board gets, through the
            // shipping CreatePresenter rig — one card face implementation, not a second one drifting
            // here. anchorGo's scale carries down to every child quad/label uniformly.
            var anchorGo = new GameObject($"PlayReveal_{reveal.Def.Name}");
            anchorGo.transform.position = pos.Value + new Vector3(0f, 0f, RevealZBias);
            anchorGo.transform.localScale = Vector3.one * playRevealScale;

            CardPresenter card = _board.BuildDetachedCard(anchorGo.transform);
            card.Bind(reveal.Def, new CardLiveState(new InstanceId(-1)));
            if (_artProvider != null && !string.IsNullOrEmpty(reveal.Def.ArtId))
                _artProvider.LoadArt(reveal.Def.ArtId, card.SetArt);

            StartCoroutine(FlyReveal(anchorGo, ResolveHandOrigin(reveal),
                                     pos.Value + new Vector3(0f, 0f, RevealZBias),
                                     ResolveDestination(reveal), timing));
        }

        /// <summary>
        /// The beat itself: out of the hand, still and magnified to be read, then down into
        /// whatever slot the card is going to — arriving as the entry releases and the board
        /// redraws with the real card, which is the point.
        ///
        /// <para>Timing is not decorative here. The three phases sum to
        /// <see cref="RevealTiming.TotalHoldSeconds"/>, the same number GameBootstrap hands the
        /// classifier as the entry's hold, so this coroutine and the playback queue finish
        /// together. A phase with nothing to animate (no hand slot to leave, no destination to
        /// land in) still WAITS out its share rather than returning early — cutting it short
        /// would destroy the card before the board redraws and leave a hole where the handoff
        /// should be. Retune a phase on <see cref="RevealTiming"/>, never here.</para>
        /// </summary>
        private IEnumerator FlyReveal(GameObject go, Vector3? origin, Vector3 hold,
                                      Vector3? destination, RevealTiming timing)
        {
            Transform t = go.transform;
            float big = playRevealScale;

            if (timing.FlyInSeconds > 0f)
            {
                if (origin.HasValue)
                {
                    Vector3 from = origin.Value + new Vector3(0f, 0f, RevealZBias);
                    for (float e = 0f; e < timing.FlyInSeconds; e += Time.deltaTime)
                    {
                        float k = Mathf.SmoothStep(0f, 1f, e / timing.FlyInSeconds);
                        t.position   = Vector3.Lerp(from, hold, k);
                        t.localScale = Vector3.one * Mathf.Lerp(1f, big, k);
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

            t.position   = hold;
            t.localScale = Vector3.one * big;

            if (timing.HoldSeconds > 0f)
                yield return new WaitForSeconds(timing.HoldSeconds);

            if (timing.FlyOutSeconds > 0f)
            {
                if (destination.HasValue)
                {
                    Vector3 to = destination.Value + new Vector3(0f, 0f, RevealZBias);
                    for (float e = 0f; e < timing.FlyOutSeconds; e += Time.deltaTime)
                    {
                        float k = Mathf.SmoothStep(0f, 1f, e / timing.FlyOutSeconds);
                        t.position   = Vector3.Lerp(hold, to, k);
                        t.localScale = Vector3.one * Mathf.Lerp(big, 1f, k);
                        yield return null;
                    }
                }
                else
                {
                    yield return new WaitForSeconds(timing.FlyOutSeconds);
                }
            }

            // Linger is what a card with nowhere to go does INSTEAD of flying, so a card that
            // just landed does not get one — enforced here rather than left to the authored 0,
            // because the authored value is not trustworthy across this change. Unity's
            // domain-reload restore matches serialized fields by NAME: an Editor holding the
            // two-field RevealTiming restores HoldSeconds and LingerSeconds onto the four-field
            // one and lets the new fields keep their initializers. A minion would come back with
            // its old 0.35s linger, which IS the double-render — the exact bug this flight was
            // written to remove, silently reintroduced by a reload rather than by an edit.
            //
            // Structural, not defensive: by the time the entry has released the real card is
            // already sitting exactly where this one is. There is no value of LingerSeconds that
            // is correct for a card with a destination.
            if (timing.LingerSeconds > 0f && !destination.HasValue)
                yield return new WaitForSeconds(timing.LingerSeconds);

            Destroy(go);
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

        /// <summary>Centre of the play area, derived from the scene rather than hard-coded: the
        /// two hero anchors are mirrored about the origin (ProceduralBoardSetup places them at
        /// ±HeroX, ±HeroY), so their midpoint is the point between the two board rows however the
        /// layout is retuned.</summary>
        private Vector3? ResolveCentrePosition()
        {
            if (boardAnchors == null || _state == null || _state.Players.Length < 2) return null;

            Transform a = boardAnchors.HeroAnchor(_state.Players[0].Id);
            Transform b = boardAnchors.HeroAnchor(_state.Players[1].Id);
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
    /// <para>FLY-IN, HOLD and FLY-OUT all cost the match real time: the playback entry is held for
    /// their sum (<see cref="TotalHoldSeconds"/>), so none of the card's effects are visible for
    /// any of it. Only HOLD is time spent READING — the other two are travel, and travel that is
    /// too slow reads as the game being sluggish rather than as the card being important.</para>
    ///
    /// <para>LINGER is the one phase that is nearly free, and the one that is dangerous. It runs
    /// AFTER the entry releases, so the board has already redrawn underneath it. For a spell that
    /// is exactly right — nothing takes a cast spell's place, so it can fade over its own effect.
    /// For anything that LANDS somewhere, a non-zero linger is the double-render bug: the reveal
    /// and the real card, both on screen, both the same minion (Sam, 2026-08-19). Give those types
    /// a fly-out instead; arriving as the board redraws is what a handoff looks like.</para>
    /// </summary>
    [Serializable]
    public struct RevealTiming
    {
        [Tooltip("Travel from the hand slot the card was played out of, up to the reveal position, " +
                 "growing from real-card size to full reveal size.")]
        [Range(0f, 3f)] public float FlyInSeconds;

        [Tooltip("Still, magnified, at the reveal position. The part that is actually READ — tune " +
                 "this one for readability and the travel phases for feel.")]
        [Range(0f, 3f)] public float HoldSeconds;

        [Tooltip("Travel down into whatever slot the card is going to, shrinking back to real-card " +
                 "size, landing exactly as the board redraws with the real card. 0 for a type with " +
                 "no destination (a spell).")]
        [Range(0f, 3f)] public float FlyOutSeconds;

        [Tooltip("Time on screen AFTER the entry releases and the board has moved on. Applies ONLY " +
                 "to a card with no destination — a card that lands somewhere IGNORES this at " +
                 "runtime, whatever you set here, because by then the real card is already in the " +
                 "slot and any overlap is two copies of it.")]
        [Range(0f, 3f)] public float LingerSeconds;

        /// <summary>What the playback entry is held for: the whole flight, not just the still part.
        /// The board must not redraw until the card has landed, or the thing it is handing over to
        /// appears underneath it mid-flight.</summary>
        public float TotalHoldSeconds => FlyInSeconds + HoldSeconds + FlyOutSeconds;

        public RevealTiming(float flyInSeconds, float holdSeconds, float flyOutSeconds,
                            float lingerSeconds)
        {
            FlyInSeconds = flyInSeconds;
            HoldSeconds = holdSeconds;
            FlyOutSeconds = flyOutSeconds;
            LingerSeconds = lingerSeconds;
        }
    }
}
