using System.IO;
using System.Linq;
using NUnit.Framework;
using Kingdom.Core.Collection;
using Kingdom.Core.Tests.Support;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>
    /// <see cref="BotRosterFile"/> against the shipped roster. The point is the refusal: the server
    /// will not boot on an illegal bot deck, so the dev deck builder must never write one.
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class BotRosterFileTests
    {
        private static string RosterJson =>
            File.ReadAllText(Path.Combine(RepoPaths.RepoRoot, "tooling", "data", "bot-roster.json"))
                .Replace("\r\n", "\n");

        // Not byte-identical: the file sometimes splits a card's two copies, and a write groups them.
        [Test]
        public void Writing_what_was_read_keeps_every_card_and_is_stable()
        {
            string once = BotRosterFile.Write(BotRosterFile.Read(RosterJson));
            Assert.That(BotRosterFile.Write(BotRosterFile.Read(once)), Is.EqualTo(once));

            using var before = System.Text.Json.JsonDocument.Parse(RosterJson);
            using var after = System.Text.Json.JsonDocument.Parse(once);
            var rowsBefore = before.RootElement.EnumerateArray().ToList();
            var rowsAfter = after.RootElement.EnumerateArray().ToList();
            Assert.That(rowsAfter.Count, Is.EqualTo(rowsBefore.Count));
            for (int i = 0; i < rowsBefore.Count; i++)
            {
                foreach (string field in new[] { "accountUsername", "displayName", "primaryClass", "secondaryClass" })
                    Assert.That(rowsAfter[i].GetProperty(field).GetString(),
                                Is.EqualTo(rowsBefore[i].GetProperty(field).GetString()));
                Assert.That(rowsAfter[i].GetProperty("cards").EnumerateArray().Select(c => c.GetInt32()).OrderBy(c => c),
                            Is.EqualTo(rowsBefore[i].GetProperty("cards").EnumerateArray().Select(c => c.GetInt32()).OrderBy(c => c)));
            }
        }

        [Test]
        public void An_illegal_deck_is_refused_and_the_roster_left_alone()
        {
            DeckList deck = BotRosterFile.Read(RosterJson)[0].Deck;
            deck.Entries.RemoveAt(0);   // under 30 cards

            bool written = BotRosterFile.TryUpdate(RosterJson, deck, ShippedCards.Db,
                                                   out string json, out string refusal);

            Assert.That(written, Is.False);
            Assert.That(refusal, Is.Not.Empty);
            Assert.That(json, Is.EqualTo(RosterJson));
        }

        [Test]
        public void A_legal_edit_changes_only_that_bots_cards()
        {
            var bots = BotRosterFile.Read(RosterJson);
            DeckList deck = bots[0].Deck;
            deck.Entries.Reverse();

            bool written = BotRosterFile.TryUpdate(RosterJson, deck, ShippedCards.Db,
                                                   out string json, out string refusal);

            Assert.That(written, Is.True, refusal);
            var after = BotRosterFile.Read(json);
            Assert.That(after[0].Deck.Entries.Select(e => e.CardId),
                        Is.EqualTo(deck.Entries.Select(e => e.CardId)));
            Assert.That(after.Skip(1).Select(b => BotRosterFile.Write(new[] { b })),
                        Is.EqualTo(bots.Skip(1).Select(b => BotRosterFile.Write(new[] { b }))));
        }
    }
}
