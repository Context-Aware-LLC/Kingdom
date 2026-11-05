using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Kingdom.Core;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;

namespace Kingdom.Tools.NetSoak
{
    /// <summary>
    /// The one-deck-per-ordered-class-pair input shape shared by training capture and policy arena.
    /// The deck attached to a seat comes from that seat's ordered primary/secondary pairing instead
    /// of <see cref="DefaultDeckGenerator"/>.
    /// </summary>
    internal sealed class PolicyArenaDeckPool
    {
        private readonly IReadOnlyDictionary<(CardClass primary, CardClass secondary), IReadOnlyList<int>> _decks;

        private PolicyArenaDeckPool(
            string sourceDirectory,
            string sha256,
            IReadOnlyDictionary<(CardClass primary, CardClass secondary), IReadOnlyList<int>> decks)
        {
            SourceDirectory = sourceDirectory;
            Sha256 = sha256;
            _decks = decks;
        }

        public string SourceDirectory { get; }
        public string Sha256 { get; }

        public static PolicyArenaDeckPool Load(
            string directory,
            ICardDatabase db,
            IEnumerable<CardClass> classes)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Arena deck directory must not be empty.", nameof(directory));
            if (db is null) throw new ArgumentNullException(nameof(db));
            if (classes is null) throw new ArgumentNullException(nameof(classes));

            string fullPath = Path.GetFullPath(directory);
            CardClass[] requested = classes.Distinct().OrderBy(value => value).ToArray();
            if (requested.Length < 2 || requested.Any(value => value == CardClass.NEUTRAL))
                throw new ArgumentException(
                    "Arena deck pools require at least two distinct playable classes.", nameof(classes));

            var selected = new HashSet<CardClass>(requested);
            var decks = new Dictionary<(CardClass, CardClass), IReadOnlyList<int>>();
            var labels = new Dictionary<(CardClass, CardClass), string>();
            foreach (var loaded in BalanceCorpusLoader.Load(fullPath, db))
            {
                if (!selected.Contains(loaded.primary) || !selected.Contains(loaded.secondary)) continue;
                if (loaded.primary == loaded.secondary) continue;

                var key = (loaded.primary, loaded.secondary);
                if (decks.ContainsKey(key))
                    throw new InvalidDataException(
                        $"Arena deck pool has more than one deck for {key.primary}/{key.secondary}: "
                        + $"'{labels[key]}' and '{loaded.label}'.");
                decks.Add(key, loaded.cardIds);
                labels.Add(key, loaded.label);
            }

            var missing = new List<string>();
            foreach (CardClass primary in requested)
                foreach (CardClass secondary in requested)
                    if (primary != secondary && !decks.ContainsKey((primary, secondary)))
                        missing.Add($"{primary}/{secondary}");
            if (missing.Count > 0)
                throw new InvalidDataException(
                    $"Arena deck pool '{fullPath}' is missing {missing.Count} ordered pairing(s): "
                    + string.Join(", ", missing));

            return new PolicyArenaDeckPool(fullPath, Hash(decks), decks);
        }

        /// <summary>
        /// The server's bot roster as a pool: each seat plays the roster bot of its class, whatever
        /// the opposing class. This is the deck a live bot actually plays, so an arena on it
        /// measures the bot players meet rather than the training pool's decks.
        /// </summary>
        public static PolicyArenaDeckPool FromRoster(
            string rosterPath, ICardDatabase db, IEnumerable<CardClass> classes)
        {
            string fullPath = Path.GetFullPath(rosterPath);
            var byClass = new Dictionary<CardClass, IReadOnlyList<int>>();
            foreach (var bot in Kingdom.Net.Server.Bots.BotRoster.LoadFromFile(fullPath, db))
            {
                if (byClass.ContainsKey(bot.PrimaryClass))
                    throw new InvalidDataException(
                        $"Roster '{fullPath}' has more than one {bot.PrimaryClass} bot; an arena seat needs one deck per class.");
                byClass.Add(bot.PrimaryClass, bot.Cards);
            }
            CardClass[] requested = classes.Distinct().OrderBy(value => value).ToArray();
            var missing = requested.Where(value => !byClass.ContainsKey(value)).ToArray();
            if (missing.Length > 0)
                throw new InvalidDataException(
                    $"Roster '{fullPath}' has no bot for: {string.Join(", ", missing)}");
            var decks = new Dictionary<(CardClass, CardClass), IReadOnlyList<int>>();
            foreach (CardClass primary in requested)
                foreach (CardClass secondary in requested)
                    if (primary != secondary) decks.Add((primary, secondary), byClass[primary]);
            return new PolicyArenaDeckPool(fullPath, Hash(decks), decks);
        }

        /// <summary>The one deck for an ordered primary/secondary pairing.</summary>
        public IReadOnlyList<int> Deck(CardClass primary, CardClass secondary)
            => _decks.TryGetValue((primary, secondary), out IReadOnlyList<int>? deck)
                ? deck
                : throw new InvalidDataException($"Arena deck pool has no {primary}/{secondary} deck.");

        public MatchSetup Resolve(CardClass first, CardClass second)
        {
            if (!_decks.TryGetValue((first, second), out IReadOnlyList<int>? firstDeck)
                || !_decks.TryGetValue((second, first), out IReadOnlyList<int>? secondDeck))
                throw new InvalidDataException(
                    $"Arena deck pool has no reciprocal pair for {first}/{second}.");
            return new MatchSetup(first, firstDeck, second, secondDeck);
        }

        private static string Hash(
            IReadOnlyDictionary<(CardClass primary, CardClass secondary), IReadOnlyList<int>> decks)
        {
            var canonical = new StringBuilder();
            foreach (var pair in decks.OrderBy(pair => pair.Key.primary).ThenBy(pair => pair.Key.secondary))
            {
                canonical.Append(pair.Key.primary).Append('/').Append(pair.Key.secondary).Append(':')
                    .AppendJoin(',', pair.Value).Append('\n');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
                .ToLowerInvariant();
        }
    }
}
