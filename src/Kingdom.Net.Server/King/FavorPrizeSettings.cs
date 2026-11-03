using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Kingdom.Net.Server.King
{
    /// <summary>
    /// The King's Favor gold, a server setting because the right amounts depend on how many people
    /// play (Sam). From <c>KINGDOM_FAVOR_PRIZES</c>, for example
    /// <c>{"gold":[500,300,200],"poolEmptyGold":200,"qualifyGold":50}</c>.
    /// </summary>
    /// <param name="Gold">A closed reign's gold by place, 1st first. Its count is the number of paid places.</param>
    /// <param name="PoolEmptyGold">Paid instead of a card back to a player who already owns the whole pool.</param>
    /// <param name="QualifyGold">Paid for the first qualifying score in a reign.</param>
    public sealed record FavorPrizeSettings(IReadOnlyList<long> Gold, long PoolEmptyGold, long QualifyGold)
    {
        public const string EnvVar = "KINGDOM_FAVOR_PRIZES";

        /// <summary>The settings from <c>KINGDOM_FAVOR_PRIZES</c>, or null: no Favor gold is paid.</summary>
        public static FavorPrizeSettings? FromEnvironment() => Parse(KingdomEnv.Get(EnvVar));

        /// <summary>
        /// Null when unset, and ALSO null when the value is not exactly the three keys holding
        /// non-negative whole numbers, logged either way. A typo must pay nothing rather than a
        /// guess: a misspelled key read as 0 would look like a deliberate setting.
        /// </summary>
        public static FavorPrizeSettings? Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                Console.Error.WriteLine("[king] {0} is unset; the King's Favor pays no gold.", EnvVar);
                return null;
            }

            try
            {
                using JsonDocument doc = JsonDocument.Parse(raw);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return Invalid(raw);

                List<long>? gold = null;
                long? poolEmpty = null, qualify = null;
                foreach (JsonProperty p in root.EnumerateObject())
                {
                    switch (p.Name)
                    {
                        case "gold" when gold is null && p.Value.ValueKind == JsonValueKind.Array:
                            gold = new List<long>();
                            foreach (JsonElement e in p.Value.EnumerateArray())
                            {
                                if (Amount(e) is not long amount) return Invalid(raw);
                                gold.Add(amount);
                            }
                            break;
                        case "poolEmptyGold" when poolEmpty is null:
                            poolEmpty = Amount(p.Value);
                            if (poolEmpty is null) return Invalid(raw);
                            break;
                        case "qualifyGold" when qualify is null:
                            qualify = Amount(p.Value);
                            if (qualify is null) return Invalid(raw);
                            break;
                        default:
                            return Invalid(raw);
                    }
                }
                if (gold is null || poolEmpty is null || qualify is null) return Invalid(raw);
                return new FavorPrizeSettings(gold.AsReadOnly(), poolEmpty.Value, qualify.Value);
            }
            catch (JsonException)
            {
                return Invalid(raw);
            }
        }

        private static long? Amount(JsonElement e)
            => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long v) && v >= 0 ? v : (long?)null;

        private static FavorPrizeSettings? Invalid(string raw)
        {
            Console.Error.WriteLine("[king] {0}='{1}' is not valid; the King's Favor pays no gold.", EnvVar, raw);
            return null;
        }
    }
}
