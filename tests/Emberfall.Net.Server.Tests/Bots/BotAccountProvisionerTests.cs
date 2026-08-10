using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Accounts;
using Emberfall.Net.Server.Bots;
using Emberfall.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Emberfall.Net.Server.Tests.Bots
{
    [TestFixture]
    [Category("Unit")]
    public sealed class BotAccountProvisionerTests
    {
        private sealed class RecordingSink : IBotAccountSink
        {
            public readonly List<ResolvedBotAccount> Ready = new List<ResolvedBotAccount>();
            public void MarkReady(ResolvedBotAccount account) => Ready.Add(account);
        }

        [Test]
        public async Task Provisions_Every_Roster_Entry_As_A_Superuser_Account()
        {
            var store = new InMemoryAccountStore(new FakeTime());
            var roster = new[]
            {
                new BotRosterEntry("ember_knight", CardClass.EMBERWARD, CardClass.STONEWARDEN,
                    new[] { "ember_recruit" }),
                new BotRosterEntry("tide_warden", CardClass.TIDECALLER, CardClass.GRAVEKEEP,
                    new[] { "tide_initiate" }),
            };
            var sink = new RecordingSink();
            var provisioner = new BotAccountProvisioner(store, roster, sink);

            await provisioner.RunOnceAsync(CancellationToken.None);

            Assert.That(sink.Ready, Has.Count.EqualTo(2));
            foreach (var resolved in sink.Ready)
            {
                var account = await store.FindByPlayerIdAsync(resolved.Account.Value);
                Assert.That(account, Is.Not.Null);
                Assert.That(account!.Kind, Is.EqualTo(AccountKind.Superuser));
            }
        }

        [Test]
        public async Task Is_Idempotent_Across_Repeated_Runs()
        {
            var store = new InMemoryAccountStore(new FakeTime());
            var roster = new[]
            {
                new BotRosterEntry("ember_knight", CardClass.EMBERWARD, CardClass.STONEWARDEN,
                    new[] { "ember_recruit" }),
            };
            var sink = new RecordingSink();
            var provisioner = new BotAccountProvisioner(store, roster, sink);

            await provisioner.RunOnceAsync(CancellationToken.None);
            await provisioner.RunOnceAsync(CancellationToken.None);

            Assert.That(sink.Ready, Has.Count.EqualTo(2), "one MarkReady per run is fine; the ACCOUNT must be the same");
            Assert.That(sink.Ready[0].Account, Is.EqualTo(sink.Ready[1].Account));
        }
    }
}
