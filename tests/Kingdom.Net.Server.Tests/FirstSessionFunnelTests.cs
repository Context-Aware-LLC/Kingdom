using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.Telemetry;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests
{
    /// <summary>
    /// The server half of the first-session funnel (pre-publication item 2;
    /// <c>docs/dev-docs/specs/first-session-funnel.md</c>): client-row validation and stamping, the
    /// endpoint's auth and limits, the Sentry signature, the gate's row, and the match socket's
    /// hello / queue / queue_refused / first_action rows.
    /// </summary>
    [TestFixture]
    public sealed class FirstSessionFunnelTests
    {
        private const string Session = "0123456789abcdef0123456789abcdef";
        private const string Device = "test-device-token";
        private const long Now = 1_800_000_000_000; // 2027-01-15

        // ── Identity ─────────────────────────────────────────────────────────────

        [Test]
        public void Device_Hash_Matches_A_Vector_Computed_Outside_This_Codebase()
        {
            // python: hashlib.sha256('test-device-token'.encode()).hexdigest()[:16]
            Assert.That(FunnelFormat.DeviceHash(Device), Is.EqualTo("fdc2f4194f79710d"));
            // A GUID-shaped token, the shape DeviceIdentity.Token actually has.
            Assert.That(FunnelFormat.DeviceHash("aa14e45f-ce8f-4b6e-a1c2-d3e4f5a6b7c8"), Is.EqualTo("1790a31b698ff02d"));
        }

        // ── Client row validation ────────────────────────────────────────────────

        private static FunnelRow? Build(string json, string? device = "dev0dev0dev0dev0", string? account = null)
        {
            using var doc = JsonDocument.Parse(json);
            return ClientTelemetryEndpoint.TryBuildRow(doc.RootElement, device, account, "AT", Now);
        }

        private static string Ts(long ms = Now) => FunnelFormat.Timestamp(ms);

        [Test]
        public void A_Valid_Gate_Row_Is_Stamped_And_Normalised()
        {
            var row = Build($"{{\"milestone\":\"gate\",\"session_id\":\"{Session}\",\"client_ts\":\"2027-01-15T08:00:00Z\"," +
                            "\"build\":\"0.0.14+31\",\"platform\":\"android\",\"outcome\":\"unreachable\",\"attempts\":3," +
                            "\"cause\":\"timeout:dns\"}", account: "acct");
            Assert.That(row, Is.Not.Null);
            Assert.That(row!.Type, Is.EqualTo("client_milestone"));
            Assert.That(row.Get("client_ts"), Is.EqualTo("2027-01-15T08:00:00.000Z"));
            Assert.That(row.Get("device"), Is.EqualTo("dev0dev0dev0dev0"));
            Assert.That(row.Get("account_id"), Is.EqualTo("acct"));
            Assert.That(row.Get("at"), Is.EqualTo("AT"));
            Assert.That(row.Fields.Single(f => f.Name == "attempts").Value, Is.EqualTo(3));
        }

        [Test]
        public void Absent_Server_Fields_Stay_Absent()
        {
            var row = Build($"{{\"milestone\":\"launch\",\"session_id\":\"{Session}\",\"client_ts\":\"{Ts()}\"}}", device: null);
            Assert.That(row, Is.Not.Null);
            Assert.That(row!.Has("device"), Is.False);
            Assert.That(row.Has("account_id"), Is.False);
        }

        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"T\",\"extra\":1}", TestName = "Unknown field")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"T\",\"account_id\":\"x\"}", TestName = "Client claims account_id")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"T\",\"device\":\"x\"}", TestName = "Client claims device")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"T\",\"at\":\"x\"}", TestName = "Client claims at")]
        [TestCase("{\"milestone\":\"launch\",\"milestone\":\"exit\",\"session_id\":\"S\",\"client_ts\":\"T\"}", TestName = "Duplicate field")]
        [TestCase("{\"milestone\":\"crash\",\"session_id\":\"S\",\"client_ts\":\"T\"}", TestName = "Unknown milestone")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"0123456789ABCDEF0123456789ABCDEF\",\"client_ts\":\"T\"}", TestName = "Uppercase session")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"0123\",\"client_ts\":\"T\"}", TestName = "Short session")]
        [TestCase("{\"milestone\":\"launch\",\"client_ts\":\"T\"}", TestName = "Missing session")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"yesterday\"}", TestName = "Unparseable client_ts")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"2001-01-01T00:00:00.000Z\"}", TestName = "Absurdly old client_ts")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"2027-02-30T08:00:00.000Z\"}", TestName = "Impossible date client_ts")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"2027-01-15T10:00:00.000+02:00\"}", TestName = "Offset not Z client_ts")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"2027-01-15 08:00:00Z\"}", TestName = "Space separator client_ts")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"01/15/2027 08:00:00\"}", TestName = "Culture format client_ts")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"2099-01-01T00:00:00.000Z\"}", TestName = "Future client_ts")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"T\",\"outcome\":\"pass\"}", TestName = "Outcome off gate and sign_in")]
        [TestCase("{\"milestone\":\"gate\",\"session_id\":\"S\",\"client_ts\":\"T\"}", TestName = "Gate without outcome")]
        [TestCase("{\"milestone\":\"gate\",\"session_id\":\"S\",\"client_ts\":\"T\",\"outcome\":\"signed_in\"}", TestName = "Sign-in outcome on gate")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"T\",\"attempts\":2}", TestName = "Attempts off gate")]
        [TestCase("{\"milestone\":\"gate\",\"session_id\":\"S\",\"client_ts\":\"T\",\"outcome\":\"pass\",\"attempts\":\"2\"}", TestName = "Attempts as string")]
        [TestCase("{\"milestone\":\"gate\",\"session_id\":\"S\",\"client_ts\":\"T\",\"outcome\":\"pass\",\"attempts\":-1}", TestName = "Negative attempts")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"T\",\"cause\":\"Has Caps\"}", TestName = "Cause charset")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"T\",\"cause\":\"<b>x</b>\"}", TestName = "Cause markup")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"T\",\"cause\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}", TestName = "Cause 65 chars")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"T\",\"platform\":\"windows\"}", TestName = "Unknown platform")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"T\",\"build\":\"1.0 beta\"}", TestName = "Build charset")]
        [TestCase("{\"milestone\":\"queue\",\"session_id\":\"S\",\"client_ts\":\"T\",\"match_id\":\"0123456789abcdef0123456789abcdef\"}", TestName = "match_id off board_playable")]
        [TestCase("{\"milestone\":\"board_playable\",\"session_id\":\"S\",\"client_ts\":\"T\",\"match_id\":\"nope\"}", TestName = "Malformed match_id")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"T\",\"source\":\"menu\"}", TestName = "Source off queue")]
        [TestCase("{\"milestone\":\"queue\",\"session_id\":\"S\",\"client_ts\":\"T\",\"source\":\"deeplink\"}", TestName = "Unknown source")]
        [TestCase("{\"milestone\":\"launch\",\"session_id\":\"S\",\"client_ts\":\"T\",\"build\":7}", TestName = "Build as number")]
        [TestCase("[\"launch\"]", TestName = "Row not an object")]
        public void An_Invalid_Row_Is_Dropped(string template)
        {
            string json = template.Replace("\"S\"", $"\"{Session}\"").Replace("\"T\"", $"\"{Ts()}\"");
            Assert.That(Build(json), Is.Null);
        }

        [TestCase("{\"milestone\":\"sign_in\",\"session_id\":\"S\",\"client_ts\":\"T\",\"outcome\":\"skipped\",\"cause\":\"gate:stale\"}")]
        [TestCase("{\"milestone\":\"board_playable\",\"session_id\":\"S\",\"client_ts\":\"T\",\"match_id\":\"0123456789abcdef0123456789abcdef\"}")]
        [TestCase("{\"milestone\":\"queue\",\"session_id\":\"S\",\"client_ts\":\"T\",\"source\":\"result_view\"}")]
        [TestCase("{\"milestone\":\"pause\",\"session_id\":\"S\",\"client_ts\":\"T\",\"platform\":\"ios\",\"build\":\"0.0.14+editor\"}")]
        public void A_Valid_Row_Is_Kept(string template)
        {
            string json = template.Replace("\"S\"", $"\"{Session}\"").Replace("\"T\"", $"\"{Ts()}\"");
            Assert.That(Build(json), Is.Not.Null);
        }

        // ── The endpoint: auth, limits, durability ───────────────────────────────

        private static byte[] Rows(int n, string milestone = "pause", long at = Now)
        {
            var rows = Enumerable.Range(0, n).Select(i =>
                $"{{\"milestone\":\"{milestone}\",\"session_id\":\"{Session}\",\"client_ts\":\"{Ts(at - i)}\"}}");
            return Encoding.UTF8.GetBytes("{\"rows\":[" + string.Join(",", rows) + "]}");
        }

        private static (ClientTelemetryEndpoint Endpoint, InMemoryFunnelSink Sink, ClockAt Clock, InMemoryAccountStore Store)
            Endpoint(IIdentityTokenVerifier? verifier = null)
        {
            var clock = new ClockAt(Now);
            var sink = new InMemoryFunnelSink();
            var store = new InMemoryAccountStore(clock);
            return (new ClientTelemetryEndpoint(sink, verifier, store, clock), sink, clock, store);
        }

        [Test]
        public async Task Neither_Credential_Is_401()
        {
            var (ep, sink, _, _) = Endpoint();
            var r = await ep.PostAsync(Rows(1), bearer: null, deviceToken: null, "1.2.3.4");
            Assert.That(r.HttpStatus, Is.EqualTo(401));
            Assert.That(sink.Rows, Is.Empty);
        }

        [Test]
        public async Task Device_Token_Alone_Writes_Rows_Under_Its_Hash_And_No_Account()
        {
            var (ep, sink, _, _) = Endpoint();
            var r = await ep.PostAsync(Rows(2), null, Device, "1.2.3.4");
            Assert.That(r.HttpStatus, Is.EqualTo(200));
            Assert.That(r.Accepted, Is.EqualTo(2));
            Assert.That(sink.Rows.Select(x => x.Get("device")), Is.All.EqualTo("fdc2f4194f79710d"));
            Assert.That(sink.Rows.Any(x => x.Has("account_id")), Is.False);
            Assert.That(sink.Rows.Any(x => x.Fields.Any(f => (f.Value as string) == Device)), Is.False,
                "the raw device token is never stored");
        }

        [Test]
        public async Task A_Verified_Bearer_Stamps_The_Account_The_Gate_Would_Seat()
        {
            var verifier = new StubVerifier(TokenVerificationResult.Ok(new VerifiedIdentity("sub-1", "a@example.com", true)));
            var (ep, sink, _, store) = Endpoint(verifier);
            var r = await ep.PostAsync(SignIn(), "tok", Device, "1.2.3.4");
            Assert.That(r.HttpStatus, Is.EqualTo(200), r.Status);
            var account = await store.ResolveOrCreateAsync("supabase", CredentialHash.Of("sub-1"));
            Assert.That(sink.Rows.Single().Get("account_id"), Is.EqualTo(account.PlayerId));
            Assert.That(sink.Rows.Single().Get("device"), Is.EqualTo("fdc2f4194f79710d"));
        }

        private static byte[] SignIn() => Encoding.UTF8.GetBytes(
            $"{{\"rows\":[{{\"milestone\":\"sign_in\",\"session_id\":\"{Session}\",\"client_ts\":\"{Ts()}\",\"outcome\":\"signed_in\"}}]}}");

        [Test]
        public async Task A_Bearer_That_Fails_Is_401_Even_With_A_Device_Token()
        {
            var verifier = new StubVerifier(TokenVerificationResult.Fail(TokenVerificationFailure.SignatureInvalid));
            var (ep, sink, _, _) = Endpoint(verifier);
            var r = await ep.PostAsync(SignIn(), "forged", Device, "1.2.3.4");
            Assert.That(r.HttpStatus, Is.EqualTo(401), "no silent downgrade to the device token");
            Assert.That(sink.Rows, Is.Empty);
        }

        [Test]
        public async Task Size_And_Shape_Limits()
        {
            var (ep, _, _, _) = Endpoint();
            Assert.That((await ep.PostAsync(Rows(ClientTelemetryEndpoint.MaxRowsPerRequest + 1), null, Device, "a")).HttpStatus, Is.EqualTo(413));
            Assert.That((await ep.PostAsync(null, null, Device, "a")).HttpStatus, Is.EqualTo(413), "over the byte cap");
            Assert.That((await ep.PostAsync(Encoding.UTF8.GetBytes("{\"rows\":{}}"), null, Device, "a")).HttpStatus, Is.EqualTo(400));
            Assert.That((await ep.PostAsync(Encoding.UTF8.GetBytes("not json"), null, Device, "a")).HttpStatus, Is.EqualTo(400));
            Assert.That((await ep.PostAsync(Rows(1), null, new string('x', 257), "a")).HttpStatus, Is.EqualTo(400));
        }

        [Test]
        public async Task Invalid_Rows_Are_Dropped_And_Counted_Without_Failing_The_Batch()
        {
            var (ep, sink, _, _) = Endpoint();
            string body = "{\"rows\":[" +
                $"{{\"milestone\":\"launch\",\"session_id\":\"{Session}\",\"client_ts\":\"{Ts()}\"}}," +
                $"{{\"milestone\":\"launch\",\"session_id\":\"{Session}\",\"client_ts\":\"{Ts()}\",\"account_id\":\"x\"}}]}}";
            var r = await ep.PostAsync(Encoding.UTF8.GetBytes(body), null, Device, "a");
            Assert.That((r.HttpStatus, r.Accepted, r.Dropped), Is.EqualTo((200, 1, 1)));
            Assert.That(sink.Rows, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task Per_Device_Limit_Is_A_Session_Burst_Then_A_Row_Per_Ten_Seconds()
        {
            var (ep, sink, clock, _) = Endpoint();
            Assert.That((await ep.PostAsync(Rows(40), null, Device, "a")).HttpStatus, Is.EqualTo(200));
            Assert.That((await ep.PostAsync(Rows(1), null, Device, "a")).HttpStatus, Is.EqualTo(429));
            Assert.That((await ep.PostAsync(Rows(1), null, "another-device", "a")).HttpStatus, Is.EqualTo(200),
                "the limit is per device");
            clock.Advance(10_000);
            Assert.That((await ep.PostAsync(Rows(1), null, Device, "a")).HttpStatus, Is.EqualTo(200));
            Assert.That((await ep.PostAsync(Rows(1), null, Device, "a")).HttpStatus, Is.EqualTo(429));
            Assert.That(sink.Rows, Has.Count.EqualTo(42));
        }

        [Test]
        public async Task Rotating_Device_Tokens_Hits_The_Per_Address_Limit()
        {
            var (ep, _, _, _) = Endpoint();
            int ok = 0;
            for (int i = 0; i < 20; i++)
                if ((await ep.PostAsync(Rows(40), null, "rotated-" + i, "9.9.9.9")).HttpStatus == 200) ok++;
            Assert.That(ok, Is.EqualTo(10), "ten devices' burst per address");
        }

        [Test]
        public async Task A_Sink_That_Cannot_Write_Is_503_So_The_Client_Keeps_Its_Rows()
        {
            var (ep, sink, _, _) = Endpoint();
            sink.Fail = true;
            Assert.That((await ep.PostAsync(Rows(1), null, Device, "a")).HttpStatus, Is.EqualTo(503));
        }

        [Test]
        public void The_Limiter_Table_Is_Bounded_And_Sheds_Only_Refilled_Buckets()
        {
            var clock = new ClockAt(Now);
            var limiter = new FunnelRateLimiter(clock, burst: 4, refillMsPerRow: 1_000, maxKeys: 3);
            Assert.That(limiter.TryTake("a", 4) && limiter.TryTake("b", 4) && limiter.TryTake("c", 4), Is.True);
            Assert.That(limiter.TryTake("d", 1), Is.False, "full table, nothing refilled: a new key waits");
            Assert.That(limiter.KeyCount, Is.EqualTo(3));
            clock.Advance(4_000);
            Assert.That(limiter.TryTake("d", 1), Is.True, "refilled buckets are shed to make room");
            Assert.That(limiter.KeyCount, Is.LessThanOrEqualTo(3));
        }

        // ── Sentry ───────────────────────────────────────────────────────────────

        private const string Secret = "sentry-client-secret";

        private static byte[] SentryError(string tagsJson, string path = "error", string userId = "x") => Encoding.UTF8.GetBytes(
            "{\"action\":\"created\",\"data\":{\"" + path + "\":{\"event_id\":\"bb78c1407cea4519aa397afc059c793d\"," +
            "\"release\":\"0.0.14+31\",\"tags\":" + tagsJson + ",\"user\":{\"id\":\"" + userId + "\"}}}}");

        [Test]
        public void The_Device_Comes_From_Device_Hash_Then_The_User_Id_When_Sentry_Owns_The_Device_Tag()
        {
            // Sentry fills `device` with the model on a real event (2026-10-23), so it must not win.
            byte[] tagged = SentryError(
                $"[[\"session_id\",\"{Session}\"],[\"device\",\"Pixel 5a\"],[\"device_hash\",\"fdc2f4194f79710d\"]]",
                userId: "0000000000000000");
            var a = SentryWebhook.Handle(Secret, tagged, false, SentryWebhook.Sign(Secret, tagged), "error", Now);
            Assert.That(a.Row!.Get("device"), Is.EqualTo("fdc2f4194f79710d"));

            byte[] older = SentryError($"[[\"session_id\",\"{Session}\"],[\"device\",\"Pixel 5a\"]]", userId: "fdc2f4194f79710d");
            var b = SentryWebhook.Handle(Secret, older, false, SentryWebhook.Sign(Secret, older), "error", Now);
            Assert.That(b.Row!.Get("device"), Is.EqualTo("fdc2f4194f79710d"));
        }

        [Test]
        public void Sentry_Signature_Is_Hmac_Sha256_Hex_Of_The_Raw_Body()
        {
            // python: hmac.new(b'sentry-client-secret', b'{}', hashlib.sha256).hexdigest()
            Assert.That(SentryWebhook.Sign(Secret, Encoding.UTF8.GetBytes("{}")),
                Is.EqualTo("c4c61b46bc0d465acc8d3b7391267c342345ad2b9552bfc04696231fcbe48bba"));
        }

        [Test]
        public void A_Signed_Error_Becomes_A_Crash_Row_With_The_Funnel_Keys_And_Nothing_Else()
        {
            byte[] body = SentryError($"[[\"session_id\",\"{Session}\"],[\"device\",\"fdc2f4194f79710d\"],[\"level\",\"fatal\"]]");
            var o = SentryWebhook.Handle(Secret, body, false, SentryWebhook.Sign(Secret, body), "error", Now);
            Assert.That(o.HttpStatus, Is.EqualTo(200));
            Assert.That(o.Row, Is.Not.Null);
            Assert.That(o.Row!.Type, Is.EqualTo("crash"));
            Assert.That(o.Row.Get("session_id"), Is.EqualTo(Session));
            Assert.That(o.Row.Get("device"), Is.EqualTo("fdc2f4194f79710d"));
            Assert.That(o.Row.Get("sentry_event_id"), Is.EqualTo("bb78c1407cea4519aa397afc059c793d"));
            Assert.That(o.Row.Get("release"), Is.EqualTo("0.0.14+31"));
            Assert.That(o.Row.Fields.Select(f => f.Name),
                Is.EqualTo(new[] { "session_id", "device", "sentry_event_id", "release", "at" }));
        }

        [Test]
        public void An_Event_Alert_Reads_The_Event_Under_Data_Event()
        {
            byte[] body = SentryError($"[[\"session_id\",\"{Session}\"]]", path: "event");
            var o = SentryWebhook.Handle(Secret, body, false, SentryWebhook.Sign(Secret, body), "event_alert", Now);
            Assert.That(o.Row?.Get("session_id"), Is.EqualTo(Session));
            Assert.That(o.Row!.Has("device"), Is.False);
        }

        [Test]
        public void A_Bad_Or_Missing_Signature_Is_401_And_Writes_Nothing()
        {
            byte[] body = SentryError("[]");
            Assert.That(SentryWebhook.Handle(Secret, body, false, SentryWebhook.Sign("wrong", body), "error", Now).HttpStatus, Is.EqualTo(401));
            Assert.That(SentryWebhook.Handle(Secret, body, false, null, "error", Now).HttpStatus, Is.EqualTo(401));
            byte[] tampered = (byte[])body.Clone();
            tampered[^3] = (byte)'X';
            Assert.That(SentryWebhook.Handle(Secret, tampered, false, SentryWebhook.Sign(Secret, body), "error", Now).Row, Is.Null);
        }

        [Test]
        public void Other_Resources_Are_200_With_No_Row()
        {
            byte[] body = Encoding.UTF8.GetBytes("{\"action\":\"created\",\"data\":{\"installation\":{}}}");
            var o = SentryWebhook.Handle(Secret, body, false, SentryWebhook.Sign(Secret, body), "installation", Now);
            Assert.That((o.HttpStatus, o.Row), Is.EqualTo((200, (FunnelRow?)null)));
        }

        [Test]
        public void Malformed_Tags_Are_Dropped_Not_Stored()
        {
            byte[] body = SentryError("[[\"session_id\",\"<script>\"],[\"device\",\"TOOLONGTOOLONGTOOLONG\"]]");
            var o = SentryWebhook.Handle(Secret, body, false, SentryWebhook.Sign(Secret, body), "error", Now);
            Assert.That(o.Row!.Has("session_id") || o.Row.Has("device"), Is.False);
        }

        [Test]
        public void An_Oversized_Sentry_Body_Is_413()
            => Assert.That(SentryWebhook.Handle(Secret, default, true, "x", "error", Now).HttpStatus, Is.EqualTo(413));

        // ── gate_served ──────────────────────────────────────────────────────────

        [Test]
        public void Gate_Served_Reruns_The_Clients_Verdict_From_Its_Headers()
        {
            string Outcome(string? platform, string? build, string? contract = null) =>
                ServerFunnelRows.GateServed(Session, Device, platform, build, contract, "interfaces.v5", 40, 7, Now).Get("outcome")!;

            Assert.That(Outcome("android", "0.0.14+31"), Is.EqualTo("stale"));
            Assert.That(Outcome("android", "0.0.15+40"), Is.EqualTo("pass"));
            Assert.That(Outcome("ios", "0.0.14+31"), Is.EqualTo("pass"));
            Assert.That(Outcome("android", "0.0.14+editor"), Is.EqualTo("pass"), "no store build is never stale by number");
            Assert.That(Outcome("desktop", "0.0.15+40", "interfaces.v4"), Is.EqualTo("stale"), "older contract");
            Assert.That(Outcome(null, "0.0.14+31"), Is.Null, "nothing to decide on: no outcome, never a guessed pass");
        }

        [Test]
        public void Gate_Served_Drops_Bad_Headers_And_Hashes_The_Device()
        {
            var row = ServerFunnelRows.GateServed("not-a-session", Device, "windows", "1 0", null, "interfaces.v5", 0, 0, Now);
            Assert.That(row.Has("session_id") || row.Has("platform") || row.Has("build"), Is.False);
            Assert.That(row.Get("device"), Is.EqualTo("fdc2f4194f79710d"));

            var bare = ServerFunnelRows.GateServed(null, null, null, null, null, "interfaces.v5", 0, 0, Now);
            Assert.That(bare.Fields.Select(f => f.Name), Is.EqualTo(new[] { "at" }));
        }

        // ── Durability: the JSONL buffer and the drain ───────────────────────────

        [Test]
        public void Funnel_Rows_Share_The_Jsonl_Buffer_And_Drain_Into_Raw_Event_Only()
        {
            string dir = Path.Combine(Path.GetTempPath(), "kingdom-funnel-" + Guid.NewGuid().ToString("N"));
            try
            {
                var sink = new JsonlTelemetrySink(dir, "test");
                Assert.That(sink.Write(new[]
                {
                    ServerFunnelRows.Queue("acct", Now),
                    new FunnelRow("client_milestone").Str("milestone", "gate").Int("attempts", 2),
                }), Is.True);

                string[] lines = File.ReadAllLines(sink.Path);
                Assert.That(lines, Has.Length.EqualTo(2));
                Assert.That(lines[0], Is.EqualTo("{\"type\":\"queue\",\"env\":\"test\",\"account_id\":\"acct\",\"at\":\"" + Ts() + "\"}"));
                Assert.That(lines[1], Does.Contain("\"attempts\":2"));

                foreach (string line in lines)
                {
                    var into = new List<Npgsql.NpgsqlBatchCommand>();
                    Assert.That(TelemetryDrain.TryBuildLine(line, "uid", into), Is.True);
                    Assert.That(into, Has.Count.EqualTo(1));
                    Assert.That(into[0].CommandText, Does.StartWith("insert into raw_event"));
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        // ── The real app: routes and the match socket ────────────────────────────

        [Test]
        [CancelAfter(60_000)]
        public async Task Update_Gate_Answers_Unchanged_And_Writes_Gate_Served()
        {
            var sink = new InMemoryFunnelSink();
            await WithAppAsync(sink, null, null, async (http, _) =>
            {
                var bare = await http.GetStringAsync("/update-gate");

                using var req = new HttpRequestMessage(HttpMethod.Get, "/update-gate");
                req.Headers.Add(ServerFunnelRows.HeaderSession, Session);
                req.Headers.Add(ServerFunnelRows.HeaderDevice, Device);
                req.Headers.Add(ServerFunnelRows.HeaderPlatform, "android");
                req.Headers.Add(ServerFunnelRows.HeaderBuild, "0.0.14+31");
                var withHeaders = await (await http.SendAsync(req)).Content.ReadAsStringAsync();

                Assert.That(withHeaders, Is.EqualTo(bare), "the answer does not depend on the headers");
                var rows = sink.OfType("gate_served");
                Assert.That(rows, Has.Count.EqualTo(2));
                Assert.That(rows[0].Fields.Select(f => f.Name), Is.EqualTo(new[] { "at" }));
                Assert.That(rows[1].Get("session_id"), Is.EqualTo(Session));
                Assert.That(rows[1].Get("device"), Is.EqualTo("fdc2f4194f79710d"));
                Assert.That(rows[1].Get("outcome"), Is.EqualTo("pass"));
            });
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task Telemetry_Routes_Over_Http()
        {
            var sink = new InMemoryFunnelSink();
            await WithAppAsync(sink, null, null, async (http, _) =>
            {
                var none = await http.PostAsync("/telemetry/client", new ByteArrayContent(Rows(1)));
                Assert.That(none.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

                // The real app runs on the wall clock, so this row is stamped by it.
                byte[] live = Rows(1, at: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                using var req = new HttpRequestMessage(HttpMethod.Post, "/telemetry/client") { Content = new ByteArrayContent(live) };
                req.Headers.Add(ServerFunnelRows.HeaderDevice, Device);
                var ok = await http.SendAsync(req);
                Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(sink.OfType("client_milestone"), Has.Count.EqualTo(1));

                using var huge = new HttpRequestMessage(HttpMethod.Post, "/telemetry/client")
                    { Content = new ByteArrayContent(new byte[ClientTelemetryEndpoint.MaxBodyBytes + 1]) };
                huge.Headers.Add(ServerFunnelRows.HeaderDevice, Device);
                Assert.That((await http.SendAsync(huge)).StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));

                var sentry = await http.PostAsync("/telemetry/sentry", new ByteArrayContent(SentryError("[]")));
                Assert.That(sentry.StatusCode, Is.EqualTo(HttpStatusCode.NotFound), "no secret, no route");
            });

            var sentrySink = new InMemoryFunnelSink();
            await WithAppAsync(sentrySink, null, Secret, async (http, _) =>
            {
                byte[] body = SentryError($"[[\"session_id\",\"{Session}\"]]");
                using var req = new HttpRequestMessage(HttpMethod.Post, "/telemetry/sentry") { Content = new ByteArrayContent(body) };
                req.Headers.Add(SentryWebhook.SignatureHeader, SentryWebhook.Sign(Secret, body));
                req.Headers.Add(SentryWebhook.ResourceHeader, "error");
                Assert.That((await http.SendAsync(req)).StatusCode, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(sentrySink.OfType("crash").Single().Get("session_id"), Is.EqualTo(Session));

                using var bad = new HttpRequestMessage(HttpMethod.Post, "/telemetry/sentry") { Content = new ByteArrayContent(body) };
                bad.Headers.Add(SentryWebhook.SignatureHeader, "00");
                bad.Headers.Add(SentryWebhook.ResourceHeader, "error");
                Assert.That((await http.SendAsync(bad)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(sentrySink.OfType("crash"), Has.Count.EqualTo(1));
            });
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Signed_In_Match_Writes_Hello_Queue_And_One_First_Action_Per_Seat()
        {
            var sink = new InMemoryFunnelSink();
            var verifier = new StubVerifier(TokenVerificationResult.Ok(new VerifiedIdentity("sub-1", "a@example.com", true)));
            var store = new InMemoryAccountStore(new FakeTime());
            await WithAppAsync(sink, verifier, null, async (_, wsUri) =>
            {
                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c0, "device-a", "tok");
                await c1.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c1, "device-b", "tok");
                var start0 = await ExpectAsync<MatchStartMsg>(c0);
                var start1 = await ExpectAsync<MatchStartMsg>(c1);

                var matchId = new MatchId(start0.Snapshot.State.Id.Value);
                await SendAsync(c0, ProtocolCodec.Encode(
                    new IntentMsg(0, new MulliganIntent(start0.PlayerId, Array.Empty<InstanceId>())), matchId, 1));
                await SendAsync(c1, ProtocolCodec.Encode(
                    new IntentMsg(0, new MulliganIntent(start1.PlayerId, Array.Empty<InstanceId>())), matchId, 1));
                await SendAsync(c1, ProtocolCodec.Encode(new IntentMsg(0, new ConcedeIntent(start1.PlayerId)), matchId, 2));
                await ExpectAsync<MatchEndMsg>(c0);

                var hellos = sink.OfType("hello");
                Assert.That(hellos.Select(h => h.Get("device")),
                    Is.EquivalentTo(new[] { FunnelFormat.DeviceHash("device-a"), FunnelFormat.DeviceHash("device-b") }));
                Assert.That(sink.OfType("queue"), Has.Count.EqualTo(2));
                var firsts = sink.OfType("first_action");
                Assert.That(firsts, Has.Count.EqualTo(2), "one per seat; the concede is a second action");
                Assert.That(firsts.Select(f => f.Get("match_id")), Is.All.EqualTo(matchId.Value));
                Assert.That(hellos.Concat(firsts).Select(r => r.Get("account_id")).Distinct().Count(), Is.EqualTo(1),
                    "both sockets are the one stub subject, and every row names the same account");
            }, store: store);
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Signed_In_Pairing_At_The_Cap_Writes_Queue_Refused_Capacity()
        {
            var sink = new InMemoryFunnelSink();
            var verifier = new StubVerifier(TokenVerificationResult.Ok(new VerifiedIdentity("sub-1", "a@example.com", true)));
            await WithAppAsync(sink, verifier, null, async (_, wsUri) =>
            {
                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c0, "device-a", "tok");
                await c1.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c1, "device-b", "tok");
                await ExpectAsync<MatchStartMsg>(c0);
                await ExpectAsync<MatchStartMsg>(c1);

                using var c2 = new ClientWebSocket();
                await c2.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c2, "device-c", "tok");
                await AwaitCloseAsync(c2);

                var refused = sink.OfType("queue_refused");
                Assert.That(refused, Has.Count.EqualTo(1));
                Assert.That(refused[0].Get("reason"), Is.EqualTo("capacity"));
                Assert.That(sink.OfType("queue"), Has.Count.EqualTo(2), "a refused pairing never entered the queue");
            }, maxSessions: 1);
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task Guest_Tooling_Seats_Write_No_Funnel_Rows()
        {
            var sink = new InMemoryFunnelSink();
            await WithAppAsync(sink, null, null, async (_, wsUri) =>
            {
                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c0, "guest-a", "");
                await c1.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c1, "guest-b", "");
                var start0 = await ExpectAsync<MatchStartMsg>(c0);
                await ExpectAsync<MatchStartMsg>(c1);
                var matchId = new MatchId(start0.Snapshot.State.Id.Value);
                await SendAsync(c0, ProtocolCodec.Encode(new IntentMsg(0, new ConcedeIntent(start0.PlayerId)), matchId, 1));
                await ExpectAsync<MatchEndMsg>(c1);
                Assert.That(sink.Rows, Is.Empty);
            });
        }

        // ── Harness ──────────────────────────────────────────────────────────────

        private sealed class ClockAt : ITimeSource
        {
            public ClockAt(long now) => NowMs = now;
            public long NowMs { get; private set; }
            public void Advance(long ms) => NowMs += ms;
        }

        private sealed class StubVerifier : IIdentityTokenVerifier
        {
            private readonly TokenVerificationResult _result;
            public StubVerifier(TokenVerificationResult result) => _result = result;
            public Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
                => Task.FromResult(_result);
        }

        private static async Task WithAppAsync(
            InMemoryFunnelSink sink, IIdentityTokenVerifier? verifier, string? sentrySecret,
            Func<HttpClient, Uri, Task> body, IAccountStore? store = null, int maxSessions = 64)
        {
            string replays = Path.Combine(Path.GetTempPath(), "kingdom-funnel-" + Guid.NewGuid().ToString("N"));
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                Funnel = sink,
                SentryWebhookSecret = sentrySecret,
                TokenVerifier = verifier,
                AccountStore = store,
                Options = new ServerOptions
                {
                    HelloTimeoutMs = 30_000,
                    AllowGuestMatches = true,
                    MaxSessions = maxSessions,
                },
            });
            await built.App.StartAsync();
            try
            {
                var baseUri = new Uri(built.App.Urls.First());
                using var http = new HttpClient { BaseAddress = baseUri };
                await body(http, new Uri($"ws://127.0.0.1:{baseUri.Port}/ws"));
            }
            finally
            {
                await built.App.StopAsync();
                try { if (Directory.Exists(replays)) Directory.Delete(replays, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static Task SendHelloAsync(ClientWebSocket ws, string deviceToken, string authToken)
            => SendAsync(ws, ProtocolCodec.Encode(
                new HelloMsg(deviceToken, ProtocolContract.Version, "test", "funnel-tests", DisplayName: "", AuthToken: authToken),
                ProtocolCodec.PreMatch, 0));

        private static Task SendAsync(ClientWebSocket ws, byte[] frame)
            => ws.SendAsync(frame, WebSocketMessageType.Text, true, CancellationToken.None);

        private static async Task AwaitCloseAsync(ClientWebSocket ws)
        {
            var buffer = new byte[64 * 1024];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            WebSocketReceiveResult result;
            do result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            while (result.MessageType != WebSocketMessageType.Close);
        }

        private static async Task<T> ExpectAsync<T>(ClientWebSocket ws) where T : NetMessage
        {
            var buffer = new byte[1024 * 1024];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                int total = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new InvalidOperationException($"Socket closed while waiting for {typeof(T).Name}.");
                    total += result.Count;
                } while (!result.EndOfMessage);

                var res = ProtocolCodec.Decode(new ReadOnlySpan<byte>(buffer, 0, total).ToArray());
                if (res.Ok && res.Message is T hit) return hit;
            }
        }
    }
}
