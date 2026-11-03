using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;
using Kingdom.Net.Server.King;
using Kingdom.Net.Server.Social;
using Kingdom.Net.Server.Tests.Support;
using NUnit.Framework;

namespace Kingdom.Net.Server.Tests.King
{
    /// <summary>
    /// Choosing a back from the pool (#421). The back id is client-authored, so most of these are
    /// refusals: a back the choice does not offer, a reign with nothing open, someone else's
    /// choice, a second claim. The ones nobody would report are the races: two claims granting
    /// twice, and two choices sharing the last back the player lacks.
    /// </summary>
    [TestFixture]
    [Category("Unit")]
    public sealed class PrizeChoiceEndpointTests
    {
        private static readonly FavorPrizeSettings Prizes = new FavorPrizeSettings(new long[] { 500, 300, 200 }, 150, 50);
        private static readonly int[] Pool = { 201, 202 };
        private const int PlaceBack = 101;

        /// <summary>"good" and "other" are two signed-in players; any other token is rejected.</summary>
        private sealed class Verifier : IIdentityTokenVerifier
        {
            public int Calls { get; private set; }

            public Task<TokenVerificationResult> VerifyAsync(string rawToken, CancellationToken ct = default)
            {
                Calls++;
                return Task.FromResult(rawToken == "good" || rawToken == "other"
                    ? TokenVerificationResult.Ok(new VerifiedIdentity("subject-" + rawToken, rawToken + "@example.com", EmailVerified: true))
                    : TokenVerificationResult.Fail(TokenVerificationFailure.SignatureInvalid));
            }
        }

        /// <summary>Card backs as the ledger keys them, without the catalog, as in KingPayoutSweepTests:
        /// the pool backs do not exist in <see cref="CardBackDefinitions"/> yet.</summary>
        private sealed class FakeBacks : IPrizeBacks
        {
            private readonly HashSet<string> _keys = new HashSet<string>(StringComparer.Ordinal);
            private readonly object _lock = new object();
            public HashSet<(string Player, int Back)> Owned { get; } = new HashSet<(string, int)>();
            public List<string> Applied { get; } = new List<string>();
            public int Calls { get; private set; }
            public bool FailGrants { get; set; }

            /// <summary>The grant lands, then the call throws: a ledger write that committed and timed out.</summary>
            public bool ThrowAfterGrant { get; set; }

            /// <summary>When set, that many OwnsAsync calls each wait until all of them have arrived,
            /// so concurrent requests are all past their reads before any of them writes.</summary>
            public int HoldOwns { get; set; }
            private int _held;
            private readonly TaskCompletionSource<bool> _release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task GrantAsync(Account account, int backId, string eventId, CancellationToken ct)
            {
                lock (_lock) Calls++;
                if (FailGrants) throw new InvalidOperationException("ledger down");
                lock (_lock)
                {
                    string key = backId + ":" + account.PlayerId + ":" + eventId;
                    if (_keys.Add(key))
                    {
                        Applied.Add(key);
                        Owned.Add((account.PlayerId, backId));
                    }
                }
                if (ThrowAfterGrant) throw new TimeoutException("ledger answered late");
                return Task.CompletedTask;
            }

            public async Task<bool> OwnsAsync(Account account, int backId, CancellationToken ct)
            {
                bool wait = false;
                lock (_lock)
                {
                    if (_held < HoldOwns)
                    {
                        wait = true;
                        if (++_held == HoldOwns) _release.TrySetResult(true);
                    }
                }
                if (wait) await _release.Task.ConfigureAwait(false);
                lock (_lock) return Owned.Contains((account.PlayerId, backId));
            }
        }

        private sealed class Harness
        {
            public FakeTime Time { get; } = new FakeTime();
            public InMemoryAccountStore Accounts { get; }
            public InMemoryFavorStore Store { get; } = new InMemoryFavorStore();
            public InMemoryEntitlementLedger Ledger { get; } = new InMemoryEntitlementLedger();
            public FakeBacks Backs { get; } = new FakeBacks();
            public Verifier Verifier { get; } = new Verifier();
            public PrizeChoiceEndpoint Endpoint { get; }

            public Harness(int postsPerMinute = 100)
            {
                Accounts = new InMemoryAccountStore(Time);
                Endpoint = new PrizeChoiceEndpoint(Verifier, Accounts, Store, Backs, new EconomyService(Ledger, Time),
                    Prizes, Pool, new FriendRequestLimiter(Time, max: postsPerMinute, windowMs: 60_000));
            }

            public async Task<Account> AccountAsync(string token)
                => await Accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of("subject-" + token), token + "@example.com");

            /// <summary>Writes a paid reign's prize row with its choice open, as the sweep's back pass leaves it.</summary>
            public async Task<string> OpenChoiceAsync(string token, int reign, int place = 1)
            {
                string player = (await AccountAsync(token)).PlayerId;
                await Store.RecordPrizeAsync(new FavorPrize(reign, player, place, 500, null, false));
                await Store.ClosePaidAsync(reign, player, "name");
                await Store.SettleBackAsync(reign, player, null, true);
                return player;
            }

            public Task<PrizeChoiceResult> ChooseAsync(string? bearer, int reign, int backId)
                => Endpoint.ChooseAsync(Body("{\"reign\":" + reign + ",\"backId\":" + backId + "}"), bearer);

            public Task<long> GoldAsync(string player) => Ledger.BalanceAsync(player, EconomyItems.Gold);

            /// <summary>The Tuesday sweep over the same store and backs, with no place backs and no
            /// reign due: all it has to do is deliver.</summary>
            public KingPayoutSweep Sweep()
                => new KingPayoutSweep(Store, Accounts, new EconomyService(Ledger, Time), Backs, Prizes,
                    Time.NowMs, Time, Array.Empty<int>(), Pool);
        }

        private static ReadOnlyMemory<byte>? Body(string json) => Encoding.UTF8.GetBytes(json);

        // ── Choosing ──────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Choose_grants_the_back_and_closes_the_choice()
        {
            var h = new Harness();
            string p = await h.OpenChoiceAsync("good", 2);

            PrizeChoiceResult result = await h.ChooseAsync("good", 2, 202);
            FavorPrize? row = await h.Store.PrizeAsync(2, p);
            IReadOnlyList<FavorPrize> undelivered = await h.Store.UndeliveredChoicesAsync(null);
            PrizeChoiceResult read = await h.Endpoint.ReadAsync("good");

            Assert.Multiple(() =>
            {
                Assert.That((result.Status, result.HttpStatus, result.BackId), Is.EqualTo((PrizeChoiceStatus.Granted, 200, (int?)202)));
                Assert.That(h.Backs.Applied, Is.EqualTo(new[] { "202:" + p + ":king-choice:2" }),
                    "the key reverse-favor-prize.sql reverses a chosen back by");
                Assert.That(row, Is.EqualTo(new FavorPrize(2, p, 1, 500, 202, false)));
                Assert.That(undelivered, Is.Empty, "a confirmed grant is marked delivered");
                Assert.That(read.Choices, Is.Empty);
                Assert.That(h.Backs.Calls, Is.EqualTo(1), "the read after it grants nothing");
            });
        }

        [Test]
        public async Task Choosing_twice_grants_once_and_answers_the_recorded_back()
        {
            var h = new Harness();
            await h.OpenChoiceAsync("good", 2);

            await h.ChooseAsync("good", 2, 201);
            PrizeChoiceResult same = await h.ChooseAsync("good", 2, 201);
            PrizeChoiceResult other = await h.ChooseAsync("good", 2, 202);

            Assert.That((same.Status, same.BackId), Is.EqualTo((PrizeChoiceStatus.Granted, (int?)201)));
            Assert.That((other.Status, other.BackId), Is.EqualTo((PrizeChoiceStatus.Granted, (int?)201)),
                "the server's recorded back, not the one the repeat named");
            Assert.That(h.Backs.Applied, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task Two_concurrent_claims_naming_different_backs_grant_once()
        {
            // The grant key carries the back, so it alone would let both through. Both requests are
            // held until each has read the choice open and the pool unowned.
            var h = new Harness();
            await h.OpenChoiceAsync("good", 2);
            h.Backs.HoldOwns = 2;

            PrizeChoiceResult[] results = await Task.WhenAll(
                Task.Run(() => h.ChooseAsync("good", 2, 201)),
                Task.Run(() => h.ChooseAsync("good", 2, 202)));

            Assert.That(results.Select(r => r.Status), Is.All.EqualTo(PrizeChoiceStatus.Granted));
            Assert.That(results[0].BackId, Is.EqualTo(results[1].BackId), "both are told the winner's back");
            Assert.That(h.Backs.Applied, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task A_back_outside_the_pool_is_invalid()
        {
            var h = new Harness();
            string p = await h.OpenChoiceAsync("good", 2);

            PrizeChoiceResult result = await h.ChooseAsync("good", 2, 999);

            Assert.That(result.Status, Is.EqualTo(PrizeChoiceStatus.Invalid));
            Assert.That(result.HttpStatus, Is.EqualTo(400));
            Assert.That(h.Backs.Applied, Is.Empty);
            Assert.That((await h.Store.PrizeAsync(2, p))!.ChoiceOpen, Is.True, "a refused claim leaves the choice open");
        }

        [Test]
        public async Task An_owned_back_is_invalid()
        {
            var h = new Harness();
            string p = await h.OpenChoiceAsync("good", 2);
            h.Backs.Owned.Add((p, 201));

            PrizeChoiceResult result = await h.ChooseAsync("good", 2, 201);

            Assert.That(result.Status, Is.EqualTo(PrizeChoiceStatus.Invalid));
            Assert.That(h.Backs.Applied, Is.Empty);
        }

        [Test]
        public async Task A_place_back_is_invalid()
        {
            var h = new Harness();
            await h.OpenChoiceAsync("good", 2);

            Assert.That((await h.ChooseAsync("good", 2, PlaceBack)).Status, Is.EqualTo(PrizeChoiceStatus.Invalid));
            Assert.That(h.Backs.Applied, Is.Empty);
        }

        [Test]
        public async Task A_reign_with_no_open_choice_answers_none()
        {
            var h = new Harness();
            string p = (await h.AccountAsync("good")).PlayerId;
            // A place back the sweep settled is a prize row with no choice.
            await h.Store.RecordPrizeAsync(new FavorPrize(3, p, 1, 500, null, false));
            await h.Store.ClosePaidAsync(3, p, "name");
            await h.Store.SettleBackAsync(3, p, PlaceBack, false);

            Assert.That((await h.ChooseAsync("good", 3, 201)).Status, Is.EqualTo(PrizeChoiceStatus.None));
            Assert.That((await h.ChooseAsync("good", 7, 201)).Status, Is.EqualTo(PrizeChoiceStatus.None));
            Assert.That(h.Backs.Applied, Is.Empty);
        }

        [Test]
        public async Task Another_players_choice_answers_none()
        {
            var h = new Harness();
            string theirs = await h.OpenChoiceAsync("other", 2);

            PrizeChoiceResult result = await h.ChooseAsync("good", 2, 201);

            Assert.That(result.Status, Is.EqualTo(PrizeChoiceStatus.None));
            Assert.That(h.Backs.Applied, Is.Empty);
            Assert.That((await h.Store.PrizeAsync(2, theirs))!.ChoiceOpen, Is.True);
        }

        // ── A grant that does not confirm (controller ruling: a choice never reopens) ─────

        [Test]
        public async Task A_grant_that_throws_leaves_the_choice_closed_and_the_next_read_delivers_it_once()
        {
            var h = new Harness();
            string p = await h.OpenChoiceAsync("good", 2);
            h.Backs.FailGrants = true;

            PrizeChoiceResult failed = await h.ChooseAsync("good", 2, 201);
            PrizeChoiceResult another = await h.ChooseAsync("good", 2, 202);   // the ledger is still down
            FavorPrize? row = await h.Store.PrizeAsync(2, p);
            h.Backs.FailGrants = false;
            PrizeChoiceResult first = await h.Endpoint.ReadAsync("good");
            int callsAfterFirst = h.Backs.Calls;
            PrizeChoiceResult second = await h.Endpoint.ReadAsync("good");
            PrizeChoiceResult repeat = await h.ChooseAsync("good", 2, 202);

            Assert.Multiple(() =>
            {
                Assert.That((failed.Status, failed.HttpStatus), Is.EqualTo((PrizeChoiceStatus.Unavailable, 503)));
                Assert.That(another.HttpStatus, Is.EqualTo(503), "still owed, still undeliverable: no second pick, no false none");
                Assert.That(row, Is.EqualTo(new FavorPrize(2, p, 1, 500, 201, false)), "closed, with the back it chose");
                Assert.That(first.Choices, Is.Empty);
                Assert.That(second.Choices, Is.Empty);
                Assert.That(h.Backs.Calls, Is.EqualTo(callsAfterFirst), "the second read grants nothing");
                Assert.That((repeat.Status, repeat.BackId), Is.EqualTo((PrizeChoiceStatus.Granted, (int?)201)),
                    "a repeat claim is told the back it already has");
                Assert.That(h.Backs.Applied, Is.EqualTo(new[] { "201:" + p + ":king-choice:2" }));
            });
        }

        [Test]
        public async Task A_grant_that_throws_is_delivered_once_by_the_sweep()
        {
            var h = new Harness();
            string p = await h.OpenChoiceAsync("good", 2);
            h.Backs.FailGrants = true;
            await h.ChooseAsync("good", 2, 201);
            h.Backs.FailGrants = false;

            await h.Sweep().SweepAsync(CancellationToken.None);
            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(h.Backs.Applied, Is.EqualTo(new[] { "201:" + p + ":king-choice:2" }));
            Assert.That(h.Backs.Calls, Is.EqualTo(2), "the failed grant, then one delivery; the second sweep grants nothing");
            Assert.That(await h.Store.UndeliveredChoicesAsync(null), Is.Empty);
        }

        [Test]
        public async Task A_sweep_that_cannot_deliver_yet_keeps_trying()
        {
            var h = new Harness();
            await h.OpenChoiceAsync("good", 2);
            h.Backs.FailGrants = true;
            await h.ChooseAsync("good", 2, 201);

            await h.Sweep().SweepAsync(CancellationToken.None);   // still down: logged, not thrown
            h.Backs.FailGrants = false;
            await h.Sweep().SweepAsync(CancellationToken.None);

            Assert.That(h.Backs.Applied, Has.Count.EqualTo(1));
            Assert.That(await h.Store.UndeliveredChoicesAsync(null), Is.Empty);
        }

        [Test]
        public async Task A_grant_that_landed_then_threw_allows_no_second_pick()
        {
            var h = new Harness();
            string p = await h.OpenChoiceAsync("good", 2);
            h.Backs.ThrowAfterGrant = true;

            PrizeChoiceResult timedOut = await h.ChooseAsync("good", 2, 201);
            h.Backs.ThrowAfterGrant = false;
            PrizeChoiceResult again = await h.ChooseAsync("good", 2, 202);
            PrizeChoiceResult read = await h.Endpoint.ReadAsync("good");

            Assert.That(timedOut.HttpStatus, Is.EqualTo(503));
            Assert.That((again.Status, again.BackId), Is.EqualTo((PrizeChoiceStatus.Granted, (int?)201)),
                "the retry is told what it got, not what it named");
            Assert.That(read.Choices, Is.Empty);
            Assert.That(h.Backs.Applied, Is.EqualTo(new[] { "201:" + p + ":king-choice:2" }),
                "the re-delivery is a duplicate on the same key, and 202 is never granted");
            Assert.That(await h.Store.UndeliveredChoicesAsync(null), Is.Empty);
        }

        [Test]
        public async Task A_back_still_owed_is_not_offered_on_a_second_choice()
        {
            var h = new Harness();
            await h.OpenChoiceAsync("good", 2);
            await h.OpenChoiceAsync("good", 3, place: 2);
            h.Backs.FailGrants = true;   // 201 is chosen for reign 2 and stays undelivered throughout

            await h.ChooseAsync("good", 2, 201);
            PrizeChoiceResult read = await h.Endpoint.ReadAsync("good");
            PrizeChoiceResult again = await h.ChooseAsync("good", 3, 201);

            Assert.That(read.Choices.Select(c => (c.Reign, string.Join(",", c.BackIds))), Is.EqualTo(new[] { (3, "202") }));
            Assert.That(again.Status, Is.EqualTo(PrizeChoiceStatus.Invalid));
            Assert.That(h.Backs.Applied, Is.Empty);
        }

        [Test]
        public async Task A_choice_the_operator_reversed_answers_none()
        {
            var h = new Harness();
            string p = await h.OpenChoiceAsync("good", 2);
            await h.ChooseAsync("good", 2, 201);
            h.Store.Reverse(2, p);

            PrizeChoiceResult result = await h.ChooseAsync("good", 2, 201);

            Assert.That(result.Status, Is.EqualTo(PrizeChoiceStatus.None));
            Assert.That(h.Backs.Calls, Is.EqualTo(1));
        }

        [Test]
        public void The_pool_leaves_out_ids_the_catalog_does_not_hold()
        {
            IReadOnlyList<int> pool = PrizeChoiceEndpoint.CatalogPool(new[] { 201, 999, 202 }, id => id != 999);

            Assert.That(pool, Is.EqualTo(new[] { 201, 202 }));
        }

        // ── Reading ───────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Pool_excludes_owned_backs_when_read()
        {
            var h = new Harness();
            string p = await h.OpenChoiceAsync("good", 2, place: 3);
            h.Backs.Owned.Add((p, 201));

            PrizeChoiceResult result = await h.Endpoint.ReadAsync("good");

            Assert.That(result.Status, Is.EqualTo(PrizeChoiceStatus.Ok));
            PrizeChoice only = result.Choices.Single();
            Assert.That((only.Reign, only.Place, only.BackIds.ToArray()), Is.EqualTo((2, 3, new[] { 202 })));
        }

        [Test]
        public async Task A_choice_the_pool_ran_dry_under_pays_gold_once_when_read()
        {
            var h = new Harness();
            string p = await h.OpenChoiceAsync("good", 2);
            h.Backs.Owned.Add((p, 201));
            h.Backs.Owned.Add((p, 202));

            PrizeChoiceResult first = await h.Endpoint.ReadAsync("good");
            PrizeChoiceResult second = await h.Endpoint.ReadAsync("good");
            long gold = await h.GoldAsync(p);
            FavorPrize? row = await h.Store.PrizeAsync(2, p);
            PrizeChoiceResult post = await h.ChooseAsync("good", 2, 201);

            Assert.Multiple(() =>
            {
                Assert.That(first.Choices, Is.Empty);
                Assert.That(second.Choices, Is.Empty);
                Assert.That(gold, Is.EqualTo(Prizes.PoolEmptyGold));
                Assert.That(row!.ChoiceOpen, Is.False);
                Assert.That((post.Status, post.Gold), Is.EqualTo((PrizeChoiceStatus.Gold, (long?)Prizes.PoolEmptyGold)));
            });
        }

        [Test]
        public async Task Pool_gold_the_sweep_already_paid_is_not_paid_again()
        {
            // Same key as the sweep's, so whichever settles first, the other pays nothing.
            var h = new Harness();
            Account account = await h.AccountAsync("good");
            await h.OpenChoiceAsync("good", 2);
            h.Backs.Owned.Add((account.PlayerId, 201));
            h.Backs.Owned.Add((account.PlayerId, 202));
            await KingPayoutSweep.GrantGoldAsync(new EconomyService(h.Ledger, h.Time), account, Prizes.PoolEmptyGold,
                KingPayoutSweep.PoolEmptyEventId(2), 2, CancellationToken.None);

            await h.Endpoint.ReadAsync("good");

            Assert.That(await h.GoldAsync(account.PlayerId), Is.EqualTo(Prizes.PoolEmptyGold));
        }

        [Test]
        public async Task Two_choices_sharing_the_last_back_settle_the_loser_in_gold()
        {
            var h = new Harness();
            string p = await h.OpenChoiceAsync("good", 2);
            await h.OpenChoiceAsync("good", 3, place: 2);
            h.Backs.Owned.Add((p, 202));

            PrizeChoiceResult before = await h.Endpoint.ReadAsync("good");
            PrizeChoiceResult chose = await h.ChooseAsync("good", 2, 201);
            PrizeChoiceResult after = await h.Endpoint.ReadAsync("good");
            PrizeChoiceResult late = await h.ChooseAsync("good", 3, 201);
            long gold = await h.GoldAsync(p);

            Assert.Multiple(() =>
            {
                Assert.That(before.Choices.Select(c => (c.Reign, c.BackIds.Single())), Is.EqualTo(new[] { (2, 201), (3, 201) }));
                Assert.That(chose.Status, Is.EqualTo(PrizeChoiceStatus.Granted));
                Assert.That(after.Choices, Is.Empty);
                Assert.That((late.Status, late.Gold), Is.EqualTo((PrizeChoiceStatus.Gold, (long?)Prizes.PoolEmptyGold)));
                Assert.That(gold, Is.EqualTo(Prizes.PoolEmptyGold));
                Assert.That(h.Backs.Applied, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public async Task A_post_for_a_choice_the_pool_ran_dry_under_settles_it_and_answers_gold()
        {
            var h = new Harness();
            string p = await h.OpenChoiceAsync("good", 2);
            h.Backs.Owned.Add((p, 201));
            h.Backs.Owned.Add((p, 202));

            PrizeChoiceResult result = await h.ChooseAsync("good", 2, 201);

            Assert.That((result.Status, result.Gold), Is.EqualTo((PrizeChoiceStatus.Gold, (long?)Prizes.PoolEmptyGold)));
            Assert.That(await h.GoldAsync(p), Is.EqualTo(Prizes.PoolEmptyGold));
            Assert.That((await h.Store.PrizeAsync(2, p))!.ChoiceOpen, Is.False);
        }

        // ── Refusals at the door ──────────────────────────────────────────────────────────

        [Test]
        public async Task A_bad_bearer_is_401()
        {
            var h = new Harness();
            await h.OpenChoiceAsync("good", 2);

            PrizeChoiceResult read = await h.Endpoint.ReadAsync("forged");
            PrizeChoiceResult choose = await h.ChooseAsync("forged", 2, 201);
            PrizeChoiceResult none = await h.ChooseAsync(null, 2, 201);

            Assert.That(new[] { read, choose, none }.Select(r => (r.Status, r.HttpStatus)),
                Is.All.EqualTo((PrizeChoiceStatus.Unauthenticated, 401)));
            Assert.That(h.Backs.Applied, Is.Empty);
        }

        [Test]
        public async Task An_oversized_body_is_refused_before_auth()
        {
            var h = new Harness();
            await h.OpenChoiceAsync("good", 2);
            int callsBefore = h.Verifier.Calls;

            PrizeChoiceResult result = await h.Endpoint.ChooseAsync(null, "good");

            Assert.That((result.Status, result.HttpStatus), Is.EqualTo((PrizeChoiceStatus.Invalid, 413)));
            Assert.That(h.Verifier.Calls, Is.EqualTo(callsBefore), "the verifier is never asked");
        }

        [TestCase("")]
        [TestCase("not json")]
        [TestCase("[2,201]")]
        [TestCase("{\"reign\":2}")]
        [TestCase("{\"reign\":\"2\",\"backId\":201}")]
        [TestCase("{\"reign\":2,\"backId\":2.5}")]
        [TestCase("{\"reign\":2,\"backId\":99999999999}")]
        public async Task A_malformed_body_is_invalid(string json)
        {
            var h = new Harness();
            await h.OpenChoiceAsync("good", 2);

            PrizeChoiceResult result = await h.Endpoint.ChooseAsync(Body(json), "good");

            Assert.That((result.Status, result.HttpStatus), Is.EqualTo((PrizeChoiceStatus.Invalid, 400)));
            Assert.That(h.Backs.Applied, Is.Empty);
        }

        [Test]
        public async Task Claims_are_rate_limited_per_account()
        {
            var h = new Harness(postsPerMinute: 2);
            await h.OpenChoiceAsync("good", 2);

            await h.ChooseAsync("good", 2, 999);
            await h.ChooseAsync("good", 2, 999);
            PrizeChoiceResult third = await h.ChooseAsync("good", 2, 201);
            PrizeChoiceResult someoneElse = await h.ChooseAsync("other", 2, 201);

            Assert.That((third.Status, third.HttpStatus), Is.EqualTo((PrizeChoiceStatus.RateLimited, 429)));
            Assert.That(someoneElse.Status, Is.EqualTo(PrizeChoiceStatus.None));
            Assert.That(h.Backs.Applied, Is.Empty);
        }

        [Test]
        public async Task Without_a_ledger_both_routes_are_unavailable()
        {
            var h = new Harness();
            var endpoint = new PrizeChoiceEndpoint(h.Verifier, h.Accounts, h.Store, null, null, null, Pool,
                new FriendRequestLimiter(h.Time));

            Assert.That((await endpoint.ReadAsync("good")).HttpStatus, Is.EqualTo(503));
            Assert.That((await endpoint.ChooseAsync(Body("{\"reign\":2,\"backId\":201}"), "good")).HttpStatus, Is.EqualTo(503));
        }

        // ── Through the composition root ──────────────────────────────────────────────────

        private static async Task<(HttpStatusCode Status, JsonElement Body, bool NoStore)> SendAsync(
            Func<HttpClient, Uri, Task<HttpResponseMessage>> send,
            InMemoryFavorStore? favor = null, InMemoryAccountStore? accounts = null)
        {
            var app = ServerApp.Build(new ServerConfig
            {
                Urls = "http://127.0.0.1:0",
                CardsDir = SessionHarness.CardDataDir(),
                AccountStore = accounts ?? new InMemoryAccountStore(new FakeTime()),
                TokenVerifier = new Verifier(),
                EntitlementLedger = new InMemoryEntitlementLedger(),
                FavorPrizes = Prizes,
                Favor = favor,
            });
            await app.StartAsync();
            try
            {
                using var http = new HttpClient();
                using HttpResponseMessage response = await send(http, new Uri(app.Urls.First()));
                string json = await response.Content.ReadAsStringAsync();
                return (response.StatusCode, JsonDocument.Parse(json).RootElement.Clone(),
                    response.Headers.CacheControl?.NoStore == true);
            }
            finally
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
        }

        private static HttpRequestMessage Request(HttpMethod method, Uri root, string path, string? bearer, string? body = null)
        {
            var request = new HttpRequestMessage(method, new Uri(root, path));
            if (bearer != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return request;
        }

        [Test]
        [Category("Contract")]
        [CancelAfter(30_000)]
        public async Task Routes_answer_401_to_a_bad_bearer_and_ok_to_a_good_one()
        {
            var badRead = await SendAsync((h, root) => h.SendAsync(Request(HttpMethod.Get, root, "/king/prize", "forged")));
            var badChoose = await SendAsync((h, root) => h.SendAsync(
                Request(HttpMethod.Post, root, "/king/prize/choose", "forged", "{\"reign\":2,\"backId\":201}")));
            var goodRead = await SendAsync((h, root) => h.SendAsync(Request(HttpMethod.Get, root, "/king/prize", "good")));
            var goodChoose = await SendAsync((h, root) => h.SendAsync(
                Request(HttpMethod.Post, root, "/king/prize/choose", "good", "{\"reign\":2,\"backId\":201}")));

            Assert.Multiple(() =>
            {
                Assert.That(badRead.Status, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(badRead.Body.GetProperty("status").GetString(), Is.EqualTo("unauthenticated"));
                Assert.That(badChoose.Status, Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(goodRead.Status, Is.EqualTo(HttpStatusCode.OK));
                Assert.That(goodRead.Body.GetProperty("status").GetString(), Is.EqualTo("ok"));
                Assert.That(goodRead.Body.GetProperty("choices").GetArrayLength(), Is.Zero);
                Assert.That(goodRead.NoStore, Is.True);
                Assert.That(goodChoose.Body.GetProperty("status").GetString(), Is.EqualTo("none"));
            });
        }

        [Test]
        [Category("Contract")]
        [CancelAfter(30_000)]
        public async Task A_closed_choice_answers_its_back_or_its_gold_over_http()
        {
            // The real ledger and the catalog's one back: the shape HttpKingGateway reads.
            var accounts = new InMemoryAccountStore(new FakeTime());
            var favor = new InMemoryFavorStore();
            string p = (await accounts.ResolveOrCreateAsync("supabase", CredentialHash.Of("subject-good"), "good@example.com")).PlayerId;
            foreach (int reign in new[] { 2, 3 })
            {
                await favor.RecordPrizeAsync(new FavorPrize(reign, p, 1, 500, null, false));
                await favor.ClosePaidAsync(reign, p, "name");
                await favor.SettleBackAsync(reign, p, null, true);
            }
            await favor.CloseChoiceAsync(2, p, CardBackDefinitions.DefaultId);   // chosen, not yet delivered
            await favor.CloseChoiceAsync(3, p, null);                            // settled in gold

            var back = await SendAsync((h, root) => h.SendAsync(
                Request(HttpMethod.Post, root, "/king/prize/choose", "good", "{\"reign\":2,\"backId\":999}")), favor, accounts);
            var gold = await SendAsync((h, root) => h.SendAsync(
                Request(HttpMethod.Post, root, "/king/prize/choose", "good", "{\"reign\":3,\"backId\":999}")), favor, accounts);

            IReadOnlyList<FavorPrize> undelivered = await favor.UndeliveredChoicesAsync(null);

            Assert.Multiple(() =>
            {
                Assert.That(back.Body.GetProperty("status").GetString(), Is.EqualTo("granted"));
                Assert.That(back.Body.GetProperty("backId").GetInt32(), Is.EqualTo(CardBackDefinitions.DefaultId));
                Assert.That(gold.Body.GetProperty("status").GetString(), Is.EqualTo("gold"));
                Assert.That(gold.Body.GetProperty("gold").GetInt64(), Is.EqualTo(Prizes.PoolEmptyGold));
                Assert.That(undelivered, Is.Empty, "the repeat claim delivered it");
            });
        }

        [Test]
        [Category("Contract")]
        [CancelAfter(30_000)]
        public async Task An_oversized_body_is_refused_before_auth_over_http()
        {
            // A forged bearer would be a 401; the 413 shows the cap answered first.
            string padded = "{\"reign\":2,\"backId\":201,\"pad\":\"" + new string('x', PrizeChoiceEndpoint.MaxBodyBytes) + "\"}";

            var result = await SendAsync((h, root) => h.SendAsync(
                Request(HttpMethod.Post, root, "/king/prize/choose", "forged", padded)));

            Assert.That(result.Status, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
            Assert.That(result.Body.GetProperty("status").GetString(), Is.EqualTo("invalid"));
        }
    }
}
