using System;
using System.IO;
using System.Text;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Tools.OddsDisclosure
{
    /// <summary>
    /// EF-080f / NOR-260. Generates the pack-odds reference from the live constants in
    /// PackRarityOdds, PackPityState, BoosterPack, and DeckValidator. The output is committed to
    /// docs/dev-docs/data/pack-odds.md. Nothing generated goes in docs/pub-docs (Sam, 2026-10-10):
    /// the player-facing page, docs/pub-docs/progression/pack-odds.md, is hand-authored from this one,
    /// so a change to a constant here means regenerating AND editing that page.
    ///
    /// ShardValues is deliberately NOT read here any more. A pack converts nothing to shards
    /// (Sam, 2026-08-20), so a shard payout table on a page about what packs contain would be
    /// describing a transaction that cannot happen. Those numbers belong to Reduce, in the
    /// collection, and to whatever disclosure covers crafting.
    ///
    /// Usage: Emberfall.Tools.OddsDisclosure [outputPath]
    ///   outputPath  path to write; defaults to docs/dev-docs/data/pack-odds.md relative to cwd.
    ///
    /// Running without a path prints to stdout so the output can be reviewed or diffed before commit.
    /// </summary>
    internal static class Program
    {
        private const string DefaultOutputPath = "docs/dev-docs/data/pack-odds.md";
        private const string RegenerateCommand =
            "dotnet run --project src/Emberfall.Tools.OddsDisclosure -- docs/dev-docs/data/pack-odds.md";

        private static int Main(string[] args)
        {
            string? outputPath = args.Length > 0 ? args[0] : null;

            var odds = PackRarityOdds.Default;
            string markdown = BuildDisclosure(odds);

            if (outputPath == null)
            {
                Console.Write(markdown);
                return 0;
            }

            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Console.Error.WriteLine($"Output directory does not exist: {dir}");
                return 1;
            }

            File.WriteAllText(outputPath, markdown, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Console.WriteLine($"Written: {outputPath}");
            return 0;
        }

        private static string BuildDisclosure(PackRarityOdds odds)
        {
            double commonPct = odds.CommonPermille / 10.0;
            double rarePct = odds.RarePermille / 10.0;
            double epicPct = odds.EpicPermille / 10.0;
            double legendaryPct = odds.LegendaryPermille / 10.0;

            var sb = new StringBuilder();

            sb.AppendLine($"<!-- GENERATED — do not edit by hand. Regenerate with: {RegenerateCommand} -->");
            sb.AppendLine();
            sb.AppendLine("# Pack Odds — Pendingham");
            sb.AppendLine();
            sb.AppendLine($"Each pack contains **{BoosterPack.DefaultCardCount} cards** drawn from");
            sb.AppendLine("that pack's set.");
            sb.AppendLine("All packs of the same type use identical odds and the same card pool,");
            sb.AppendLine("whether purchased with premium or earned currency.");
            sb.AppendLine();

            sb.AppendLine("## Base odds per slot");
            sb.AppendLine();
            sb.AppendLine("These are the odds applied to each card slot individually, before guarantees.");
            sb.AppendLine();
            sb.AppendLine("| Rarity    | Chance per slot |");
            sb.AppendLine("|-----------|----------------|");
            sb.AppendLine($"| Common    | {commonPct:F1}%          |");
            sb.AppendLine($"| Rare      | {rarePct:F1}%          |");
            sb.AppendLine($"| Epic      | {epicPct:F1}%           |");
            sb.AppendLine($"| Legendary | {legendaryPct:F1}%           |");
            sb.AppendLine();

            sb.AppendLine("## Guarantees");
            sb.AppendLine();
            sb.AppendLine("After the base draw, each pack is adjusted to honour the following rules:");
            sb.AppendLine();
            sb.AppendLine($"- **Rare floor.** Every pack contains at least one Rare or better.");
            sb.AppendLine($"- **Epic pity.** If you open {PackPityState.EpicPityPacks - 1} consecutive packs from a set without an Epic,");
            sb.AppendLine($"  your next pack is guaranteed to contain one (guaranteed on pack {PackPityState.EpicPityPacks} of a dry streak).");
            sb.AppendLine($"- **First Legendary window.** Your first Legendary from a set is guaranteed");
            sb.AppendLine($"  by your {PackPityState.LegendaryFirstWindowPacks}th pack from that set.");
            sb.AppendLine($"- **Legendary pity.** After your first Legendary, one is guaranteed");
            sb.AppendLine($"  every {PackPityState.LegendaryPityPacks} consecutive packs from that set without one.");
            sb.AppendLine();
            sb.AppendLine("**Counters are per expansion set.** Opening Pendingham packs does not affect");
            sb.AppendLine("your pity counter for any other set.");
            sb.AppendLine();
            sb.AppendLine("When a pack owes both an Epic and a Legendary, it delivers both —");
            sb.AppendLine("they upgrade separate slots rather than collapsing into one.");
            sb.AppendLine();

            // There is no duplicate protection and no preference of any kind (Sam, 2026-08-20), so this
            // section states the absence rather than omitting it. An odds disclosure that simply goes
            // quiet about duplicates reads as though some protection exists and was left undescribed —
            // which is the exact impression the previous version of this section gave while promising a
            // dupe shield the code has never had at the strength a player would read into it.
            sb.AppendLine("## Duplicates");
            sb.AppendLine();
            sb.AppendLine("**Legendary slots never give you a Legendary you already own.** While a copy is");
            sb.AppendLine("in your collection it is taken out of your Legendary draws entirely. Reduce it");
            sb.AppendLine("and it becomes available again. Once you own every Legendary in the pairing,");
            sb.AppendLine("further Legendary slots can only repeat one — they still give you the card.");
            sb.AppendLine();
            sb.AppendLine("**Common, Rare and Epic slots lean toward cards you are missing, but nothing is");
            sb.AppendLine("off the table.** Within a slot's rarity, every card can be drawn; how likely it is");
            sb.AppendLine("depends on how many copies you already hold:");
            sb.AppendLine();
            sb.AppendLine("| Copies you hold                | Relative chance |");
            sb.AppendLine("|--------------------------------|-----------------|");
            sb.AppendLine($"| None                           | {BoosterPackOpener.DrawWeights.Unowned,15} |");
            sb.AppendLine($"| Some, below the deck limit     | {BoosterPackOpener.DrawWeights.PartiallyOwned,15} |");
            sb.AppendLine($"| At or above the deck limit     | {BoosterPackOpener.DrawWeights.AtOrOverCap,15} |");
            sb.AppendLine();
            sb.AppendLine("These are relative weights, not percentages. A card you already hold at the deck");
            sb.AppendLine("limit is still reachable — it is simply outweighed by cards you have never seen.");
            sb.AppendLine("Copies handed to you earlier in the same pack count toward this too.");
            sb.AppendLine();
            sb.AppendLine("**Nothing a pack gives you is destroyed or converted.** Copies beyond the");
            sb.AppendLine($"{DeckValidator.MaxCopies(Rarity.COMMON)} a deck may run "
                          + $"({DeckValidator.MaxCopies(Rarity.LEGENDARY)} for a Legendary) stay in");
            sb.AppendLine("your collection as cards. Your collection has no copy limit; only decks do.");
            sb.AppendLine();
            sb.AppendLine("Shards come from **Reduce**, in your collection, where you choose which copy to");
            sb.AppendLine("break down. A pack never makes that choice for you.");
            sb.AppendLine();

            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("*This page is generated from the shipped odds constants and is updated");
            sb.AppendLine("whenever those constants change. Last regenerated for EF-080f / NOR-260.*");

            return sb.ToString();
        }
    }
}
