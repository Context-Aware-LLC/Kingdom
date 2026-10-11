using System;
using System.Collections.Generic;
using System.IO;
using Kingdom.Core;
using Kingdom.Core.Contracts;
using Kingdom.Net.Protocol;
using Kingdom.Net.Server.Data;
using Kingdom.Net.Server.Replay;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Net.Server.Tests
{
    /// <summary>Replay store, deck provisioning, card DB (design §5-bis/§9/§10).</summary>
    [TestFixture]
    [Category("Unit")]
    public class StoreAndDataTests
    {
        [Test]
        public void JsonlReplayStore_Rows_Decode_Back_To_The_Same_Intents()
        {
            string dir = Path.Combine(Path.GetTempPath(), "emberfall-replay-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new JsonlReplayStore(dir);
                var id = new MatchId("m-jsonl");
                var p0 = new PlayerId("p0");
                var p1 = new PlayerId("p1");

                store.Begin(id, 0xDEADUL, p0, p1);
                store.Append(id, new EndTurnIntent(p0));
                store.Append(id, new ConcedeIntent(p1));
                store.Complete(id, MatchOutcome.Player0Win);

                string[] lines = File.ReadAllLines(store.PathFor(id));
                Assert.That(lines, Has.Length.EqualTo(4), "header + 2 intents + footer");
                Assert.That(lines[0], Does.Contain("\"seed\":57005"), "seed lives ONLY in the server-side log");
                Assert.That(lines[3], Does.Contain("Player0Win"));

                // Intent rows are full codec envelopes — decode with the same hardened parser.
                var r1 = ProtocolCodec.Decode(System.Text.Encoding.UTF8.GetBytes(lines[1]));
                Assert.That(r1.Ok, Is.True, $"{r1.Error} {r1.Detail}");
                Assert.That(((IntentMsg)r1.Message!).Intent, Is.EqualTo(new EndTurnIntent(p0)));
                var r2 = ProtocolCodec.Decode(System.Text.Encoding.UTF8.GetBytes(lines[2]));
                Assert.That(((IntentMsg)r2.Message!).Intent, Is.EqualTo(new ConcedeIntent(p1)));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void PrebuiltDecks_Are_Thirty_Cards_Each_And_Fully_Resolvable()
        {
            // The RC F1 drift guard: the ported decklists must resolve against the live card data
            // the server actually deals from.
            //
            // The size is asserted against DeckValidator.DeckSize, not a literal 30. These lists are
            // dealt to a real seat, so a fallback deck that is not the format's size is a seat playing
            // a different game — and the two numbers drifting apart is precisely how it would happen
            // without anything failing.
            var db = SessionHarness.Db;
            foreach (var deck in new[] { PrebuiltDecks.EmberwardDeck(), PrebuiltDecks.TidecallerDeck() })
            {
                Assert.That(deck.Count, Is.EqualTo(Kingdom.Core.Collection.DeckValidator.DeckSize),
                    "a fallback deck is dealt to a real seat and must be the format's size");
                var copies = new Dictionary<int, int>();
                foreach (int cardId in deck)
                {
                    Assert.That(db.TryGet(cardId, out _), Is.True, $"deck card '{cardId}' missing from card data");
                    copies[cardId] = copies.TryGetValue(cardId, out int n) ? n + 1 : 1;
                }
                foreach (var kv in copies)
                    Assert.That(kv.Value, Is.LessThanOrEqualTo(2), $"'{kv.Key}' exceeds the 2-copy cap");
            }
        }

        [Test]
        public void FileCardDatabase_Loads_The_Canonical_Card_Data()
        {
            var db = FileCardDatabase.LoadDirectory(SessionHarness.CardDataDir());
            Assert.That(db.TryGet(ShippedCards.Id("neutral_stone_sentry"), out var def), Is.True);
            Assert.That(def.Handle, Is.EqualTo("neutral_stone_sentry"));
            Assert.That(db.AllCollectible(), Is.Not.Empty);
        }

        [Test]
        public void FileCardDatabase_Refuses_An_Empty_Directory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "emberfall-empty-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Assert.Throws<FileNotFoundException>(() => FileCardDatabase.LoadDirectory(dir),
                    "a server with no cards must fail loudly at startup, not deal nothing");
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void TokenBucket_Integer_Refill_And_Cap()
        {
            var opts = new ServerOptions();   // cap 10, refill 2/s
            var bucket = new TokenBucket(opts, nowMs: 0);

            for (int i = 0; i < opts.RateCapacity; i++)
                Assert.That(bucket.TryTake(1, 0), Is.True, $"take {i + 1} within capacity");
            Assert.That(bucket.TryTake(1, 0), Is.False, "empty");

            Assert.That(bucket.TryTake(1, 400), Is.False, "400ms * 2/s = 0.8 tokens — not enough");
            Assert.That(bucket.TryTake(1, 500), Is.True, "500ms * 2/s = 1 token exactly (integer milli-math)");

            // Long idle clamps at capacity, never beyond.
            Assert.That(bucket.TryTake(1, 3_600_000), Is.True);
            for (int i = 0; i < opts.RateCapacity - 1; i++)
                Assert.That(bucket.TryTake(1, 3_600_000), Is.True, "refilled to cap");
            Assert.That(bucket.TryTake(1, 3_600_000), Is.False, "cap is a hard ceiling");
        }
    }
}
