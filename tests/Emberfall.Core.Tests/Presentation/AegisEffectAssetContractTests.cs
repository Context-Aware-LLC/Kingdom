using System.IO;
using System.Text.RegularExpressions;
using Emberfall.Core.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Core.Tests.Presentation
{
    [TestFixture]
    [Category("Presentation")]
    public sealed class AegisEffectAssetContractTests
    {
        [Test]
        public void Aegis_material_bundles_the_painted_dome_and_preserves_its_alpha()
        {
            string assets = Path.Combine(RepoPaths.RepoRoot, "unity", "Assets");
            string shaderPath = Path.Combine(assets, "Shaders", "AegisDome.shader");
            string texturePath = Path.Combine(assets, "Art", "UI", "Board", "effect_aegis_golden_dome.png");
            string materialPath = Path.Combine(assets, "Materials", "M_Aegis.mat");
            string shader = File.ReadAllText(shaderPath);
            string textureMeta = File.ReadAllText(texturePath + ".meta");
            string material = File.ReadAllText(materialPath);
            string addresses = File.ReadAllText(Path.Combine(assets,
                "AddressableAssetsData", "AssetGroups", "UI_Core.asset"));

            // Follow the GUID links: a PNG sitting outside Assets, or an unreferenced shader,
            // does not reach a player build even though the source files look correct.
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(texturePath), Is.True);
                Assert.That(material, Does.Contain("fileID: 4800000, guid: " + GuidOf(shaderPath)));
                Assert.That(material, Does.Contain("fileID: 2800000, guid: " + GuidOf(texturePath)));
                Assert.That(material, Does.Contain("- _BaseMap:"));
                Assert.That(addresses, Does.Contain("m_GUID: " + GuidOf(materialPath)));
                Assert.That(addresses, Does.Contain("m_Address: materials/M_Aegis"));
                Assert.That(textureMeta, Does.Contain("alphaUsage: 1"));
                Assert.That(textureMeta, Does.Contain("alphaIsTransparency: 1"));
                Assert.That(textureMeta, Does.Contain("nPOTScale: 0"));
                Assert.That(shader, Does.Contain("SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv)"));
                Assert.That(shader, Does.Contain("dome.a * _AegisColor.a"),
                    "Gain/break opacity must multiply the texture alpha, preserving its hollow centre.");
                Assert.That(shader, Does.Contain("ZWrite Off"));
                Assert.That(shader, Does.Contain("Blend SrcAlpha OneMinusSrcAlpha"));
            });
        }

        [Test]
        public void Board_gate_cancels_a_break_before_the_unchanged_state_early_return()
        {
            string binder = File.ReadAllText(Path.Combine(
                RepoPaths.RepoRoot, "unity", "Assets", "Scripts", "Presentation", "CardMaterialBinder.cs"));
            int applyStart = binder.IndexOf("private void ApplyAegis(");
            int applyEnd = binder.IndexOf("private void CaptureAegisBase(");
            string apply = binder.Substring(applyStart, applyEnd - applyStart);
            int gate = apply.IndexOf("if (!_boardStatusEffectsVisible)");
            int earlyReturn = apply.IndexOf("if (want == _aegisOn) return;");
            int hide = apply.IndexOf("HideAegis();");

            Assert.Multiple(() =>
            {
                Assert.That(binder, Does.Contain(
                        "bool want = _boardStatusEffectsVisible && (auras & AuraSet.Shield) != 0;"),
                    "Aegis is a board-minion treatment; a shielded card in hand must not show its quad.");
                Assert.That(binder, Does.Contain("ApplyAegis(auras);"),
                    "The board-visibility transition must immediately refresh Aegis.");

                Assert.That(gate, Is.GreaterThanOrEqualTo(0));
                Assert.That(hide, Is.GreaterThan(gate));
                Assert.That(earlyReturn, Is.GreaterThan(hide),
                    "A break already cleared _aegisOn: leaving the board must still hide its fading renderer.");
            });
        }

        private static string GuidOf(string assetPath) =>
            Regex.Match(File.ReadAllText(assetPath + ".meta"), @"(?m)^guid: (\w+)").Groups[1].Value;
    }
}
