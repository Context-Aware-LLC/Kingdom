using Kingdom.Presentation;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    public sealed class BackgroundVariantPolicyTests
    {
        [TestCase(false, 0, "Desktop", 4096, BackgroundTier.DesktopTablet)]
        [TestCase(true, 411, "Pixel 9 Pro", 8192, BackgroundTier.Mobile)]
        [TestCase(true, 599, "Android", 4096, BackgroundTier.Mobile)]
        [TestCase(true, 600, "Android", 4096, BackgroundTier.DesktopTablet)]
        [TestCase(true, 800, "Android Tablet", 4096, BackgroundTier.DesktopTablet)]
        [TestCase(true, 0, "iPad13,5", 8192, BackgroundTier.DesktopTablet)]
        [TestCase(true, 0, "iPhone17,1", 8192, BackgroundTier.Mobile)]
        [TestCase(true, 0, null, 4096, BackgroundTier.Mobile)]
        [TestCase(true, 800, "Android Tablet", 2048, BackgroundTier.Mobile)]
        public void Selects_device_class_with_a_safe_unknown_handheld_fallback(
            bool handheld, int smallestWidthDp, string? model, int maxTextureSize, BackgroundTier expected)
        {
            Assert.That(BackgroundVariantPolicy.Choose(handheld, smallestWidthDp, model, maxTextureSize), Is.EqualTo(expected));
        }

        [Test]
        public void Tiers_have_distinct_asset_addresses()
        {
            foreach (string key in BackgroundVariantPolicy.Keys)
                Assert.That(BackgroundVariantPolicy.Address(BackgroundTier.Mobile, key),
                    Is.Not.EqualTo(BackgroundVariantPolicy.Address(BackgroundTier.DesktopTablet, key)));
        }
    }
}
