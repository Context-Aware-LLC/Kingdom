using Emberfall.Core;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// EF-062. Defines the board's logical world-space bounds and the device-facing layout
    /// budgets derived from them. The live perspective rig consumes these bounds; the original
    /// orthographic safe-area solve remains as a deterministic reference and compatibility path.
    ///
    /// <para>The stable fact is the logical design box: height 9.2 and width 15.76 world units.
    /// Camera fitting may change projected scale, but gameplay anchors and clearance checks stay
    /// expressed against that one box.</para>
    ///
    /// <para>RETIREMENT TRIGGER (NOR-358): if you unlock portrait autorotation
    /// (ProjectSettings.asset <c>allowedAutorotateToPortrait</c>), revisit all three
    /// UI PanelSettings assets — Deckbuilder, MainMenu, MatchSettings. They currently use
    /// <c>ScaleWithScreenSize</c> (<c>m_ScaleMode: 2</c>) with <c>m_Match: 1</c> (pure height
    /// axis). That choice follows from the board being landscape-only: height is the stable axis
    /// across all supported aspects. Portrait support makes height unstable; the match axis must
    /// be re-evaluated before any portrait build ships.</para>
    ///
    /// <para>Measured 2026-08-02 by <c>tooling/scripts/device_layout.py</c>, which parses these
    /// same constants out of the shipping source and prints the full table. Run it rather than
    /// re-deriving anything here by hand.</para>
    /// </summary>
    public static class DeviceLayout
    {
        /// <summary>
        /// Half the logical board height. <see cref="BoardCameraRig"/> fits this bound rather than
        /// relying on the legacy orthographic size serialized in BoardNetworked.unity.
        /// </summary>
        public const float DesignHalfHeight = 4.6f;

        /// <summary>
        /// Half the world width the bottom row actually needs, in world units. Set by the
        /// widest element of the seat cluster: the outboard hero socket, whose edge is
        /// <c>ProceduralBoardSetup.HeroClusterOuterEdge</c> 7.88. The deck pile is second at
        /// 7.83; the narrower portrait is deliberately inset between its two sockets.
        ///
        /// RETIREMENT TRIGGER: if you move the hero cluster, DeckX or PileW outboard, this number
        /// and <see cref="MinAspect"/> move with them, and CLAUDE.md / the device-layout spec
        /// state a 16:9 floor that stops being true. DeviceLayoutVerify fails on the mismatch
        /// rather than letting it ship silently.
        /// </summary>
        public const float DesignHalfWidth = 7.88f;

        /// <summary>
        /// Narrowest landscape aspect at which the hero cluster still fits on screen: 1.71304.
        /// 16:9 (1.7778) clears it by 0.298 world units — 35 px on a 1080p panel, which is the
        /// entire horizontal safe-area budget at that aspect. 16:10 (1.6) and 4:3 (1.333) do
        /// not clear it at all.
        /// </summary>
        public const float MinAspect = DesignHalfWidth / DesignHalfHeight;

        /// <summary>
        /// Widest aspect the backdrop art covers. BoardPlate_Master.png is 4612 x 2080, which
        /// at <c>tex.height / 9.2</c> = 226.087 px/unit spans |x| &lt;= 10.1996 — an aspect of
        /// 2.21731. Past this the plate stops and bare camera background shows at the left and
        /// right edges: 3 px per side at 20:9 (2.2222, the Pixel 5a), 97 px per side at the
        /// 21:9 of an Xperia 1 V.
        ///
        /// RETIREMENT TRIGGER: re-baking BoardPlate_Master at a different width changes this.
        /// It is asserted against the texture's real dimensions in DeviceLayoutVerify.
        /// </summary>
        public const float MaxAspect = 2.21731f;

        /// <summary>Pixel width of BoardPlate_Master.png, asserted by DeviceLayoutVerify.</summary>
        public const int BackdropTexWidth = 4612;

        /// <summary>Pixel height of BoardPlate_Master.png, asserted by DeviceLayoutVerify.</summary>
        public const int BackdropTexHeight = 2080;

        /// <summary>
        /// What <see cref="Solve"/> worked out for one screen + safe-area pair.
        /// </summary>
        public readonly struct Solution
        {
            /// <summary>Camera orthographicSize to use. Never below <see cref="DesignHalfHeight"/>.</summary>
            public readonly float OrthoSize;

            /// <summary>
            /// World-space offset to add to the camera's x/y so the SAFE rect, rather than the
            /// physical screen, is centred on the board origin.
            /// </summary>
            public readonly Vector2 CentreOffset;

            /// <summary>Full visible world height, <c>2 * OrthoSize</c> — what the backdrop must span.</summary>
            public float VisibleHeight => OrthoSize * 2f;

            /// <summary>True when the safe rect is the whole screen and nothing had to give.</summary>
            public readonly bool IsIdentity;

            public Solution(float orthoSize, Vector2 centreOffset, bool isIdentity)
            {
                OrthoSize = orthoSize;
                CentreOffset = centreOffset;
                IsIdentity = isIdentity;
            }
        }

        /// <summary>
        /// Fit the design box inside the device's safe rect WITHOUT letterboxing.
        ///
        /// <para>The ticket's reference point is that Hearthstone renders ~2154 of a Pixel 5a's
        /// 2400 px and throws away 10% of the screen. This does the other thing: the camera
        /// still covers the physical screen edge to edge, so the backdrop fills it, and instead
        /// the camera zooms out and re-centres until the design box lands entirely inside the
        /// safe rect. What that costs is scale, not screen — 6.1% at a 24 dp gesture bar,
        /// 12.2% at a 48 dp navigation bar, on a 1080p panel.</para>
        ///
        /// <para>Below <see cref="MinAspect"/> the width term takes over and the same solve
        /// zooms out to fit the plate rather than clipping it, so an unsupported aspect
        /// degrades instead of losing the hero.</para>
        /// </summary>
        /// <param name="safeAreaPx"><c>Screen.safeArea</c>, in pixels, origin bottom-left.</param>
        /// <param name="screenW"><c>Screen.width</c>.</param>
        /// <param name="screenH"><c>Screen.height</c>.</param>
        public static Solution Solve(Rect safeAreaPx, int screenW, int screenH)
        {
            if (screenW <= 0 || screenH <= 0)
                return new Solution(DesignHalfHeight, Vector2.zero, true);

            // A safe area of zero size is what a platform with no cutout API reports on some
            // Android OEM builds. Treat it as "no insets" rather than dividing by it.
            float safeW = safeAreaPx.width  > 1f ? Mathf.Min(safeAreaPx.width,  screenW) : screenW;
            float safeH = safeAreaPx.height > 1f ? Mathf.Min(safeAreaPx.height, screenH) : screenH;
            float safeX = safeAreaPx.width  > 1f ? safeAreaPx.x : 0f;
            float safeY = safeAreaPx.height > 1f ? safeAreaPx.y : 0f;

            // Visible world half-height at orthoSize S is S; the safe rect owns S * safeH/screenH
            // of it, and must hold DesignHalfHeight.
            float byHeight = DesignHalfHeight * screenH / safeH;

            // Half-width at S is S * aspect = S * screenW/screenH; the safe rect owns
            // S * screenW/screenH * safeW/screenW = S * safeW/screenH of it, and must hold
            // DesignHalfWidth. The screenW terms cancel — width depends on the safe rect's
            // pixel width against the screen's pixel HEIGHT, which is the ortho camera's only
            // scale input.
            float byWidth = DesignHalfWidth * screenH / safeW;

            float ortho = Mathf.Max(DesignHalfHeight, Mathf.Max(byHeight, byWidth));

            // Re-centre: shift the camera so the SAFE rect's centre, not the screen's, sits on
            // the board origin. World y of pixel row p is (camY - S) + (p/screenH)*2S, so
            // putting the safe centre at 0 needs camY = -(safeCentre - screenCentre)/screenH*2S.
            //
            // THE SIGN IS THE OPPOSITE OF THE INTUITION. A bottom inset puts the safe rect's
            // centre ABOVE the screen centre, and the camera moves DOWN to meet it — which is
            // what makes the board appear to lift clear of the gesture bar. SelfCheck case 5
            // asserts the resulting world extents rather than the offset, so getting this
            // backwards fails the harness instead of shipping a board pushed INTO the bar.
            float offX = -((safeX + safeW * 0.5f) - screenW * 0.5f) / screenH * (2f * ortho);
            float offY = -((safeY + safeH * 0.5f) - screenH * 0.5f) / screenH * (2f * ortho);

            bool identity = Mathf.Approximately(ortho, DesignHalfHeight)
                            && Mathf.Abs(offX) < 1e-4f && Mathf.Abs(offY) < 1e-4f;

            return new Solution(ortho, new Vector2(offX, offY), identity);
        }

        /// <summary>
        /// Safe-area insets for a UI Toolkit panel, in PANEL units (#237).
        ///
        /// <para>Named for the four screen edges, not for CSS order. All four are &gt;= 0 and are
        /// measured inward from the corresponding panel edge, so they drop straight onto
        /// padding-left / padding-top / padding-right / padding-bottom.</para>
        /// </summary>
        public readonly struct PanelInsets
        {
            public readonly float Left, Top, Right, Bottom;

            public PanelInsets(float left, float top, float right, float bottom)
            {
                Left = left; Top = top; Right = right; Bottom = bottom;
            }

            /// <summary>True when nothing is inset — a desktop screen, the Editor, or an Android
            /// build whose OEM reports no cutout.</summary>
            public bool IsIdentity => Left < 0.5f && Top < 0.5f && Right < 0.5f && Bottom < 0.5f;

            public override string ToString()
                => $"L{Left:F1} T{Top:F1} R{Right:F1} B{Bottom:F1}";
        }

        /// <summary>
        /// The UI Toolkit counterpart of <see cref="Solve"/> (#237). The board camera has had a
        /// safe-area answer since EF-062; the UI Toolkit screens had none, so on a Pixel 5a the
        /// identity plate, the shard counter and the footer sat under the 136 px cutout.
        ///
        /// <para>WHY THIS IS NOT SIMPLY THE PIXEL RECT. A panel does not draw in screen pixels.
        /// All three PanelSettings use <c>ScaleWithScreenSize</c>, so a panel has its own
        /// coordinate space and an inset expressed in px is wrong there by the scale factor.
        /// Taking the panel's own height in its own units is what makes this exact without this
        /// file having to know the reference resolution or the match axis — both are authored in
        /// the asset and free to change (see the NOR-358 retirement trigger above).</para>
        ///
        /// <para>Y IS FLIPPED between the two spaces and that is the whole trap.
        /// <c>Screen.safeArea</c> has its origin bottom-left; a panel has its origin top-left. So
        /// the safe area's <c>yMin</c> — the gesture bar — is the BOTTOM padding, and the TOP
        /// padding comes from <c>screenH - yMax</c>. Swapping the two is invisible on any device
        /// whose insets happen to be symmetric, and every phone tested so far has a zero top
        /// inset, so nothing on the bench would catch it.</para>
        /// </summary>
        /// <param name="safeAreaPx"><c>Screen.safeArea</c>, in pixels, origin bottom-left.</param>
        /// <param name="screenW"><c>Screen.width</c>.</param>
        /// <param name="screenH"><c>Screen.height</c>.</param>
        /// <param name="panelH">The panel's height in PANEL units — the resolved height of the
        /// panel's own visual tree. The px-to-panel factor is uniform, so one axis fixes both.</param>
        public static PanelInsets SolvePanelInsets(Rect safeAreaPx, int screenW, int screenH, float panelH)
        {
            if (screenW <= 0 || screenH <= 0 || panelH <= 0f)
                return default;

            // Same degenerate-safe-area guard as Solve: some Android OEM builds report an empty
            // Rect where they mean "no cutout". Read it as no insets, not as a screen that is
            // entirely unsafe.
            if (safeAreaPx.width <= 1f || safeAreaPx.height <= 1f)
                return default;

            float leftPx   = Mathf.Max(0f, safeAreaPx.xMin);
            float rightPx  = Mathf.Max(0f, screenW - safeAreaPx.xMax);
            float bottomPx = Mathf.Max(0f, safeAreaPx.yMin);           // origin bottom-left ...
            float topPx    = Mathf.Max(0f, screenH - safeAreaPx.yMax);  // ... so THIS is the top

            float toPanel = panelH / screenH;
            return new PanelInsets(leftPx * toPanel, topPx * toPanel,
                                   rightPx * toPanel, bottomPx * toPanel);
        }

        /// <summary>
        /// On-screen pixel width of a card ON THE BOARD at a given ortho size. 137.3 px on any
        /// 1080p phone at the authored 4.6 — 52% of the 265 px collection tile EF-050's acuity
        /// margin was computed against, which is the whole reason EF-062 gates that re-check.
        /// A card IN HAND is drawn larger; see <see cref="HandCardPixelWidth"/>.
        /// </summary>
        public static float CardPixelWidth(int screenH, float orthoSize)
            => BoardController.CardW * screenH / (2f * orthoSize);

        /// <summary>On-screen pixel height of a board card. 193.7 px on any 1080p phone at 4.6.</summary>
        public static float CardPixelHeight(int screenH, float orthoSize)
            => BoardController.CardH * screenH / (2f * orthoSize);

        /// <summary>
        /// On-screen pixel width of a card IN HAND — the board figure times
        /// <see cref="HandFanLayout.RestScale"/>. This is the number the rules-text legibility
        /// re-check is measured against (NOR-244), not <see cref="CardPixelWidth"/>.
        /// </summary>
        public static float HandCardPixelWidth(int screenH, float orthoSize)
            => HandFanLayout.HandCardW * screenH / (2f * orthoSize);

        /// <summary>On-screen pixel height of a card in hand.</summary>
        public static float HandCardPixelHeight(int screenH, float orthoSize)
            => HandFanLayout.HandCardH * screenH / (2f * orthoSize);

        /// <summary>
        /// Asserts that <see cref="DesignHalfWidth"/> still equals what the board actually
        /// builds, and that <see cref="Solve"/> does what it claims. Called from the EF-001
        /// headless harness through <c>DeviceLayoutVerify</c>; lives here because the layout
        /// constants it reads are assembly-private to Emberfall.Presentation.
        /// </summary>
        /// <param name="report">Human-readable lines, one per assertion, pass or fail.</param>
        /// <returns>True when every assertion holds.</returns>
        public static bool SelfCheck(out string report)
        {
            var sb = new System.Text.StringBuilder();
            bool ok = true;

            // 1. The design half-width is the widest thing the bottom row builds.
            float heroEdge = ProceduralBoardSetup.HeroClusterOuterEdge;
            float deckEdge = ProceduralBoardSetup.DeckX + DeckPileView.PileW * 0.5f;
            float widest = Mathf.Max(heroEdge, deckEdge);
            ok &= Line(sb, Mathf.Abs(widest - DesignHalfWidth) < 0.001f,
                $"DesignHalfWidth {DesignHalfWidth:F4} == widest built element {widest:F4} "
                + $"(hero cluster {heroEdge:F4}, deck pile {deckEdge:F4})");

            // 1b. The vertical mana layout is exactly 2 x 5 square cells, with all ten
            //     MatchEngine slots represented and the tray still inside the design box.
            float manaCellW = ManaTrayView.RailW / ManaTrayView.GridColumns;
            float manaCellH = ManaTrayView.RailH / ManaTrayView.GridRows;
            int manaSlots = ManaTrayView.GridColumns * ManaTrayView.GridRows;
            float manaEdgeY = ProceduralBoardSetup.ManaGridY + ManaTrayView.RailH * 0.5f;
            ok &= Line(sb, Mathf.Abs(manaCellW - manaCellH) < 0.001f,
                $"mana cells are square ({manaCellW:F3} x {manaCellH:F3})");
            ok &= Line(sb, manaSlots == MatchEngine.MaxMana,
                $"mana grid carries {manaSlots} slots == MatchEngine.MaxMana {MatchEngine.MaxMana}");
            ok &= Line(sb, manaEdgeY <= DesignHalfHeight + 0.001f,
                $"mana grid edge |y| {manaEdgeY:F3} <= design edge {DesignHalfHeight:F2}");

            // 1c. The enlarged local sockets intentionally consume the remaining vertical
            //     budget, but must never cross the safe-area design edge.
            float socketEdgeY = ProceduralBoardSetup.HeroY + ProceduralBoardSetup.SocketDrop
                                + HeroSocketsView.FrameSize * 0.5f;
            ok &= Line(sb, socketEdgeY <= DesignHalfHeight + 0.001f,
                $"local socket edge |y| {socketEdgeY:F3} <= design edge {DesignHalfHeight:F2}");

            // 2. 16:9 is inside the declared range; 16:10 is outside it. Those two facts are
            //    what docs/dev-docs/ui/device-layout.md §2 promises, so they are what is checked.
            ok &= Line(sb, MinAspect <= 16f / 9f,
                $"MinAspect {MinAspect:F5} <= 16:9 {16f / 9f:F5} — 16:9 is supported");
            ok &= Line(sb, MinAspect > 1.6f,
                $"MinAspect {MinAspect:F5} > 16:10 1.60000 — 16:10 is correctly out of range");

            // 3. MaxAspect is the backdrop's real coverage, not a wish.
            float plateAspect = BackdropTexWidth / (float)BackdropTexHeight;
            ok &= Line(sb, Mathf.Abs(plateAspect - MaxAspect) < 0.0005f,
                $"MaxAspect {MaxAspect:F5} == BoardPlate_Master {BackdropTexWidth}x"
                + $"{BackdropTexHeight} aspect {plateAspect:F5}");

            // 4. No insets at a supported aspect must change nothing at all — this component
            //    runs on every board and an identity case that is not identity would move the
            //    whole layout on desktop.
            Solution full = Solve(new Rect(0f, 0f, 2400f, 1080f), 2400, 1080);
            ok &= Line(sb, full.IsIdentity && Mathf.Approximately(full.OrthoSize, DesignHalfHeight),
                $"2400x1080 with no insets -> identity, orthoSize {full.OrthoSize:F4}");

            // 5. A bottom inset must put the design box inside the safe rect. 132 px is a 48 dp
            //    navigation bar at the 2.75x density of a 1080p 6.3" phone. Checked by mapping
            //    the safe rect's pixel edges back into world space through the solution — an
            //    assertion on the offset's sign alone would pass a board shoved INTO the bar.
            const float inset = 132f;
            Solution bar = Solve(new Rect(0f, inset, 2400f, 1080f - inset), 2400, 1080);
            SafeWorldRect(bar, new Rect(0f, inset, 2400f, 1080f - inset), 2400, 1080,
                out float bl, out float bb, out float br, out float bt);
            ok &= Line(sb, bb <= -DesignHalfHeight + 0.001f && bt >= DesignHalfHeight - 0.001f,
                $"48dp bottom inset -> orthoSize {bar.OrthoSize:F4}, safe rect spans y "
                + $"{bb:F4}..{bt:F4}, holds +/-{DesignHalfHeight:F2}");
            ok &= Line(sb, bl <= -DesignHalfWidth + 0.001f && br >= DesignHalfWidth - 0.001f,
                $"48dp bottom inset -> safe rect spans x {bl:F4}..{br:F4}, "
                + $"holds +/-{DesignHalfWidth:F2}");

            // 5b. A left-edge cutout, which is where a portrait hole-punch lands in landscape.
            Solution cut = Solve(new Rect(96f, 0f, 2400f - 96f, 1080f), 2400, 1080);
            SafeWorldRect(cut, new Rect(96f, 0f, 2400f - 96f, 1080f), 2400, 1080,
                out float cl, out float cb, out float cr, out float ct);
            ok &= Line(sb, cl <= -DesignHalfWidth + 0.001f && cr >= DesignHalfWidth - 0.001f
                           && cb <= -DesignHalfHeight + 0.001f && ct >= DesignHalfHeight - 0.001f,
                $"96px left cutout -> orthoSize {cut.OrthoSize:F4}, safe rect spans x "
                + $"{cl:F4}..{cr:F4} y {cb:F4}..{ct:F4}");

            // 6. A degenerate safe area (some Android OEM builds report an empty Rect) must be
            //    read as "no insets", not divided by.
            Solution zero = Solve(new Rect(0f, 0f, 0f, 0f), 2400, 1080);
            ok &= Line(sb, zero.IsIdentity,
                $"empty safeArea -> identity, orthoSize {zero.OrthoSize:F4}");

            // 7. The rules band on the worst card in the fan (outermost, so it carries the full
            //    sag) must clear the bottom screen edge AT REST. This is the assertion NOR-244
            //    exists to hold: HandFanLayout.RestScale enlarges the text, and CentreY is what
            //    puts it on screen to be read — either one moving alone silently undoes the fix.
            const float bandBottomFrac = 0.305f;   // CardPresenter: centre 0.20 CardH below the
                                                   // card centre, half-height 0.105 CardH
            float bandBottom = -(HandFanLayout.CentreY + HandFanLayout.Sag)
                               - bandBottomFrac * HandFanLayout.HandCardH;
            ok &= Line(sb, bandBottom > -DesignHalfHeight,
                $"hand rules band bottom y {bandBottom:F4} > screen edge {-DesignHalfHeight:F2} "
                + $"— readable at rest, {(bandBottom + DesignHalfHeight):F3} clear");

            // 7c. THE BOARD ROW'S TWO CLEARANCES (#234). The card on the board grew by 1/0.9 and
            //     spent the whole of the vertical slack between the centreline and the resting
            //     hand. Nothing guarded that budget before, which is why the ticket costed only the
            //     horizontal one and read the row as having 3.2 units to spare — it does, sideways.
            //     These are the two edges the card actually stops against, and either one going
            //     negative is invisible until a screenshot of a full board with taunts on it.
            //
            //     Assert, do not merely report: both were solved to a target here, so a later tune
            //     of CardH, RowY or BulwarkRowOffset that eats one is a regression, not a choice.

            // The lane at the centreline, where a bulwarked minion of each seat steps forward to
            // meet the other. Both seats step, so the visible gap is twice this.
            float bulwarkLane = ProceduralBoardSetup.RowY
                                - BoardController.BulwarkRowOffset
                                - BoardController.CardH * 0.5f;
            ok &= Line(sb, bulwarkLane > 0.05f,
                $"bulwark row inner edge |y| {bulwarkLane:F4} > 0.05 — two opposed taunt rows keep "
                + $"a {2f * bulwarkLane:F3} lane at the centreline "
                + $"(RowY {ProceduralBoardSetup.RowY:F2} - step {BoardController.BulwarkRowOffset:F2} "
                + $"- CardH/2 {BoardController.CardH * 0.5f:F4})");

            // The hand fan draws IN FRONT of the board row and tucks under it deliberately
            // (HandFanLayout.CentreY). That is only safe while its top edge stops above the stat
            // row — the lowest thing a minion carries and the one thing on it you must be able to
            // read at a glance. Not "above the card's bottom edge": the overlap is the design.
            float statRowY = ProceduralBoardSetup.RowY + BoardController.CardH * BoardController.StatRowFrac;
            float handClear = -HandFanLayout.TopEdgeY - statRowY;
            ok &= Line(sb, handClear > 0.05f,
                $"hand top |y| {-HandFanLayout.TopEdgeY:F4} clears the board stat row at "
                + $"{statRowY:F4} by {handClear:F4} — the fan overlaps the frame skirt and not the "
                + $"attack/health digits");

            // 7b. The UI Toolkit insets (#237). The board camera has had a safe-area answer
            //     since EF-062 and the panels had none; this is that answer's arithmetic.
            //     Numbers are the Pixel 5a's, quoted from the SafeAreaCamera line on the
            //     2026-08-21 device run: 2400x1080, safeArea x136 y0 w2264 h1080.
            const int p5aW = 2400, p5aH = 1080;
            var p5aSafe = new Rect(136f, 0f, 2264f, 1080f);

            // At a 1:1 panel (panelH == screenH) the insets ARE the pixel insets — the case that
            // pins the px arithmetic on its own, before any scaling is involved.
            PanelInsets raw = SolvePanelInsets(p5aSafe, p5aW, p5aH, p5aH);
            ok &= Line(sb, Mathf.Abs(raw.Left - 136f) < 0.01f && Mathf.Abs(raw.Right) < 0.01f
                           && Mathf.Abs(raw.Top) < 0.01f && Mathf.Abs(raw.Bottom) < 0.01f,
                $"Pixel 5a safeArea x136 w2264 @1:1 panel -> insets {raw} (expect L136 T0 R0 B0)");

            // And scaled into a panel's own units. An inset applied WITHOUT this conversion is
            // wrong by the whole scale factor, which on this screen is over 4x.
            PanelInsets scaled = SolvePanelInsets(p5aSafe, p5aW, p5aH, 250f);
            ok &= Line(sb, Mathf.Abs(scaled.Left - 136f * 250f / 1080f) < 0.01f,
                $"same, into a 250-unit-tall panel -> insets {scaled} "
                + $"(expect L{136f * 250f / 1080f:F1}, i.e. px / {1080f / 250f:F2})");

            // The y flip. A gesture bar is a BOTTOM screen inset and must come out as Bottom,
            // never Top — every device tested so far has a zero top inset, so a swapped pair
            // would look correct on all of them.
            PanelInsets barIns = SolvePanelInsets(new Rect(0f, 132f, 2400f, 1080f - 132f), p5aW, p5aH, p5aH);
            ok &= Line(sb, Mathf.Abs(barIns.Bottom - 132f) < 0.01f && Mathf.Abs(barIns.Top) < 0.01f,
                $"48dp gesture bar (a BOTTOM screen inset) -> {barIns} (expect B132 T0, not the reverse)");

            // The mirror of it. The pair is what actually pins the flip; either alone does not.
            PanelInsets topIns = SolvePanelInsets(new Rect(0f, 0f, 2400f, 1080f - 90f), p5aW, p5aH, p5aH);
            ok &= Line(sb, Mathf.Abs(topIns.Top - 90f) < 0.01f && Mathf.Abs(topIns.Bottom) < 0.01f,
                $"90px top inset -> {topIns} (expect T90 B0)");

            // Identity and degenerate cases: this runs on every screen, and a non-identity
            // identity would move all six layouts on desktop.
            ok &= Line(sb, SolvePanelInsets(new Rect(0f, 0f, 2400f, 1080f), p5aW, p5aH, 250f).IsIdentity,
                "2400x1080 with no insets -> panel insets identity");
            ok &= Line(sb, SolvePanelInsets(new Rect(0f, 0f, 0f, 0f), p5aW, p5aH, 250f).IsIdentity,
                "empty safeArea -> panel insets identity (the OEM 'no cutout API' case)");

            // 8. The measured figure EF-062 exists to produce, restated so a harness run
            //    carries it. Not an assertion — a number in the log.
            sb.AppendLine($"      card @1080p, orthoSize {DesignHalfHeight:F2}: board "
                + $"{CardPixelWidth(1080, DesignHalfHeight):F1} x "
                + $"{CardPixelHeight(1080, DesignHalfHeight):F1} px, hand "
                + $"{HandCardPixelWidth(1080, DesignHalfHeight):F1} x "
                + $"{HandCardPixelHeight(1080, DesignHalfHeight):F1} px "
                + $"(x{HandFanLayout.RestScale:F2})");

            report = sb.ToString().TrimEnd();
            return ok;
        }

        /// <summary>
        /// Maps a safe rect's pixel edges into world space through a solution, assuming the
        /// camera was authored at the origin. The inverse of what <see cref="Solve"/> did, so
        /// a sign or scale error in one shows up as a failed extent in the other.
        /// </summary>
        private static void SafeWorldRect(Solution s, Rect safePx, int screenW, int screenH,
                                          out float left, out float bottom,
                                          out float right, out float top)
        {
            float halfH = s.OrthoSize;
            float halfW = s.OrthoSize * screenW / screenH;
            float camX = s.CentreOffset.x;
            float camY = s.CentreOffset.y;

            left   = camX - halfW + safePx.xMin / screenW * (2f * halfW);
            right  = camX - halfW + safePx.xMax / screenW * (2f * halfW);
            bottom = camY - halfH + safePx.yMin / screenH * (2f * halfH);
            top    = camY - halfH + safePx.yMax / screenH * (2f * halfH);
        }

        private static bool Line(System.Text.StringBuilder sb, bool pass, string text)
        {
            sb.AppendLine($"      {(pass ? "ok  " : "FAIL")}  {text}");
            return pass;
        }
    }
}
