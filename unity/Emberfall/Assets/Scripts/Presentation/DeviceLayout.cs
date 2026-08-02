using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// EF-062. The board is an orthographic world-space scene, which makes its on-screen
    /// geometry exact arithmetic rather than a matter of taste. This is where that arithmetic
    /// lives, and the only place a device-facing layout number may be written down.
    ///
    /// <para>THE ONE FACT EVERYTHING ELSE FOLLOWS FROM: an orthographic camera fixes the
    /// VERTICAL extent and lets width float with aspect. So the short edge alone sets scale —
    /// every 1080p phone renders a byte-identical card no matter its aspect or its ppi — and
    /// a wider aspect buys horizontal room rather than shrinking anything.</para>
    ///
    /// <para>Measured 2026-08-02 by <c>tooling/scripts/device_layout.py</c>, which parses these
    /// same constants out of the shipping source and prints the full table. Run it rather than
    /// re-deriving anything here by hand.</para>
    /// </summary>
    public static class DeviceLayout
    {
        /// <summary>
        /// Half the visible world height, and the authored orthographicSize of BoardCamera in
        /// Board.unity and BoardNetworked.unity. DeviceLayoutVerify asserts the scenes agree —
        /// a second copy of a board constant has gone stale on this project before.
        /// </summary>
        public const float DesignHalfHeight = 4.6f;

        /// <summary>
        /// Half the world width the bottom row actually needs, in world units. Set by the
        /// widest element of the seat cluster: the hero plate, whose centre is
        /// <c>ProceduralBoardSetup.HeroX</c> 6.98 and whose width is
        /// <c>HeroPlateView.PlateW</c> 1.8, so its outer edge is at |x| 7.88. The deck pile is
        /// second at 7.83.
        ///
        /// RETIREMENT TRIGGER: if you move HeroX, PlateW, DeckX or PileW outboard, this number
        /// and <see cref="MinAspect"/> move with them, and CLAUDE.md / the device-layout spec
        /// state a 16:9 floor that stops being true. DeviceLayoutVerify fails on the mismatch
        /// rather than letting it ship silently.
        /// </summary>
        public const float DesignHalfWidth = 7.88f;

        /// <summary>
        /// Narrowest landscape aspect at which the hero plate still fits on screen: 1.71304.
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
        /// On-screen pixel width of a card at a given ortho size. 137.3 px on any 1080p phone
        /// at the authored 4.6 — 52% of the 265 px collection tile EF-050's acuity margin was
        /// computed against, which is the whole reason EF-062 gates that re-check.
        /// </summary>
        public static float CardPixelWidth(int screenH, float orthoSize)
            => BoardController.CardW * screenH / (2f * orthoSize);

        /// <summary>On-screen pixel height of a card. 193.7 px on any 1080p phone at 4.6.</summary>
        public static float CardPixelHeight(int screenH, float orthoSize)
            => BoardController.CardH * screenH / (2f * orthoSize);

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
            float heroEdge = ProceduralBoardSetup.HeroX + HeroPlateView.PlateW * 0.5f;
            float deckEdge = ProceduralBoardSetup.DeckX + DeckPileView.PileW * 0.5f;
            float widest = Mathf.Max(heroEdge, deckEdge);
            ok &= Line(sb, Mathf.Abs(widest - DesignHalfWidth) < 0.001f,
                $"DesignHalfWidth {DesignHalfWidth:F4} == widest built element {widest:F4} "
                + $"(hero plate {heroEdge:F4}, deck pile {deckEdge:F4})");

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

            // 7. The measured figure EF-062 exists to produce, restated so a harness run
            //    carries it. Not an assertion — a number in the log.
            sb.AppendLine($"      card in hand @1080p, orthoSize {DesignHalfHeight:F2}: "
                + $"{CardPixelWidth(1080, DesignHalfHeight):F1} x "
                + $"{CardPixelHeight(1080, DesignHalfHeight):F1} px");

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
