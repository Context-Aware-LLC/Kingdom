using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Emberfall.Net.Server.Data
{
    /// <summary>
    /// A content digest over the `*.cards.json` files the server actually loaded, reported on
    /// `/healthz` so a stale deployment is DETECTABLE rather than silent.
    ///
    /// WHY THIS EXISTS. The corpus is baked into the image (`Dockerfile.server` COPYs
    /// `unity/Assets/CardData/` to `/app/cards/`), frozen at `fly deploy`. On
    /// 2026-08-28 the live server resolved a four-day-old copy: card 1067 printed
    /// "Summon a 1/1 Gear Wisp" and summoned nothing, because the deployed rules said
    /// ON_DEATH/DRAW 2. It was not an error at any point — the card simply behaved like an
    /// older version of itself. Naming the corpus is what converts that into a mismatch
    /// somebody can see. See `docs/dev-docs/decisions/ADR-0008-card-corpus-delivery.md`.
    ///
    /// THE ALGORITHM IS MIRRORED IN PYTHON and the two must agree byte for byte —
    /// `corpus_digest()` in `tooling/card-admin/build_data.py` is what compares this value
    /// against the working tree and triggers a deploy. It hashes RAW FILE BYTES, never parsed
    /// rows: a digest over deserialized objects would have to keep two serializers in step,
    /// which is the drift generator this is here to remove. Keep the two in sync or the
    /// comparison silently reports "behind" forever and redeploys on every run.
    ///
    /// RETIREMENT TRIGGER: when ADR-0008 step 1 lands (the corpus becomes one immutable
    /// versioned artifact in `corpus_version`, published rather than baked), the published
    /// digest replaces this computed one. Delete this class, the `corpus` field in
    /// `ServerApp`'s `/healthz`, and the deploy half of `build_data.py` in that commit.
    /// </summary>
    public static class CorpusDigest
    {
        /// <summary>Digest length in hex characters. Long enough that a collision is not a
        /// practical concern, short enough to read off a terminal and compare by eye.</summary>
        public const int Length = 16;

        /// <summary>The digest a directory with no card files yields. Distinct from any real
        /// digest so "no corpus" never reads as "some corpus".</summary>
        public const string Empty = "0000000000000000";

        /// <summary>
        /// Digest every `*.cards.json` under <paramref name="dir"/>, recursively — the same
        /// enumeration <see cref="FileCardDatabase.LoadDirectory"/> performs, so the digest
        /// covers exactly the rows the server is serving and cannot describe a different set.
        /// </summary>
        public static string Compute(string dir)
        {
            if (!Directory.Exists(dir)) return Empty;

            var files = Directory.GetFiles(dir, "*.cards.json", SearchOption.AllDirectories);
            if (files.Length == 0) return Empty;

            // Sort on the RELATIVE path, not the absolute one: the container loads from
            // /app/cards and the working tree from unity/Assets/CardData, so an
            // absolute sort could order the same corpus two different ways on the two sides.
            var entries = new List<KeyValuePair<string, string>>(files.Length);
            foreach (var f in files)
                entries.Add(new KeyValuePair<string, string>(Relative(dir, f), f));
            entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            using var running = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var entry in entries)
            {
                // "<relative path>\n<sha256 of the file's bytes>\n" per file. Feeding the path
                // in as well means a RENAME moves the digest — a set file that changed name is
                // a different corpus even when every byte of card text is untouched.
                running.AppendData(Encoding.UTF8.GetBytes(entry.Key));
                running.AppendData(Newline);
                running.AppendData(Encoding.UTF8.GetBytes(HexOfFile(entry.Value)));
                running.AppendData(Newline);
            }

            return Hex(running.GetHashAndReset()).Substring(0, Length);
        }

        private static readonly byte[] Newline = { (byte)'\n' };

        /// <summary>Path relative to <paramref name="root"/>, forward-slashed. Windows authoring
        /// and a Linux container must produce the same string for the same file.</summary>
        private static string Relative(string root, string file)
        {
            string rel = Path.GetRelativePath(root, file);
            return rel.Replace('\\', '/');
        }

        /// <summary>SHA-256 of a file's bytes, read as BYTES. Reading as text would decode and
        /// re-encode, and a BOM or a line ending would move the digest on one side only.</summary>
        private static string HexOfFile(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Hex(sha.ComputeHash(stream));
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
