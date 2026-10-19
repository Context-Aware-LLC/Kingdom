using System;
using System.IO;
using NUnit.Framework;
using Kingdom.Core.Collection;

namespace Kingdom.Core.Tests.Collection
{
    /// <summary>
    /// The device account registry — two players on one machine (Sam 2026-07-27, testing scope).
    ///
    /// <para>The behaviours worth pinning are the ones that would otherwise be relearned the hard
    /// way: the registry sits at the root and not in a scope, the first account is grandfathered onto
    /// the identity already in use rather than migrated off it, two accounts can never share a wire
    /// token, forgetting an account leaves its collection alone, and two accounts genuinely hold two
    /// separate collections.</para>
    /// </summary>
    [TestFixture]
    [Category("Collection")]
    public sealed class LocalAccountTests
    {
        private string _root = null!;
        private const string DeviceToken = "d64d9242-f332-43d8-b134-52cb83ac6432";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "kingdom-account-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* a temp dir we could not remove fails nothing */ }
        }

        private JsonLocalAccountStore Store() => JsonLocalAccountStore.InRoot(_root);

        // ---- where the file lives ------------------------------------------------------

        [Test]
        public void Registry_lives_at_the_root_beside_the_scope_folders_not_inside_one()
        {
            // The load-bearing fact about this file: you cannot open a scope until you know which
            // account you are, so the list of accounts cannot be inside a scope.
            string path = Store().FilePath;
            Assert.That(Path.GetFileName(path), Is.EqualTo("accounts.json"));
            Assert.That(Path.GetDirectoryName(path), Is.EqualTo(_root));
        }

        [Test]
        public void A_device_that_predates_the_registry_reads_as_empty_rather_than_throwing()
        {
            Assert.That(Store().LoadAll(), Is.Empty);
        }

        // ---- grandfathering ------------------------------------------------------------

        [Test]
        public void EnsureDefault_adopts_the_identity_already_in_use()
        {
            var all = LocalAccountRoster.EnsureDefault(Store(), DeviceToken);

            Assert.That(all, Has.Count.EqualTo(1));
            Assert.That(all[0].Id, Is.EqualTo(DeviceToken), "the existing collection folder must not move");
            Assert.That(all[0].WireToken, Is.EqualTo(DeviceToken), "and its wire identity must not change");
            Assert.That(all[0].IsGrandfathered, Is.True);
        }

        [Test]
        public void EnsureDefault_is_idempotent_and_never_reshuffles()
        {
            LocalAccountRoster.EnsureDefault(Store(), DeviceToken);
            Store().Save(new LocalAccount("second-id", "second-token"));

            var all = LocalAccountRoster.EnsureDefault(Store(), "a-completely-different-token");

            Assert.That(all, Has.Count.EqualTo(2), "a second call must not mint a third account");
            Assert.That(all[0].Id, Is.EqualTo(DeviceToken));
            Assert.That(all[1].Id, Is.EqualTo("second-id"));
        }

        [Test]
        public void A_minted_account_keeps_its_wire_token_distinct_from_its_folder_name()
        {
            // Id becomes a directory name and gets logged; WireToken is a bearer credential. Only the
            // grandfathered account is allowed to conflate them.
            var minted = new LocalAccount("account-2-id", "account-2-token");
            Assert.That(minted.IsGrandfathered, Is.False);
        }

        // ---- the guard that matters ----------------------------------------------------

        [Test]
        public void Two_accounts_cannot_share_a_wire_token()
        {
            var store = Store();
            store.Save(new LocalAccount("first", "shared-token"));

            var ex = Assert.Throws<ArgumentException>(
                () => store.Save(new LocalAccount("second", "shared-token")));

            Assert.That(ex!.Message, Does.Contain("first"));
            Assert.That(store.LoadAll(), Has.Count.EqualTo(1), "the refusal must not half-write");
        }

        [Test]
        public void Re_saving_an_account_keeps_its_own_wire_token()
        {
            var store = Store();
            store.Save(new LocalAccount("first", "token-a"));

            // The uniqueness check must skip the row being replaced, or no account could ever be
            // written twice.
            Assert.DoesNotThrow(() => store.Save(new LocalAccount("first", "token-a")));
            Assert.That(store.LoadAll(), Has.Count.EqualTo(1));
        }

        [Test]
        public void Save_upserts_in_place_so_the_switcher_list_does_not_reorder()
        {
            var store = Store();
            store.Save(new LocalAccount("first", "token-a"));
            store.Save(new LocalAccount("second", "token-b"));
            store.Save(new LocalAccount("first", "token-c"));

            var all = store.LoadAll();
            Assert.That(all[0].Id, Is.EqualTo("first"));
            Assert.That(all[0].WireToken, Is.EqualTo("token-c"));
            Assert.That(all[1].Id, Is.EqualTo("second"));
        }

        // ---- forgetting ----------------------------------------------------------------

        [Test]
        public void Delete_forgets_the_row_and_leaves_the_collection_on_disk()
        {
            var store = Store();
            store.Save(new LocalAccount("keeper", "token-a"));
            store.Save(new LocalAccount("doomed", "token-b"));

            var profiles = JsonProfileStore.InDirectory(_root, "doomed");
            profiles.Save(new PlayerProfile("deck-perdition"));

            store.Delete("doomed");

            Assert.That(store.LoadAll(), Has.Count.EqualTo(1));
            Assert.That(File.Exists(profiles.FilePath), Is.True,
                        "a mis-clicked testing affordance must not destroy a real collection");
            Assert.That(profiles.Load().LastPlayedDeckId, Is.EqualTo("deck-perdition"));
        }

        [Test]
        public void Delete_is_idempotent()
        {
            var store = Store();
            store.Save(new LocalAccount("only", "token-a"));
            store.Delete("nobody");
            store.Delete("only");
            store.Delete("only");
            Assert.That(store.LoadAll(), Is.Empty);
        }

        // ---- lookup --------------------------------------------------------------------

        [Test]
        public void Find_returns_null_for_a_dangling_active_pointer()
        {
            // Real case, not a bug: the active-account pointer in PlayerPrefs outlives the account it
            // names when one is forgotten. The caller falls back to the first account.
            var all = LocalAccountRoster.EnsureDefault(Store(), DeviceToken);
            Assert.That(LocalAccountRoster.Find(all, "forgotten-id"), Is.Null);
            Assert.That(LocalAccountRoster.Find(all, DeviceToken), Is.Not.Null);
            Assert.That(LocalAccountRoster.Find(all, ""), Is.Null);
        }

        // ---- the point of the whole exercise -------------------------------------------

        [Test]
        public void Two_accounts_hold_two_separate_collections_under_one_root()
        {
            var store = Store();
            LocalAccountRoster.EnsureDefault(store, DeviceToken);
            store.Save(new LocalAccount("account-2", "token-2"));

            var one = JsonProfileStore.InDirectory(_root, DeviceToken);
            var two = JsonProfileStore.InDirectory(_root, "account-2");
            one.Save(new PlayerProfile("deck-one"));
            two.Save(new PlayerProfile("deck-two"));

            Assert.That(one.Load().LastPlayedDeckId, Is.EqualTo("deck-one"));
            Assert.That(two.Load().LastPlayedDeckId, Is.EqualTo("deck-two"));
            Assert.That(one.FilePath, Is.Not.EqualTo(two.FilePath));
        }

        // ---- format --------------------------------------------------------------------

        [Test]
        public void A_row_missing_its_wire_token_is_refused_rather_than_defaulted()
        {
            // Defaulting wireToken to id would silently give a minted account the grandfathered
            // account's shape, and the symptom would surface much later, on the wire.
            File.WriteAllText(Store().FilePath,
                "{ \"schemaVersion\": 1, \"accounts\": [ { \"id\": \"a\" } ] }");

            Assert.Throws<CollectionFormatException>(() => Store().LoadAll());
        }

        [Test]
        public void A_registry_written_by_a_newer_build_is_refused()
        {
            File.WriteAllText(Store().FilePath,
                "{ \"schemaVersion\": 999, \"accounts\": [] }");

            Assert.Throws<CollectionFormatException>(() => Store().LoadAll());
        }

        [Test]
        public void An_account_needs_both_an_id_and_a_token()
        {
            Assert.Throws<ArgumentException>(() => new LocalAccount("", "token"));
            Assert.Throws<ArgumentException>(() => new LocalAccount("id", "  "));
        }

        // ---- identity binding ----------------------------------------------------------
        //
        // The reassociation rule. Every case below is silent when it goes wrong: the player either
        // gets somebody else's collection or is told their own is empty, and nothing throws in
        // either direction. That is why they are pinned here rather than left to the call site.

        [Test]
        public void An_account_is_unclaimed_until_an_identity_takes_it()
        {
            var account = new LocalAccount("scope-1", "token-1");

            Assert.That(account.IsClaimed, Is.False);
            Assert.That(account.AuthUserId, Is.Empty);
        }

        [Test]
        public void A_claim_round_trips_through_the_registry()
        {
            Store().Save(new LocalAccount("scope-1", "token-1").ClaimedBy("supabase-user-1"));

            var read = Store().LoadAll()[0];
            Assert.That(read.AuthUserId, Is.EqualTo("supabase-user-1"));
            Assert.That(read.IsClaimed, Is.True);
        }

        [Test]
        public void A_registry_written_before_claims_existed_still_loads_as_unclaimed()
        {
            // Every accounts.json on every device today has this shape. Refusing it, or defaulting
            // it to anything but unclaimed, would brick a boot that has nothing wrong with it.
            File.WriteAllText(Store().FilePath,
                "{ \"schemaVersion\": 1, \"accounts\": [ { \"id\": \"a\", \"wireToken\": \"a\" } ] }");

            var read = Store().LoadAll()[0];
            Assert.That(read.IsClaimed, Is.False);
            Assert.That(read.AuthUserId, Is.Empty);
        }

        [Test]
        public void An_unclaimed_registry_writes_the_bytes_it_always_did()
        {
            // The field is omitted, not written empty, so arming this costs an untouched device
            // nothing on disk.
            Store().Save(new LocalAccount("a", "a"));

            Assert.That(File.ReadAllText(Store().FilePath), Does.Not.Contain("authUserId"));
        }

        [Test]
        public void A_returning_identity_is_sent_to_its_own_account_not_the_one_on_screen()
        {
            // The whole point of a returning-player path. The device is sitting on account two;
            // signing in as the identity that owns account one must move them to account one.
            var accounts = new[]
            {
                new LocalAccount("scope-1", "token-1", "user-1"),
                new LocalAccount("scope-2", "token-2"),
            };

            var plan = LocalAccountRoster.PlanBinding(accounts, "scope-2", "user-1", out var target);

            Assert.That(plan, Is.EqualTo(LocalAccountRoster.BindingPlan.SwitchToExisting));
            Assert.That(target!.Id, Is.EqualTo("scope-1"));
        }

        [Test]
        public void A_first_sign_in_adopts_the_collection_already_being_played()
        {
            // Minting beside it instead would strand the roster and decks built since install behind
            // a folder nothing points at any more — and it would look exactly like data loss.
            var accounts = new[] { new LocalAccount(DeviceToken, DeviceToken) };

            var plan = LocalAccountRoster.PlanBinding(accounts, DeviceToken, "user-1", out var target);

            Assert.That(plan, Is.EqualTo(LocalAccountRoster.BindingPlan.AdoptCurrent));
            Assert.That(target!.Id, Is.EqualTo(DeviceToken));
        }

        [Test]
        public void A_second_player_signing_in_on_a_borrowed_device_gets_a_new_account()
        {
            // The account on screen belongs to someone else. Adopting it would hand over their
            // roster; this is the case that must NOT adopt.
            var accounts = new[] { new LocalAccount("scope-1", "token-1", "user-1") };

            var plan = LocalAccountRoster.PlanBinding(accounts, "scope-1", "user-2", out var target);

            Assert.That(plan, Is.EqualTo(LocalAccountRoster.BindingPlan.MintNew));
            Assert.That(target, Is.Null);
        }

        [Test]
        public void A_fresh_install_with_no_accounts_yet_mints_rather_than_throwing()
        {
            var plan = LocalAccountRoster.PlanBinding(Array.Empty<LocalAccount>(), null, "user-1", out var target);

            Assert.That(plan, Is.EqualTo(LocalAccountRoster.BindingPlan.MintNew));
            Assert.That(target, Is.Null);
        }

        [Test]
        public void Re_binding_the_same_identity_is_idempotent()
        {
            // Every launch after the first runs this path. It must not write, and it must not throw.
            var accounts = new[] { new LocalAccount("scope-1", "token-1", "user-1") };

            var plan = LocalAccountRoster.PlanBinding(accounts, "scope-1", "user-1", out var target);

            Assert.That(plan, Is.EqualTo(LocalAccountRoster.BindingPlan.SwitchToExisting));
            Assert.That(target!.Id, Is.EqualTo("scope-1"));
        }

        [Test]
        public void An_account_refuses_to_be_claimed_out_from_under_its_owner()
        {
            var claimed = new LocalAccount("scope-1", "token-1", "user-1");

            Assert.Throws<InvalidOperationException>(() => claimed.ClaimedBy("user-2"));
            Assert.DoesNotThrow(() => claimed.ClaimedBy("user-1"));
        }
    }
}
