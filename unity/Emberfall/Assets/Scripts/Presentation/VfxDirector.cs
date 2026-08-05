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

        [Header("Spell reveal (opponent-spell-cast-has-no-visible-tell)")]
        [Tooltip("How long the whole entry holds before the spell's effect becomes visible — the " +
                 "beat the reveal is read in. Also fed to MatchRunner.SpellRevealClassifier via " +
                 "GameBootstrap, so this is the one tunable for the pause itself.")]
        [SerializeField, Range(0f, 3f)] private float spellRevealHoldSeconds = 0.6f;
        [Tooltip("How much longer the reveal visual stays up AFTER the hold ends (overlapping the " +
                 "now-visible effect) so it doesn't feel like it vanishes the instant damage lands.")]
        [SerializeField, Range(0f, 3f)] private float spellRevealLingerSeconds = 1.2f;
        [Tooltip("Reveal card size as a multiple of the in-hand/board card size.")]
        [SerializeField, Range(0.5f, 2f)] private float spellRevealScale = 1.2f;

        private readonly Dictionary<PresentationCue, ParticleSystem> _lookup = new();
        private GameState _state;
        private IArtProvider _artProvider;
        private BoardController _board;

        /// <summary>The one tunable MatchRunner needs to know about — how long the reveal holds
        /// up the rest of the command. Read by GameBootstrap when it builds the classifier
        /// delegate, so this stays the single source for the number (Sam, 2026-08-01).</summary>
        public float SpellRevealHoldSeconds => spellRevealHoldSeconds;

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

        /// <summary>Called by MatchRunner.OnSpellReveal, the instant a spell-cast entry begins its
        /// extra hold — shows the cast card over its target (or the caster's hero, if there's no
        /// single target) for spellRevealHoldSeconds + spellRevealLingerSeconds, then self-destroys.
        /// Uses _state as of THIS call (the pre-effect snapshot: MatchRunner fires this before the
        /// held entry releases), which matters for a lethal hit — the target is still on the board
        /// in this snapshot even though it won't be once the hold ends.</summary>
        public void ShowSpellReveal(SpellReveal reveal)
        {
            if (boardAnchors == null || reveal.Def == null || _board == null) return;

            Vector3? pos = ResolvePosition(reveal.Target) ?? ResolveHeroPosition(reveal.Controller);
            if (pos == null) return;

            // A real card, not a stretched texture: BuildDetachedCard gives the same frame,
            // art-cover-fit and name label a card in hand or on the board gets, through the
            // shipping CreatePresenter rig — one card face implementation, not a second one drifting
            // here. anchorGo's scale carries down to every child quad/label uniformly.
            var anchorGo = new GameObject($"SpellReveal_{reveal.Def.Name}");
            anchorGo.transform.position = pos.Value + new Vector3(0f, 0f, -0.2f); // in front of the target
            anchorGo.transform.localScale = Vector3.one * spellRevealScale;

            CardPresenter card = _board.BuildDetachedCard(anchorGo.transform);
            card.Bind(reveal.Def, new CardLiveState(new InstanceId(-1)));
            if (_artProvider != null && !string.IsNullOrEmpty(reveal.Def.ArtId))
                _artProvider.LoadArt(reveal.Def.ArtId, card.SetArt);

            Destroy(anchorGo, spellRevealHoldSeconds + spellRevealLingerSeconds);
        }

        private Vector3? ResolveHeroPosition(PlayerId controller)
        {
            if (boardAnchors == null || _state == null) return null;
            foreach (var ps in _state.Players)
                if (ps.Id == controller)
                    return boardAnchors.HeroAnchor(ps.Id)?.position;
            return null;
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
}
