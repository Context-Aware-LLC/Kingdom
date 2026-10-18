using Kingdom.Net.Client.Social;
using Kingdom.Net.Protocol;
using NUnit.Framework;

namespace Kingdom.Net.Client.Tests.Social
{
    [TestFixture]
    [Category("Unit")]
    public sealed class FriendLinkTests
    {
        private static readonly string Code = FriendCode.FromValue(123_456_789_012);

        [Test]
        public void The_shared_link_carries_the_code_as_a_person_reads_it()
        {
            Assert.That(FriendLink.For(Code),
                Is.EqualTo("https://kingdomofbadideas.com/f/" + FriendCode.Display(Code)));
        }

        [Test]
        public void Both_forms_read_back_to_the_canonical_code()
        {
            string display = FriendCode.Display(Code);
            foreach (string url in new[]
            {
                FriendLink.For(Code),
                "https://www.kingdomofbadideas.com/f/" + display.ToLowerInvariant() + "/",
                "emberfall://friend/" + Code,
                "EMBERFALL://FRIEND/" + display,
            })
            {
                Assert.That(FriendLink.TryRead(url, out string code), Is.True, url);
                Assert.That(code, Is.EqualTo(Code), url);
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not a url")]
        [TestCase("emberfall://auth?code=abc123")]
        [TestCase("emberfall://friend/")]
        [TestCase("http://kingdomofbadideas.com/f/")]
        [TestCase("https://example.com/f/")]
        [TestCase("https://kingdomofbadideas.com/cards")]
        public void Anything_else_is_not_a_friend_link(string? url)
        {
            Assert.That(FriendLink.TryRead(url, out string code), Is.False);
            Assert.That(code, Is.Empty);
        }

        [Test]
        public void A_link_whose_code_fails_its_check_is_refused()
        {
            // Swap the last two symbols: the check symbol catches every adjacent swap.
            string swapped = Code.Substring(0, 8) + Code[9] + Code[8];
            Assume.That(swapped, Is.Not.EqualTo(Code));

            Assert.That(FriendLink.TryRead("https://kingdomofbadideas.com/f/" + swapped, out _), Is.False);
            Assert.That(FriendLink.TryRead("http://kingdomofbadideas.com/f/" + Code, out _), Is.False,
                "plain http is not the link the Share button sends");
        }
    }
}
