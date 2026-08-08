using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests
{
    /// <summary>
    /// EF-073 / VULN-002 — the display name is peer-authored text rendered on the victim's client.
    ///
    /// <para><b>Why these tests go over a real socket and not through PlayerName directly.</b> There
    /// are already unit tests for the predicate (<c>PlayerProfileTests</c>), and they were green for
    /// the entire time this vulnerability was open — because they test the SENDER's copy. VULN-002 §5
    /// is explicit that "the local half is already built and green" is what makes this ship by
    /// accident: a code sweep sees a validator, tests, and a call on every save, and reads that as
    /// "names are validated".</para>
    ///
    /// <para>So every test here sends a hello that <b>no honest client would produce</b> — the exact
    /// thing an attacker gets by patching their client or editing profile.json — and asserts on what
    /// the OPPONENT's <c>MatchStartMsg</c> carries. That is the only assertion that distinguishes a
    /// validator that runs on arrival from one that runs on the half that does not count.</para>
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    // Boots real Kestrel hosts on ephemeral ports — same reason GuestIdentityTests is serialized.
    [NonParallelizable]
    public sealed class DisplayNameOnTheWireTests
    {
        private const string TokenA = "aa14e45fce8f4b6ea1c2d3e4f5a6b7c8";
        private const string TokenB = "bb23456789abcdef0123456789abcdef";

        /// <summary>The server's fallback is the seat id it minted (MatchGate.NameForSeat).</summary>
        private const string SeatIdPrefix = "guest-";

        // ── The name that passes ─────────────────────────────────────────────────

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Valid_Name_Reaches_The_Opponents_Nameplate()
        {
            // The feature half: without this the whole task is "delete the field".
            await WithPairAsync("Sam Ember", "", (start0, start1) =>
            {
                Assert.That(start1.Opponent.DisplayName, Is.EqualTo("Sam Ember"),
                    "a name that passes PlayerName.Check must reach the opponent");
                Assert.That(start0.Opponent.DisplayName, Does.StartWith(SeatIdPrefix),
                    "the seat that sent no name gets the server-assigned fallback");
            });
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task The_SERVER_Normalizes_Rather_Than_Trusting_The_Senders_Form()
        {
            // An honest client would have normalized before saving. This one did not, which is
            // precisely what "the sender's validator is advice they can decline" means: the server
            // must store PlayerName.Check's Normalized value, not the bytes it was handed.
            await WithPairAsync("   Sam     Ember   ", "", (_, start1) =>
                Assert.That(start1.Opponent.DisplayName, Is.EqualTo("Sam Ember"),
                    "the whitespace collapse must happen on ARRIVAL, not be trusted from the sender"));
        }

        // ── The names that must not ──────────────────────────────────────────────

        [Test]
        [CancelAfter(60_000)]
        public async Task Rich_Text_Markup_Never_Reaches_The_Opponent()
        {
            // VULN-002 §3: "<size=400%>" on a TMP label redraws the layout on the VICTIM's machine.
            // VersusOverlay disables richText, but that is depth — this asserts the server-side fix.
            await WithPairAsync("<size=400%>PWNED", "", (_, start1) =>
            {
                Assert.That(start1.Opponent.DisplayName, Does.Not.Contain("<"),
                    "markup must be refused server-side, not merely neutralised by the renderer");
                Assert.That(start1.Opponent.DisplayName, Does.StartWith(SeatIdPrefix));
            });
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task An_Overlong_Name_Never_Reaches_The_Opponent()
        {
            // VULN-002 §3: "a 4,000-character name in a box laid out for 16". MaxLength is enforced
            // on the sender today, and a length rule enforced only at the sender is not enforced.
            await WithPairAsync(new string('a', 4_000), "", (_, start1) =>
            {
                Assert.That(start1.Opponent.DisplayName.Length, Is.LessThanOrEqualTo(64),
                    "a 4,000-character name must not be forwarded");
                Assert.That(start1.Opponent.DisplayName, Does.StartWith(SeatIdPrefix));
            });
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Bidi_Override_Never_Reaches_The_Opponent()
        {
            // U+202E reverses the rendered run, so a nameplate reads as something other than its
            // bytes. Invisible by definition: no reviewer looking at the string would catch it.
            await WithPairAsync("Sam‮maS", "", (_, start1) =>
            {
                AssertDoesNotContain(start1.Opponent.DisplayName, '‮',
                    "a bidi override must never be forwarded to a renderer");
                Assert.That(start1.Opponent.DisplayName, Does.StartWith(SeatIdPrefix));
            });
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Zero_Width_Character_Never_Reaches_The_Opponent()
        {
            // Two accounts that render identically (VULN-002 §3). Format category, same refusal.
            await WithPairAsync("Sam​Ember", "", (_, start1) =>
            {
                AssertDoesNotContain(start1.Opponent.DisplayName, '​',
                    "a zero-width character must never be forwarded to a renderer");
                Assert.That(start1.Opponent.DisplayName, Does.StartWith(SeatIdPrefix));
            });
        }

        [TestCase("SYSTEM")]
        [TestCase("Opponent")]
        [TestCase("You")]
        [TestCase("Unnamed player")]
        [TestCase("Emberfall")]
        [TestCase("  sYsTeM  ")]   // normalization + case must not be an escape hatch
        [CancelAfter(60_000)]
        public async Task A_Name_Our_Own_UI_Authors_Cannot_Be_Claimed(string reserved)
        {
            // VULN-002 §4.3. Every character in these is legal, so no character-class rule reaches
            // them — this is the one mitigation that is about MEANING rather than bytes.
            await WithPairAsync(reserved, "", (_, start1) =>
                Assert.That(start1.Opponent.DisplayName, Does.StartWith(SeatIdPrefix),
                    $"'{reserved}' impersonates our own chrome and must be refused"));
        }

        // ── The refusal must not be a denial of service ──────────────────────────

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Refused_Name_Still_Seats_The_Player()
        {
            // The fallback is a substitution, NOT a closed socket. A player whose name is bad — or
            // who is on an old client that sends none — still gets a match; dropping the connect
            // would be a support incident for no security gain (MatchGate, VULN-002 §4.1).
            await WithPairAsync("<<<>>>", "", (start0, start1) =>
            {
                Assert.That(start0, Is.Not.Null);
                Assert.That(start1, Is.Not.Null, "a refused name must never cost the player their match");
                Assert.That(start1.Opponent.DisplayName, Does.StartWith(SeatIdPrefix));
            });
        }

        [Test]
        [CancelAfter(60_000)]
        public async Task A_Hello_With_No_Name_Field_At_All_Still_Pairs()
        {
            // Wire compatibility with a client built before this field existed. 'displayName' is
            // optional in the codec, and absent must mean empty rather than a decode failure.
            await WithServerAsync(async wsUri =>
            {
                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await SendLegacyHelloWithoutNameFieldAsync(c0, TokenA);
                await c1.ConnectAsync(wsUri, CancellationToken.None);
                await SendLegacyHelloWithoutNameFieldAsync(c1, TokenB);

                var start0 = await ExpectAsync<MatchStartMsg>(c0);
                var start1 = await ExpectAsync<MatchStartMsg>(c1);

                Assert.That(start0.Opponent.DisplayName, Does.StartWith(SeatIdPrefix));
                Assert.That(start1.Opponent.DisplayName, Does.StartWith(SeatIdPrefix));
            });
        }

        // ── Harness ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Ordinal "this character is absent", and it must stay ordinal.
        ///
        /// <para><b>Do not replace this with <c>Does.Not.Contain("‮")</c>.</b> NUnit's substring
        /// constraint compares CULTURE-SENSITIVELY, and bidi/zero-width characters are
        /// collation-IGNORABLE — so the needle folds to the empty string, every haystack "contains"
        /// it, and the assertion fails against a perfectly good <c>guest-2</c>. Written the other way
        /// round (asserting presence) the same fold makes the test pass no matter what the server
        /// sent, which is the failure mode that matters here: a green test over an unchecked name.
        /// <c>string.IndexOf(char)</c> is ordinal and has neither behaviour.</para>
        /// </summary>
        private static void AssertDoesNotContain(string actual, char forbidden, string because)
            => Assert.That(actual.IndexOf(forbidden), Is.LessThan(0),
                $"{because} — got '{actual}'");

        /// <summary>
        /// Pair two sockets, seat 0 claiming <paramref name="name0"/> and seat 1
        /// <paramref name="name1"/>, then hand both <c>MatchStartMsg</c>s to the assertion.
        /// Seat 1's message carries seat 0's name, so <c>start1.Opponent.DisplayName</c> is what
        /// <paramref name="name0"/> actually became after the server was done with it.
        /// </summary>
        private static Task WithPairAsync(
            string name0, string name1, Action<MatchStartMsg, MatchStartMsg> assert)
            => WithServerAsync(async wsUri =>
            {
                using var c0 = new ClientWebSocket();
                using var c1 = new ClientWebSocket();
                await c0.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c0, TokenA, name0);
                await c1.ConnectAsync(wsUri, CancellationToken.None);
                await SendHelloAsync(c1, TokenB, name1);

                var start0 = await ExpectAsync<MatchStartMsg>(c0);
                var start1 = await ExpectAsync<MatchStartMsg>(c1);
                assert(start0, start1);
            });

        private static async Task WithServerAsync(Func<Uri, Task> body)
        {
            string replays = Path.Combine(Path.GetTempPath(), "emberfall-displayname-" + Guid.NewGuid().ToString("N"));
            var built = ServerApp.BuildWithGate(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                ReplaysDir = replays,
                // Generous for the same reason GuestIdentityTests is: a short deadline made the
                // PAIRING tests flaky under load, and every test here needs a pair to exist.
                Options = new ServerOptions { HelloTimeoutMs = 30_000 },
            });
            await built.App.StartAsync();
            try
            {
                var baseUri = new Uri(built.App.Urls.First());
                await body(new Uri($"ws://127.0.0.1:{baseUri.Port}/ws"));
            }
            finally
            {
                await built.App.StopAsync();
                await TryDeleteDirectoryAsync(replays);
            }
        }

        private static Task SendHelloAsync(ClientWebSocket ws, string deviceToken, string displayName)
        {
            var frame = ProtocolCodec.Encode(
                new HelloMsg(deviceToken, ProtocolContract.Version, "test", "displayname-tests", displayName),
                ProtocolCodec.PreMatch, 0);
            return ws.SendAsync(frame, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        /// <summary>
        /// A hello frame with no <c>displayName</c> key at all — hand-built, because the encoder
        /// always writes the field now and the point of the test is the client that predates it.
        /// </summary>
        private static Task SendLegacyHelloWithoutNameFieldAsync(ClientWebSocket ws, string deviceToken)
        {
            string json =
                "{\"v\":1,\"type\":\"hello\",\"matchId\":\"" + ProtocolCodec.PreMatch.Value + "\",\"seq\":0," +
                "\"body\":{\"deviceToken\":\"" + deviceToken + "\"," +
                "\"clientContractVersion\":\"" + ProtocolContract.Version + "\"," +
                "\"platform\":\"test\",\"buildId\":\"legacy\"}}";
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            return ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static async Task TryDeleteDirectoryAsync(string dir)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                    return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                await Task.Delay(50).ConfigureAwait(false);
            }
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
