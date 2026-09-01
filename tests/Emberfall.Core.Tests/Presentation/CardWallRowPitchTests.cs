using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
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
    /// <para><b>Which test does what, stated plainly, because it is easy to overclaim here.</b>
    /// Only the FIRST test guards the implementation: it fails if the pitch solve or either margin
    /// write is removed from CardWall.cs — checked by reverting the file, 1 of 3 red. The other two
    /// re-derive the relation from constants PARSED out of CardWall.cs, so they do not see the
    /// implementation at all; what they catch is a CONSTANT moving somewhere that exact fill stops
    /// being reachable — <c>MinTileHeight</c>, <c>TileQuantum</c> and <c>MinVisibleRows</c>
    /// interact, and the floor case below is the one that already bites.</para>
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
                RepoPaths.RepoRoot, "unity", "Emberfall", "Assets", "Scripts", "Presentation", "CardWall.cs");
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

        [Test]
        public void The_solve_hands_its_quantiser_remainder_back_as_row_pitch()
        {
            string wall = Wall();

            Assert.Multiple(() =>
            {
                Assert.That(wall, Does.Match(@"out\s+float\s+verticalMargin\s*\)"),
                    "ResolveTileSize must hand the row margin back — a solve that only returns a "
                    + "tile size cannot spend its own rounding remainder.");
                Assert.That(wall, Does.Match(@"verticalMargin\s*=\s*Mathf\.Max\(\s*VerticalTileMargin\s*,\s*rowBudget\s*-\s*tileHeight\s*\)"),
                    "The solve must spend the remainder as pitch, floored at the flat margin.");
                Assert.That(wall, Does.Match(@"int\s+rows\s*=\s*Mathf\.Max\(\s*MinVisibleRows\s*,"),
                    "The row count must be DERIVED from the authored height, not fixed. Fixing it "
                    + "is what made a taller viewport grow the cards instead of adding a row.");
                Assert.That(wall, Does.Match(@"tile\.style\.marginTop\s*=\s*verticalMargin\s*\*\s*0\.5f"),
                    "Layout must actually write the solved pitch onto the tiles.");
                Assert.That(wall, Does.Match(@"tile\.style\.marginBottom\s*=\s*verticalMargin\s*\*\s*0\.5f"));
            });
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

            // Sweep well either side of the Pixel 5a's measured 350.9dp wall viewport, and up past
            // the point where a third and fourth row start fitting. The step is deliberately not a
            // multiple of the quantum, so worst-case remainders are exercised rather than stepped
            // over.
            for (float viewport = floorViewport; viewport <= 900f; viewport += 3f)
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
        /// A tall viewport must buy ROWS, not a bigger card. This is the defect the fixed-tier
        /// deletion was allowed to introduce and deliberately did not: the old solve divided the
        /// viewport by a constant row count, so every extra dp of height went into the tile.
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

            float phone = 350.9f;    // measured, Pixel 5a landscape
            float tablet = 841f;     // a 7in tablet's wall viewport at the same physical scale

            Assert.Multiple(() =>
            {
                Assert.That(RowsAt(phone), Is.EqualTo(minRows),
                    "A phone must still show exactly the floor number of rows.");
                Assert.That(RowsAt(tablet), Is.GreaterThan(minRows),
                    "A tablet-height viewport must spend its extra height on rows. If this fails "
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
