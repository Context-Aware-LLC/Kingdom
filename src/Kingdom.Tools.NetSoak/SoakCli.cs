using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using Kingdom.Core;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;

namespace Kingdom.Tools.NetSoak
{
    /// <summary>
    /// The <see cref="Program"/> helpers both command-line halves use: argument parsing, the
    /// throughput line, deck resolution, card-data lookup and build provenance. NetSoak plays the
    /// Greedy and scripted soaks; Kingdom.Tools.TrainingSoak, which references this assembly and is
    /// granted its internals in <c>AssemblyInfo.cs</c>, plays the checkpoint, search and league
    /// commands. Moved here unchanged from <c>Program</c> so the two halves cannot drift apart.
    /// </summary>
    internal static class SoakCli
    {
        /// <summary>
        /// Ceiling on intents per match. Not a turn cap: it exists so a rules bug that leaves both
        /// seats with a legal move and no progress is REPORTED as a capped match rather than hanging
        /// the run overnight. Both decks are 30 cards into a 30 HP hero, so a healthy match finishes
        /// far inside this; anything that trips it is a finding.
        /// </summary>
        internal const int IntentCap = 4000;

        internal static string? ParseOptionalStringFlag(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], name, StringComparison.Ordinal)) continue;
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{name} requires a value.");
                return args[i + 1];
            }
            return null;
        }

        internal static ulong ParseUlongFlag(string[] args, string name, ulong fallback)
        {
            string? raw = ParseOptionalStringFlag(args, name);
            if (raw is null) return fallback;
            if (!ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value))
                throw new ArgumentException($"{name} must be an unsigned integer.");
            return value;
        }

        /// <summary>
        /// One line of wall-clock throughput with the collector's share of it. Console only: no
        /// file a run writes may depend on how fast it ran. The pause and allocation figures are
        /// here because this harness allocates heavily, and a worker count that buys no speed
        /// shows up as pause time long before it shows up anywhere else.
        /// </summary>
        internal static string ThroughputLine(string tag, int games, int workers, TimeSpan elapsed)
        {
            double seconds = Math.Max(elapsed.TotalSeconds, 1e-9);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"[{tag}] throughput: {games} game(s) in {seconds:0.0}s = {games / seconds:0.0}/s "
                + $"workers={workers} serverGC={System.Runtime.GCSettings.IsServerGC} "
                + $"gcPause={GC.GetTotalPauseDuration().TotalSeconds:0.0}s "
                + $"gen0={GC.CollectionCount(0)} gen2={GC.CollectionCount(2)} "
                + $"allocated={GC.GetTotalAllocatedBytes() / 1e9:0.0}GB");
        }

        /// <summary>Most games in flight a run may ask for. A checkpoint seat starts one scorer
        /// process per worker, so a typo here is a machine full of Python.</summary>
        internal const int MaxWorkers = 64;

        internal static int ParseWorkers(string[] args)
            => ValidateWorkers(ParseOptionalStringFlag(args, "--workers"));

        internal static int ValidateWorkers(string? raw)
        {
            if (raw is null) return 1;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int workers)
                || workers < 1 || workers > MaxWorkers)
                throw new ArgumentException(
                    $"--workers must be an integer from 1 to {MaxWorkers} (got '{raw}').");
            return workers;
        }

        // ── class-vs-class deck resolution (EF-030) ────────────────────────────────

        /// <summary>The mirror of <see cref="PrebuiltDecks.EmberwardVsTidecaller"/>, seats swapped —
        /// and deliberately carrying no HP argument of its own, so it cannot drift from that pair.</summary>
        internal static MatchSetup TidecallerFirst() => new MatchSetup(
            CardClass.TIDECALLER, PrebuiltDecks.TidecallerDeck(),
            CardClass.EMBERWARD, PrebuiltDecks.EmberwardDeck());

        /// <summary>
        /// The two per-seat setups the soak alternates between. With no <c>--p0</c>/<c>--p1</c> flags
        /// this is the original fixed EMBERWARD/TIDECALLER pairing, byte-for-byte unchanged, so the
        /// sanctioned <c>ai_soak</c> sample is untouched. With both flags it is a generated matchup:
        /// each seat gets a real 30-card deck for the requested class.
        ///
        /// <para><b>Why each seat is a dual-class character (A/B and B/A).</b> A deck in Kingdom is
        /// always drawn from PRIMARY ∪ SECONDARY ∪ NEUTRAL — there is no mono-class pool, and a thin
        /// class (EMBERWARD has 17 collectible copies) cannot reach 30 legal cards from its own pool
        /// plus NEUTRAL alone. Pairing p0 as A/B and p1 as B/A gives both seats the SAME card pool, so
        /// the only asymmetry left is the hero power (which always comes from PrimaryClass) and the
        /// seat — exactly the two things this soak already isolates. It is the fairest A-vs-B read the
        /// dual-class model admits; a deck-file pair (a later EF-030 form) can override it.</para>
        /// </summary>
        internal static (MatchSetup even, MatchSetup odd) ResolveSoakSetups(
            CardClass? p0Class, CardClass? p1Class, ICardDatabase db,
            int maxSecondaryCopies = MaxSecondaryClassCards, ulong reroll = 0, bool includeNeutral = true)
        {
            if (p0Class is null && p1Class is null)
                return (PrebuiltDecks.EmberwardVsTidecaller(), TidecallerFirst());
            if (p0Class is null || p1Class is null)
                throw new ArgumentException("Pass BOTH --p0 <CLASS> and --p1 <CLASS>, or neither.");

            CardClass a = p0Class.Value, b = p1Class.Value;
            IReadOnlyList<int> deckA = GenerateClassDeck(a, SecondaryFor(a, b), db, reroll, maxSecondaryCopies, includeNeutral);
            IReadOnlyList<int> deckB = GenerateClassDeck(b, SecondaryFor(b, a), db, reroll, maxSecondaryCopies, includeNeutral);

            // 30-card decks into the game-standard 30 HP (MatchSetup's own default), not the 20/20
            // dial the two hand-built M1 decks use — a generated field is the real game shape.
            var even = new MatchSetup(a, deckA, b, deckB, heroMaxHealth: 30);
            var odd = new MatchSetup(b, deckB, a, deckA, heroMaxHealth: 30);   // seats swapped
            return (even, odd);
        }

        /// <summary>Ceiling on secondary-class copies in a matrix deck — see <see cref="GenerateClassDeck"/>.</summary>
        internal const int MaxSecondaryClassCards = 5;

        /// <summary>
        /// A legal 30-card deck for <paramref name="primary"/>, generated deterministically and
        /// validated to L1–L5 inside <see cref="DefaultDeckGenerator.Generate"/>.
        ///
        /// <para><b>Drawn from Primary ∪ NEUTRAL, plus up to <see cref="MaxSecondaryClassCards"/> copies
        /// of <paramref name="secondary"/> (Sam, 2026-09-05, same day as the ruling this replaces).</b>
        /// <c>SecondaryFor</c> sets <paramref name="secondary"/> to the OPPONENT'S class, so with it
        /// fully unconstrained a "class" cell was actually measuring an unweighted mix of two classes'
        /// cards — the VERDANT-vs-GRAVEKEEP soak deck labelled VERDANT held 7 VERDANT cards against 12
        /// GRAVEKEEP (NOR-186, 2026-08-03). Excluding it entirely was the first fix that same day; a
        /// small cap replaced it once a same-day re-read of the STONEWARDEN number wanted to rule out
        /// the single fixed-seed deck being a specifically bad draw rather than the class's own pool
        /// being weak — a full exclusion was overcorrecting for what NOR-186 actually found.</para>
        ///
        /// <para><b>Seeded from BOTH classes, not just the primary.</b> <c>SecondaryFor</c> already
        /// varies per matrix cell (it is usually "the opponent"), so folding it into the seed means a
        /// class's deck is freshly rerolled every cell it appears in — once per <c>matchesPerCell</c>
        /// games — instead of one fixed build for the whole run. <see cref="Program.RunTraceAsync"/> calls
        /// <see cref="ResolveSoakSetups"/> with the same (p0, p1) pair a cell used, so it still
        /// reproduces that exact cell's decks; only the run-wide "one deck per class" property is
        /// gone, and that property was never a documented contract.</para>
        /// </summary>
        /// <param name="reroll">Folded into the seed so a caller can ask for a different build of the
        /// same pairing. Zero is the matrix's one-deck-per-cell build.</param>
        internal static IReadOnlyList<int> GenerateClassDeck(CardClass primary, CardClass secondary, ICardDatabase db,
            ulong reroll = 0, int maxSecondaryCopies = MaxSecondaryClassCards, bool includeNeutral = true)
        {
            if (!DefaultDeckGenerator.CanGenerate(primary, secondary, db, maxSecondaryCopies: maxSecondaryCopies,
                                                  includeNeutral: includeNeutral))
                throw new DeckGenerationException(
                    $"{primary}/{secondary} cannot supply {DeckValidator.DeckSize} legal cards from the loaded pool"
                    + (!includeNeutral ? " from the primary class alone." : maxSecondaryCopies == 0 ? " with no secondary-class copies." : "."));

            ulong seed = 0xDECC0DEUL + (ulong)primary * 97UL + (ulong)secondary + reroll * 1_000_003UL;
            DeckList deck = DefaultDeckGenerator.Generate(
                primary, secondary, db, new DeterministicRandom(seed),
                $"soak-deck-{primary}-{secondary}", maxSecondaryCopies: maxSecondaryCopies,
                includeNeutral: includeNeutral);

            var ids = new List<int>(DeckValidator.DeckSize);
            foreach (DeckEntry e in deck.Entries)
                for (int c = 0; c < e.Count; c++) ids.Add(e.CardId);
            return ids;
        }

        /// <summary>
        /// The second, pool-filling class for a seat whose class identity is <paramref name="primary"/>:
        /// the opponent's class when they differ (making both seats' pools identical), else the first
        /// other playable class so a mirror (A vs A) still reaches 30 cards.
        /// </summary>
        internal static CardClass SecondaryFor(CardClass primary, CardClass opponent)
        {
            if (opponent != primary && opponent != CardClass.NEUTRAL) return opponent;
            foreach (CardClass c in Enum.GetValues<CardClass>())
                if (c != CardClass.NEUTRAL && c != primary) return c;
            throw new ArgumentException($"No second class available to fill a {primary} deck.");
        }

        /// <summary>Read an integer that follows <paramref name="flag"/> in the argv tail (e.g. --pace 500).</summary>
        internal static int ParseIntFlag(string[] args, string flag, int dflt)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == flag && int.TryParse(args[i + 1], out int v)) return Math.Max(0, v);
            return dflt;
        }

        internal static int Fail(string msg) { Console.Error.WriteLine($"SOAK: {msg}"); return 2; }

        /// <summary>
        /// Stable synthetic device tokens for the two soak seats (C-G1-8). Deliberately fixed rather
        /// than per-run random: `ai_soak` is a sanctioned sample source (telemetry-spec §1), so its
        /// matches should roll up under two consistent synthetic accounts instead of inventing a new
        /// pair every run. Obviously non-human, so they can be filtered out of ladder-only views.
        /// </summary>
        internal static string SoakDevice(int seat) => $"ai-soak-seat-{seat}";

        internal static int ParseCount(string[] args, int idx, int dflt)
            => args.Length > idx && int.TryParse(args[idx], out int n) && n > 0 ? n : dflt;

        internal static string FindCardData()
        {
            string? env = Kingdom.Net.Server.KingdomEnv.Get("KINGDOM_CARDS");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
            string? path = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && path is not null; i++, path = Path.GetDirectoryName(path))
            {
                string candidate = Path.Combine(path, "unity", "Assets", "CardData");
                if (Directory.Exists(candidate)) return candidate;
            }
            throw new DirectoryNotFoundException("Set KINGDOM_CARDS or run from the repo tree (unity/Assets/CardData).");
        }

        internal static string ResolveBuildRevision(string cardData)
        {
            string? configured = Kingdom.Net.Server.KingdomEnv.Get("KINGDOM_BUILD_REVISION");
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

            try
            {
                string repository = Path.GetFullPath(Path.Combine(cardData, "..", "..", "..", ".."));
                var start = new ProcessStartInfo("git")
                {
                    WorkingDirectory = repository,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                start.ArgumentList.Add("rev-parse");
                start.ArgumentList.Add("HEAD");
                using Process? process = Process.Start(start);
                if (process is not null)
                {
                    string revision = process.StandardOutput.ReadToEnd().Trim();
                    if (process.WaitForExit(5_000) && process.ExitCode == 0 && revision.Length > 0)
                        return revision;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException
                                      || ex is System.ComponentModel.Win32Exception)
            {
                // Packaged tools may have no Git executable or checkout. Assembly provenance below
                // remains deterministic and KINGDOM_BUILD_REVISION can pin a deployment explicitly.
            }

            return typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "unknown-build";
        }
    }
}
