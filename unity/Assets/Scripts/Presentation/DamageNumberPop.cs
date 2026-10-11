using TMPro;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// A small "-n" that rises off a wounded character and fades (Sam, 2026-10-11 — a trial; he
    /// vetoed it on 2026-10-04 in favour of the <see cref="HealthNumber"/> pulse, which stays).
    ///
    /// <para>It owns itself: spawned loose in the world rather than under the character, so a
    /// minion that dies of the hit does not take its number with it, and driven from Update so
    /// no host has to outlive it.</para>
    /// </summary>
    internal sealed class DamageNumberPop : MonoBehaviour
    {
        private const float PopSeconds   = 0.08f;
        private const float HoldSeconds  = 0.45f;
        private const float FadeSeconds  = 0.40f;
        private const float RiseDistance = 0.85f;
        private const float StartScale   = 0.6f;
        private const float FontSize     = 7f;
        // Nearer the camera than a lunging attacker, and drawn over it.
        private const float ZTowardCamera = -0.5f;
        private const int   SortingOrder  = 90;

        private TextMeshPro _label;
        private Vector3 _from;
        private float _delay;
        private float _elapsed;

        /// <summary>Show <c>-amount</c> at <paramref name="worldPosition"/> after
        /// <paramref name="delaySeconds"/>.</summary>
        public static void Spawn(Vector3 worldPosition, int amount, float delaySeconds)
        {
            if (!Application.isPlaying || amount <= 0) return;

            var go = new GameObject("DamageNumberPop");
            go.transform.position = worldPosition + new Vector3(0f, 0f, ZTowardCamera);

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text      = "-" + amount;
            tmp.fontSize  = FontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color     = HealthInk.Reddest;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode     = TextOverflowModes.Overflow;
            tmp.rectTransform.sizeDelta = new Vector2(4f, 1.5f);
            BoardController.ApplyUiFont(tmp);
            tmp.outlineColor = Color.black;
            tmp.outlineWidth = 0.25f;
            var mr = tmp.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = SortingOrder;

            var pop = go.AddComponent<DamageNumberPop>();
            pop._label = tmp;
            pop._from  = go.transform.position;
            pop._delay = delaySeconds;
            tmp.enabled = delaySeconds <= 0f;
        }

        private void Update()
        {
            if (_delay > 0f)
            {
                _delay -= Time.deltaTime;
                if (_delay > 0f) return;
                _label.enabled = true;
            }

            _elapsed += Time.deltaTime;
            float life = HoldSeconds + FadeSeconds;
            if (_elapsed >= life)
            {
                Destroy(gameObject);
                return;
            }

            float t = _elapsed / life;
            // Fast off the mark, slowing as it fades.
            transform.position = _from + Vector3.up * (RiseDistance * (1f - (1f - t) * (1f - t)));
            transform.localScale = Vector3.one *
                Mathf.Lerp(StartScale, 1f, Mathf.Clamp01(_elapsed / PopSeconds));
            _label.alpha = 1f - Mathf.Clamp01((_elapsed - HoldSeconds) / FadeSeconds);
        }
    }
}
