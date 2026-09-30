using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Where the local player's opening cards sit while they choose what to replace (#401): one
    /// row, apart from the hand, not overlapping, flat to the camera and larger than a hand card.
    /// Sam, 2026-09-30: "you separated the cards and displayed them head on nice and large".
    ///
    /// <para><b>It moves the HAND ANCHORS, not the cards.</b> The cards stay the hand's own
    /// presenters, parented to their hand slots, so tapping still reaches the same
    /// <see cref="ClickableCard"/>, the red replace mark still rides the card, and the intent the
    /// confirm button sends is untouched — this is presentation only. When the seat's mulligan is
    /// done, <see cref="BoardAnchors"/> puts the fan back and every card, kept or replaced, lands
    /// in the ordinary hand.</para>
    ///
    /// <para><b>The row sits ABOVE the confirm button's prompt.</b> It is nearer the camera than
    /// the button (which stays on the board midline, <see cref="MulliganConfirmButtonView"/>), so a
    /// card drawn over the button would take every press. The band runs from just above
    /// <see cref="MulliganConfirmButtonView.PromptTopWorld"/> to the top of the safe area.</para>
    /// </summary>
    internal static class MulliganRowLayout
    {
        // Same plane and gap as the Unearth offer, so the two head-on surfaces read as one idiom.
        private const float CameraDistance = 10f;
        private const float CardGap        = 0.28f;
        private const float MaxCardScale   = 2f;

        private const float RowWidthFraction  = 0.86f;
        private const float CardHeightFraction = 0.88f;

        // Viewport clearance above the prompt and below the safe top edge.
        private const float PromptClearance = 0.025f;
        private const float TopClearance    = 0.02f;

        // A band thinner than this (a camera nothing like the authored rig) would draw the cards
        // smaller than the hand they replace; the caller falls back to the fan instead.
        private const float MinBandHeight = 0.2f;

        /// <summary>
        /// Poses <paramref name="slots"/> as a head-on row of <paramref name="count"/> cards. Slots
        /// beyond <paramref name="count"/> park on the last live position, as in
        /// <see cref="HandFanLayout.Apply"/>. Returns false, touching nothing, when no usable band
        /// exists — the caller then lays out the ordinary fan.
        /// </summary>
        internal static bool Apply(Transform[] slots, int count, Camera camera)
        {
            if (slots == null || slots.Length == 0 || camera == null) return false;
            count = Mathf.Clamp(count, 0, slots.Length);
            if (count == 0) return false;

            Rect safe = FacingCardRow.SafeViewport();
            Vector3 prompt = camera.WorldToViewportPoint(MulliganConfirmButtonView.PromptTopWorld);
            if (prompt.z <= 0f) return false;
            float yMin = Mathf.Max(safe.yMin, prompt.y + PromptClearance);
            float yMax = safe.yMax - TopClearance;
            if (yMax - yMin < MinBandHeight) return false;

            var band = Rect.MinMaxRect(safe.xMin, yMin, safe.xMax, yMax);
            if (!FacingCardRow.TrySolve(camera, band, CameraDistance, count, CardGap, MaxCardScale,
                    RowWidthFraction, CardHeightFraction, out FacingCardRow.Solution row))
                return false;

            for (int i = 0; i < slots.Length; i++)
            {
                Transform slot = slots[i];
                if (slot == null) continue;

                int index = Mathf.Min(i, count - 1);
                slot.SetPositionAndRotation(row.Centre + row.Rotation * row.SlotOffset(index, count),
                                            row.Rotation);
                // The card draws at board size here (BoardController disables HandCardHover, which
                // owns the hand's RestScale), so the slot carries the whole row scale.
                float parentScale = slot.parent != null ? slot.parent.lossyScale.x : 1f;
                slot.localScale = Vector3.one * (row.CardScale / Mathf.Max(0.0001f, parentScale));
            }
            return true;
        }
    }
}
