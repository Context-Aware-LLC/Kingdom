using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kingdom.Net.Client.Gate;
using Kingdom.Net.Client.Telemetry;
using Kingdom.Net.Client.Tests.Auth;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Telemetry
{
    [TestFixture]
    [Category("Unit")]
    public sealed class FunnelQueueTests
    {
        private const string SessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string SessionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const long T0 = 1_792_756_800_123; // 2026-10-23T12:00:00.123Z

        private string _dir = "";

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "funnel-queue-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
        }

        private string QueuePath => Path.Combine(_dir, "telemetry", "funnel-queue.jsonl");

        private static FunnelStamp Stamp(string session) => new FunnelStamp(session, "0.0.14+31", FunnelPlatform.Android);

        [Test]
        public void A_row_queued_by_one_process_reloads_in_the_next_with_its_ORIGINAL_session_id()
        {
            var first = new FunnelQueue(new FileFunnelQueueStore(QueuePath));
            first.Enqueue(FunnelRow.Gate(Stamp(SessionA), T0, FunnelOutcome.Unreachable, 3));
            first.Enqueue(FunnelRow.Plain(FunnelMilestone.Exit, Stamp(SessionA), T0 + 5));

            // A relaunch: a new process, a new session id, the same file.
            var second = new FunnelQueue(new FileFunnelQueueStore(QueuePath));
            second.Enqueue(FunnelRow.Plain(FunnelMilestone.Launch, Stamp(SessionB), T0 + 60_000));

            IReadOnlyList<string> rows = second.Snapshot();
            Assert.That(rows, Has.Count.EqualTo(3));
            Assert.That(rows[0], Does.Contain("\"milestone\":\"gate\"").And.Contain("\"session_id\":\"" + SessionA + "\""));
            Assert.That(rows[1], Does.Contain("\"milestone\":\"exit\"").And.Contain("\"session_id\":\"" + SessionA + "\""));
            Assert.That(rows[2], Does.Contain("\"milestone\":\"launch\"").And.Contain("\"session_id\":\"" + SessionB + "\""));
        }

        [Test]
        public void Past_the_cap_the_OLDEST_rows_go_and_the_file_agrees()
        {
            var queue = new FunnelQueue(new FileFunnelQueueStore(QueuePath), capacity: 3);
            for (int i = 0; i < 5; i++)
                queue.Enqueue(FunnelRow.Queue(Stamp(SessionA), T0 + i, "menu-" + i));

            Assert.That(queue.Count, Is.EqualTo(3));
            Assert.That(queue.Snapshot()[0], Does.Contain("menu-2"));
            Assert.That(queue.Snapshot()[2], Does.Contain("menu-4"));

            var reloaded = new FunnelQueue(new FileFunnelQueueStore(QueuePath), capacity: 3);
            Assert.That(reloaded.Snapshot(), Is.EqualTo(queue.Snapshot()));
        }

        [Test]
        public void A_torn_last_line_from_a_crash_is_dropped_and_the_rows_before_it_survive()
        {
            var queue = new FunnelQueue(new FileFunnelQueueStore(QueuePath));
            queue.Enqueue(FunnelRow.Plain(FunnelMilestone.Launch, Stamp(SessionA), T0));
            File.AppendAllText(QueuePath, "{\"milestone\":\"pause\",\"sess");

            var reloaded = new FunnelQueue(new FileFunnelQueueStore(QueuePath));
            reloaded.Enqueue(FunnelRow.Plain(FunnelMilestone.Launch, Stamp(SessionB), T0 + 1));

            Assert.That(reloaded.Count, Is.EqualTo(2));
            var onDisk = new FunnelQueue(new FileFunnelQueueStore(QueuePath));
            Assert.That(onDisk.Snapshot(), Is.EqualTo(reloaded.Snapshot()));
        }

        [Test]
        public void A_store_that_throws_on_every_call_never_reaches_the_caller()
        {
            var logged = new List<string>();
            FunnelQueue queue = null!;
            Assert.DoesNotThrow(() =>
            {
                queue = new FunnelQueue(new ThrowingStore(), capacity: 2, log: logged.Add);
                queue.Enqueue(FunnelRow.Plain(FunnelMilestone.Launch, Stamp(SessionA), T0));
                queue.Enqueue(FunnelRow.Plain(FunnelMilestone.Pause, Stamp(SessionA), T0 + 1));
                queue.Enqueue(FunnelRow.Plain(FunnelMilestone.Resume, Stamp(SessionA), T0 + 2));
                queue.Remove(queue.Peek(1));
            });

            // The rows the disk refused are still held for this process to send.
            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(logged, Is.Not.Empty);
        }

        [Test]
        public void A_queue_file_in_an_unwritable_place_is_absorbed()
        {
            // A path whose parent is a FILE: every directory create and write fails.
            Directory.CreateDirectory(_dir);
            string blocker = Path.Combine(_dir, "blocker");
            File.WriteAllText(blocker, "x");
            var store = new FileFunnelQueueStore(Path.Combine(blocker, "funnel-queue.jsonl"));

            Assert.DoesNotThrow(() =>
            {
                var queue = new FunnelQueue(store);
                queue.Enqueue(FunnelRow.Plain(FunnelMilestone.Launch, Stamp(SessionA), T0));
                Assert.That(queue.Count, Is.EqualTo(1));
            });
        }

        [Test]
        public void Remove_takes_exactly_the_sent_rows_even_when_more_arrived_meanwhile()
        {
            var queue = new FunnelQueue(new FileFunnelQueueStore(QueuePath));
            queue.Enqueue(FunnelRow.Plain(FunnelMilestone.Launch, Stamp(SessionA), T0));
            IReadOnlyList<FunnelQueue.Entry> sent = queue.Peek(10);
            queue.Enqueue(FunnelRow.Plain(FunnelMilestone.Pause, Stamp(SessionA), T0 + 1));

            queue.Remove(sent);

            Assert.That(queue.Snapshot().Single(), Does.Contain("\"milestone\":\"pause\""));
            Assert.That(new FunnelQueue(new FileFunnelQueueStore(QueuePath)).Snapshot(),
                Is.EqualTo(queue.Snapshot()));
        }

        // ── The row shape (docs/dev-docs/specs/first-session-funnel.md) ─────────────────────

        [Test]
        public void A_gate_row_carries_exactly_the_contract_fields()
        {
            string json = FunnelRow.Gate(Stamp(SessionA), T0, FunnelOutcome.ForGate(UpdateGateVerdict.Unreachable), 3).ToJson();

            Assert.That(json, Is.EqualTo(
                "{\"milestone\":\"gate\",\"session_id\":\"" + SessionA + "\"," +
                "\"client_ts\":\"2026-10-23T12:00:00.123Z\",\"build\":\"0.0.14+31\",\"platform\":\"android\"," +
                "\"outcome\":\"unreachable\",\"attempts\":3}"));
        }

        [TestCase(UpdateGateVerdict.Pass, "pass")]
        [TestCase(UpdateGateVerdict.UpdateRequired, "stale")]
        [TestCase(UpdateGateVerdict.Unreachable, "unreachable")]
        public void Each_gate_verdict_maps_to_its_contract_outcome(UpdateGateVerdict verdict, string outcome)
            => Assert.That(FunnelOutcome.ForGate(verdict), Is.EqualTo(outcome));

        [TestCase("auth:Network", "auth:network")]
        [TestCase("Send Code / Network!", "send_code___network_")]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void A_cause_is_fitted_to_the_contract_alphabet(string? raw, string? expected)
            => Assert.That(FunnelRow.SanitizeCause(raw), Is.EqualTo(expected));

        [Test]
        public void A_cause_is_capped_at_64_characters()
            => Assert.That(FunnelRow.SanitizeCause(new string('a', 100))!.Length, Is.EqualTo(64));

        private sealed class ThrowingStore : IFunnelQueueStore
        {
            public IReadOnlyList<string> Load() => throw new IOException("disk gone");
            public void Append(string line) => throw new IOException("disk gone");
            public void Replace(IReadOnlyList<string> lines) => throw new UnauthorizedAccessException("read-only");
        }
    }
}
