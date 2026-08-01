using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Tools.CardCi
{
    /// <summary>
    /// CI Stage 6 — content C-series gate (hist-docs/source-snapshots/2026-07-31-pre-reorganization/project/M2-KICKOFF.md WS C3). Validates every row of every
    /// <c>*.cards.json</c> under the given card-data directory through the shared
    /// <see cref="CardValidator"/> (C1–C20), checks id uniqueness across the whole library
    /// (C2 batch check), and verifies each <c>artId</c> resolves to an art file on disk.
    ///
    /// Exit code 1 on any Error-severity issue (parse failure, C-series error, duplicate id).
    /// Missing art is a WARNING, never blocking: per the placeholder-art policy (M2 decision 18),
    /// card data must not slip when art slips — the runtime E1 lint and CardPresenter fallback
    /// own the player-facing consequence.
    ///
    /// Usage: Emberfall.Tools.CardCi &lt;cardDataDir&gt; [artDir]
    ///   cardDataDir  directory scanned recursively for *.cards.json
    ///   artDir       optional; directory scanned recursively for art files. artId "art/&lt;name&gt;"
    ///                must resolve to "art_&lt;name&gt;.&lt;ext&gt;" (the dev-docs/specs/card-generation-prompt.md:18).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.Error.WriteLine("usage: Emberfall.Tools.CardCi <cardDataDir> [artDir]");
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
            var cardFileOf = new Dictionary<string, string>(); // defId -> source file (first seen)

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
            var validator = new CardValidator();

            // ── C1–C20 per card ──────────────────────────────────────────────────────
            foreach (var def in allCards)
            {
                var report = validator.Validate(def, db);
                foreach (var issue in report.Issues)
                {
                    string where = $"{cardFileOf[def.Id]}:{def.Id}";
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
                    string expected = def.ArtId.Replace('/', '_'); // "art/flame_bolt" -> "art_flame_bolt"
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

            Console.WriteLine();
            Console.WriteLine($"CardCi: {allCards.Count} cards across {files.Length} set file(s) — {errors} error(s), {warnings} warning(s).");
            return errors > 0 ? 1 : 0;
        }
    }

    /// <summary>Minimal ICardDatabase over the loaded library, for token-resolution checks (C12/C13).</summary>
    internal sealed class InMemoryCardDatabase : ICardDatabase
    {
        private readonly Dictionary<string, CardDefinition> _cards = new(StringComparer.Ordinal);

        public InMemoryCardDatabase(IEnumerable<CardDefinition> defs)
        {
            foreach (var d in defs) _cards[d.Id] = d; // last wins; C2 reports the duplicate separately
        }

        public bool TryGet(string cardId, out CardDefinition def) => _cards.TryGetValue(cardId, out def!);

        public CardDefinition Get(string cardId) => _cards[cardId];

        public IReadOnlyCollection<CardDefinition> AllCollectible()
            => _cards.Values.Where(d => d.Collectible).ToArray();
    }
}
