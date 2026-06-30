using System;
using System.Collections.Generic;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Subscribes to the engine's D7 event stream and plays one-shot particle effects per
    /// the board-and-vfx.md §2 table. Each VfxEvent maps to a prefab assigned in the Inspector;
    /// null entries are silently skipped (VFX asset not yet authored). Uses Instantiate + timed
    /// Destroy for M1; replace with ObjectPool<T> in M2 (board-and-vfx.md §3).
    ///
    /// Persistent auras (Shield / Taunt / Frozen tinting) are handled by CardPresenter.ApplyAuras()
    /// via ICardLiveState.Changed — no per-frame event required.
    ///
    /// Phase C step 5 scene setup: add VfxDirector MonoBehaviour to the GameBootstrap GameObject
    /// and assign boardAnchors + any prefabs you have in the Inspector. Null prefab = no VFX.
    /// </summary>
    public sealed class VfxDirector : MonoBehaviour
    {
        [Header("D7 Event → Prefab table (board-and-vfx.md §2)")]
        [SerializeField] private VfxEventEntry[] table = Array.Empty<VfxEventEntry>();

        [Header("Board anchors for world-position resolution")]
        [SerializeField] private BoardAnchors boardAnchors;

        private readonly Dictionary<VfxEvent, ParticleSystem> _lookup = new();
        private GameState _state;

        private void Awake()
        {
            foreach (var entry in table)
                if (entry.Prefab != null)
                    _lookup[entry.Kind] = entry.Prefab;
        }

        // Called by MatchRunner.OnStateUpdated — keeps _state current for anchor resolution.
        public void OnStateUpdated(GameState state) => _state = state;

        // Called by MatchRunner.OnMatchEvent — plays one effect per event in emission order.
        public void OnMatchEvent(IReadOnlyList<GameEvent> batch)
        {
            foreach (var e in batch)
                PlayEvent(e);
        }

        private void PlayEvent(GameEvent e)
        {
            if (!_lookup.TryGetValue(e.Kind, out var prefab) || prefab == null) return;

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
    public struct VfxEventEntry
    {
        public VfxEvent Kind;
        public ParticleSystem Prefab;
    }
}
