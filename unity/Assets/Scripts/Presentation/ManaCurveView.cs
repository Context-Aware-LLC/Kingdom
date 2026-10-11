using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;   // ICardDatabase lives here, not in Core.Cards
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// The eight-bucket mana curve strip under a deck being edited: costs 0..6, then 7+.
    /// A pure read of a decklist — it owns no state, so it is safe to rebuild on every mutation.
    /// </summary>
    public static class ManaCurveView
    {
        /// <summary>
        /// Bar height in PIXELS, not <c>Length.Percent</c>.
        ///
        /// <para>This is the reason the curve rendered as nothing for a while. <c>curve-col</c> is
        /// content-sized — <c>.db-curve</c> uses <c>align-items: flex-end</c> rather than
        /// <c>stretch</c>, so the column has no explicit height — and a percentage height with no
        /// resolved parent size silently collapses to 0 rather than erroring. The figure is
        /// <c>db-curve</c>'s 56px strip minus the cost label's line height plus its margin.</para>
        /// </summary>
        private const float MaxBarPx = 36f;

        private const int Buckets = 8;   // 0..6, then 7+

        /// <summary>Rebuild <paramref name="strip"/> from <paramref name="deck"/>. Both may be null,
        /// which is the not-editing case and draws nothing.</summary>
        public static void Rebuild(VisualElement strip, DeckList deck, ICardDatabase db)
        {
            if (strip == null || deck == null || db == null) return;

            strip.Clear();
            var counts = new int[Buckets];
            int peak = 1;
            foreach (var e in deck.Entries)
            {
                if (!db.TryGet(e.CardId, out var def)) continue;
                int b = Mathf.Clamp(def.Cost, 0, Buckets - 1);
                counts[b] += e.Count;
                if (counts[b] > peak) peak = counts[b];
            }

            for (int i = 0; i < Buckets; i++)
            {
                var col = new VisualElement(); col.AddToClassList("curve-col");
                var bar = new VisualElement(); bar.AddToClassList("curve-bar");
                bar.style.height = MaxBarPx * counts[i] / peak;
                var label = new Label(i < Buckets - 1 ? i.ToString() : "7+");
                label.AddToClassList("curve-label");
                col.Add(bar); col.Add(label);
                strip.Add(col);
            }
        }
    }
}
