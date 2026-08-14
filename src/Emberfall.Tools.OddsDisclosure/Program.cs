using System;
using System.IO;
using System.Text;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;

namespace Emberfall.Tools.OddsDisclosure
{
    /// <summary>
    /// EF-080f / NOR-260. Generates the published pack-odds disclosure from the live constants in
    /// PackRarityOdds, PackPityState, BoosterPack, and ShardValues. The output is a player-facing
    /// Markdown file committed to docs/pub-docs/progression/pack-odds.md.
    ///
    /// Usage: Emberfall.Tools.OddsDisclosure [outputPath]
    ///   outputPath  path to write; defaults to docs/pub-docs/progression/pack-odds.md relative to cwd.
    ///
    /// Running without a path prints to stdout so the output can be reviewed or diffed before commit.
    /// </summary>
    internal static class Program
    {
        private const string DefaultOutputPath = "docs/pub-docs/progression/pack-odds.md";
        private const string RegenerateCommand =
            "dotnet run --project src/Emberfall.Tools.OddsDisclosure -- docs/pub-docs/progression/pack-odds.md";

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
            sb.AppendLine("# Pack Odds — Emberfall");
            sb.AppendLine();
            sb.AppendLine($"Each pack contains **{BoosterPack.DefaultCardCount} cards** drawn from");
            sb.AppendLine("the Core set combined with the pack's expansion set.");
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
            sb.AppendLine("**Counters are per expansion set.** Opening Emberfall packs does not affect");
            sb.AppendLine("your pity counter for any other set.");
            sb.AppendLine();
            sb.AppendLine("When a pack owes both an Epic and a Legendary, it delivers both —");
            sb.AppendLine("they upgrade separate slots rather than collapsing into one.");
            sb.AppendLine();

            sb.AppendLine("## Duplicate protection");
            sb.AppendLine();
            sb.AppendLine("Within each rarity, a pack prefers cards you do not yet own at their copy limit.");
            sb.AppendLine("You will not receive a duplicate until you own the maximum allowed copies");
            sb.AppendLine("of every card at that rarity in the pool.");
            sb.AppendLine();
            sb.AppendLine("When all cards of a rarity are already owned at the copy limit, that slot");
            sb.AppendLine("pays out in Shards instead of a card. The card you would have received is");
            sb.AppendLine("still revealed before it converts. Your pity counter resets based on the");
            sb.AppendLine("rarity rolled, even when the slot becomes Shards.");
            sb.AppendLine();
            sb.AppendLine("| Rarity    | Shards per converted slot |");
            sb.AppendLine("|-----------|--------------------------|");
            sb.AppendLine($"| Common    | {ShardValues.Common,4}                     |");
            sb.AppendLine($"| Rare      | {ShardValues.Rare,4}                     |");
            sb.AppendLine($"| Epic      | {ShardValues.Epic,4}                     |");
            sb.AppendLine($"| Legendary | {ShardValues.Legendary,4}                     |");
            sb.AppendLine();

            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("*This page is generated from the shipped odds constants and is updated");
            sb.AppendLine("whenever those constants change. Last regenerated for EF-080f / NOR-260.*");

            return sb.ToString();
        }
    }
}
