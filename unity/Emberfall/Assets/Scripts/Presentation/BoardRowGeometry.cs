namespace Emberfall.Presentation
{
    /// <summary>
    /// The board row's arithmetic: where a position sits, and which position an x falls in. One
    /// expression each way, so the two can never disagree about the row they describe.
    ///
    /// <para>NO <c>UnityEngine</c>, DELIBERATELY, AND THAT IS LOAD-BEARING. Since #139 the drop's x
    /// is what decides where a minion is played, which means <see cref="DragTargetingModel"/> —
    /// documented as free of MonoBehaviour, Transform, collider and camera — has to be able to ask.
    /// It is not a style rule: <c>tests/Emberfall.Core.Tests</c> compiles that model in with a
    /// <c>&lt;Compile Include&gt;</c> and no Unity assemblies at all, so a <c>Mathf</c> or a
    /// <c>Vector3</c> reached from there breaks `dotnet test` while Unity stays green.</para>
    ///
    /// <para>RETIREMENT TRIGGER: this file is linked into that csproj by path. If you rename or move
    /// it, or add a UnityEngine dependency to it, fix
    /// <c>tests/Emberfall.Core.Tests/Emberfall.Core.Tests.csproj</c> in the same commit — a broken
    /// link there fails the headless build, not the Unity one.</para>
    /// </summary>
    public static class BoardRowGeometry
    {
        /// <summary>Distance between adjacent minions. Was <c>ProceduralBoardSetup.SlotSpacing</c>;
        /// it moved here so the placement solve could see it without dragging Unity along.</summary>
        public const float SlotSpacing = 1.35f;

        /// <summary>Where position <paramref name="index"/> sits when the row is centred about
        /// <paramref name="half"/>. Used both when the anchors are first built and every time the
        /// occupied count changes.</summary>
        public static float XForSlot(int index, float half) => (index - half) * SlotSpacing;

        /// <summary>
        /// THE INVERSE: which insert position does a world x fall in, on a row of
        /// <paramref name="count"/> minions? This is how a minion dropped from hand picks where it
        /// goes (Sam, 2026-08-12, #139: "the position the minion is played in should be determined
        /// by proximity to a particular X-axis").
        ///
        /// <para>It replaces 14 invisible slot colliders outright. Those asked the player to hit one
        /// of seven boxes that between them covered 63% of the row, and every miss failed silently —
        /// "like trying to shoot a basketball into an invisible Dixie cup" (Sam). Every x answers
        /// now, so the gesture cannot miss; only the ANSWER varies.</para>
        ///
        /// <para>The boundary between one position and the next is an existing minion's CENTRE, not
        /// the midpoint between two of them: cross a minion's centre and you land after it, which is
        /// the reading that matches "I dropped it on that minion's right-hand side". A row of
        /// <paramref name="count"/> minions has count+1 insert positions and the result is clamped to
        /// that range — so an empty board always answers 0 rather than offering seven
        /// indistinguishable choices that all produce the same board.</para>
        /// </summary>
        public static int InsertIndexForX(float worldX, int count)
        {
            if (count <= 0) return 0;

            // Minion i sits at XForSlot(i, half), so it is left of the drop when
            // i < worldX / SlotSpacing + half. The insert position is how many satisfy that.
            float half = (count - 1) * 0.5f;
            int index = (int)System.Math.Ceiling(worldX / SlotSpacing + half);

            if (index < 0) return 0;
            return index > count ? count : index;
        }
    }
}
