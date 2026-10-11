using System.Collections;
using TMPro;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The colour a health number rests at — "the redder the deader" (Sam, 2026-10-04).
    ///
    /// <para>1 health is the reddest red unless full health is 1. Between a scratch and that, red
    /// deepens with every point lost, so a character whose full health is 2 has no in-between and
    /// the gradient only exists from 3 up. Green means buffed AND unhurt: one point of damage
    /// replaces it with the faintest red.</para>
    /// </summary>
    internal static class HealthInk
    {
        public static readonly Color Buffed  = new Color(0.20f, 0.80f, 0.25f);
        public static readonly Color Reddest = new Color(1.00f, 0.10f, 0.08f);
        /// <summary>What the scale starts from: the red a character would show with nothing
        /// lost, if it showed any. One point down is already a step past this.</summary>
        private static readonly Color Scratch = new Color(1.00f, 0.80f, 0.76f);

        /// <param name="baseMax">Full health before any buff; equal to <paramref name="max"/>
        /// for a character that cannot be buffed.</param>
        public static Color Resting(int current, int max, int baseMax)
        {
            if (current >= max) return max > baseMax ? Buffed : Color.white;
            if (current <= 1) return Reddest;
            return Color.Lerp(Scratch, Reddest, (max - current) / (float)(max - 1));
        }
    }

    /// <summary>
    /// One health number on screen. When its value changes it does not just change: the old value
    /// swells and turns toward red (or green, for a gain), takes the new value at the peak, and
    /// settles back to its resting size and colour (Sam, 2026-10-04 — this replaces a floating
    /// damage number, which the game does not have).
    ///
    /// <para>Shared by <see cref="CardPresenter"/> and <see cref="HeroPlateView"/> so a minion and
    /// a hero take a hit the same way. The host runs the coroutine; this only owns the state.</para>
    /// </summary>
    internal sealed class HealthNumber
    {
        private const float SwellSeconds  = 0.10f;
        private const float SettleSeconds = 0.18f;
        private const float PeakScale     = 1.5f;

        private readonly MonoBehaviour _host;
        private TMP_Text _label;
        private int? _shown;
        private Color _rest;
        private Vector3 _restScale;
        private Coroutine _pulse;

        public HealthNumber(MonoBehaviour host) => _host = host;

        /// <summary>Draw <paramref name="value"/>, pulsing if it differs from what was last drawn.
        /// The first draw after construction or <see cref="Forget"/> never pulses.</summary>
        public void Show(TMP_Text label, int value, Color rest)
        {
            if (label == null) return;
            if (label != _label) { Settle(); _label = label; _shown = null; }

            bool changed = _shown.HasValue && _shown.Value != value;
            bool rose = changed && value > _shown.Value;
            _shown = value;
            _rest = rest;

            if (!changed)
            {
                // A repaint with the same value must not cut a running pulse short.
                if (_pulse == null) Draw();
                return;
            }

            Settle();
            if (!Application.isPlaying || _host == null || !_host.isActiveAndEnabled) return;

            _restScale = _label.transform.localScale;
            _pulse = _host.StartCoroutine(Pulse(value.ToString(), rose ? HealthInk.Buffed : HealthInk.Reddest));
        }

        /// <summary>The number no longer belongs to the character it was showing: the next
        /// <see cref="Show"/> draws without a pulse.</summary>
        public void Forget()
        {
            Settle();
            _shown = null;
        }

        /// <summary>Stop a running pulse and draw the final value at rest, now. For a host that is
        /// being disabled — its coroutine dies with it, and the number would be left swollen.</summary>
        public void Settle()
        {
            if (_pulse == null) return;
            if (_host != null) _host.StopCoroutine(_pulse);
            _pulse = null;
            if (_label == null) return;
            _label.transform.localScale = _restScale;
            Draw();
        }

        private void Draw()
        {
            if (_label == null || !_shown.HasValue) return;
            _label.text = _shown.Value.ToString();
            _label.color = _rest;
        }

        private IEnumerator Pulse(string text, Color peak)
        {
            Transform t = _label.transform;
            Color from = _label.color;

            for (float e = 0f; e < SwellSeconds; e += Time.deltaTime)
            {
                float p = e / SwellSeconds;
                t.localScale = _restScale * Mathf.Lerp(1f, PeakScale, p);
                _label.color = Color.Lerp(from, peak, p);
                yield return null;
            }

            _label.text = text;

            for (float e = 0f; e < SettleSeconds; e += Time.deltaTime)
            {
                float p = e / SettleSeconds;
                t.localScale = _restScale * Mathf.Lerp(PeakScale, 1f, p);
                _label.color = Color.Lerp(peak, _rest, p);
                yield return null;
            }

            t.localScale = _restScale;
            _label.color = _rest;
            _pulse = null;
        }
    }
}
