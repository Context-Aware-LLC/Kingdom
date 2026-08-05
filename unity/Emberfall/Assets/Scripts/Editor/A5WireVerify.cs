using UnityEditor;
using UnityEngine;
using Emberfall.Core.Contracts;
using Emberfall.Core.Serialization;
using Emberfall.Net.Protocol;

namespace Emberfall.Editor
{
    /// <summary>
    /// A5 commit-2 named verify (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/a5-wire-protocol-design.md §11 / §12 finding 9): Serialization's
    /// first Unity compile — proves IncrementalHash (GameStateHasher) and the full wire round-trip
    /// work under the Unity runtime. Kept as a menu item so the check is re-runnable after Unity
    /// or package upgrades.
    /// </summary>
    public static class A5WireVerify
    {
        [MenuItem("Emberfall/Verify A5 Wire (hasher + round-trip)")]
        public static void Run()
        {
            var p0Id = new PlayerId("p0");
            var p1Id = new PlayerId("p1");
            var p0 = new PlayerState(p0Id, new HeroInstance(new InstanceId(1), p0Id, CardClass.EMBERWARD, 30));
            var p1 = new PlayerState(p1Id, new HeroInstance(new InstanceId(2), p1Id, CardClass.TIDECALLER, 30));
            var hand = new CardInstance(new InstanceId(3), "card_x", p0Id)
            {
                Zone = Zone.Hand,
                CurrentAttack = 2,
                CurrentHealth = 3,
                MaxHealth = 3,
            };
            hand.Keywords.Add(Keyword.BULWARK);
            hand.Keywords.Add(Keyword.SHROUD);
            p0.Hand.Add(hand);
            var s = new GameState(new MatchId("m-unity-verify"), new DeterministicRandom(42UL), p0, p1)
            {
                StateVersion = 5,
                Phase = TurnPhase.Main,
                TurnNumber = 2,
                NextInstanceCounter = 4,
            };
            s.Rng.NextUInt64(); // live cursor, like a real mid-match state

            string hash = GameStateHasher.Hash(s);
            Debug.Log($"[A5Verify] GameStateHasher (IncrementalHash) OK under Unity: {hash}");

            var bytes = ProtocolCodec.Encode(SnapshotMsg.Of(s), s.Id, 1);
            var decoded = ProtocolCodec.Decode(bytes);
            if (!decoded.Ok)
            {
                Debug.LogError($"[A5Verify] wire decode FAILED under Unity: {decoded.Error} {decoded.Detail}");
                return;
            }

            var snap = (SnapshotMsg)decoded.Message;
            if (GameStateHasher.Hash(snap.State) == hash && snap.VerifyIntegrity())
                Debug.Log($"[A5Verify] GREEN — hash-preserving wire round-trip under Unity ({bytes.Length} bytes).");
            else
                Debug.LogError("[A5Verify] hash MISMATCH across the wire under Unity.");
        }
    }
}
