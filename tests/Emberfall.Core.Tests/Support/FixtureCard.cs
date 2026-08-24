using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Tests.Support
{
    /// <summary>
    /// A stable numeric card id for a HAND-AUTHORED FIXTURE, minted from the mnemonic the fixture
    /// reads by — <c>FixtureCard.Id("thorns_minion")</c> where the engine needs an <c>int</c>.
    ///
    /// <para><b>This is not a handle lookup, and the distinction is the whole reason it is allowed
    /// to exist.</b> <see cref="Emberfall.Core.Cards.CardDefinition.Handle"/> is a real field on a
    /// real row and the ruling is that nothing may resolve against it (Sam, 2026-08-15: the test is
    /// that you can change a handle and have nothing break). What this maps is a string that appears
    /// nowhere but inside one test file, naming a card that exists nowhere but inside that test. No
    /// authored row, no set file, no save file and no wire message is reachable from here — passing
    /// a real card's handle to this returns a number that is NOT that card's id, which is the
    /// property that keeps the two systems from ever being confused for one another.</para>
    ///
    /// <para><b>Ids land above 1_000_000, deliberately far above every shipped row</b> (banded
    /// 1-1300 as of 2026-08-24, MINION from 1001 up). A fixture id therefore never collides with a
    /// real one, so a test that accidentally reaches live card data fails loudly on a missing id
    /// rather than quietly resolving to whichever card happened to hash there. That puts every
    /// fixture id in MINION range, which is fine because the C-series band rule is validation, not
    /// runtime — a fixture that IS about validation takes the banded
    /// <see cref="Id(string, CardType)"/> overload instead.</para>
    /// </summary>
    public static class FixtureCard
    {
        // A 400_000-wide band collided in practice: ~300 fixture mnemonics over 400k slots is an
        // 11% birthday collision, and 'sneak' and 'core_rare_012' found it on 2026-08-24. Two
        // billion slots put it at roughly 1 in 20_000 instead, and the check below still catches it.
        private const int Base = 1_000_000;
        private const int Span = 2_000_000_000;

        private static readonly Dictionary<int, string> Minted = new Dictionary<int, string>();
        private static readonly object Gate = new object();

        /// <summary>The id for <paramref name="mnemonic"/>. Same string, same number, always.</summary>
        public static int Id(string mnemonic) => Mint(mnemonic, Base, Span);

        /// <summary>
        /// The id for a fixture whose subject is CARD VALIDATION — rule C1B checks that an id sits
        /// inside its type's band, so a row headed for <c>CardValidator</c> cannot use the wide band
        /// above. Bands: HERO_POWER 1-50, WEAPON 51-150, SPELL 151-1000, MINION 1001 and up.
        ///
        /// <para>These windows are small — 850 ids for a spell — so this collides far sooner than
        /// <see cref="Id(string)"/> does. The collision check throws rather than letting two fixtures
        /// quietly share a row. Reach for it only when the validator is the thing under test.</para>
        /// </summary>
        public static int Id(string mnemonic, CardType type)
        {
            switch (type)
            {
                case CardType.HERO_POWER: return Mint(mnemonic, 1, 50);
                case CardType.WEAPON: return Mint(mnemonic, 51, 100);
                case CardType.SPELL: return Mint(mnemonic, 151, 850);
                default: return Mint(mnemonic, 1001, 900_000);
            }
        }

        private static int Mint(string mnemonic, int lo, int span)
        {
            // FNV-1a. Chosen because it is spelled out here rather than taken from the framework:
            // string.GetHashCode is randomized per process, which would make a fixture id differ
            // between two runs of the same test and quietly break every determinism assertion that
            // hashes state.
            unchecked
            {
                uint h = 2166136261u;
                foreach (char c in mnemonic)
                {
                    h ^= c;
                    h *= 16777619u;
                }
                int id = lo + (int)(h % (uint)span);
                lock (Gate)
                {
                    if (Minted.TryGetValue(id, out string? other) && other != mnemonic)
                        throw new System.InvalidOperationException(
                            $"Fixture card ids collided: '{mnemonic}' and '{other}' both mint {id}. " +
                            "Rename one of them — the fixtures would otherwise share a definition.");
                    Minted[id] = mnemonic;
                }
                return id;
            }
        }

        /// <summary>
        /// The mnemonic <paramref name="id"/> was minted from — the inverse of <see cref="Id"/>,
        /// for a fixture helper that is handed the id and still wants to write a readable handle,
        /// name or art path onto the row it builds. Only ids this process minted are known.
        /// </summary>
        public static string Handle(int id)
        {
            lock (Gate)
            {
                if (Minted.TryGetValue(id, out string? mnemonic)) return mnemonic;
            }
            throw new System.InvalidOperationException(
                $"{id} was not minted by FixtureCard.Id, so it has no fixture mnemonic.");
        }

        /// <summary>The ids for a list of mnemonics, in order — a fixture deck.</summary>
        public static List<int> Ids(params string[] mnemonics)
        {
            var list = new List<int>(mnemonics.Length);
            foreach (string m in mnemonics) list.Add(Id(m));
            return list;
        }
    }
}
