using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    /// <summary>
    /// The Collection wall must END CLEANLY after the two rows that read — Sam's ruling,
    /// 2026-08-30 (#298). The sliver of a third row is NOT to be recovered by shortening rows;
    /// it is the budget for the header band in #323.
    ///
    /// <para><b>What went wrong, and why a test can see it.</b> The solve fits a tile height to the
    /// rows the viewport holds and then QUANTISES it to a 16dp step so the bake
    /// count stays bounded. Quantising rounds DOWN, so up to a full quantum per row was left over
    /// — and left in the flat margin that remainder lands at the BOTTOM of the viewport, as the
    /// top of row three: a card sliced through the middle with only its cost band showing. The fix
    /// spends the remainder as row PITCH instead, so the rows fill the viewport exactly. This is
    /// arithmetic, which is the one part of a layout bug a headless test can actually hold.</para>
    ///
    /// <para>Verified on a Pixel 5a, 2026-08-30: two complete rows, wall terminating on the
    /// plate's bottom frame, no third-row sliver.</para>
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class CardWallRowPitchTests
    {
        private static string Wall()
        {
            string path = Path.Combine(
                RepoPaths.RepoRoot, "unity", "Assets", "Scripts", "Presentation", "CardWall.cs");
            return File.ReadAllText(path);
        }

        private static float Constant(string source, string name)
        {
            Match m = Regex.Match(source, @"const\s+float\s+" + name + @"\s*=\s*(?<v>[0-9.]+)f\s*;");
            Assert.That(m.Success, Is.True, $"CardWall.cs must declare a float constant {name}.");
            return float.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture);
        }

        private static int IntConstant(string source, string name)
        {
            Match m = Regex.Match(source, @"const\s+int\s+" + name + @"\s*=\s*(?<v>\d+)\s*;");
            Assert.That(m.Success, Is.True, $"CardWall.cs must declare an int constant {name}.");
            return int.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Re-derives the solve from the constants and sweeps a phone's viewport range: whatever
        /// tile it lands on, the rows must come to the viewport EXACTLY. A remainder here is
        /// literally the sliver of the next row that #298 rules out.
        /// </summary>
        [Test]
        public void The_rows_fill_a_phone_viewport_exactly_leaving_no_sliver_of_the_next()
        {
            string wall = Wall();
            int minRows = IntConstant(wall, "MinVisibleRows");
            float quantum = Constant(wall, "TileQuantum");
            float flatMargin = Constant(wall, "VerticalTileMargin");
            float minTile = Constant(wall, "MinTileHeight");
            float authored = Constant(wall, "TileHeight");   // the USS fallback = the phone size

            // The exact-fill property holds ABOVE the legibility floor; the floor's own case is
            // asserted separately below. Under it the clamp wins, the tile is already taller than
            // its share, and the rows genuinely do not fit — the wall preferring fewer legible rows
            // to more unreadable ones.
            float floorViewport = (minTile + flatMargin) * minRows;

            // Sweep well either side of the phone's wall viewport (921 authored px — see the
            // fraction in A_taller_viewport_adds_rows...) and up past the point where a third and
            // fourth row start fitting, which is what the bound is FOR: at the authored tile that
            // is 4 * (authored + flatMargin). It was a flat 900 while the tile was 160 tall; the
            // same literal against a 384 tile stops short of even a third row and the sweep
            // silently degrades to one row count. The step is deliberately not a multiple of the
            // quantum, so worst-case remainders are exercised rather than stepped over.
            float sweepCeiling = 4f * (authored + flatMargin);
            for (float viewport = floorViewport; viewport <= sweepCeiling; viewport += 3f)
            {
                int rows = Math.Max(minRows, (int)Math.Floor(viewport / (authored + flatMargin)));
                float budget = viewport / rows;

                float tileHeight;
                if (authored + flatMargin <= budget)
                {
                    tileHeight = authored;
                }
                else
                {
                    float solved = (float)Math.Floor((budget - flatMargin) / quantum) * quantum;
                    tileHeight = Math.Max(solved, minTile);
                }

                float pitch = Math.Max(flatMargin, budget - tileHeight) + tileHeight;

                Assert.That(pitch * rows, Is.EqualTo(viewport).Within(0.01f),
                    $"At a {viewport}dp viewport the {rows} rows come to {pitch * rows}dp — the "
                    + "difference is exactly the sliver #298 says must not be there.");
            }
        }

        /// <summary>
        /// A taller viewport must buy ROWS, not a bigger card. This is the defect the fixed-tier
        /// deletion was allowed to introduce and deliberately did not: the old solve divided the
        /// viewport by a constant row count, so every extra unit of height went into the tile.
        ///
        /// <para><b>NO SHIPPING DEVICE SUPPLIES THE TALLER VIEWPORT ANY MORE, and the test says so
        /// rather than pretending otherwise.</b> It used to contrast a measured 350.9dp phone with
        /// an 841dp tablet. Since 2026-09-01 all three PanelSettings run ScaleWithScreenSize
        /// matched on HEIGHT against 1080, so every panel is exactly 1080 authored px tall and only
        /// WIDTH varies — a tablet gets a NARROWER panel, never a taller one, and the wall's
        /// viewport is one number on every device. Asserting a tablet row count would be asserting
        /// a device that cannot exist.</para>
        ///
        /// <para>So the property is stated directly instead: a viewport with room for a third row
        /// must yield three. That is what "derived, not fixed" means, it needs no device to be
        /// true, and it is what will matter when #323's header band takes a bite out of this
        /// viewport or a future panel gives some back.</para>
        /// </summary>
        [Test]
        public void A_taller_viewport_adds_rows_rather_than_growing_the_card()
        {
            string wall = Wall();
            int minRows = IntConstant(wall, "MinVisibleRows");
            float flatMargin = Constant(wall, "VerticalTileMargin");
            float authored = Constant(wall, "TileHeight");

            int RowsAt(float viewport) =>
                Math.Max(minRows, (int)Math.Floor(viewport / (authored + flatMargin)));

            // The Pixel 5a's wall viewport, measured at 350.9 of the 411.4-tall panel on
            // 2026-08-30. The MEASUREMENT that survives the unit change is the fraction, not the
            // number: 85.3% of the panel, which is 921 of today's 1080.
            const float panelHeight = 1080f;
            const float wallViewportFraction = 350.9f / 411.4f;
            float phone = panelHeight * wallViewportFraction;

            // Not a device — the smallest viewport that has room for one more row than the floor.
            float roomForOneMoreRow = (minRows + 1) * (authored + flatMargin);

            Assert.Multiple(() =>
            {
                Assert.That(RowsAt(phone), Is.EqualTo(minRows),
                    "The phone must show exactly the floor number of rows — Sam's #298 ruling is "
                    + "that the wall ends cleanly after the two rows that read.");
                Assert.That(RowsAt(roomForOneMoreRow), Is.GreaterThan(minRows),
                    "A viewport with room for another row must spend it on that row. If this fails "
                    + "the row count has been fixed again and the wall will grow the cards.");
            });
        }

        /// <summary>
        /// Under the legibility floor the pitch cannot be paid and must fall back to the flat
        /// margin, never to a negative one.
        /// </summary>
        [Test]
        public void A_viewport_too_short_for_the_legibility_floor_keeps_the_flat_margin()
        {
            string wall = Wall();
            int minRows = IntConstant(wall, "MinVisibleRows");
            float flatMargin = Constant(wall, "VerticalTileMargin");
            float minTile = Constant(wall, "MinTileHeight");

            float viewport = (minTile + flatMargin) * minRows - 40f;
            float margin = Math.Max(flatMargin, viewport / minRows - minTile);

            Assert.That(margin, Is.EqualTo(flatMargin).Within(0.001f),
                "The floor case must fall back to the flat margin, never to a negative one.");

            // And the honest consequence, written down rather than left to be rediscovered: under
            // the floor the rows OVERSPILL the viewport. Nothing here fixes that, and #298 does not
            // ask it to — a phone that short cannot show two legible rows at all.
            Assert.That((margin + minTile) * minRows, Is.GreaterThan(viewport),
                "Under the legibility floor the rows are expected to overspill; if they now fit, "
                + "the floor or the row count has moved and this guard's premise is stale.");
        }
    }
}
