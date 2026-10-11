using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Decks;
using Kingdom.Net.Server.Identity;
using Microsoft.Extensions.Hosting;

namespace Kingdom.Net.Server.Bots
{
    /// <summary>A resolved, ready-to-seat bot identity — the account exists and is Superuser-flagged,
    /// and its fixed deck is already built. Consumed by <see cref="BotPool"/>.</summary>
    public sealed record ResolvedBotAccount(string Username, AccountId Account, string DisplayName, SeatDeck Deck);

    /// <summary>The seam <see cref="BotAccountProvisioner"/> writes into once an entry is provisioned —
    /// implemented by <see cref="BotPool"/>. Kept as its own interface so this class is testable without
    /// constructing a full pool.</summary>
    public interface IBotAccountSink
    {
        void MarkReady(ResolvedBotAccount account);
    }

    /// <summary>
    /// Self-provisions every roster entry's account on startup, idempotently — mirrors the existing
    /// <c>AccountSchemaInitializer</c>/<c>TelemetryDrain</c> hosted-service pattern. No manual
    /// admin step: <see cref="IAccountStore.ResolveOrCreateAsync"/> is a pure function of the credential,
    /// so re-running this on every boot always resolves to the same accounts (design doc §Bot roster).
    /// </summary>
    public sealed class BotAccountProvisioner : BackgroundService
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

        private readonly IAccountStore _store;
        private readonly IReadOnlyList<BotRosterEntry> _roster;
        private readonly IBotAccountSink _sink;

        public BotAccountProvisioner(IAccountStore store, IReadOnlyList<BotRosterEntry> roster, IBotAccountSink sink)
        {
            _store = store;
            _roster = roster;
            _sink = sink;
        }

        /// <summary>
        /// Retries forever until a full pass succeeds, mirroring
        /// <see cref="Accounts.AccountSchemaInitializer.ExecuteAsync"/>'s exact shape — never lets an
        /// exception escape. Unguarded (<c>=&gt; RunOnceAsync(...)</c>) is what this replaced:
        /// <see cref="BackgroundService"/>'s default unhandled-exception behavior is
        /// <c>StopHost</c>, so hitting Postgres before the accounts schema migration finishes on a
        /// fresh deployment (this service is registered after
        /// <see cref="Accounts.AccountSchemaInitializer"/>) would otherwise take the whole match
        /// server down rather than retry. A logged success line is the only way "bot-fill is armed"
        /// becomes observable — nothing else reports it.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                    Console.Error.WriteLine(
                        "[bots] bot-fill armed: {0} bot account(s) provisioned.", _roster.Count);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[bots] provisioning failed, retrying: {0}", ex.Message);
                    await DelaySafe(stoppingToken).ConfigureAwait(false);
                }
            }
        }

        private static async Task DelaySafe(CancellationToken ct)
        {
            try { await Task.Delay(RetryDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        /// <summary>The provisioning pass itself, separated from <see cref="ExecuteAsync"/> so a test
        /// can await exactly one run instead of racing a hosted service's background start.</summary>
        public async Task RunOnceAsync(CancellationToken ct)
        {
            foreach (var entry in _roster)
            {
                string credentialHash = DeriveCredentialHash(entry.AccountUsername);
                var account = await _store.ResolveOrCreateAsync("bot", credentialHash, ct: ct).ConfigureAwait(false);
                if (account.Kind != AccountKind.Superuser)
                    await _store.SetKindAsync(account.PlayerId, AccountKind.Superuser, ct).ConfigureAwait(false);

                var deck = new SeatDeck(entry.PrimaryClass, entry.Cards);
                _sink.MarkReady(new ResolvedBotAccount(
                    entry.AccountUsername,
                    AccountId.FromAuthenticatedAccount(account.PlayerId),
                    entry.DisplayName,
                    deck));
            }
        }

        /// <summary>
        /// The bot's account IDENTITY, derived from the roster username alone.
        ///
        /// <para><b>Editing an accountUsername in bot-roster.json re-mints that bot's account.</b>
        /// <see cref="IAccountStore.ResolveOrCreateAsync"/> is a pure function of this hash, which is
        /// what makes re-provisioning on every boot idempotent — and equally what makes a renamed
        /// entry resolve to a brand-new account while the old one is left behind with whatever is
        /// keyed to it. It is deliberately NOT derived from the display name: renaming a bot on the
        /// versus screen must not touch its identity, which is why the roster carries the two
        /// separately (see <see cref="BotRosterEntry"/>).</para>
        /// </summary>
        private static string DeriveCredentialHash(string accountUsername)
        {
            Span<byte> digest = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes("bot:" + accountUsername), digest);
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
    }
}
