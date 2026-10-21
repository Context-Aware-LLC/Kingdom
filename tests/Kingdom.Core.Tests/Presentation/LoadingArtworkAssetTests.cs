using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.RegularExpressions;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class LoadingArtworkAssetTests
    {
        // A missing Addressables entry can look correct in Editor while shipping a blank player
        // screen. Check the actual GUID-to-address pairing and the imported full-image rectangle.
        [TestCase("LoadingPlate_VerdantWatering")]
        [TestCase("LoadingPlate_ClockwrightCollection")]
        [TestCase("LoadingPlate_GravekeepDog")]
        [TestCase("LoadingPlate_ClockwrightBreakfast")]
        public void Approved_loading_art_is_registered_and_imports_the_complete_image(string name)
        {
            string assets = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets");
            string path = Path.Combine(assets, "Art", "Backgrounds", "Loading", name + ".png");
            byte[] png = File.ReadAllBytes(path);
            Assert.That(png[..8], Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
            int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
            string meta = File.ReadAllText(path + ".meta");
            string guid = Regex.Match(meta, @"(?m)^guid: ([0-9a-f]{32})\r?$").Groups[1].Value;
            string group = File.ReadAllText(Path.Combine(assets,
                "AddressableAssetsData", "AssetGroups", "Backgrounds.asset"));

            Assert.Multiple(() =>
            {
                Assert.That(guid, Has.Length.EqualTo(32));
                Assert.That(width, Is.GreaterThanOrEqualTo(1900));
                Assert.That(height, Is.GreaterThanOrEqualTo(800));
                Assert.That((double)width / height, Is.EqualTo(21.0 / 9.0).Within(0.01));
                Assert.That(meta, Does.Match($@"(?m)^\s+width: {width}\r?$"));
                Assert.That(meta, Does.Match($@"(?m)^\s+height: {height}\r?$"));
                Assert.That(meta, Does.Contain("enableMipMap: 0"));
                Assert.That(group, Does.Match(
                    $@"m_GUID: {guid}\r?\n\s+m_Address: backgrounds/{Regex.Escape(name)}\r?\n"),
                    "The player address must point to this PNG's GUID, not another painting.");
            });
        }
    }
}
