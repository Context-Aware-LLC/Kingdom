using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client;
using Emberfall.Net.Client.Tests.Support;
using Emberfall.Net.Protocol;
using Emberfall.Net.Server;
using NUnit.Framework;
using Emberfall.Core.Tests.Support;

namespace Emberfall.Net.Client.Tests
{
    [TestFixture]
    [Category("Contract")]
    public sealed class DeckHandshakeTests
    {
        [Test]
        public void Selected_Deck_Is_The_Second_Handshake_Frame_On_The_Deck_Route()
        {
            var transport = new RecordingTransport();
            var deck = new SubmitDeckMsg(
                CardClass.GRAVEKEEP,
                CardClass.VERDANT,
                new[] { new DeckCardLine(FixtureCard.Id("M1-GRAVEKEEP-01"), 2) });
            using var session = new NetworkedMatchSession(transport, new ClientOptions
            {
                DeviceToken = "test-device",
                DeckSubmission = deck,
            });

            _ = session.ConnectAsync(new Uri("wss://example.test/ws?region=west"));

            Assert.That(transport.ConnectedUri, Is.Not.Null);
            Assert.That(transport.ConnectedUri!.AbsolutePath, Is.EqualTo("/ws"));
            Assert.That(transport.ConnectedUri.Query, Does.Contain("region=west"));
            Assert.That(transport.ConnectedUri.Query, Does.Contain("deck=1"));
            Assert.That(transport.Sent, Has.Count.EqualTo(2));

            DecodeResult hello = ProtocolCodec.Decode(transport.Sent[0]);
            DecodeResult submitted = ProtocolCodec.Decode(transport.Sent[1]);
            Assert.That(submitted.Message, Is.TypeOf<SubmitDeckMsg>());
            var decodedDeck = (SubmitDeckMsg)submitted.Message!;
            Assert.Multiple(() =>
            {
                Assert.That(hello.Ok, Is.True, hello.Detail);
                Assert.That(hello.Message, Is.TypeOf<HelloMsg>());
                Assert.That(hello.Seq, Is.Zero);
                Assert.That(submitted.Ok, Is.True, submitted.Detail);
                Assert.That(submitted.Seq, Is.EqualTo(1));
                Assert.That(decodedDeck.PrimaryClass, Is.EqualTo(deck.PrimaryClass));
                Assert.That(decodedDeck.SecondaryClass, Is.EqualTo(deck.SecondaryClass));
                Assert.That(decodedDeck.Cards, Has.Count.EqualTo(1));
                Assert.That(decodedDeck.Cards[0].CardId, Is.EqualTo(FixtureCard.Id("M1-GRAVEKEEP-01")));
                Assert.That(decodedDeck.Cards[0].Count, Is.EqualTo(2));
            });
        }

        [Test]
        public void No_Selected_Deck_Preserves_The_Prebuild_Handshake()
        {
            var transport = new RecordingTransport();
            using var session = new NetworkedMatchSession(transport, new ClientOptions
            {
                DeviceToken = "test-device",
            });
            var uri = new Uri("wss://example.test/ws?region=west");

            _ = session.ConnectAsync(uri);

            Assert.That(transport.ConnectedUri, Is.EqualTo(uri));
            Assert.That(transport.Sent, Has.Count.EqualTo(1));
            Assert.That(ProtocolCodec.Decode(transport.Sent[0]).Message, Is.TypeOf<HelloMsg>());
        }

        [Test]
        [CancelAfter(60_000)]
        [NonParallelizable]
        public async Task Selected_Decks_Are_Dealt_By_The_Real_Server()
        {
            string replays = Path.Combine(Path.GetTempPath(), "emberfall-client-deck-" + Guid.NewGuid().ToString("N"));
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = LoopbackServerPeer.CardDataDir(),
                ReplaysDir = replays,
            });
            await app.StartAsync();

            using var c0 = SessionWithDeck("device-0", "0", CardClass.GRAVEKEEP, CardClass.VERDANT, 7);
            using var c1 = SessionWithDeck("device-1", "1", CardClass.TIDECALLER, CardClass.EMBERWARD, 11);
            try
            {
                var baseUri = new Uri(app.Urls.First());
                var wsUri = new Uri($"ws://127.0.0.1:{baseUri.Port}/ws");

                await Task.WhenAll(c0.ConnectAsync(wsUri), c1.ConnectAsync(wsUri));

                Assert.Multiple(() =>
                {
                    Assert.That(LocalHeroClass(c0), Is.EqualTo(CardClass.GRAVEKEEP));
                    Assert.That(LocalHeroClass(c1), Is.EqualTo(CardClass.TIDECALLER));
                });
            }
            finally
            {
                await app.StopAsync();
                // Training capture copies a completed game's .jsonl on a background task that
                // StopAsync does not await, so the file can still be open here. A temp dir we
                // could not clean is not a test failure -- the same guard the bot-fill
                // fixtures already carry. Identified 2026-09-02: this is the
                // ClientDeckSubmissionTests flake, an IOException out of Directory.Delete.
                try { if (Directory.Exists(replays)) Directory.Delete(replays, recursive: true); }
                catch (IOException) { }
            }
        }

        /// <param name="deckId">Was a characterId until ADR-0009, and was the id the submission
        /// carried on the wire. <c>SubmitDeckMsg</c> has no id member at all now — its own doc had
        /// always said the one it carried "is not an identity the server trusts" — so this is only a
        /// local handle for the generated deck and the seed it is named after.</param>
        private static NetworkedMatchSession SessionWithDeck(
            string deviceToken, string deckId, CardClass primary, CardClass secondary, ulong seed)
        {
            DeckList deck = DefaultDeckGenerator.Generate(
                primary, secondary, LoopbackServerPeer.Db, new DeterministicRandom(seed), "deck-" + deckId);
            var lines = new List<DeckCardLine>(deck.Entries.Count);
            foreach (DeckEntry entry in deck.Entries)
                lines.Add(new DeckCardLine(entry.CardId, entry.Count));

            return new NetworkedMatchSession(new WebSocketTransport(), new ClientOptions
            {
                DeviceToken = deviceToken,
                DeckSubmission = new SubmitDeckMsg(primary, secondary, lines),
                InboundSilenceMs = 600_000,
            });
        }

        private static CardClass LocalHeroClass(NetworkedMatchSession session)
        {
            GameState state = session.Current;
            return state.Players[0].Id == session.Player
                ? state.Players[0].Hero.Class
                : state.Players[1].Hero.Class;
        }

        private sealed class RecordingTransport : IWireTransport
        {
            private readonly TaskCompletionSource<byte[]?> _inbound =
                new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Uri? ConnectedUri { get; private set; }
            public List<byte[]> Sent { get; } = new List<byte[]>();
            public event Action<WireCloseReason, string?>? OnClosed;

            public Task ConnectAsync(Uri uri, CancellationToken ct)
            {
                ConnectedUri = uri;
                return Task.CompletedTask;
            }

            public Task SendAsync(ReadOnlyMemory<byte> utf8, CancellationToken ct)
            {
                Sent.Add(utf8.ToArray());
                return Task.CompletedTask;
            }

            public Task<byte[]?> ReceiveAsync(CancellationToken ct) => _inbound.Task.WaitAsync(ct);

            public Task CloseAsync(WireCloseReason reason)
            {
                OnClosed?.Invoke(reason, null);
                return Task.CompletedTask;
            }
        }
    }
}
