using System;
using System.IO;
using NUnit.Framework;
using Emberfall.Core.Collection;

namespace Emberfall.Core.Tests.Collection
{
    /// <summary>
    /// The device account registry — two players on one machine (Sam 2026-07-27, testing scope).
    ///
    /// <para>The behaviours worth pinning are the ones that would otherwise be rediscovered the hard
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
            _root = Path.Combine(Path.GetTempPath(), "emberfall-account-tests", Guid.NewGuid().ToString("N"));
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
            profiles.Save(new PlayerProfile("Perdition"));

            store.Delete("doomed");

            Assert.That(store.LoadAll(), Has.Count.EqualTo(1));
            Assert.That(File.Exists(profiles.FilePath), Is.True,
                        "a mis-clicked testing affordance must not destroy a real collection");
            Assert.That(profiles.Load().DisplayName, Is.EqualTo("Perdition"));
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
            one.Save(new PlayerProfile("MrSoNord"));
            two.Save(new PlayerProfile("Seat Two"));

            Assert.That(one.Load().DisplayName, Is.EqualTo("MrSoNord"));
            Assert.That(two.Load().DisplayName, Is.EqualTo("Seat Two"));
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
    }
}
