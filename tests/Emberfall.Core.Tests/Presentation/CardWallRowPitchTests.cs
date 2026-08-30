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
    /// <para><b>What went wrong, and why a test can see it.</b> The derived compact tier solves a
    /// tile height for <c>TargetVisibleRows</c> and then QUANTISES it to a 16dp step so the bake
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
    /// being reachable — <c>MinTileHeight</c>, <c>TileQuantum</c> and <c>TargetVisibleRows</c>
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
        public void The_derived_tier_hands_its_quantiser_remainder_back_as_row_pitch()
        {
            string wall = Wall();

            Assert.Multiple(() =>
            {
                Assert.That(wall, Does.Match(@"out\s+float\s+verticalMargin\s*\)"),
                    "ResolveTileTier must hand the row margin back — a tier that only returns a "
                    + "tile size cannot spend its own rounding remainder.");
                Assert.That(wall, Does.Match(@"verticalMargin\s*=\s*Mathf\.Max\(\s*VerticalTileMargin\s*,\s*viewportHeight\s*/\s*TargetVisibleRows\s*-\s*tileHeight\s*\)"),
                    "The derived tier must solve the pitch from the viewport, floored at the flat margin.");
                Assert.That(wall, Does.Match(@"tile\.style\.marginTop\s*=\s*verticalMargin\s*\*\s*0\.5f"),
                    "Layout must actually write the solved pitch onto the tiles.");
                Assert.That(wall, Does.Match(@"tile\.style\.marginBottom\s*=\s*verticalMargin\s*\*\s*0\.5f"));
            });
        }

        [Test]
        public void Two_rows_fill_a_phone_viewport_exactly_leaving_no_third_row_sliver()
        {
            string wall = Wall();
            int rows = IntConstant(wall, "TargetVisibleRows");
            float quantum = Constant(wall, "TileQuantum");
            float flatMargin = Constant(wall, "VerticalTileMargin");
            float minTile = Constant(wall, "MinTileHeight");

            // The exact-fill property holds ABOVE the legibility floor, and the floor's own case is
            // asserted separately below. Under it the clamp wins, the tile is already taller than
            // its share, and two rows genuinely do not fit — that is the wall preferring fewer
            // legible rows to more unreadable ones, and it predates this solve.
            float floorViewport = (minTile + flatMargin) * rows;

            // The Collection's measured viewport on a Pixel 5a is 291dp; sweep well either side of
            // it. The step is deliberately not a multiple of the quantum, so the worst-case
            // remainder is exercised rather than stepped over.
            for (float viewport = floorViewport; viewport <= 420f; viewport += 3f)
            {
                float solved = viewport / rows - flatMargin;
                solved = (float)Math.Floor(solved / quantum) * quantum;
                float tileHeight = Math.Max(solved, minTile);
                float pitch = Math.Max(flatMargin, viewport / rows - tileHeight) + tileHeight;

                Assert.That(pitch * rows, Is.EqualTo(viewport).Within(0.01f),
                    $"At a {viewport}dp viewport the rows come to {pitch * rows}dp — the difference "
                    + "is exactly the sliver of the next row that #298 says must not be there.");
            }
        }

        [Test]
        public void A_viewport_too_short_for_the_legibility_floor_keeps_the_flat_margin()
        {
            string wall = Wall();
            int rows = IntConstant(wall, "TargetVisibleRows");
            float flatMargin = Constant(wall, "VerticalTileMargin");
            float minTile = Constant(wall, "MinTileHeight");

            // Below the floor the tile is already taller than its share, so the pitch cannot be
            // paid and the wall shows fewer legible rows rather than more unreadable ones. The
            // clamp is what stops that case producing a NEGATIVE margin.
            float viewport = (minTile + flatMargin) * rows - 40f;
            float margin = Math.Max(flatMargin, viewport / rows - minTile);

            Assert.That(margin, Is.EqualTo(flatMargin).Within(0.001f),
                "The floor case must fall back to the flat margin, never to a negative one.");

            // And the honest consequence, written down rather than left to be rediscovered: under
            // the floor the rows OVERSPILL the viewport. Nothing here fixes that, and #298 does not
            // ask it to — a phone that short cannot show two legible rows at all.
            Assert.That((margin + minTile) * rows, Is.GreaterThan(viewport),
                "Under the legibility floor two rows are expected to overspill; if they now fit, "
                + "the floor or the row count has moved and this guard's premise is stale.");
        }
    }
}
