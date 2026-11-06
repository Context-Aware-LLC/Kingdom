using System;
using System.Collections.Generic;
using System.IO;
using Kingdom.Core.Collection;
using Kingdom.Core.Contracts;
using Kingdom.Presentation;
using Kingdom.Session;
using UnityEditor;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>
    /// Editor tools for the player account — the profile + decks that
    /// <see cref="PlayerAccount"/> writes under <c>Application.persistentDataPath</c>.
    ///
    /// <para><b>Why these exist.</b> A collection is a few files in a folder no player ever opens,
    /// written by code that runs once on first launch and then never again. "Show" is the only way to
    /// answer "did that actually save?" without hunting through <c>%userprofile%/AppData/LocalLow</c>;
    /// "Self-Check" is the only way to exercise creation and deletion without spending this
    /// machine's real first run, which happens exactly once.</para>
    ///
    /// <para>Self-Check runs entirely in a temp directory and never touches the real account — the
    /// reason <see cref="PlayerAccount"/> keeps a public constructor taking a root and a key.</para>
    /// </summary>
    public static class PlayerAccountTools
    {
        private const string ProbeKey = "self-check-device-token";

        /// <summary>
        /// Finish a Google (or mailed-link) sign-in in the Editor. Windows has no handler for
        /// <c>emberfall://</c>, so the browser stops at "the scheme does not have a registered
        /// handler" instead of returning to the game. Copy that <c>emberfall://auth?code=...</c> URL,
        /// then run this while the sign-in screen is up in Play mode. The code is single-use and
        /// pairs with the most recent Google button press, so use the URL from that attempt.
        /// </summary>
        [MenuItem("Kingdom/Dev/Account/Deliver Deep Link From Clipboard")]
        public static void DeliverDeepLinkFromClipboard()
        {
            string url = (GUIUtility.systemCopyBuffer ?? "").Trim().Trim('\'', '"');
            if (!url.StartsWith("emberfall://", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning("[SignIn] The clipboard does not hold an emberfall:// link. Copy the " +
                                 "URL from the browser's error, then run this again.");
                return;
            }
            if (!EditorApplication.isPlaying ||
                !SignInGateController.EditorDeliverDeepLink(url))
            {
                Debug.LogWarning("[SignIn] No sign-in screen is listening. Enter Play mode on the " +
                                 "sign-in screen first.");
                return;
            }
            Debug.Log("[SignIn] Delivered the deep link from the clipboard.");
        }

        [MenuItem("Kingdom/Dev/Account/Show This Device's Account")]
        public static void ShowAccount()
        {
            PlayerAccount account;
            try
            {
                account = PlayerAccount.Local;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PlayerAccount] could not open this device's collection — {ex.GetType().Name}: {ex.Message}");
                return;
            }

            var report = new System.Text.StringBuilder();
            report.AppendLine("[PlayerAccount] this device");
            AppendAccountRegistry(report);
            report.AppendLine($"  scope      : {account.ScopeDirectory}");
            report.AppendLine($"  exists     : {Directory.Exists(account.ScopeDirectory)}");
            report.AppendLine($"  name       : {AuthGateway.DisplayNameOr("(none on the identity)")}");

            var decks = account.Decks();
            report.AppendLine($"  decks      : {decks.Count} / {DeckCreation.MaxPerAccount}");
            foreach (DeckList d in decks)
                report.AppendLine($"    · '{d.Name}' ({d.PrimaryClass}/{d.SecondaryClass}) " +
                                  $"{d.TotalCount}/30 id={d.Id}");

            // The token itself is deliberately NOT printed: it is a bearer identity, and a console log
            // is the easiest thing in the project to paste into a bug report.
            Debug.Log(report.ToString());
        }

        /// <summary>
        /// The device's accounts and which one is signed in (<see cref="DeviceAccounts"/>). Prints ids,
        /// never wire tokens — those are bearer credentials, and a console log is the easiest thing in
        /// the project to paste into a bug report. "grandfathered" means id and token are the same
        /// value, which is true of the first account and only of that one.
        /// </summary>
        private static void AppendAccountRegistry(System.Text.StringBuilder report)
        {
            try
            {
                var all = DeviceAccounts.All();
                string activeId = DeviceAccounts.Active.Id;
                report.AppendLine($"  accounts   : {all.Count} on this device");
                for (int i = 0; i < all.Count; i++)
                {
                    LocalAccount a = all[i];
                    report.AppendLine($"    {(a.Id == activeId ? "→" : " ")} {i + 1}. " +
                                      $"{DeviceAccounts.LabelOf(a, i)}  id={a.Id}" +
                                      $"{(a.IsGrandfathered ? "  (grandfathered)" : "")}");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"  accounts   : UNREADABLE — {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Exercise the whole account path in a scratch directory: deck creation with a generated
        /// starter, queue eligibility, and deletion. Logs an error and stops at the first thing that
        /// is not what the code claims.
        ///
        /// <para><b>It never reaches the server.</b> A real write waits for the server before it
        /// touches the cache; this writes the scratch cache directly, standing in for an accepted
        /// write, so it can run signed out and cannot touch anybody's real roster.</para>
        ///
        /// <para>Deleting the LAST deck succeeds and leaves an empty account (ADR-0009); the old
        /// roster refused it.</para>
        /// </summary>
        [MenuItem("Kingdom/Verify/Player Account Self-Check")]
        public static void SelfCheck()
        {
            string root = Path.Combine(Path.GetTempPath(), "kingdom-account-selfcheck");
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (IOException ex)
            {
                Debug.LogError($"[SelfCheck] could not clear {root}: {ex.Message}");
                return;
            }

            var log = new System.Text.StringBuilder();
            log.AppendLine($"[SelfCheck] scratch root = {root}");

            try
            {
                var account = new PlayerAccount(root, ProbeKey);
                IDeckStore cache = JsonDeckStore.InDirectory(root, ProbeKey);   // the file the account reads

                ICardDatabase db = CardDatabaseLoader.Load();

                if (!Check(log, !account.OffersStarterChoice(),
                           "the FIRST deck is never offered the blank-deck choice")) return;
                if (!Check(log, account.CanCreateDeck(), "an empty account has room for a deck")) return;

                const CardClass primary = CardClass.EMBERWARD;
                const CardClass secondary = CardClass.TIDECALLER;
                if (!Check(log, PlayerAccount.CanCreate(primary, secondary, db),
                           $"{primary}/{secondary} can fill a legal 30")) return;
                if (!Check(log, !PlayerAccount.CanCreate(primary, primary, db),
                           "a class cannot be paired with itself")) return;

                // A scratch account has no ownership record, and creation must refuse rather than
                // generate from the whole pool. Then give it the superuser record, the one any
                // pairing can fill, the way a sync would (CollectionGateway is the real writer).
                bool refused = false;
                try { account.PrepareDeck(db, "Ashen Vow", primary, secondary, StarterDeck.Generated); }
                catch (OwnershipUnknownException) { refused = true; }
                if (!Check(log, refused, "with no ownership record, creation refuses")) return;
                account.SaveOwnership(OwnershipRecord.AllAtLimit);

                DeckList created = account.PrepareDeck(db, "Ashen Vow", primary, secondary,
                                                       StarterDeck.Generated);
                if (!Check(log, account.Decks().Count == 0,
                           "a prepared deck is in no cache until the server accepts it")) return;
                cache.Save(created);
                log.AppendLine($"  created '{created.Name}' ({created.PrimaryClass}/{created.SecondaryClass})");
                log.AppendLine($"    deck id = {created.Id}");
                if (!Check(log, !string.IsNullOrEmpty(created.Id), "creation minted an id")) return;

                IReadOnlyList<DeckSummary> rows = account.DeckRows(db, "no ownership record");
                if (!Check(log, rows.Count == 1, "the account holds exactly the deck just made")) return;
                DeckSummary row = rows[0];
                log.AppendLine($"    starter deck = {row.TotalCount}/30, " +
                               $"queue-eligible={row.Queue.IsEligible} ({row.Queue.Reason})");
                if (!Check(log, row.TotalCount == 30 && row.Queue.IsEligible,
                           "the generated starter is a queue-eligible 30")) return;

                if (!Check(log, account.OffersStarterChoice(),
                           "the SECOND deck onward is offered the choice")) return;

                foreach (string f in Directory.GetFiles(account.ScopeDirectory))
                    log.AppendLine($"  wrote {Path.GetFileName(f)} ({new FileInfo(f).Length} bytes)");

                cache.Delete(created.Id);
                if (!Check(log, account.Decks().Count == 0,
                           "deleting the last deck leaves an empty account, not a refusal")) return;

                log.AppendLine("[SelfCheck] PASS");
                Debug.Log(log.ToString());
            }
            catch (Exception ex)
            {
                log.AppendLine($"[SelfCheck] THREW — {ex.GetType().Name}: {ex.Message}");
                Debug.LogError(log.ToString());
            }
        }

        /// <summary>Records the assertion either way; a failure logs everything gathered so far and returns false.</summary>
        private static bool Check(System.Text.StringBuilder log, bool condition, string what)
        {
            log.AppendLine($"  [{(condition ? "ok" : "FAIL")}] {what}");
            if (!condition)
            {
                log.AppendLine("[SelfCheck] FAILED");
                Debug.LogError(log.ToString());
            }
            return condition;
        }
    }
}
