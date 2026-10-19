using System;
using System.Collections.Generic;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Collection
{
    /// <summary>
    /// The name a new deck starts with: one per pairing, the same either way round, so Gravekeep and
    /// Verdant make <i>Compost</i> whichever is primary (whimsy pass, Sam 2026-10-17). A second deck
    /// of a pairing takes a regnal number, <i>Compost II</i>.
    ///
    /// <para>Deck names never reach the opponent — the server names every submitted deck
    /// "Submitted" — so a name that gives away the pairing does not give away the hidden class.</para>
    /// </summary>
    public static class DeckNames
    {
        private static readonly Dictionary<(CardClass, CardClass), string> ByPairing =
            new Dictionary<(CardClass, CardClass), string>
            {
                [(CardClass.EMBERWARD, CardClass.TIDECALLER)] = "Hot Water",
                [(CardClass.EMBERWARD, CardClass.GRAVEKEEP)] = "Pyre Insurance",
                [(CardClass.EMBERWARD, CardClass.VERDANT)] = "Controlled Burn",
                [(CardClass.EMBERWARD, CardClass.CLOCKWRIGHT)] = "Overheating",
                [(CardClass.EMBERWARD, CardClass.NIGHTVEIL)] = "Arson",
                [(CardClass.EMBERWARD, CardClass.STONEWARDEN)] = "Brick Oven",
                [(CardClass.EMBERWARD, CardClass.TEMPEST)] = "Wildfire Season",
                [(CardClass.TIDECALLER, CardClass.GRAVEKEEP)] = "Burial at Sea",
                [(CardClass.TIDECALLER, CardClass.VERDANT)] = "Overwatering",
                [(CardClass.TIDECALLER, CardClass.CLOCKWRIGHT)] = "Rust",
                [(CardClass.TIDECALLER, CardClass.NIGHTVEIL)] = "Low Visibility",
                [(CardClass.TIDECALLER, CardClass.STONEWARDEN)] = "Erosion",
                [(CardClass.TIDECALLER, CardClass.TEMPEST)] = "Small Craft Advisory",
                [(CardClass.GRAVEKEEP, CardClass.VERDANT)] = "Compost",
                [(CardClass.GRAVEKEEP, CardClass.CLOCKWRIGHT)] = "Spare Parts",
                [(CardClass.GRAVEKEEP, CardClass.NIGHTVEIL)] = "Graveyard Shift",
                [(CardClass.GRAVEKEEP, CardClass.STONEWARDEN)] = "Permanent Residents",
                [(CardClass.GRAVEKEEP, CardClass.TEMPEST)] = "Rain at the Funeral",
                [(CardClass.VERDANT, CardClass.CLOCKWRIGHT)] = "Lawnmower",
                [(CardClass.VERDANT, CardClass.NIGHTVEIL)] = "Hedge Maze",
                [(CardClass.VERDANT, CardClass.STONEWARDEN)] = "Rock Garden",
                [(CardClass.VERDANT, CardClass.TEMPEST)] = "Spring Showers",
                [(CardClass.CLOCKWRIGHT, CardClass.NIGHTVEIL)] = "Wiretap",
                [(CardClass.CLOCKWRIGHT, CardClass.STONEWARDEN)] = "Building Permit",
                [(CardClass.CLOCKWRIGHT, CardClass.TEMPEST)] = "Electric Bill",
                [(CardClass.NIGHTVEIL, CardClass.STONEWARDEN)] = "Secret Passage",
                [(CardClass.NIGHTVEIL, CardClass.TEMPEST)] = "Thunder, Quietly",
                [(CardClass.STONEWARDEN, CardClass.TEMPEST)] = "Lightning Rod",
            };

        /// <summary>The pairing's name, or "New Deck" for a pairing that has none.</summary>
        public static string ForPairing(CardClass a, CardClass b)
        {
            // Keyed in enum order, so the table needs each pair once.
            var key = a <= b ? (a, b) : (b, a);
            return ByPairing.TryGetValue(key, out string name) ? name : "New Deck";
        }

        /// <summary>
        /// The pairing's name if no deck holds it yet, otherwise the lowest regnal number that is
        /// free: <i>Compost</i>, <i>Compost II</i>, <i>Compost III</i>. Case-insensitive, since two
        /// names differing only in case are indistinguishable on a plate.
        /// </summary>
        public static string Unused(CardClass a, CardClass b, IEnumerable<string> taken)
        {
            string root = ForPairing(a, b);
            var used = new HashSet<string>(taken ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (!used.Contains(root)) return root;
            for (int n = 2; ; n++)
            {
                string candidate = root + " " + Roman(n);
                if (!used.Contains(candidate)) return candidate;
            }
        }

        private static readonly (int Value, string Numeral)[] Numerals =
        {
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
            (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
        };

        internal static string Roman(int n)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (value, numeral) in Numerals)
                for (; n >= value; n -= value) sb.Append(numeral);
            return sb.ToString();
        }
    }
}
