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
    /// (shaders/README.md §3, hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §5.2). Added to the card root by BoardController;
    /// call SetupRenderers() first, then Init() after ICardLiveState exists.
    ///
    /// Param ownership (shaders/README.md §3 consolidated table):
    ///   _FlashAmount    → bodyRenderer   SG_CardComposite DamageFlash branch — spike/decay on
    ///                     DamageApplied/HealthRestored
    ///   _ShroudAmount  → bodyRenderer   SG_Shroud alpha body — tween to 0.6 (not 1; keeps card readable)
    ///   _FrostAmount    → frostRenderer  SG_Frost — tween 0↔1 on AuraSet.Frosted change (0.3s)
    ///   SG_Glow enable  → glowRenderer   toggled by AuraSet.Glow (ARCANE_SURGE) | selection | AttackReadiness.Ready
    ///   SG_Aegis enable → aegisRenderer  toggled by AuraSet.Shield only; Bulwark is spatial (row offset)
    ///
    /// DEATH IS NOT HERE. The death burn is <see cref="CardDeathDissolve"/>, driven by
    /// BoardController, because dissolving one renderer out of ~13 dissolves nothing a player can
    /// see — and this binder's bodyRenderer wears M_CardBody (SG_CardComposite), which has no
    /// _DissolveAmount at all, so the tween that used to live here wrote to a property that did
    /// not exist on the material it wrote to.
    /// </summary>
    public sealed class CardMaterialBinder : MonoBehaviour
    {
        private Renderer _bodyRenderer;
        private Renderer _glowRenderer;   // null until SG_Glow shader asset assigned
        private Renderer _aegisRenderer;  // null until SG_Aegis shader asset assigned
        private Renderer _frostRenderer;  // wears M_Frost; the ONLY renderer that declares _FrostAmount

        private InstanceId _id;
        private ICardLiveState _live;
        private MatchRunner _matchRunner;
        private MaterialPropertyBlock _mpb;

        private Coroutine _flashRoutine;
        private Coroutine _frostRoutine;
        private Coroutine _shroudRoutine;
        private float _frostCurrent;
        private float _shroudCurrent;

        // Durations from hist-docs/source-snapshots/2026-07-31-pre-reorganization/techart/board-and-vfx.md §5 and shaders/README.md §3.
        private const float FlashDecay    = 0.25f;
        private const float FrostDur      = 0.30f;
        private const float ShroudDur    = 0.40f;
        private const float ShroudTarget = 0.60f; // spec: not full 1 — keeps minion readable under shroud

        // ── Setup (called by BoardController before Init) ──────────────────────────

        public void SetupRenderers(Renderer body, Renderer glow, Renderer aegis, Renderer frost = null)
        {
            _bodyRenderer  = body;
            _glowRenderer  = glow;
            _aegisRenderer = aegis;
            _frostRenderer = frost;
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
        private bool? _targetOverride; // non-null while targeting is armed: glow = valid-target only

        private void OnChanged(CardChange what)
        {
            if ((what & CardChange.Auras) != 0)
                ApplyAuras(_live.Auras);
            if ((what & CardChange.Affordance) != 0)
                ApplyGlow();
        }

        // Glow overlay is shared by three signals: Arcane Surge aura, selection highlight,
        // and can-attack readiness. Any one keeps it on. While targeting is armed
        // (_targetOverride set by BoardController from InputRouter.TargetingChanged) the
        // normal signals are suppressed so ONLY valid targets glow — targeting-state
        // visibility beats readiness noise for the duration of the aim.
        private void ApplyGlow()
        {
            if (_glowRenderer == null) return;
            if (_targetOverride.HasValue)
            {
                _glowRenderer.enabled = _targetOverride.Value || _selectionGlow;
                return;
            }
            bool auraGlow  = _live != null && (_live.Auras & AuraSet.Glow) != 0;
            bool readyGlow = _live != null && _live.AttackReadiness == AttackReadiness.Ready;
            _glowRenderer.enabled = _selectionGlow || auraGlow || readyGlow;
        }

        /// <summary>Targeting-mode glow override: true/false while armed (valid / not a
        /// target), null to restore the normal aura|selection|readiness combine.</summary>
        public void SetTargetOverride(bool? isValidTarget)
        {
            _targetOverride = isValidTarget;
            ApplyGlow();
        }

        private void ApplyAuras(AuraSet auras)
        {
            float frostTarget   = (auras & AuraSet.Frosted)  != 0 ? 1f            : 0f;
            float shroudTarget = (auras & AuraSet.Shroud) != 0 ? ShroudTarget : 0f;

            // _FrostAmount goes to the FROST renderer, not the body. The body wears M_CardBody
            // (SG_CardComposite), which does not declare the property — pushing it there set
            // nothing, silently, which is why the frost cue never appeared.
            //
            // The renderer is enabled for the whole tween including the thaw, and switched off only
            // once the amount is back to 0; disabling it the moment Frosted clears would pop the ice
            // away instead of melting it.
            if (!Mathf.Approximately(_frostCurrent, frostTarget))
            {
                if (_frostRenderer != null && frostTarget > 0f) _frostRenderer.enabled = true;
                Restart(ref _frostRoutine, TweenFloat(
                    v =>
                    {
                        _frostCurrent = v;
                        PushFloatTo(_frostRenderer, ShaderIds.FrostAmount, v);
                        if (_frostRenderer != null && Mathf.Approximately(v, 0f))
                            _frostRenderer.enabled = false;
                    },
                    _frostCurrent, frostTarget, FrostDur));
            }

            if (!Mathf.Approximately(_shroudCurrent, shroudTarget))
                Restart(ref _shroudRoutine, TweenFloat(
                    v => { _shroudCurrent = v; PushFloat(ShaderIds.ShroudAmount, v); },
                    _shroudCurrent, shroudTarget, ShroudDur));

            // Glow: combined signal (aura / selection / can-attack) — see ApplyGlow().
            ApplyGlow();

            // Aegis underlay: Aegis only. Bulwark reads spatially (front row,
            // decision 2026-07-17) — no keyword glow, so the shared gold bubble
            // unambiguously means Shield.
            if (_aegisRenderer != null)
                _aegisRenderer.enabled = (auras & AuraSet.Shield) != 0;
        }

        // ── MatchRunner.OnMatchEvent — one-shot damage/heal flash ─────────────────

        private void OnMatchEvent(IReadOnlyList<GameEvent> batch)
        {
            foreach (GameEvent e in batch)
            {
                bool concerns = (e.Target.HasValue && e.Target.Value.Equals(_id))
                             || (e.Source.HasValue && e.Source.Value.Equals(_id));
                if (!concerns) continue;

                switch (e.Kind)
                {
                    case GameEventKind.DamageApplied:
                    case GameEventKind.HealthRestored:
                        Restart(ref _flashRoutine, FlashDecayRoutine());
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

        private void PushFloat(int propId, float value) => PushFloatTo(_bodyRenderer, propId, value);

        // Per-instance, via MaterialPropertyBlock: the shared material asset must never be written,
        // or every card on the board frosts when one does.
        private void PushFloatTo(Renderer target, int propId, float value)
        {
            if (target == null) return;
            target.GetPropertyBlock(_mpb);
            _mpb.SetFloat(propId, value);
            target.SetPropertyBlock(_mpb);
        }
    }
}
