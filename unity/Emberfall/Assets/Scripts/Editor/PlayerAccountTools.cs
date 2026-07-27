using System;
using System.IO;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Presentation;
using Emberfall.Session;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Editor tools for the player account — the profile + character roster + decks that
    /// <see cref="PlayerAccount"/> writes under <c>Application.persistentDataPath</c>.
    ///
    /// <para><b>Why these exist.</b> A collection is a few files in a folder no player ever opens,
    /// written by code that runs once on first launch and then never again. "Show" is the only way to
    /// answer "did that actually save?" without hunting through <c>%userprofile%/AppData/LocalLow</c>;
    /// "Self-Check" is the only way to exercise creation and the delete cascade without spending this
    /// machine's real first run, which happens exactly once.</para>
    ///
    /// <para>Self-Check runs entirely in a temp directory and never touches the real account — the
    /// reason <see cref="PlayerAccount"/> keeps a public constructor taking a root and a key.</para>
    /// </summary>
    public static class PlayerAccountTools
    {
        private const string ProbeKey = "self-check-device-token";

        [MenuItem("Emberfall/Player Account/Show This Device's Account")]
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
            report.AppendLine($"  name       : {(account.NeedsDisplayName ? "(never set)" : account.Profile.DisplayName)}");

            var roster = account.Roster();
            var decks = account.Decks();
            report.AppendLine($"  characters : {roster.Count}");
            report.AppendLine($"  decks      : {decks.Count}");
            foreach (Character c in roster)
                report.AppendLine($"    · {c.Name} ({c.PrimaryClass}/{c.SecondaryClass}) " +
                                  $"id={c.Id} default={(string.IsNullOrEmpty(c.DefaultDeckId) ? "(none)" : c.DefaultDeckId)}");
            foreach (DeckList d in decks)
                report.AppendLine($"    · deck '{d.Name}' {d.TotalCount}/30 character={d.CharacterId} id={d.Id}");

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
        /// Exercise the whole account path in a scratch directory: name refusal, name normalization,
        /// character creation with a generated starter deck, queue eligibility, and the delete cascade.
        /// Logs an error and stops at the first thing that is not what the code claims.
        /// </summary>
        [MenuItem("Emberfall/Player Account/Self-Check (scratch directory)")]
        public static void SelfCheck()
        {
            string root = Path.Combine(Path.GetTempPath(), "emberfall-account-selfcheck");
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

                if (!Check(log, account.NeedsDisplayName, "a fresh account needs a name")) return;
                if (!Check(log, account.DisplayNameOr("Unnamed player") == "Unnamed player",
                           "an unnamed account renders the fallback")) return;

                NameCheck refused = account.SetDisplayName("ab");
                log.AppendLine($"  refusal for 'ab' → \"{refused.Reason}\"");
                if (!Check(log, !refused.IsValid, "a two-character name is refused")) return;
                if (!Check(log, !File.Exists(CollectionPaths.Profile(root, ProbeKey)),
                           "a refused name writes no file")) return;

                NameCheck accepted = account.SetDisplayName("  Sam   Ember ");
                log.AppendLine($"  stored name = \"{accepted.Normalized}\"");
                if (!Check(log, accepted.IsValid && accepted.Normalized == "Sam Ember",
                           "whitespace is collapsed rather than refused")) return;

                account.ReloadProfile();
                if (!Check(log, account.Profile.DisplayName == "Sam Ember", "the name survives a reload")) return;

                ICardDatabase db = CardDatabaseLoader.Load();

                if (!Check(log, !account.OffersStarterChoice(),
                           "the FIRST character is never offered the blank-deck choice")) return;

                const CardClass primary = CardClass.EMBERWARD;
                const CardClass secondary = CardClass.TIDECALLER;
                if (!Check(log, PlayerAccount.CanCreate(primary, secondary, db),
                           $"{primary}/{secondary} can fill a legal 30")) return;
                if (!Check(log, !PlayerAccount.CanCreate(primary, primary, db),
                           "a class cannot be paired with itself")) return;

                Character created = account.CreateCharacter("Ashen Vow", primary, secondary, db);
                log.AppendLine($"  created '{created.Name}' ({created.PrimaryClass}/{created.SecondaryClass})");
                log.AppendLine($"    character id = {created.Id}");
                log.AppendLine($"    default deck = {created.DefaultDeckId}");
                if (!Check(log, !string.IsNullOrEmpty(created.Id) && !string.IsNullOrEmpty(created.DefaultDeckId),
                           "creation minted both ids")) return;

                CharacterDecks decks = account.DecksOf(created, db);
                DeckSummary? opening = decks.Opening;
                if (!Check(log, decks.Count == 1 && decks.DefaultResolved && opening.HasValue,
                           "the new character opens on its resolved starter deck")) return;
                log.AppendLine($"    starter deck = {opening.Value.TotalCount}/30, " +
                               $"queue-eligible={opening.Value.Queue.IsEligible} ({opening.Value.Queue.Reason})");
                if (!Check(log, opening.Value.TotalCount == 30 && opening.Value.Queue.IsEligible,
                           "the generated starter is a queue-eligible 30")) return;

                if (!Check(log, account.OffersStarterChoice(),
                           "the SECOND character onward is offered the choice")) return;

                foreach (string f in Directory.GetFiles(account.ScopeDirectory))
                    log.AppendLine($"  wrote {Path.GetFileName(f)} ({new FileInfo(f).Length} bytes)");

                int destroyed = account.DeleteCharacter(created.Id);
                log.AppendLine($"  delete cascade destroyed {destroyed} deck(s)");
                if (!Check(log, destroyed == 1 && account.Roster().Count == 0 && account.Decks().Count == 0,
                           "deleting a character takes its decks with it")) return;

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
