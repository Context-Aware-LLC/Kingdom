using System;
using System.Collections.Generic;

namespace Kingdom.Training.Corpus
{
    /// <summary>The path-independent integrity index for one materialized corpus. File names are
    /// labels relative to their respective raw or materialized directory; hashes carry identity.</summary>
    public sealed record DatasetManifest(
        int SchemaVersion,
        int EpisodeSchemaVersion,
        string SourceManifestFile,
        string SourceManifestSha256,
        string CardDataSha256,
        int EpisodeCount,
        int DecisionCount,
        IReadOnlyList<DatasetShard> Shards)
    {
        public const int CurrentSchemaVersion = 3;
    }

    public sealed record DatasetShard(string File, int Records, string Sha256);
}
