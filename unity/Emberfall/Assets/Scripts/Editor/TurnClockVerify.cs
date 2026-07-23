using Emberfall.Presentation;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Spawns the <see cref="TurnClockStubDriver"/> into the open scene so the E2 visible turn clock
    /// (C-G1-2a) can be eyeballed in Play mode without a server — hot-seat has no clock, so this is
    /// how "visible turn clock, hot-seat-green" is demonstrated. Sibling in spirit to
    /// <see cref="B3TransportVerify"/>: a named, one-click verification of a slice the .NET tests
    /// cannot show (they prove the seam raises the event; only the engine shows the pixels).
    /// </summary>
    public static class TurnClockVerify
    {
        [MenuItem("Emberfall/Verify Turn Clock (stub)")]
        public static void Spawn()
        {
            var go = new GameObject("TurnClockStub");
            go.AddComponent<TurnClockStubDriver>();
            Selection.activeGameObject = go;
            Debug.Log("[TurnClockVerify] Spawned stub at origin. Enter Play mode: the clock counts " +
                      "down from 75s and turns red in the last 10s, re-arming each cycle. Frame the " +
                      "GameObject's origin in the Game view (world-space TMP).");
        }
    }
}
