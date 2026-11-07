using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Kingdom.Core.Tests.Support;
using Kingdom.Presentation;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class LoadingArtworkAssetTests
    {
        public static IEnumerable<TestCaseData> Variants()
        {
            foreach (BackgroundTier tier in Enum.GetValues<BackgroundTier>())
                foreach (string key in BackgroundVariantPolicy.Keys)
                    yield return new TestCaseData(tier, key);
        }

        [TestCase(BackgroundTier.Mobile, "Local", 0)]
        [TestCase(BackgroundTier.DesktopTablet, "Remote", 1)]
        public void Delivery_groups_keep_large_art_out_of_the_installed_app(BackgroundTier tier, string location, int bundleMode)
        {
            string data = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets", "AddressableAssetsData");
            string settings = File.ReadAllText(Path.Combine(data, "AddressableAssetSettings.asset"));
            string schema = File.ReadAllText(Path.Combine(data, "AssetGroups", "Schemas",
                BackgroundVariantPolicy.Group(tier) + "_BundledAssetGroupSchema.asset"));
            foreach (string path in new[] { "BuildPath", "LoadPath" })
            {
                string id = Regex.Match(settings, $@"- m_Id: ([a-f0-9]+)\r?\n\s+m_Name: {location}\.{path}\r?\n").Groups[1].Value;
                Assert.That(id, Has.Length.EqualTo(32));
                Assert.That(schema, Does.Match($@"m_{path}:\r?\n\s+m_Id: {id}\r?\n"));
            }
            Assert.That(schema, Does.Contain($"m_BundleMode: {bundleMode}"));
            Assert.That(schema, Does.Contain("m_UseAssetBundleCache: 1"));
            Assert.That(settings, Does.Contain("m_BuildRemoteCatalog: 0"), "Boot must use the embedded catalog offline.");
            Assert.That(settings, Does.Contain("https://kingdomofbadideas.com/game-assets/backgrounds/v1/[BuildTarget]"));
        }

        // Check the actual delivered PNG and GUID/address pairing for every device tier. Editor
        // AssetDatabase loading alone would hide an absent or incorrectly registered player asset.
        [TestCaseSource(nameof(Variants))]
        public void Background_variant_is_registered_at_full_delivery_resolution(BackgroundTier tier, string name)
        {
            string assets = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets");
            string path = Path.Combine(RepoPaths.RepoRoot, "unity", BackgroundVariantPolicy.AssetPath(tier, name));
            byte[] png = File.ReadAllBytes(path);
            Assert.That(png[..8], Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
            int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
            string meta = File.ReadAllText(path + ".meta");
            string guid = Regex.Match(meta, @"(?m)^guid: ([0-9a-f]{32})\r?$").Groups[1].Value;
            string group = File.ReadAllText(Path.Combine(assets,
                "AddressableAssetsData", "AssetGroups", BackgroundVariantPolicy.Group(tier) + ".asset"));

            Assert.Multiple(() =>
            {
                Assert.That(guid, Has.Length.EqualTo(32));
                Assert.That(width, Is.EqualTo(BackgroundVariantPolicy.Width(tier)));
                Assert.That(height, Is.EqualTo(BackgroundVariantPolicy.Height(tier)));
                Assert.That(width * 9, Is.EqualTo(height * 16));
                Assert.That(meta, Does.Contain("spriteMode: 1"));
                Assert.That(meta, Does.Contain("nPOTScale: 0"));
                Assert.That(meta, Does.Contain("enableMipMap: 0"));
                Assert.That(group, Does.Match(
                    $@"m_GUID: {guid}\r?\n\s+m_Address: {Regex.Escape(BackgroundVariantPolicy.Address(tier, name))}\r?\n"),
                    "The player address must point to this PNG's GUID, not another painting.");
                foreach (string platform in new[] { "Standalone", "Android", "iOS" })
                    Assert.That(meta, Does.Match($@"buildTarget: {platform}\r?\n\s+maxTextureSize: {(tier == BackgroundTier.DesktopTablet ? 4096 : 2048)}\r?\n"));
            });
        }
    }
}
