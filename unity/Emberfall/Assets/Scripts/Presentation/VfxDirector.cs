using System;
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

        [Header("Play reveal — every played card gets a readable beat")]
        [Tooltip("A cast spell resolves and is gone, so its reveal IS the whole tell. Longest hold.")]
        [SerializeField] private RevealTiming spellReveal = new RevealTiming(0.9f, 1.2f);
        [Tooltip("A minion announces itself by arriving a moment later, so its reveal is a lead-in " +
                 "rather than the whole story — about half a spell's, and it clears out fast so it " +
                 "isn't sitting on top of the board it just changed.")]
        [SerializeField] private RevealTiming minionReveal = new RevealTiming(0.45f, 0.35f);
        [Tooltip("An equip is a discrete moment like a cast, but the weapon stays visible in its " +
                 "socket afterwards.")]
        [SerializeField] private RevealTiming weaponReveal = new RevealTiming(0.6f, 0.6f);
        [Tooltip("Reveal card size as a multiple of the in-hand/board card size.")]
        [SerializeField, Range(0.5f, 2f)] private float playRevealScale = 1.2f;

        private readonly Dictionary<PresentationCue, ParticleSystem> _lookup = new();
        private GameState _state;
        private IArtProvider _artProvider;
        private BoardController _board;

        /// <summary>The tunables MatchRunner needs to know about — how long each card type's
        /// reveal holds up the rest of the command. Read by GameBootstrap when it builds the
        /// classifier delegate, so this stays the single source for the numbers (Sam, 2026-08-01;
        /// per-type 2026-08-18).</summary>
        public PlayRevealHolds RevealHolds =>
            new PlayRevealHolds(minionReveal.HoldSeconds, spellReveal.HoldSeconds, weaponReveal.HoldSeconds);

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
            anchorGo.transform.position = pos.Value + new Vector3(0f, 0f, -0.2f); // in front of the target
            anchorGo.transform.localScale = Vector3.one * playRevealScale;

            CardPresenter card = _board.BuildDetachedCard(anchorGo.transform);
            card.Bind(reveal.Def, new CardLiveState(new InstanceId(-1)));
            if (_artProvider != null && !string.IsNullOrEmpty(reveal.Def.ArtId))
                _artProvider.LoadArt(reveal.Def.ArtId, card.SetArt);

            Destroy(anchorGo, timing.HoldSeconds + timing.LingerSeconds);
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
    /// One card type's play-reveal timing. HOLD is the part that costs the match time — the whole
    /// playback entry waits on it, so nothing the card does is visible yet. LINGER is free by
    /// comparison: the entry has already released and the board is moving again underneath, the
    /// card just hasn't faded out. Tune hold for readability and linger for how abruptly the beat
    /// ends.
    /// </summary>
    [Serializable]
    public struct RevealTiming
    {
        [Tooltip("Holds the entire playback entry: the card's effects are not visible yet.")]
        [Range(0f, 3f)] public float HoldSeconds;

        [Tooltip("How much longer the card stays up AFTER the hold ends, overlapping the now-" +
                 "visible effect, so it doesn't vanish the instant damage lands.")]
        [Range(0f, 3f)] public float LingerSeconds;

        public RevealTiming(float holdSeconds, float lingerSeconds)
        {
            HoldSeconds = holdSeconds;
            LingerSeconds = lingerSeconds;
        }
    }
}
