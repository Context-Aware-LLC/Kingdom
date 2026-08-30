using System.IO;
using System.Text.RegularExpressions;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    /// <summary>
    /// One guard, for one silent failure: a ScrollView whose content container is allowed to
    /// SHRINK does not scroll — it squeezes, and every row inside it comes out sliced (#322,
    /// cause C2). Nothing reports that. The screen renders, the scroller renders, the elements
    /// are all in the tree and all found by every query; the only tell is a thumb filling its
    /// whole track over content that is visibly cut off, and you have to be looking at a phone
    /// to see it. That is the same shape as #249, where a shrinkable child with
    /// <c>overflow: hidden</c> deleted two main-menu buttons outright.
    ///
    /// <para>So this asserts the presence of a RULE, which is normally a poor kind of test. It
    /// earns its place because the rule is one line, it is invisible when it works, and deleting
    /// it looks exactly like tidying up an unused selector. The doubled class is part of the
    /// contract, not formatting: <c>.unity-...</c> alone scores (0,1,0) and ties Unity's default
    /// runtime theme, so source order would decide the winner. Same trick, same reason, as the
    /// no-visible-scrollbars rule beside it.</para>
    ///
    /// <para>What it does NOT check is that the selector matches anything at runtime. It cannot —
    /// see theme.uss's own warning that a rule's presence is not evidence a surface is covered.
    /// The class name is built at runtime as <c>ScrollView.ussClassName + "__content-container"</c>,
    /// so it exists as two fragments in UnityEngine.UIElementsModule and never as one literal to
    /// grep for. Verified on a Pixel 5a, 2026-08-30.</para>
    /// </summary>
    [TestFixture]
    [Category("Presentation")]
    public sealed class ScrollViewContentDoesNotShrinkTests
    {
        private static string Asset(params string[] parts)
        {
            string path = Path.Combine(RepoPaths.RepoRoot, "unity", "Emberfall", "Assets");
            foreach (string part in parts) path = Path.Combine(path, part);
            return File.ReadAllText(path);
        }

        [Test]
        public void Theme_stops_every_scroll_view_paying_its_overflow_in_squeezed_rows()
        {
            string theme = Asset("UI", "Common", "theme.uss");

            Assert.That(theme, Does.Match(
                    @"(?s)\.unity-scroll-view__content-container\.unity-scroll-view__content-container\s*\{[^}]*flex-shrink:\s*0;"),
                "theme.uss must keep the doubled-class content-container rule: without it a "
                + "ScrollView's content shrinks to its viewport instead of overflowing into the "
                + "scroller, and rows are clipped rather than scrolled (#322).");
        }

        [Test]
        public void A_pack_tile_reserves_its_own_height_and_cannot_be_compressed()
        {
            string shop = Asset("UI", "Shop", "shop.uss");

            // The min-height is only worth anything if the tile also refuses to shrink; a parent
            // resolving a deficit ignores a minimum it is allowed to shrink past.
            Match tile = Regex.Match(shop, @"(?s)^\.shop-pack\s*\{(?<body>.*?)\}", RegexOptions.Multiline);
            Assert.That(tile.Success, Is.True, "shop.uss must declare .shop-pack.");

            string body = tile.Groups["body"].Value;
            Assert.Multiple(() =>
            {
                Assert.That(body, Does.Match(@"min-height:\s*\d+px;"),
                    "A pack tile must reserve the real height of its rail, name, detail and buy row "
                    + "— with none, the list it sits in has no reason to reserve one and a short "
                    + "viewport cuts the tile through the middle (#283).");
                Assert.That(body, Does.Match(@"flex-shrink:\s*0;"));
            });
        }

        [Test]
        public void A_character_row_that_clips_its_corners_is_never_shrinkable()
        {
            string roster = Asset("UI", "CharacterRoster", "character-roster.uss");

            Match row = Regex.Match(roster, @"(?s)^\.character-row\s*\{(?<body>.*?)\}", RegexOptions.Multiline);
            Assert.That(row.Success, Is.True, "character-roster.uss must declare .character-row.");

            string body = row.Groups["body"].Value;
            Assert.That(body, Does.Match(@"overflow:\s*hidden;"),
                "Guard assumption: the row clips, which is what makes shrinking destructive.");
            Assert.That(body, Does.Match(@"flex-shrink:\s*0;"),
                "A shrinkable child that also clips pays a column's overflow in whole children — "
                + "here it took the name band and #307 saw the name sliced in half.");
        }
    }
}
