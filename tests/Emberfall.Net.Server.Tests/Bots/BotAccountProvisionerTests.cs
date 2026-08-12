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
                new BotRosterEntry("ember_knight", "Emberkin", CardClass.EMBERWARD, CardClass.STONEWARDEN,
                    new[] { "ember_recruit" }),
                new BotRosterEntry("tide_warden", "Saltmourn", CardClass.TIDECALLER, CardClass.GRAVEKEEP,
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

        /// <summary>The two halves must not be crossed: the nameplate carries the roster's
        /// displayName, and the identity stays keyed to the username. Asserted because the
        /// provisioner used to pass the username for both, which is the defect this pair exists to
        /// prevent recurring — a display-name edit that silently re-mints an account would look
        /// exactly like nothing happening until the bot's entitlements were gone.</summary>
        [Test]
        public async Task Seats_The_Display_Name_But_Keys_Identity_On_The_Username()
        {
            var store = new InMemoryAccountStore(new FakeTime());
            var sink = new RecordingSink();
            await new BotAccountProvisioner(store, new[]
            {
                new BotRosterEntry("ember_knight", "Emberkin", CardClass.EMBERWARD, CardClass.STONEWARDEN,
                    new[] { "ember_recruit" }),
            }, sink).RunOnceAsync(CancellationToken.None);

            Assert.That(sink.Ready[0].DisplayName, Is.EqualTo("Emberkin"));
            Assert.That(sink.Ready[0].Username, Is.EqualTo("ember_knight"));

            // Same username, different display name — must land on the SAME account.
            var renamedSink = new RecordingSink();
            await new BotAccountProvisioner(store, new[]
            {
                new BotRosterEntry("ember_knight", "Ashfell", CardClass.EMBERWARD, CardClass.STONEWARDEN,
                    new[] { "ember_recruit" }),
            }, renamedSink).RunOnceAsync(CancellationToken.None);

            Assert.That(renamedSink.Ready[0].DisplayName, Is.EqualTo("Ashfell"));
            Assert.That(renamedSink.Ready[0].Account, Is.EqualTo(sink.Ready[0].Account),
                "renaming a bot's nameplate must not re-mint its account");
        }

        [Test]
        public async Task Is_Idempotent_Across_Repeated_Runs()
        {
            var store = new InMemoryAccountStore(new FakeTime());
            var roster = new[]
            {
                new BotRosterEntry("ember_knight", "Emberkin", CardClass.EMBERWARD, CardClass.STONEWARDEN,
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
