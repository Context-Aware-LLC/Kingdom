using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Emberfall.Training.Corpus
{
    /// <summary>
    /// Durable append-only episode sidecars. A row is serialized fully before the file is opened;
    /// a failed append rolls back to its original length, and reopening the writer discards an
    /// unterminated crash tail. Static readers remain strict and never repair their input.
    /// </summary>
    public sealed class JsonlEpisodeManifestWriter
    {
        public const string FileName = "episode-manifests.jsonl";
        private readonly object _gate = new object();
        private readonly HashSet<string> _matchIds = new HashSet<string>(StringComparer.Ordinal);

        public JsonlEpisodeManifestWriter(string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            Path = System.IO.Path.Combine(outputDirectory, FileName);
            if (!File.Exists(Path)) return;
            RecoverUncommittedTail(Path);

            int lineNumber = 0;
            foreach (string line in File.ReadLines(Path, Encoding.UTF8))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                EpisodeManifest manifest;
                try
                {
                    manifest = JsonSerializer.Deserialize<EpisodeManifest>(line, CorpusJson.Options)
                        ?? throw new JsonException("Manifest row decoded as null.");
                    manifest.Validate();
                }
                catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is NotSupportedException)
                {
                    throw new InvalidDataException($"Invalid episode manifest at {Path}:{lineNumber}: {ex.Message}", ex);
                }
                if (!_matchIds.Add(manifest.MatchId))
                    throw new InvalidDataException($"Duplicate matchId '{manifest.MatchId}' in '{Path}'.");
            }
        }

        public string Path { get; }

        public bool Contains(string matchId)
        {
            lock (_gate) return _matchIds.Contains(matchId);
        }

        public void Append(EpisodeManifest manifest)
        {
            manifest.Validate();
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, CorpusJson.Options);
            lock (_gate)
            {
                if (!_matchIds.Add(manifest.MatchId))
                    throw new InvalidOperationException($"Episode '{manifest.MatchId}' is already present in '{Path}'.");

                long committedLength = File.Exists(Path) ? new FileInfo(Path).Length : 0L;
                try
                {
                    using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read,
                                                      64 * 1024, FileOptions.WriteThrough);
                    stream.Write(json, 0, json.Length);
                    stream.WriteByte((byte)'\n');
                    stream.Flush(flushToDisk: true);
                }
                catch (Exception appendError)
                {
                    _matchIds.Remove(manifest.MatchId);
                    try
                    {
                        using var rollback = new FileStream(
                            Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
                            4096, FileOptions.WriteThrough);
                        rollback.SetLength(committedLength);
                        rollback.Flush(flushToDisk: true);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new IOException(
                            $"Manifest append failed and '{Path}' could not be rolled back.",
                            new AggregateException(appendError, rollbackError));
                    }
                    throw;
                }
            }
        }

        private static void RecoverUncommittedTail(string path)
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                64 * 1024, FileOptions.WriteThrough);
            long length = stream.Length;
            if (length == 0) return;
            stream.Position = length - 1;
            if (stream.ReadByte() == (byte)'\n') return;

            byte[] buffer = new byte[64 * 1024];
            long cursor = length;
            long committedLength = 0;
            while (cursor > 0)
            {
                int count = (int)Math.Min(buffer.Length, cursor);
                long start = cursor - count;
                stream.Position = start;
                int read = stream.Read(buffer, 0, count);
                for (int i = read - 1; i >= 0; i--)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    committedLength = start + i + 1;
                    cursor = 0;
                    break;
                }
                if (committedLength == 0) cursor = start;
            }

            stream.SetLength(committedLength);
            stream.Flush(flushToDisk: true);
        }

        public static IEnumerable<EpisodeManifest> Read(string path)
        {
            int lineNumber = 0;
            foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                EpisodeManifest manifest;
                try
                {
                    manifest = JsonSerializer.Deserialize<EpisodeManifest>(line, CorpusJson.Options)
                        ?? throw new JsonException("Manifest row decoded as null.");
                    manifest.Validate();
                }
                catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is NotSupportedException)
                {
                    throw new InvalidDataException($"Invalid episode manifest at {path}:{lineNumber}: {ex.Message}", ex);
                }
                yield return manifest;
            }
        }

        public static IReadOnlyList<EpisodeManifest> ReadAll(string path)
        {
            var result = new List<EpisodeManifest>();
            foreach (EpisodeManifest manifest in Read(path)) result.Add(manifest);
            return result;
        }
    }
}
