using System.IO;
using Kingdom.Core.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class SpellSchoolEffectAssetContractTests
    {
        private static readonly (string File, string Guid)[] Sheets =
        {
            ("effect_spell_earth_buildup", "be7678213fe64791b6fab1b09089d14d"),
            ("effect_spell_earth_rock", "cdc7af0a10db4efd9b3f427eb5d1060b"),
            ("effect_spell_earth_impact", "a9c138db28ba4273900034893703d7ff"),
            ("effect_spell_heal_shimmer", "5ea1f25992c14858954a15d9ea61b045"),
            ("effect_venom_hit_poof", "3ae15b4344584b3ebdbdfe1433b29cad"),
            ("effect_spell_heal_travel", "050b03c1a3e34c85b3ef2372fe134943"),
            ("effect_spell_lightning_arc", "69dd013b64c04c2f9549224e530e4b26"),
            ("effect_spell_lightning_strike", "21c0730cc22149e79021c7aaab920e6d"),
            ("effect_spell_fire_buildup", "680a960beeb94e55be45b84b48d46e29"),
            ("effect_spell_fire_flame", "1c861f74e57c4f29a7e8f75e5cb77698"),
            ("effect_spell_fire_impact", "10ab97aff9744067823f56088bf17ef3")
        };

        [Test]
        public void Every_spell_school_sheet_is_an_addressable_sprite()
        {
            string assets = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets");
            string group = File.ReadAllText(Path.Combine(
                assets, "AddressableAssetsData", "AssetGroups", "UI_Core.asset"));
            string effects = File.ReadAllText(Path.Combine(
                assets, "Scripts", "Presentation", "SpellSchoolEffects.cs"));

            foreach (var sheet in Sheets)
            {
                string assetPath = Path.Combine(assets, "Art", "UI", "Board", sheet.File + ".png");
                string metadata = File.ReadAllText(assetPath + ".meta");
                Assert.Multiple(() =>
                {
                    Assert.That(File.Exists(assetPath), Is.True, $"Missing spell sheet {sheet.File}.");
                    Assert.That(metadata, Does.Contain($"guid: {sheet.Guid}"));
                    Assert.That(metadata, Does.Contain("textureType: 8"));
                    // A sheet missing from the group still draws in the Editor and is blank in a build.
                    Assert.That(group, Does.Contain($"m_GUID: {sheet.Guid}"));
                    Assert.That(group, Does.Contain($"m_Address: ui/{sheet.File}"));
                    Assert.That(effects, Does.Contain($"\"{sheet.File}\""));
                });
            }
        }
    }
}
