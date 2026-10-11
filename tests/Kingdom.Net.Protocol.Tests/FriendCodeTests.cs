using NUnit.Framework;
using Kingdom.Net.Protocol;

namespace Kingdom.Net.Protocol.Tests
{
    /// <summary>
    /// The friend code (#409). What these pin is the check symbol's promise: the two mistakes a
    /// person makes relaying a code — one wrong symbol, two neighbours swapped — are ALWAYS caught,
    /// not usually. That is a property of the generator polynomial, so a "simpler" checksum that
    /// passes a spot check would fail here.
    /// </summary>
    [TestFixture]
    [Category("Contract")]
    public sealed class FriendCodeTests
    {
        private static readonly long[] Samples =
        {
            0, 1, 31, 32, 0x1F_FFFF_FFFF, 0x0A5A_5A5A_5A5A, 123_456_789_012, FriendCode.Space - 1,
        };

        [Test]
        public void A_Minted_Code_Is_Ten_Alphabet_Symbols_And_Normalizes_To_Itself()
        {
            foreach (long value in Samples)
            {
                string code = FriendCode.FromValue(value);

                Assert.That(code.Length, Is.EqualTo(FriendCode.Length));
                foreach (char c in code) Assert.That(FriendCode.Alphabet.IndexOf(c), Is.Not.Negative);
                Assert.That(FriendCode.TryNormalize(code, out string normalized), Is.True);
                Assert.That(normalized, Is.EqualTo(code));
            }
        }

        [Test]
        public void Distinct_Values_Mint_Distinct_Codes()
            => Assert.That(FriendCode.FromValue(1), Is.Not.EqualTo(FriendCode.FromValue(2)));

        [Test]
        public void What_A_Person_Types_Is_Forgiven_Case_Hyphens_And_Spaces()
        {
            string code = FriendCode.FromValue(123_456_789_012);
            string typed = " " + FriendCode.Display(code).ToLowerInvariant() + " ";

            Assert.That(FriendCode.Display(code), Does.Match("^.{5}-.{5}$"));
            Assert.That(FriendCode.TryNormalize(typed, out string normalized), Is.True);
            Assert.That(normalized, Is.EqualTo(code));
        }

        [Test]
        public void Every_Single_Symbol_Substitution_Is_Rejected()
        {
            foreach (long value in Samples)
            {
                string code = FriendCode.FromValue(value);
                for (int i = 0; i < code.Length; i++)
                    foreach (char replacement in FriendCode.Alphabet)
                    {
                        if (replacement == code[i]) continue;
                        string typo = code.Substring(0, i) + replacement + code.Substring(i + 1);
                        Assert.That(FriendCode.TryNormalize(typo, out _), Is.False,
                            $"{code} -> {typo} must fail the check symbol");
                    }
            }
        }

        [Test]
        public void Every_Swap_Of_Two_Different_Neighbours_Is_Rejected()
        {
            foreach (long value in Samples)
            {
                string code = FriendCode.FromValue(value);
                for (int i = 0; i + 1 < code.Length; i++)
                {
                    if (code[i] == code[i + 1]) continue;
                    string typo = code.Substring(0, i) + code[i + 1] + code[i] + code.Substring(i + 2);
                    Assert.That(FriendCode.TryNormalize(typo, out _), Is.False,
                        $"{code} -> {typo} must fail the check symbol");
                }
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("22222-2222")]        // one short
        [TestCase("22222-222222")]      // one long
        [TestCase("O2222-22222")]       // O is not in the alphabet
        [TestCase("12222-22222")]       // nor is 1
        public void Malformed_Input_Is_Rejected(string? raw)
        {
            Assert.That(FriendCode.TryNormalize(raw, out string code), Is.False);
            Assert.That(code, Is.Empty);
        }
    }
}
