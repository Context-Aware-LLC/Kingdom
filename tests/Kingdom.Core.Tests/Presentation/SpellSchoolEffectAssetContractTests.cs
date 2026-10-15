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
            ("effect_spell_fire_impact", "10ab97aff9744067823f56088bf17ef3"),
            ("effect_spell_shadow_travel", "99408be364b341c3b6febfea100f029e"),
            ("effect_spell_shadow_impact", "039c922ced554f70aa17080a94295c06"),
            ("effect_spell_clockwright_caster", "a15153f58f9b48ad89e699962394b80a"),
            ("effect_spell_clockwright_travel", "173c00649b7f440ab3cc11c8dacb7a68"),
            ("effect_spell_clockwright_target", "fc693cba6f4d43bf960d38469702f21b"),
            ("effect_spell_lightning_charge", "acd3429bb87f4ab3827f5d9463840042"),
            ("effect_spell_lightning_hit", "a8e8f77a774b4966ae965452dfd452ef"),
            ("effect_spell_water_caster", "c6511868d056495a88aa14a1ff29dcd1"),
            ("effect_spell_water_travel", "08ba4c3b0df94e0599944f84254460a1"),
            ("effect_spell_water_splash", "5d26a9df71e54b8282927206604ebb01"),
            ("effect_spell_frost_caster", "020c8c18a4a64529b0e69dfbde5b29b6"),
            ("effect_spell_frost_travel", "5e44797afa2b4dc39e7489e620f8df6f"),
            ("effect_drown_wash", "4e7117405bbf4629b056cb8afaa43d4d"),
            ("effect_spell_gravekeep_caster", "7313910b2d6c4c398124d424cce03aa5"),
            ("effect_spell_gravekeep_travel", "0c874b888515440ebf68464e66442994"),
            ("effect_spell_gravekeep_target", "bb0ebfd92f5747e8bc6675ec6a753efb")
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
