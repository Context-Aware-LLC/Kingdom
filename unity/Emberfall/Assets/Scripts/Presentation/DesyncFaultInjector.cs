using System;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Client;
using UnityEngine;

namespace Emberfall.Presentation
{
    // [DesyncDbg] dev tool — C-G1-4 positive-path proof + resync-ladder repro (2026-07-21).
    // Kept (not stripped): compile-guarded editor/dev-build only via NetworkedGameBootstrap's
    // #if wiring, and it is the live repro for the OPEN resync-no-answer bug (client requested a
    // resync after the injected mismatch; the server never re-sent — both clients soft-locked).
    //
    // IWireTransport decorator: passes every frame through untouched until armed (F9 in
    // NetworkedGameBootstrap.Update). When armed, the NEXT inbound snapshot frame gets ONE hex
    // char of its "hash" field flipped, then the injector disarms. The JSON still parses; the
    // wire hash no longer matches the state, so VerifyIntegrity() fails and the real capture
    // path runs: [Desync] console line, .frame/.txt artifact, resync request, mirror held.
    // Corrupting the hash (not the state) means the artifact shows a clean wireHash/localHash
    // disagreement and the resync'd re-send passes verification untouched.
    //
    // ReceiveAsync runs on the background receive pump; Arm is called from the main thread —
    // hence the Interlocked flag. One-shot by design: ResyncBudget is 3 and the 4th is fatal.
    public sealed class DesyncFaultInjector : IWireTransport
    {
        private static readonly byte[] Marker = System.Text.Encoding.ASCII.GetBytes("\"hash\":\"");

        private readonly IWireTransport _inner;
        private int _armed; // 0 = passthrough, 1 = corrupt next snapshot frame

        public DesyncFaultInjector(IWireTransport inner) => _inner = inner;

        public void ArmOneShot()
        {
            if (Interlocked.Exchange(ref _armed, 1) == 0)
                Debug.LogWarning("[DesyncDbg] ARMED — next inbound snapshot frame will be corrupted.");
        }

        public Task ConnectAsync(Uri uri, CancellationToken ct) => _inner.ConnectAsync(uri, ct);

        public Task SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct) => _inner.SendAsync(utf8, ct);

        public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
        {
            byte[]? frame = await _inner.ReceiveAsync(ct);
            if (frame == null || Volatile.Read(ref _armed) == 0) return frame;

            int at = IndexOf(frame, Marker);
            if (at < 0) return frame; // not a snapshot frame — stay armed, wait for one

            if (Interlocked.Exchange(ref _armed, 0) == 0) return frame; // lost a race — someone disarmed

            int hex = at + Marker.Length;           // first hex char of the 64-char lowercase digest
            byte original = frame[hex];
            frame[hex] = (byte)(original == (byte)'a' ? 'b' : 'a');
            Debug.LogWarning($"[DesyncDbg] INJECTED — flipped hash[0] '{(char)original}'→'{(char)frame[hex]}' " +
                             "in inbound snapshot frame. Expect [Desync] capture + resync.");
            return frame;
        }

        public Task CloseAsync(WireCloseReason reason) => _inner.CloseAsync(reason);

        public event Action<WireCloseReason, string> OnClosed
        {
            add => _inner.OnClosed += value;
            remove => _inner.OnClosed -= value;
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            int limit = haystack.Length - needle.Length;
            for (int i = 0; i <= limit; i++)
            {
                int j = 0;
                while (j < needle.Length && haystack[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }
    }
}
