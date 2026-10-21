using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Tools.CardCi
{
    /// <summary>
    /// CI Stage 6 — content C-series gate (hist-docs/source-snapshots/2026-07-31-pre-reorganization/project/M2-KICKOFF.md WS C3). Validates every row of every
    /// <c>*.cards.json</c> under the given card-data directory through the shared
    /// <see cref="CardValidator"/> (C1–C21), checks id uniqueness across the whole library
    /// (C2 batch check), and verifies each <c>artId</c> resolves to an art file on disk.
    ///
    /// Exit code 1 on any Error-severity issue (parse failure, C-series error, duplicate id).
    /// Missing art is a WARNING, never blocking: per the placeholder-art policy (M2 decision 18),
    /// card data must not slip when art slips — the runtime E1 lint and CardPresenter fallback
    /// own the player-facing consequence.
    ///
    /// Usage: Kingdom.Tools.CardCi &lt;cardDataDir&gt; [artDir]
    ///   cardDataDir  directory scanned recursively for *.cards.json
    ///   artDir       optional; directory scanned recursively for art files. artId "&lt;name&gt;"
    ///                must resolve to "&lt;name&gt;.&lt;ext&gt;". The old "art/" namespace and its
    ///                flattened "art_" filename prefix were dropped 2026-08-06.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string? exportPath = Option(ref args, "--export-card-text");
            string? translationsDir = Option(ref args, "--translations");
            if (args.Length < 1)
            {
                Console.Error.WriteLine("usage: Kingdom.Tools.CardCi <cardDataDir> [artDir] " +
                    "[--translations <localizationDir>] [--export-card-text <out.json>]");
                return 2;
            }

            string cardDir = args[0];
            string? artDir = args.Length > 1 ? args[1] : null;

            if (!Directory.Exists(cardDir))
            {
                Console.Error.WriteLine($"card-data directory not found: {cardDir}");
                return 2;
            }

            var files = Directory.GetFiles(cardDir, "*.cards.json", SearchOption.AllDirectories)
                                 .OrderBy(f => f, StringComparer.Ordinal)
                                 .ToArray();
            if (files.Length == 0)
            {
                Console.Error.WriteLine($"no *.cards.json files under {cardDir} — the gate has nothing to gate.");
                return 2;
            }

            int errors = 0, warnings = 0;
            var allCards = new List<CardDefinition>();
            var cardFileOf = new Dictionary<int, string>();    // defId -> source file (first seen)

            // ── Parse every row of every set file (per-row error context) ────────────
            foreach (var file in files)
            {
                string rel = Path.GetFileName(file);
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    Console.Error.WriteLine($"ERROR {rel}: root is not a JSON array of card rows.");
                    errors++;
                    continue;
                }

                int i = 0;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    try
                    {
                        var def = CardJson.FromElement(el);
                        allCards.Add(def);
                        if (!cardFileOf.ContainsKey(def.Id)) cardFileOf[def.Id] = rel;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"ERROR {rel}[{i}]: row failed to parse — {ex.Message}");
                        errors++;
                    }
                    i++;
                }
            }

            var db = new InMemoryCardDatabase(allCards);

            if (exportPath != null)
            {
                File.WriteAllText(exportPath, CardTranslations.WriteSource(allCards, db));
                Console.WriteLine($"CardCi: wrote the English card text of {allCards.Count} cards to {exportPath}");
                return 0;
            }

            var validator = new CardValidator();

            // Collected during the C-series pass and printed as rows at the end. See the
            // "text divergences" block below for why this is a separate readout and not just
            // the C26 error lines it duplicates.
            var divergences = new List<(CardTextDivergence Kind, CardDefinition Def, string Expected)>();

            // ── C1–C21 per card ──────────────────────────────────────────────────────
            foreach (var def in allCards)
            {
                var kind = CardTextGenerator.Check(def, db, out string expected);
                if (kind != CardTextDivergence.None) divergences.Add((kind, def, expected));

                var report = validator.Validate(def, db);
                foreach (var issue in report.Issues)
                {
                    // Handle as well as number: a CI line a human reads names the card.
                    string where = $"{cardFileOf[def.Id]}:{def.Id} {def.Handle}";
                    if (issue.Severity == Severity.Error)
                    {
                        Console.Error.WriteLine($"ERROR {where}: {issue.Code} {issue.Field} — {issue.Message}");
                        errors++;
                    }
                    else
                    {
                        Console.WriteLine($"warn  {where}: {issue.Code} {issue.Field} — {issue.Message}");
                        warnings++;
                    }
                }
            }

            // ── C2 — id uniqueness across the whole library ──────────────────────────
            foreach (var issue in CardValidator.CheckDuplicateIds(allCards)
                         .DistinctBy(i => i.CardId))
            {
                Console.Error.WriteLine($"ERROR {issue.CardId}: {issue.Code} — {issue.Message}");
                errors++;
            }

            // ── Art catalog check (WARNING only — placeholder-art policy) ────────────
            if (artDir != null && Directory.Exists(artDir))
            {
                var artFiles = new HashSet<string>(
                    Directory.GetFiles(artDir, "*.*", SearchOption.AllDirectories)
                             .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                             .Select(f => Path.GetFileNameWithoutExtension(f)!),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var def in allCards)
                {
                    if (string.IsNullOrEmpty(def.ArtId)) continue; // C-series already flags shape issues
                    string expected = def.ArtId.Replace('/', '_'); // artId is the filename; no namespace to flatten
                    if (!artFiles.Contains(expected))
                    {
                        Console.WriteLine($"warn  {cardFileOf[def.Id]}:{def.Id}: ART-MISSING — '{def.ArtId}' has no {expected}.<png|jpg> under {artDir} (placeholder policy applies; not blocking).");
                        warnings++;
                    }
                }
            }
            else if (artDir != null)
            {
                Console.WriteLine($"warn  art directory not found: {artDir} — art check skipped.");
                warnings++;
            }

            // ── Text divergences, as rows (Sam, 2026-08-19) ──────────────────────────
            // Every line here is ALSO a C26 error above, so this block gates nothing and changes
            // no exit code. It exists because the two outputs answer different questions. The error
            // stream answers "is the build clean" — one line per issue, C26 interleaved with C1–C21,
            // ordered by card. This answers "which cards do I have to make a wording decision about,
            // and what are the two candidate wordings" — which is the actual triage when a fresh
            // corpus lands, and is unreadable when the pairs are scattered through a few hundred
            // lines of unrelated findings.
            //
            // The decision each row is asking for is per card and is Sam's: adapt the TEXT to what
            // the grammar can already say, or adapt the GRAMMAR to say what the card wants. Usually
            // some of both, which is why both wordings are printed in full and neither is truncated
            // — the difference between them is frequently a single word, and truncating is how you
            // hide exactly the thing being decided.
            if (divergences.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"TEXT DIVERGENCES — {divergences.Count} card(s) need a wording decision");
                Console.WriteLine();

                foreach (var (kind, def, expected) in divergences
                             .OrderBy(d => d.Kind)
                             .ThenBy(d => cardFileOf[d.Def.Id], StringComparer.Ordinal)
                             .ThenBy(d => d.Def.Id))
                {
                    Console.WriteLine($"{kind.ToString().ToUpperInvariant(),-13} {cardFileOf[def.Id]}:{def.Id}");
                    Console.WriteLine($"  authored   {Show(def.Text)}");
                    // Unrenderable carries no expected string — Check() returns "" because Generate
                    // threw. Printing that through Show() would read as "the rules say nothing",
                    // which is the opposite of what happened and the wrong triage.
                    Console.WriteLine(kind == CardTextDivergence.Unrenderable
                        ? "  rules say  (no phrasing exists for this rule shape — always a grammar gap)"
                        : $"  rules say  {Show(expected)}");
                }

                Console.WriteLine();
                foreach (var group in divergences.GroupBy(d => d.Kind).OrderBy(g => g.Key))
                    Console.WriteLine($"  {group.Key.ToString().ToUpperInvariant(),-13} {group.Count()}");
            }

            if (translationsDir != null)
                CheckTranslations(translationsDir, allCards, db, ref errors, ref warnings);

            Console.WriteLine();
            Console.WriteLine($"CardCi: {allCards.Count} cards across {files.Length} set file(s) — {errors} error(s), {warnings} warning(s).");
            return errors > 0 ? 1 : 0;
        }

        /// <summary>
        /// Every <c>values-&lt;lang&gt;/cards.json</c> under <paramref name="dir"/>: a translation for a
        /// card that does not exist, or one whose slots, name or flavor do not match its English, is an
        /// ERROR. A stale one (the English moved since) is a WARNING — the client shows that card in
        /// English, which is correct, just not finished. Each language's coverage is printed.
        /// </summary>
        private static void CheckTranslations(string dir, List<CardDefinition> cards, ICardDatabase db,
            ref int errors, ref int warnings)
        {
            if (!Directory.Exists(dir))
            {
                Console.Error.WriteLine($"ERROR translations directory not found: {dir}");
                errors++;
                return;
            }
            var byId = cards.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (string file in Directory.GetFiles(dir, "cards.json", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                string lang = Path.GetFileName(Path.GetDirectoryName(file)!);
                CardTranslations translations;
                try { translations = CardTranslations.Parse(File.ReadAllText(file)); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"ERROR {lang}/cards.json: failed to parse — {ex.Message}");
                    errors++;
                    continue;
                }

                int current = 0, stale = 0;
                foreach (int named in CardTranslations.NamedInPlural(cards.Select(c => CardTextGenerator.Template(c, db))))
                {
                    if (translations.Rows.TryGetValue(named, out CardTranslation? t) && t.Plural.Trim().Length == 0)
                    {
                        Console.Error.WriteLine($"ERROR {lang}/cards.json:{named}: no plural, and another card names it in the plural.");
                        errors++;
                    }
                }
                foreach (KeyValuePair<int, CardTranslation> row in translations.Rows.OrderBy(r => r.Key))
                {
                    if (!byId.TryGetValue(row.Key, out CardDefinition? card))
                    {
                        Console.Error.WriteLine($"ERROR {lang}/cards.json:{row.Key}: no such card.");
                        errors++;
                        continue;
                    }
                    string template = CardTextGenerator.Template(card, db);
                    IReadOnlyList<string> problems = CardTranslations.Problems(card, template, row.Value);
                    foreach (string problem in problems)
                    {
                        Console.Error.WriteLine($"ERROR {lang}/cards.json:{card.Id} {card.Handle}: {problem}.");
                        errors++;
                    }
                    if (CardTranslations.IsStale(card, template, row.Value))
                    {
                        Console.WriteLine($"warn  {lang}/cards.json:{card.Id} {card.Handle}: STALE — the English changed; shows in English until retranslated.");
                        warnings++;
                        stale++;
                    }
                    else if (problems.Count == 0) current++;
                }
                Console.WriteLine($"translations {lang}: {current} current, {stale} stale, {cards.Count - translations.Rows.Count} untranslated of {cards.Count} cards.");
            }
        }

        /// <summary>Removes <c>--name value</c> from <paramref name="args"/> and returns the value.</summary>
        private static string? Option(ref string[] args, string name)
        {
            int at = Array.IndexOf(args, name);
            if (at < 0 || at + 1 >= args.Length) return null;
            string value = args[at + 1];
            args = args.Take(at).Concat(args.Skip(at + 2)).ToArray();
            return value;
        }

        /// <summary>Quotes an authored sentence for a divergence row, distinguishing empty from absent.</summary>
        private static string Show(string text)
            => string.IsNullOrEmpty(text) ? "(none)" : $"\"{text}\"";
    }

    /// <summary>Minimal ICardDatabase over the loaded library, for token-resolution checks (C12).</summary>
    internal sealed class InMemoryCardDatabase : ICardDatabase
    {
        private readonly Dictionary<int, CardDefinition> _cards = new();

        public InMemoryCardDatabase(IEnumerable<CardDefinition> defs)
        {
            foreach (var d in defs) _cards[d.Id] = d; // last wins; C2 reports the duplicate separately
        }

        public bool TryGet(int cardId, out CardDefinition def) => _cards.TryGetValue(cardId, out def!);

        public CardDefinition Get(int cardId) => _cards[cardId];

        public IReadOnlyCollection<CardDefinition> AllCollectible()
            => _cards.Values.Where(d => d.PoolEligible).ToArray();
    }
}
