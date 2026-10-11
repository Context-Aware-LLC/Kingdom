using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UnityEngine;

namespace Kingdom.Editor
{
    /// <summary>
    /// Reader for the editor-comment sidecar the web authoring tool writes
    /// (<c>tooling/cardweb/comments/comments.json</c>, see <c>tooling/cardweb/comments.py</c> —
    /// that file is the authority on the format, this is the second reader of it).
    ///
    /// <para><b>Why a sidecar and not a field on the row.</b> Sam's ruling (2026-08-27) is that
    /// the comment does not have to reach cards.json — it only has to be tied to the card's id
    /// so the authoring tools can show it in the right place. <c>_comment</c> looks like the
    /// obvious home and is the worst one: it already means something else (it annotates test
    /// FIXTURE rows in M1.cards.snapshot.json), and it does not round-trip — nothing reads it
    /// into <see cref="Kingdom.Core.Cards.CardDefinition"/> and
    /// <see cref="CardJsonRowIO.Serialize"/> never writes it back, so a comment stored there
    /// survives exactly until this window's next SaveAll and is then dropped in silence.</para>
    ///
    /// <para><b>Keyed on the numeric id</b>, which is the only permanent handle. Not the
    /// handle — nothing may resolve against it (Sam, 2026-08-15) — and not the artId, which
    /// moving strands seeds.</para>
    ///
    /// <para><b>READ-ONLY, by scope.</b> Comments are written in the browser and displayed here.
    /// This class opens no file for writing, so it cannot be the thing that loses one — which
    /// matters because the deployed tool and the repo hold two copies of this log, and
    /// reconciling them is a union merge (<c>comments.py merge</c>), not a save.</para>
    /// </summary>
    public static class CardComments
    {
        /// <summary>One comment. Mirrors the record comments.py writes; unknown fields ignored.</summary>
        public readonly struct Comment
        {
            public readonly string Author;
            public readonly string At;
            public readonly string Text;
            public readonly bool Resolved;

            public Comment(string author, string at, string text, bool resolved)
            {
                Author = author;
                At = at;
                Text = text;
                Resolved = resolved;
            }
        }

        /// <summary>
        /// The sidecar's path, derived from <see cref="Application.dataPath"/> rather than from
        /// the process working directory.
        ///
        /// <para>Every other path in this window is project-relative ("Assets/CardData/...")
        /// because File.* resolves those against the Unity project root. This one is OUTSIDE the
        /// project — the tool that writes it is not a Unity thing — so it is anchored on
        /// dataPath (<c>unity/Assets</c>) and walked up to the repo root. Deriving it
        /// from the cwd instead would work in the Editor and break under any batchmode
        /// invocation that sets a different one.</para>
        /// </summary>
        public static string Path
        {
            get
            {
                // Assets -> Kingdom -> unity -> <repo root>
                var repo = new DirectoryInfo(Application.dataPath).Parent?.Parent?.Parent;
                return repo == null
                    ? null
                    : System.IO.Path.Combine(
                        repo.FullName, "tooling", "cardweb", "comments", "comments.json");
            }
        }

        /// <summary>
        /// Every comment in the sidecar, by card id. An absent file is an EMPTY result, not an
        /// error: the sidecar only exists once somebody has written a comment, and a window that
        /// logged a warning on every repaint because nobody had would be worse than useless.
        /// A file that fails to parse IS reported, once per distinct message — that is somebody's
        /// notes not showing up, and silence there looks exactly like "no comments".
        /// </summary>
        public static IReadOnlyDictionary<int, List<Comment>> Load()
        {
            var result = new Dictionary<int, List<Comment>>();
            string path = Path;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return result;

            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException e) { WarnOnce($"[CardComments] cannot read {path}: {e.Message}"); return result; }

            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    WarnOnce($"[CardComments] {path}: root is not an object keyed on card id.");
                    return result;
                }

                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (!int.TryParse(entry.Name, out int cardId)) continue;
                    if (entry.Value.ValueKind != JsonValueKind.Array) continue;

                    var list = new List<Comment>();
                    foreach (var item in entry.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        list.Add(new Comment(
                            Str(item, "author"),
                            Str(item, "at"),
                            Str(item, "text"),
                            item.TryGetProperty("resolved", out var r)
                                && r.ValueKind == JsonValueKind.True));
                    }
                    if (list.Count > 0) result[cardId] = list;
                }
            }
            catch (JsonException e)
            {
                WarnOnce($"[CardComments] {path} is not valid JSON: {e.Message}");
            }

            return result;
        }

        private static string Str(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : "";

        // Load() runs on demand from an IMGUI window, which repaints continuously. A warning
        // per repaint fills the console in seconds and buries the first one, which is the only
        // one that says anything.
        private static readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);

        private static void WarnOnce(string message)
        {
            if (_warned.Add(message)) Debug.LogWarning(message);
        }

        /// <summary>Forget which warnings have been shown, so a fixed file reports again.</summary>
        public static void ResetWarnings() => _warned.Clear();
    }
}
