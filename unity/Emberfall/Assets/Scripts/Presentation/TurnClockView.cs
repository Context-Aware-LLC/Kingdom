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

        // Presentation-side rope window (cosmetic tint only). The SERVER owns the real rope at
        // _opts.RopeMs; this constant just decides when the on-screen number goes red, and does not
        // need to agree with it to the millisecond.
        private const long RopeWindowMs = 10_000;

        private TextMeshPro _label;
        private Func<long>  _nowMs;
        private long?       _deadlineMs;

        /// <param name="nowMs">Unix-epoch millisecond source; defaults to the wall clock. Injectable
        /// so a stub session / test can drive the countdown deterministically.</param>
        public void Init(Func<long> nowMs = null)
        {
            _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(transform, false);
            _label = labelGo.AddComponent<TextMeshPro>();
            _label.fontSize        = 1.8f;
            _label.alignment       = TextAlignmentOptions.Center;
            _label.color           = NormalColor;
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
            _label.text  = secs.ToString();
            _label.color = remaining <= RopeWindowMs ? RopeColor : NormalColor;
        }

        private void SetVisible(bool on)
        {
            if (_label != null) _label.enabled = on;
        }
    }
}
