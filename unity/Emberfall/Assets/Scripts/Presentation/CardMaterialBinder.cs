using System;
using System.Collections;
using System.Collections.Generic;
using Emberfall.Core.Contracts;
using Emberfall.Session;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Drives the 4 per-instance MaterialPropertyBlock shader floats on a card's renderers
    /// (shaders/README.md §3, interfaces.md §5.2). Added to the card root by BoardController;
    /// call SetupRenderers() first, then Init() after ICardLiveState exists.
    ///
    /// Param ownership (shaders/README.md §3 consolidated table):
    ///   _FlashAmount    → bodyRenderer   SG_CardComposite DamageFlash branch — spike/decay on onDamage/onHeal
    ///   _DissolveAmount → bodyRenderer   SG_Dissolve — tween 0→1 over 0.4s on onDeath
    ///   _StealthAmount  → bodyRenderer   SG_Stealth alpha body — tween to 0.6 (not 1; keeps card readable)
    ///   _FrostAmount    → bodyRenderer   SG_Frost — tween 0↔1 on AuraSet.Frozen change (0.3s)
    ///   SG_Glow enable  → glowRenderer   toggled by AuraSet.Glow / affordance; self-animated, no float
    ///   SG_Aegis enable → aegisRenderer  toggled by AuraSet.Shield|Taunt; self-animated, no float
    /// </summary>
    public sealed class CardMaterialBinder : MonoBehaviour
    {
        private Renderer _bodyRenderer;
        private Renderer _glowRenderer;   // null until SG_Glow shader asset assigned
        private Renderer _aegisRenderer;  // null until SG_Aegis shader asset assigned

        private InstanceId _id;
        private ICardLiveState _live;
        private MatchRunner _matchRunner;
        private MaterialPropertyBlock _mpb;

        private Coroutine _flashRoutine;
        private Coroutine _dissolveRoutine;
        private Coroutine _frostRoutine;
        private Coroutine _stealthRoutine;
        private float _frostCurrent;
        private float _stealthCurrent;

        // Durations from board-and-vfx.md §5 and shaders/README.md §3.
        private const float FlashDecay    = 0.25f;
        private const float DissolveDur   = 0.40f;
        private const float FrostDur      = 0.30f;
        private const float StealthDur    = 0.40f;
        private const float StealthTarget = 0.60f; // spec: not full 1 — keeps minion readable under stealth

        // ── Setup (called by BoardController before Init) ──────────────────────────

        public void SetupRenderers(Renderer body, Renderer glow, Renderer aegis)
        {
            _bodyRenderer  = body;
            _glowRenderer  = glow;
            _aegisRenderer = aegis;
            _mpb = new MaterialPropertyBlock();
        }

        // ── Init (called by BoardController after ICardLiveState is created) ───────

        public void Init(ICardLiveState live, MatchRunner matchRunner)
        {
            _live        = live;
            _id          = live.Id;
            _matchRunner = matchRunner;

            _live.Changed += OnChanged;
            if (_matchRunner != null)
                _matchRunner.OnMatchEvent += OnMatchEvent;

            ApplyAuras(_live.Auras);
        }

        private void OnDestroy()
        {
            if (_live != null)        _live.Changed -= OnChanged;
            if (_matchRunner != null) _matchRunner.OnMatchEvent -= OnMatchEvent;
        }

        // ── ICardLiveState.Changed ──────────────────────────────────────────────────

        private bool _selectionGlow;

        private void OnChanged(CardChange what)
        {
            if ((what & CardChange.Auras) != 0)
                ApplyAuras(_live.Auras);
            if ((what & CardChange.Affordance) != 0)
                ApplyGlow();
        }

        // Glow overlay is shared by three signals: Spell Damage aura, selection highlight,
        // and can-attack readiness. Any one keeps it on.
        private void ApplyGlow()
        {
            if (_glowRenderer == null) return;
            bool auraGlow  = _live != null && (_live.Auras & AuraSet.Glow) != 0;
            bool readyGlow = _live != null && _live.AttackReadiness == AttackReadiness.Ready;
            _glowRenderer.enabled = _selectionGlow || auraGlow || readyGlow;
        }

        private void ApplyAuras(AuraSet auras)
        {
            float frostTarget   = (auras & AuraSet.Frozen)  != 0 ? 1f            : 0f;
            float stealthTarget = (auras & AuraSet.Stealth) != 0 ? StealthTarget : 0f;

            if (!Mathf.Approximately(_frostCurrent, frostTarget))
                Restart(ref _frostRoutine, TweenFloat(
                    v => { _frostCurrent = v; PushFloat(ShaderIds.FrostAmount, v); },
                    _frostCurrent, frostTarget, FrostDur));

            if (!Mathf.Approximately(_stealthCurrent, stealthTarget))
                Restart(ref _stealthRoutine, TweenFloat(
                    v => { _stealthCurrent = v; PushFloat(ShaderIds.StealthAmount, v); },
                    _stealthCurrent, stealthTarget, StealthDur));

            // Glow: combined signal (aura / selection / can-attack) — see ApplyGlow().
            ApplyGlow();

            // Aegis underlay: Divine Shield only. Taunt reads spatially (front row,
            // decision 2026-07-17) — no keyword glow, so the shared gold bubble
            // unambiguously means Shield.
            if (_aegisRenderer != null)
                _aegisRenderer.enabled = (auras & AuraSet.Shield) != 0;
        }

        // ── MatchRunner.OnMatchEvent — one-shot flash + dissolve ────────────────────

        private void OnMatchEvent(IReadOnlyList<GameEvent> batch)
        {
            foreach (GameEvent e in batch)
            {
                bool isTarget = e.Target.HasValue && e.Target.Value.Equals(_id);
                bool isSource = e.Source.HasValue && e.Source.Value.Equals(_id);
                if (!isTarget && !isSource) continue;

                switch (e.Kind)
                {
                    case VfxEvent.onDamage:
                    case VfxEvent.onHeal:
                        Restart(ref _flashRoutine, FlashDecayRoutine());
                        break;

                    case VfxEvent.onDeath when isTarget:
                        Restart(ref _dissolveRoutine, TweenFloat(
                            v => PushFloat(ShaderIds.DissolveAmount, v),
                            0f, 1f, DissolveDur));
                        break;
                }
            }
        }

        // ── Affordance glow (called by InputRouter or ClickableCard) ────────────────

        /// <summary>
        /// Drive the Glow overlay for the selection highlight. Additive with AuraSet.Glow
        /// and can-attack readiness — glow stays on while any signal holds.
        /// </summary>
        public void SetAffordanceGlow(bool active)
        {
            _selectionGlow = active;
            ApplyGlow();
        }

        // ── Coroutines ───────────────────────────────────────────────────────────────

        private IEnumerator FlashDecayRoutine()
        {
            PushFloat(ShaderIds.FlashAmount, 1f);
            float elapsed = 0f;
            while (elapsed < FlashDecay)
            {
                elapsed += Time.deltaTime;
                PushFloat(ShaderIds.FlashAmount, 1f - elapsed / FlashDecay);
                yield return null;
            }
            PushFloat(ShaderIds.FlashAmount, 0f);
        }

        private IEnumerator TweenFloat(Action<float> setter, float from, float to, float duration)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                setter(Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration)));
                yield return null;
            }
            setter(to);
        }

        private void Restart(ref Coroutine slot, IEnumerator routine)
        {
            if (slot != null) StopCoroutine(slot);
            slot = StartCoroutine(routine);
        }

        // ── MPB write ────────────────────────────────────────────────────────────────

        private void PushFloat(int propId, float value)
        {
            if (_bodyRenderer == null) return;
            _bodyRenderer.GetPropertyBlock(_mpb);
            _mpb.SetFloat(propId, value);
            _bodyRenderer.SetPropertyBlock(_mpb);
        }
    }
}
