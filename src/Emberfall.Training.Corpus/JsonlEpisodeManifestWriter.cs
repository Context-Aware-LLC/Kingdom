using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Emberfall.Training.Corpus
{
    /// <summary>
    /// Durable append-only episode sidecars. A row is serialized fully before the file is opened;
    /// readers reject a truncated last row instead of silently materializing an incomplete corpus.
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

                try
                {
                    using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read,
                                                      64 * 1024, FileOptions.WriteThrough);
                    stream.Write(json, 0, json.Length);
                    stream.WriteByte((byte)'\n');
                    stream.Flush(flushToDisk: true);
                }
                catch
                {
                    _matchIds.Remove(manifest.MatchId);
                    throw;
                }
            }
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
