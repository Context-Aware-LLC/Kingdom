using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Kingdom.Core.Contracts;

namespace Kingdom.Tools.NetSoak
{
    public sealed record TrainingPairing(
        CardClass FirstClass,
        CardClass SecondClass,
        int Matches);

    public sealed record TrainingCaptureGame(
        int GlobalIndex,
        int PairingIndex,
        int MatchIndex,
        CardClass FirstClass,
        CardClass SecondClass,
        CardClass P0Class,
        CardClass P1Class,
        string MatchId,
        ulong Seed);

    /// <summary>
    /// Canonical schedule for a multi-matchup training capture. The interface hides pair expansion,
    /// stable identity, seeding, and per-pair seat alternation from the match driver.
    /// </summary>
    public sealed class TrainingCapturePlan
    {
        private const ulong FirstSeed = 0xE17BEEF5EEDUL;

        private TrainingCapturePlan(
            IReadOnlyList<TrainingPairing> pairings,
            IReadOnlyList<TrainingCaptureGame> games)
        {
            Pairings = pairings;
            Games = games;
        }

        public IReadOnlyList<TrainingPairing> Pairings { get; }
        public IReadOnlyList<TrainingCaptureGame> Games { get; }

        public static TrainingCapturePlan Create(
            int matchesPerPairing,
            IEnumerable<CardClass> classes,
            ulong gameSeedOffset = 0)
        {
            if (matchesPerPairing <= 0 || (matchesPerPairing & 1) != 0)
                throw new ArgumentOutOfRangeException(
                    nameof(matchesPerPairing),
                    "Matches per pairing must be a positive even number so seats are balanced.");
            if (classes is null) throw new ArgumentNullException(nameof(classes));

            CardClass[] requested = classes.ToArray();
            if (requested.Any(c => c == CardClass.NEUTRAL))
                throw new ArgumentException("NEUTRAL is not a playable capture class.", nameof(classes));
            if (requested.Distinct().Count() != requested.Length)
                throw new ArgumentException("Capture classes must not contain duplicates.", nameof(classes));
            if (requested.Length < 2)
                throw new ArgumentException("At least two playable classes are required.", nameof(classes));
            Array.Sort(requested);

            var pairings = new List<TrainingPairing>();
            var games = new List<TrainingCaptureGame>();
            for (int first = 0; first < requested.Length - 1; first++)
            {
                for (int second = first + 1; second < requested.Length; second++)
                {
                    CardClass a = requested[first], b = requested[second];
                    int pairingIndex = pairings.Count;
                    pairings.Add(new TrainingPairing(a, b, matchesPerPairing));
                    string prefix = "training-" + Slug(a) + "-vs-" + Slug(b) + "-";
                    for (int match = 0; match < matchesPerPairing; match++)
                    {
                        bool swap = (match & 1) == 1;
                        int global = games.Count;
                        games.Add(new TrainingCaptureGame(
                            global,
                            pairingIndex,
                            match,
                            a,
                            b,
                            swap ? b : a,
                            swap ? a : b,
                            prefix + match.ToString("D6", CultureInfo.InvariantCulture),
                            checked(SeedFor(a, b, match) + gameSeedOffset)));
                    }
                }
            }
            return new TrainingCapturePlan(pairings, games);
        }

        /// <summary>
        /// The games of this schedule that <paramref name="matchIds"/> names, in schedule order and
        /// otherwise unchanged: same seed, seats and global index, so each is the game the whole
        /// schedule would have played in that slot. How a capture's lost slots are played again.
        /// </summary>
        public IReadOnlyList<TrainingCaptureGame> Only(IEnumerable<string> matchIds)
        {
            if (matchIds is null) throw new ArgumentNullException(nameof(matchIds));
            var wanted = new HashSet<string>(matchIds, StringComparer.Ordinal);
            if (wanted.Count == 0) throw new ArgumentException("No games named.", nameof(matchIds));
            string[] unknown = wanted.Except(Games.Select(game => game.MatchId), StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();
            if (unknown.Length > 0)
                throw new ArgumentException(
                    $"{unknown.Length} game(s) not in this schedule, first '{unknown[0]}'.", nameof(matchIds));
            return Games.Where(game => wanted.Contains(game.MatchId)).ToArray();
        }

        public static IReadOnlyList<CardClass> AllPlayableClasses()
            => Enum.GetValues<CardClass>().Where(c => c != CardClass.NEUTRAL).ToArray();

        private static string Slug(CardClass value)
            => value.ToString().ToLowerInvariant();

        private static ulong SeedFor(CardClass first, CardClass second, int matchIndex)
            => FirstSeed
               + ((ulong)(int)first << 40)
               + ((ulong)(int)second << 32)
               + (uint)matchIndex;
    }
}
