using System.Globalization;

namespace Emberfall.Net.Client.Shop
{
    /// <summary>
    /// Player-facing wallet strings. These live beside the shop DTOs rather than in the Unity
    /// presentation layer so the wording is covered by cloud CI, which has no Unity seat.
    /// </summary>
    public static class WalletDisplay
    {
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
    }
}
