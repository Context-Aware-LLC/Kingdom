using System.Globalization;

namespace Kingdom.Net.Client.Shop
{
    /// <summary>
    /// Player-facing wallet strings. These live beside the shop DTOs rather than in the Unity
    /// presentation layer so the wording is covered by cloud CI, which has no Unity seat.
    /// </summary>
    public static class WalletDisplay
    {
        /// <summary>
        /// No wallet snapshot is not an authoritative zero. Keep that state visible as unavailable
        /// until the gateway returns a ledger-projected balance.
        /// </summary>
        public const string ShardsUnavailable = "—";

        /// <summary>
        /// The gem balance label. A debt from a refund reads as "-200 Gems" so the minus sign and
        /// the currency are both in front of the player instead of a bare signed number.
        /// </summary>
        public static string Gems(long gems)
        {
            if (gems < 0)
            {
                // decimal, not Math.Abs: negating long.MinValue would throw in a display path.
                decimal owed = -(decimal)gems;
                return "-" + owed.ToString("N0", CultureInfo.InvariantCulture) + " Gems";
            }

            return "Gems  " + gems.ToString("N0", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// An authoritative missing ledger row is zero, not blank. The server projection supplies
        /// that zero; this formatter never guesses when the wallet itself is unavailable.
        /// The shard icon supplies the currency identity beside this amount.
        /// </summary>
        public static string Shards(long shards)
            => IconAmount(shards);

        /// <summary>
        /// Amount-only text for a wallet readout whose adjacent icon supplies the currency name.
        /// This also preserves <see cref="long.MinValue"/> for gem debt without taking an absolute
        /// value that would overflow.
        /// </summary>
        public static string IconAmount(long amount)
            => amount.ToString("N0", CultureInfo.InvariantCulture);
    }
}
