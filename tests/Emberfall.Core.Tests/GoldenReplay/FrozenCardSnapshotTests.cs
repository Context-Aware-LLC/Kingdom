using System;
using System.IO;
using System.Security.Cryptography;
using NUnit.Framework;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Core.Tests.GoldenReplay
{
    /// <summary>
    /// The frozen card set the golden replays run against must not change under them.
    ///
    /// <para><b>What this is protecting against, and why it is not paranoia.</b> Every golden
    /// fixture names its own <c>cardSetSnapshot</c> and <see cref="ReplayHarness"/> loads it with
    /// <c>JsonCardDatabase.FromSnapshotFile</c> — the replays never read live card data. That
    /// isolation is the whole reason renaming a shipping card cannot move a golden hash.</para>
    ///
    /// <para>The isolation has exactly one weak point: the frozen set is a plain JSON file sitting
    /// in the repo, so a repo-wide search-and-replace sweeps it up like any other text. On
    /// 2026-08-15 that is precisely what happened — a card-id rename rewrote ids inside this file,
    /// and all five replay fixtures went red with state-hash mismatches. Five mismatched hex
    /// strings look exactly like an engine regression, so the time went into proving the engine was
    /// fine rather than into noticing the input had moved.</para>
    ///
    /// <para><b>This test converts that into one sentence.</b> It is deliberately a whole-file
    /// digest rather than a structural check: the point is not to validate the snapshot's contents
    /// (the fixtures already do that, exhaustively, by replaying against it) but to assert that
    /// nobody touched it at all.</para>
    ///
    /// <para><b>RETIREMENT TRIGGER:</b> re-pin <see cref="ExpectedSha256"/> in the same commit that
    /// deliberately edits <c>M1.cards.snapshot.json</c> — which should only ever happen to APPEND a
    /// fixture-only, <c>collectible:false</c> card for a new golden, the way
    /// <c>test_unearth_source</c> and the ongoing-effect spells were added. Editing an existing row
    /// here rewrites history for every fixture at once and is almost never what you want. If you
    /// are here because a shipping card was renamed: this file should NOT follow that rename. An
    /// old id in the frozen set is correct — it is a point-in-time record, not live data.</para>
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class FrozenCardSnapshotTests
    {
        private const string SnapshotName = "M1.cards.snapshot.json";

        /// <summary>Pinned 2026-08-15, after reverting an accidental search-replace sweep.</summary>
        private const string ExpectedSha256 =
            "74674A6AEA8415E241D19C6DA396B24EA0872333D76C694063EDC1B78DD190B2";

        [Test]
        public void The_Frozen_Card_Set_Has_Not_Been_Modified()
        {
            string path = Path.Combine(RepoPaths.CardSetsDir, SnapshotName);
            Assert.That(File.Exists(path), Is.True, $"{SnapshotName} is missing.");

            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

            Assert.That(actual, Is.EqualTo(ExpectedSha256),
                $"{SnapshotName} was modified. The golden replays read this file instead of live "
                + "card data, so editing it silently rebaselines every fixture at once — which "
                + "surfaces as unexplained state-hash mismatches, not as an obvious cause.\n\n"
                + "If a repo-wide find/replace did this (a card-id rename is the usual culprit), "
                + "revert this file: `git checkout -- tests/Emberfall.Core.Tests/GoldenReplay/"
                + "cardsets/`. An old card id in the frozen set is CORRECT — it is a historical "
                + "record and is deliberately decoupled from the shipping corpus.\n\n"
                + "If you meant to append a fixture-only card for a new golden, re-pin "
                + "ExpectedSha256 in this same commit and say why in the fixture's rebaseline.md.");
        }
    }
}
