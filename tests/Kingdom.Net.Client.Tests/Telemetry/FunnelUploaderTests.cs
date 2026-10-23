using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kingdom.Net.Client.Gate;
using Kingdom.Net.Client.Telemetry;
using Kingdom.Net.Client.Tests.Auth;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Telemetry
{
    [TestFixture]
    [Category("Unit")]
    public sealed class FunnelUploaderTests
    {
        private const string Base = "https://emberfall.fly.dev";
        private const string Device = "11111111-2222-3333-4444-555555555555";
        private const string Session = "cccccccccccccccccccccccccccccccc";

        private FakeClock _clock = null!;
        private MemoryStore _store = null!;
        private FunnelQueue _queue = null!;
        private FakeAuthHttp _http = null!;
        private string _bearer = "";

        [SetUp]
        public void SetUp()
        {
            _clock = new FakeClock();
            _store = new MemoryStore();
            _queue = new FunnelQueue(_store);
            _http = new FakeAuthHttp();
            _bearer = "";
        }

        private FunnelUploader Uploader(int batchSize = FunnelUploader.DefaultBatchSize)
            => new FunnelUploader(_queue, Base, _http, Device, () => Task.FromResult(_bearer), _clock,
                batchSize: batchSize);

        private void Queue(int n)
        {
            for (int i = 0; i < n; i++)
                _queue.Enqueue(FunnelRow.Plain(FunnelMilestone.Launch,
                    new FunnelStamp(Session, "0.0.14+31", FunnelPlatform.Android), _clock.NowMs + i));
        }

        private static string? Header(IReadOnlyList<KeyValuePair<string, string>> headers, string name)
            => headers.Where(h => h.Key == name).Select(h => h.Value).FirstOrDefault();

        [Test]
        public async Task A_2xx_removes_the_sent_rows_from_memory_AND_disk()
        {
            Queue(2);
            _http.Reply(202, "");

            FunnelFlushResult result = await Uploader().FlushAsync();

            Assert.That(result, Is.EqualTo(FunnelFlushResult.Sent));
            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_store.Lines, Is.Empty);
            var request = _http.Requests.Single();
            Assert.That(request.Verb, Is.EqualTo("POST"));
            Assert.That(request.Url, Is.EqualTo(Base + FunnelUploader.Route));
            Assert.That(request.Body, Does.StartWith("{\"rows\":[{\"milestone\":\"launch\"").And.EndWith("}]}"));
        }

        [Test]
        public async Task A_429_keeps_every_row_and_backs_off()
        {
            Queue(3);
            _http.Reply(429, "");
            FunnelUploader uploader = Uploader();

            FunnelFlushResult result = await uploader.FlushAsync();

            Assert.That(result, Is.EqualTo(FunnelFlushResult.Kept));
            Assert.That(_queue.Count, Is.EqualTo(3));
            Assert.That(uploader.IsBackingOff, Is.True);

            // A row enqueued during the backoff does not cut it short.
            uploader.RequestFlushNow();
            Assert.That(uploader.Tick(), Is.Null);
            _clock.Advance(FunnelUploader.FirstBackoffMs);
            _http.Reply(200, "");
            Assert.That(await uploader.Tick()!, Is.EqualTo(FunnelFlushResult.Sent));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task A_network_failure_keeps_every_row()
        {
            Queue(2);
            _http.ReplyTransportFailure();

            Assert.That(await Uploader().FlushAsync(), Is.EqualTo(FunnelFlushResult.Kept));
            Assert.That(_queue.Count, Is.EqualTo(2));
            Assert.That(_store.Lines, Has.Count.EqualTo(2));
        }

        [TestCase(500)]
        [TestCase(503)]
        [TestCase(404)]
        public async Task A_server_fault_or_an_absent_route_keeps_the_rows(int status)
        {
            Queue(1);
            _http.Reply(status, "");

            Assert.That(await Uploader().FlushAsync(), Is.EqualTo(FunnelFlushResult.Kept));
            Assert.That(_queue.Count, Is.EqualTo(1));
        }

        [TestCase(400)]
        [TestCase(413)]
        [TestCase(422)]
        public async Task A_4xx_rejection_drops_that_batch_and_is_never_retried(int status)
        {
            Queue(3);
            _http.Reply(status, "{\"error\":\"bad rows\"}");

            FunnelFlushResult result = await Uploader(batchSize: 2).FlushAsync();

            Assert.That(result, Is.EqualTo(FunnelFlushResult.Dropped));
            Assert.That(_queue.Count, Is.EqualTo(1), "only the refused batch goes");
            Assert.That(_store.Lines, Has.Count.EqualTo(1));
            Assert.That(_http.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task A_long_queue_goes_in_batches_until_it_is_empty()
        {
            Queue(5);
            _http.Reply(200, "").Reply(200, "").Reply(200, "");

            Assert.That(await Uploader(batchSize: 2).FlushAsync(), Is.EqualTo(FunnelFlushResult.Sent));
            Assert.That(_http.Requests, Has.Count.EqualTo(3));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        [Test]
        public async Task A_batch_never_exceeds_the_servers_40_rows()
        {
            Queue(45);
            _http.Reply(200, "").Reply(200, "");

            await Uploader(batchSize: 500).FlushAsync();

            Assert.That(_http.Requests, Has.Count.EqualTo(2));
            Assert.That(RowsIn(_http.Requests[0].Body!), Is.EqualTo(FunnelUploader.MaxRowsPerRequest));
            Assert.That(RowsIn(_http.Requests[1].Body!), Is.EqualTo(5));
        }

        [Test]
        public async Task A_batch_never_exceeds_the_servers_32_KiB()
        {
            // 25 rows of ~2.2 KB is ~55 KB: under the row cap, over the byte cap.
            var stamp = new FunnelStamp(Session, "0.0.14+31", FunnelPlatform.Android);
            for (int i = 0; i < 25; i++)
                _queue.Enqueue(FunnelRow.BoardPlayable(stamp, _clock.NowMs, new string('m', 2000)));
            for (int i = 0; i < 4; i++) _http.Reply(200, "");

            await Uploader().FlushAsync();

            Assert.That(_queue.Count, Is.EqualTo(0));
            Assert.That(_http.Requests, Has.Count.GreaterThan(1));
            foreach (var request in _http.Requests)
                Assert.That(System.Text.Encoding.UTF8.GetByteCount(request.Body!),
                    Is.LessThanOrEqualTo(FunnelUploader.MaxBodyBytes));
        }

        [Test]
        public void No_backoff_is_shorter_than_the_servers_Retry_After()
            => Assert.That(FunnelUploader.FirstBackoffMs, Is.GreaterThanOrEqualTo(FunnelUploader.RetryAfterFloorMs));

        [Test]
        public async Task The_servers_count_of_dropped_rows_still_removes_the_whole_batch()
        {
            Queue(3);
            _http.Reply(200, "{\"status\":\"ok\",\"accepted\":2,\"dropped\":1}");

            Assert.That(await Uploader().FlushAsync(), Is.EqualTo(FunnelFlushResult.Sent));
            Assert.That(_queue.Count, Is.EqualTo(0));
        }

        private static int RowsIn(string body)
            => body.Split(new[] { "\"milestone\":" }, StringSplitOptions.None).Length - 1;

        [Test]
        public async Task Signed_out_the_device_header_alone_identifies_the_rows()
        {
            Queue(1);
            _http.Reply(200, "");

            await Uploader().FlushAsync();

            var headers = _http.Requests.Single().Headers;
            Assert.That(Header(headers, FunnelHeaders.Device), Is.EqualTo(Device));
            Assert.That(Header(headers, "Authorization"), Is.Null);
        }

        [Test]
        public async Task Signed_in_the_bearer_rides_WITH_the_device_header_so_sign_in_can_bind_them()
        {
            Queue(1);
            _bearer = "jwt-abc";
            _http.Reply(200, "");

            await Uploader().FlushAsync();

            var headers = _http.Requests.Single().Headers;
            Assert.That(Header(headers, "Authorization"), Is.EqualTo("Bearer jwt-abc"));
            Assert.That(Header(headers, FunnelHeaders.Device), Is.EqualTo(Device));
        }

        [Test]
        public async Task A_refused_bearer_is_retried_once_on_the_device_alone()
        {
            Queue(1);
            _bearer = "expired";
            _http.Reply(401, "").Reply(200, "");

            Assert.That(await Uploader().FlushAsync(), Is.EqualTo(FunnelFlushResult.Sent));
            Assert.That(_http.Requests, Has.Count.EqualTo(2));
            Assert.That(Header(_http.Requests[1].Headers, "Authorization"), Is.Null);
            Assert.That(Header(_http.Requests[1].Headers, FunnelHeaders.Device), Is.EqualTo(Device));
        }

        [Test]
        public async Task A_bearer_source_that_throws_is_no_session_not_a_failure()
        {
            Queue(1);
            _http.Reply(200, "");
            var uploader = new FunnelUploader(_queue, Base, _http, Device,
                () => throw new InvalidOperationException("auth broke"), _clock);

            Assert.That(await uploader.FlushAsync(), Is.EqualTo(FunnelFlushResult.Sent));
        }

        [Test]
        public async Task An_http_seam_that_throws_keeps_the_rows_and_does_not_throw()
        {
            Queue(1);   // FakeAuthHttp with no scripted reply throws

            FunnelFlushResult result = FunnelFlushResult.Empty;
            Assert.DoesNotThrowAsync(async () => result = await Uploader().FlushAsync());
            Assert.That(result, Is.EqualTo(FunnelFlushResult.Kept));
            Assert.That(_queue.Count, Is.EqualTo(1));
            await Task.CompletedTask;
        }

        [Test]
        public void An_enqueue_flush_is_debounced()
        {
            Queue(1);
            FunnelUploader uploader = Uploader();
            uploader.RequestFlush();

            Assert.That(uploader.Tick(), Is.Null);
            _clock.Advance(FunnelUploader.DebounceMs - 1);
            Assert.That(uploader.Tick(), Is.Null);

            _http.Reply(200, "");
            _clock.Advance(1);
            Assert.That(uploader.Tick(), Is.Not.Null);
        }

        [Test]
        public async Task A_flush_already_running_is_not_doubled()
        {
            Queue(1);
            var held = _http.HoldReply();
            FunnelUploader uploader = Uploader();

            Task<FunnelFlushResult> first = uploader.FlushAsync();
            Assert.That(await uploader.FlushAsync(), Is.EqualTo(FunnelFlushResult.Busy));

            held.SetResult(Kingdom.Net.Client.Auth.AuthHttpResponse.Completed(200, ""));
            Assert.That(await first, Is.EqualTo(FunnelFlushResult.Sent));
            Assert.That(_http.Requests, Has.Count.EqualTo(1));
        }

        [Test]
        public void Another_successful_request_triggers_a_flush_only_when_rows_wait_and_no_backoff_runs()
        {
            FunnelUploader uploader = Uploader();
            uploader.NotifyServerAnswered();
            _clock.Advance(FunnelUploader.DebounceMs);
            Assert.That(uploader.Tick(), Is.Null, "nothing queued, nothing sent");

            Queue(1);
            uploader.NotifyServerAnswered();
            _clock.Advance(FunnelUploader.DebounceMs);
            _http.Reply(200, "");
            Assert.That(uploader.Tick(), Is.Not.Null);
        }

        // ── The recorder ─────────────────────────────────────────────────────────────────

        [Test]
        public void A_resume_with_no_pause_before_it_is_not_a_row()
        {
            var recorder = new FunnelRecorder(_queue, null, _clock,
                new FunnelStamp(Session, "0.0.14+31", FunnelPlatform.Android));

            recorder.Resume();      // Unity's start-up unpause
            recorder.Pause();
            recorder.Pause();       // a second pause while paused
            recorder.Resume();

            Assert.That(_queue.Snapshot().Select(MilestoneOf), Is.EqualTo(new[] { "pause", "resume" }));
        }

        [Test]
        public void The_recorder_maps_a_gate_result_and_its_attempts()
        {
            var recorder = new FunnelRecorder(_queue, null, _clock,
                new FunnelStamp(Session, "0.0.14+31", FunnelPlatform.Editor));

            recorder.Gate(UpdateGateResult.Unreachable("no route").WithAttempts(3));
            recorder.GateError();

            IReadOnlyList<string> rows = _queue.Snapshot();
            Assert.That(rows[0], Does.Contain("\"outcome\":\"unreachable\",\"attempts\":3"));
            Assert.That(rows[1], Does.Contain("\"outcome\":\"error\""));
        }

        [Test]
        public void Exit_is_written_but_never_asks_for_a_flush()
        {
            FunnelUploader uploader = Uploader();
            var recorder = new FunnelRecorder(_queue, uploader, _clock,
                new FunnelStamp(Session, "0.0.14+31", FunnelPlatform.Android));

            recorder.Exit();
            _clock.Advance(FunnelUploader.DebounceMs * 10);

            Assert.That(_queue.Count, Is.EqualTo(1));
            Assert.That(uploader.Tick(), Is.Null);
        }

        private static string MilestoneOf(string json)
        {
            const string key = "\"milestone\":\"";
            int start = json.IndexOf(key, StringComparison.Ordinal) + key.Length;
            return json.Substring(start, json.IndexOf('"', start) - start);
        }

        private sealed class MemoryStore : IFunnelQueueStore
        {
            public List<string> Lines { get; } = new List<string>();
            public IReadOnlyList<string> Load() => Lines.ToList();
            public void Append(string line) => Lines.Add(line);
            public void Replace(IReadOnlyList<string> lines)
            {
                Lines.Clear();
                Lines.AddRange(lines);
            }
        }
    }
}
