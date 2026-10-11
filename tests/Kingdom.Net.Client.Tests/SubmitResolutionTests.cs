using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Client;
using Kingdom.Net.Client.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests
{
    /// <summary>
    /// §11 test 10 — the RC-F7 clock race. A submit at T−ε loses the race to a SERVER CLOCK
    /// (mulligan auto-confirm, <c>MatchSession.cs:362–370</c>): the version advances with no client
    /// intent, the submit resolves Ok off the clock's snapshot, and the real intent then comes back
    /// rejected. The regression: the late RejectMsg must NOT double-resolve the completed task.
    /// With <c>TrySetResult</c> (ruling 2 interim) it cannot; a mis-attribution is logged and
    /// nothing else happens (the forced resync was STRUCK).
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class SubmitResolutionTests : ClientTestBase
    {
        [Test]
        public async Task Test10_Clock_Race_Resolves_Once_Logs_Misattribution_And_Never_Throws()
        {
            var log = new List<Misattribution>();
            using var peer = new LoopbackServerPeer();
            using var c0 = new NetworkedMatchSession(peer.Transport0, new ClientOptions { DeviceToken = "test-device-0", OnMisattribution = log.Add });
            using var c1 = new NetworkedMatchSession(peer.Transport1, new ClientOptions { DeviceToken = "test-device-1" });
            await StartPairAsync(peer, c0, c1);
            Assume.That(c0.Current.Phase, Is.EqualTo(TurnPhase.Mulligan));

            // Submit a mulligan at T−ε but DON'T relay it yet — it sits in the client's outbound.
            Task<CommandResult> submit = c0.SubmitAsync(new MulliganIntent(c0.Player, Array.Empty<InstanceId>()));
            Assert.That(c0.HasPendingSubmit, Is.True);

            // The mulligan clock fires first: the server auto-confirms BOTH seats, advancing the
            // version with no client intent. The client's pending submit resolves Ok off THAT snapshot.
            await peer.AdvanceAndTickAsync(50_000); // > MulliganMs (45s)

            Assert.That(submit.IsCompleted, Is.True, "version advanced ⇒ the accept inference resolves the task");
            CommandResult okByInference = await submit;
            Assert.That(okByInference.Accepted, Is.True);
            Assert.That(c0.HasPendingSubmit, Is.False);

            // Now the real mulligan lands late and is refused (phase already left Mulligan / already done).
            await peer.RelayAsync();

            // THE REGRESSION: no double-resolve. A SetResult here would throw on the pump thread,
            // which surfaces as an unexpected Dead transition; TrySetResult keeps the session Live.
            Assert.That(c0.State, Is.EqualTo(ConnectionState.Live), "late reject must not crash the receive pump");
            Assert.That(log, Has.Count.EqualTo(1), "the mis-attribution is logged — this log IS the evidence gate");
            Assert.That(log[0].PendingSeq, Is.EqualTo(1UL), "correlated to the offending intent's envelope seq");
            Assert.That(log[0].RejectReason, Is.AnyOf("WrongPhase", "AlreadyMulliganed"),
                "the clock left the phase / marked the seat done before the real intent arrived");
        }
    }
}
