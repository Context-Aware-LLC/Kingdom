using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Emberfall.Core.Contracts;

namespace Emberfall.Net.Server.Quests
{
    /// <summary>
    /// One authored quest (dev-docs/specs/daily-quests.md §4). <see cref="Credit"/> is what one
    /// finished match contributes toward <see cref="Goal"/> — 0 for a match that does not advance
    /// it, 1 for the outcome quests, and a count for the card quests.
    ///
    /// <para><b>A definition owns no reward amount.</b> <c>IGoldRewardPolicy</c> does, for the same
    /// reason <see cref="GoldFaucet"/> refuses to take an amount from a caller: quantities have one
    /// owner and it is not the thing describing the work.</para>
    /// </summary>
    public sealed class QuestDefinition
    {
        public string Id { get; }

        /// <summary>The line the player reads. Rendered as authored — the server does the counting,
        /// so the text and the predicate cannot drift apart at runtime.</summary>
        public string Title { get; }

        public int Goal { get; }

        private readonly Func<QuestMatchFacts, int> _credit;

        public QuestDefinition(string id, string title, int goal, Func<QuestMatchFacts, int> credit)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A quest id is required.", nameof(id));
            if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("A quest title is required.", nameof(title));
            if (goal <= 0) throw new ArgumentOutOfRangeException(nameof(goal), "A quest goal must be positive.");
            Id = id;
            Title = title;
            Goal = goal;
            _credit = credit ?? throw new ArgumentNullException(nameof(credit));
        }

        public int Credit(QuestMatchFacts facts) => Math.Max(0, _credit(facts));
    }

    /// <summary>
    /// The authored quests and the rule that decides which of them a player has on a given day
    /// (dev-docs/specs/daily-quests.md §4–5).
    ///
    /// <para><b>Assignment is derived, not minted.</b> A player's quests for a UTC day are a pure
    /// function of <c>(playerId, day)</c>, so nothing runs at midnight and a player who does not log
    /// in for a week costs nothing and is owed nothing. A scheduled grant would be a moving part
    /// whose failure — for exactly the accounts that were offline — is indistinguishable from a
    /// player simply having no quests.</para>
    ///
    /// <para><b>A class-bound quest binds its class from the same hash, not from the player's
    /// decks.</b> Deck-aware binding was the first draft and it is wrong: decks change during a day,
    /// so the assignment would change under a player who had already made progress against it, and
    /// progress is keyed on the quest id. The cost is that a player can draw a quest for a class
    /// they have not built — acceptable while the catalog is small and the day rolls over anyway,
    /// and the real fix is a reroll, which needs the stored assignment this design deliberately
    /// avoids.</para>
    /// </summary>
    public static class QuestCatalog
    {
        /// <summary>How many quests a player holds at once (spec §5: one active, up to three
        /// stored). Days are derived independently, so this is simply how many days back the active
        /// set reaches.</summary>
        public const int ActiveDays = 3;

        /// <summary>
        /// The classes a class-bound quest may name. NEUTRAL is excluded — no hero is neutral, so
        /// "win as NEUTRAL" is unwinnable, and "play 8 different NEUTRAL cards" is a different and
        /// much easier quest than the class ones it would sit beside.
        /// </summary>
        private static readonly CardClass[] BindableClasses =
        {
            CardClass.EMBERWARD, CardClass.TIDECALLER, CardClass.GRAVEKEEP, CardClass.VERDANT,
            CardClass.CLOCKWRIGHT, CardClass.NIGHTVEIL, CardClass.STONEWARDEN, CardClass.TEMPEST,
        };

        /// <summary>
        /// The templates, in a fixed order. The order is part of the derivation — reordering this
        /// array reassigns every player's quests — so append rather than insert.
        /// </summary>
        private static readonly Func<CardClass, QuestDefinition>[] Templates =
        {
            _ => new QuestDefinition("win-two", "Win 2 matches", 2, f => f.Won ? 1 : 0),

            _ => new QuestDefinition("play-three", "Play 3 matches", 3, _ => 1),

            c => new QuestDefinition(
                "win-as-" + Slug(c), "Win a match as " + Name(c), 1,
                f => f.Won && f.Class == c ? 1 : 0),

            c => new QuestDefinition(
                "play-as-" + Slug(c), "Play 3 matches as " + Name(c), 3,
                f => f.Class == c ? 1 : 0),

            // Counted per match and summed across matches, so a card played on two different days'
            // matches counts twice. "Different" is true of any one match, which is the only scope
            // CardSeenRow can speak about — it is one row per CARD, not per copy.
            _ => new QuestDefinition(
                "cards-fifteen", "Play 15 cards", 15, f => f.DistinctCardsPlayed),

            c => new QuestDefinition(
                "cards-of-" + Slug(c), "Play 8 " + Name(c) + " cards", 8,
                f => f.DistinctCardsPlayedOf(c)),

            _ => new QuestDefinition(
                "quick-win", "Win a match by turn 10", 1,
                f => f.Won && f.TurnCount <= 10 ? 1 : 0),

            _ => new QuestDefinition(
                "long-game", "Win a match that runs past turn 15", 1,
                f => f.Won && f.TurnCount > 15 ? 1 : 0),
        };

        /// <summary>The one quest this player holds for this UTC day.</summary>
        public static QuestDefinition ForDay(string playerId, DateOnly day)
        {
            if (string.IsNullOrWhiteSpace(playerId))
                throw new ArgumentException("A player id is required.", nameof(playerId));

            uint seed = Seed(playerId, day);
            Func<CardClass, QuestDefinition> template = Templates[seed % (uint)Templates.Length];
            CardClass @class = BindableClasses[(seed / (uint)Templates.Length) % (uint)BindableClasses.Length];
            return template(@class);
        }

        /// <summary>
        /// Every quest this player currently holds, newest day first — <see cref="ActiveDays"/> days
        /// ending at <paramref name="today"/>.
        ///
        /// <para>Two days can derive the same quest id. They are still separate quests: progress is
        /// keyed on <c>(player, quest, day)</c> and so is the faucet's transaction id, so the same
        /// quest drawn twice pays twice, on its own day, as it should.</para>
        /// </summary>
        public static IReadOnlyList<(DateOnly Day, QuestDefinition Quest)> Active(
            string playerId, DateOnly today)
        {
            var rows = new List<(DateOnly, QuestDefinition)>(ActiveDays);
            for (int back = 0; back < ActiveDays; back++)
            {
                DateOnly day = today.AddDays(-back);
                rows.Add((day, ForDay(playerId, day)));
            }
            return rows;
        }

        /// <summary>
        /// SHA-256 over the pair, so assignment is stable across processes and deployments.
        /// <c>string.GetHashCode</c> would not be: it is randomized per process, which would reroll
        /// every player's quest on every restart.
        /// </summary>
        private static uint Seed(string playerId, DateOnly day)
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(
                Encoding.UTF8.GetBytes(playerId + "\n" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                digest);
            return BitConverter.ToUInt32(digest.Slice(0, 4)) & 0x7FFFFFFF;
        }

        private static string Slug(CardClass c) => c.ToString().ToLowerInvariant();

        /// <summary>Title case for a class name in quest text — EMBERWARD reads as shouting.</summary>
        private static string Name(CardClass c)
        {
            string raw = c.ToString();
            return raw.Substring(0, 1) + raw.Substring(1).ToLowerInvariant();
        }
    }
}
