using System;
using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Visible turn clock (M2-KICKOFF §E2 minimal slice — a C1 entry requirement: "the timer C1
    /// exercises must be seeable, or timer failures masquerade as hangs"). Procedurally built
    /// world-space TMP, like <see cref="EndTurnButtonView"/> — no UXML/asset, so no per-clone
    /// .meta GUID drift.
    ///
    /// Binds to <see cref="IMatchSession.OnTurnTimer"/>: the authority sends an ABSOLUTE Unix-epoch
    /// deadline (arm / rope / reconnect) and this counts down <c>remaining = deadline - now</c>.
    /// Cross-machine clock skew is cosmetic — the server enforces the real deadline (decision 23).
    ///
    /// SCOPE: this is the "seeable" floor (E2). Rope ANIMATION, opponent-thinking and reconnect
    /// states are E3 — here the rope window only tints the label red. Never fires in hot-seat
    /// (<see cref="LocalMatchSession"/> raises no timer); the view is verified against a stub session.
    /// </summary>
    public sealed class TurnClockView : MonoBehaviour
    {
        private static readonly Color NormalColor = new Color(0.86f, 0.88f, 0.95f);
        private static readonly Color RopeColor   = new Color(0.90f, 0.25f, 0.22f);
        private static readonly Color NormalPlate = new Color(0.12f, 0.14f, 0.19f);
        private static readonly Color RopePlate   = new Color(0.30f, 0.08f, 0.07f);

        private const float ClockW = 1.30f;
        private const float ClockH = 0.46f;

        // Presentation-side rope window (cosmetic tint only). The SERVER owns the real rope at
        // _opts.RopeMs; this constant just decides when the on-screen number goes red, and does not
        // need to agree with it to the millisecond.
        private const long RopeWindowMs = 10_000;

        private TextMeshPro _label;
        private Renderer    _face;
        private MaterialPropertyBlock _mpb;
        private Func<long>  _nowMs;
        private long?       _deadlineMs;

        /// <param name="nowMs">Unix-epoch millisecond source; defaults to the wall clock. Injectable
        /// so a stub session / test can drive the countdown deterministically.</param>
        public void Init(Func<long> nowMs = null)
        {
            _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _mpb = new MaterialPropertyBlock();

            var faceGo = GameObject.CreatePrimitive(PrimitiveType.Quad);
            faceGo.name = "Face";
            faceGo.transform.SetParent(transform, false);
            faceGo.transform.localScale = new Vector3(ClockW, ClockH, 1f);
            Destroy(faceGo.GetComponent<MeshCollider>());
            _face = faceGo.GetComponent<Renderer>();
            MaterialCatalog.Assign(_face, MaterialCatalog.Plate);
            Tint(NormalPlate);

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            _label = labelGo.AddComponent<TextMeshPro>();
            _label.fontSize        = 1.35f;
            _label.alignment       = TextAlignmentOptions.Center;
            _label.color           = NormalColor;
            _label.fontStyle       = FontStyles.Bold;
            _label.rectTransform.sizeDelta = new Vector2(ClockW, ClockH);
            _label.textWrappingMode = TextWrappingModes.NoWrap;
            _label.text            = string.Empty;
            SetVisible(false);
        }

        /// <summary>Bind to <see cref="IMatchSession.OnTurnTimer"/> (already main-thread-marshaled by
        /// the networked decorator, so this runs on the Unity thread).</summary>
        public void OnTurnTimer(TurnTimer t)
        {
            _deadlineMs = t.DeadlineMs;
            SetVisible(true);
        }

        /// <summary>Hide when there is no live turn clock — the mulligan phase (the server arms the
        /// turn clock only afterwards) and match end. Wired alongside the other views' state pump.</summary>
        public void OnStateUpdated(GameState state)
        {
            if (state == null) return;
            if (state.Phase == TurnPhase.Mulligan || state.Outcome != MatchOutcome.InProgress)
            {
                _deadlineMs = null;
                SetVisible(false);
            }
        }

        private void Update()
        {
            if (_deadlineMs == null || _label == null) return;
            long remaining = Math.Max(0, _deadlineMs.Value - _nowMs());
            int  secs      = (int)((remaining + 999) / 1000);   // ceil, so "1" shows through the final second
            bool rope = remaining <= RopeWindowMs;
            _label.text  = rope ? $"ROPE {secs}" : $"TURN {secs}";
            _label.color = rope ? RopeColor : NormalColor;
            Tint(rope ? RopePlate : NormalPlate);
        }

        private void SetVisible(bool on)
        {
            if (_face != null) _face.gameObject.SetActive(on);
            if (_label != null) _label.enabled = on;
        }

        private void Tint(Color color)
        {
            if (_face == null) return;
            _face.GetPropertyBlock(_mpb);
            _mpb.SetColor("_BaseColor", color);
            _face.SetPropertyBlock(_mpb);
        }
    }
}
