using Kingdom.Net.Server.Accounts;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.Accounts
{
    /// <summary>
    /// The entitlement ledger's composition-root seam. Until this landed, <c>IEntitlementLedger</c>
    /// appeared nowhere in <c>Kingdom.Net.Server</c> outside <c>Accounts/</c> and neither adapter
    /// was ever constructed — every economy service was written, tested and unreachable.
    ///
    /// <para>These pin the two states that matter, both of which are silent when wrong: absent means
    /// ownership stays UNKNOWN (which renders identically to owning everything), and an injected
    /// ledger must actually survive into the config a test hands to the server.</para>
    /// </summary>
    [TestFixture]
    public sealed class ServerConfigLedgerTests
    {
        [Test]
        public void TheLedgerDefaultsToNullSoADevBoxRunsWithoutAnEconomy()
            => Assert.That(new ServerConfig().EntitlementLedger, Is.Null);

        [Test]
        public void AnInjectedLedgerIsCarried()
        {
            var ledger = new InMemoryEntitlementLedger();

            Assert.That(new ServerConfig { EntitlementLedger = ledger }.EntitlementLedger,
                        Is.SameAs(ledger));
        }
    }
}
