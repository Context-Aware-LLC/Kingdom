using System;
using System.Collections.Generic;
using System.IO;
using Emberfall.Core.Collection;
using UnityEngine;

namespace Emberfall.Session
{
    /// <summary>
    /// What this player has already been shown once — the beats that must not play twice.
    /// Slice 2 of <c>docs/dev-docs/specs/first-run-and-pack-opening.md</c>.
    ///
    /// <para><b>A presentation fact, not an entitlement.</b> It lives on local disk beside
    /// <c>owned-cards.json</c> rather than in the ledger because nothing here is owed to the player:
    /// its worst failure is a reinstalling player seeing a nice animation once more. Compare
    /// <c>onboarded</c> on the account, which is a server flag and is deliberately NOT what the gift
    /// routes on (nothing calls <c>POST /onboarding/complete</c>, so it is false for every account
    /// that has ever existed — #344).</para>
    ///
    /// <para><b>It never decides whether a player gets something, only whether they get a cutscene.</b>
    /// The starter-pack <i>route</i> is the ledger balance; this file picks the <i>mode</i> once you
    /// are there. Gating the route on a marker would strand four packs behind a beat that had already
    /// played — see <c>MainMenuController.BeginFirstRun</c> and §"Slice 2" of the spec.</para>
    ///
    /// <para><b>Nothing is cached.</b> The file is two lines and is read at most twice in a session,
    /// while a cache would have to be invalidated on every <see cref="DeviceAccounts.SwitchTo"/> the
    /// way <see cref="PlayerAccount.InvalidateLocal"/> is — a bug worth not being able to have. Every
    /// read resolves the scope fresh.</para>
    ///
    /// <para><b>Every failure reads as "not marked".</b> A missing file is the normal first-run state,
    /// and an unreadable one must not be able to block the gift it exists only to suppress. A write
    /// that fails is logged and swallowed for the same reason: losing the marker costs one replayed
    /// animation, and throwing out of it would cost the player their pack-opening.</para>
    /// </summary>
    public static class FirstRunMarkers
    {
        /// <summary>
        /// The gift has been seen. Written on the first pack a player OPENS, never on arrival at the
        /// vault — a player who force-quits the instant the scene loads has not had their gift yet.
        /// </summary>
        public const string Gift = "gift";

        /// <summary>
        /// <c>first-run.json</c>, in the scope directory beside <c>owned-cards.json</c> and
        /// <c>decks.json</c>.
        ///
        /// <para><b>Deliberately not in <see cref="CollectionPaths"/></b>, which is the layout of the
        /// player's <i>collection</i> — the things they own. This is a record of what they have been
        /// shown. It shares the folder because it shares the identity scope, and for no other
        /// reason.</para>
        /// </summary>
        public const string FileName = "first-run.json";

        /// <summary>True when <paramref name="marker"/> has already been written for this player.</summary>
        public static bool Has(string marker)
        {
            if (string.IsNullOrEmpty(marker)) return false;
            return Load().Contains(marker);
        }

        /// <summary>
        /// Record <paramref name="marker"/> as seen. Idempotent — re-marking rewrites the same file
        /// rather than growing it, so a caller does not have to check first.
        /// </summary>
        public static void Mark(string marker)
        {
            if (string.IsNullOrEmpty(marker)) return;

            List<string> markers = Load();
            if (markers.Contains(marker)) return;
            markers.Add(marker);

            string path = PathOrNull();
            if (path == null) return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonUtility.ToJson(new Payload { seen = markers.ToArray() }));
            }
            catch (Exception ex)
            {
                // Non-fatal by construction — see the type remarks. The player replays a beat; they
                // do not lose anything they own.
                Debug.LogWarning($"[FirstRun] could not record '{marker}' " +
                                 $"({ex.GetType().Name}: {ex.Message}).");
            }
        }

        private static List<string> Load()
        {
            string path = PathOrNull();
            if (path == null) return new List<string>();

            try
            {
                if (!File.Exists(path)) return new List<string>();
                Payload payload = JsonUtility.FromJson<Payload>(File.ReadAllText(path));
                return payload?.seen == null ? new List<string>() : new List<string>(payload.seen);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FirstRun] could not read {FileName} " +
                                 $"({ex.GetType().Name}: {ex.Message}); treating it as unmarked.");
                return new List<string>();
            }
        }

        /// <summary>
        /// The file for the signed-in player, or null when there is no resolvable one.
        ///
        /// <para><see cref="PlayerAccount.Local"/> reads Unity API and throws on a corrupt collection.
        /// Both are real: this is called from screens that may run before an account exists at all.
        /// Null means "no marker store", which every caller already handles as "not marked".</para>
        /// </summary>
        private static string PathOrNull()
        {
            try
            {
                return Path.Combine(PlayerAccount.Local.ScopeDirectory, FileName);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FirstRun] no player scope to store markers in " +
                                 $"({ex.GetType().Name}: {ex.Message}).");
                return null;
            }
        }

        /// <summary><see cref="JsonUtility"/> serializes fields on a concrete type and cannot see a
        /// collection on its own, so the set travels as an array in a wrapper.</summary>
        [Serializable]
        private sealed class Payload
        {
            public string[] seen;
        }
    }
}
