using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kingdom.Training.Corpus
{
    internal static class CorpusJson
    {
        public static JsonSerializerOptions Options { get; } = Create();

        private static JsonSerializerOptions Create()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
