using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>
    /// EF-080c stub feed. Reads store transactions from a JSONL file until EF-080d lands a real
    /// platform adapter. Each non-blank line is one JSON object with a <c>kind</c> field:
    ///
    /// <code>
    /// {"kind":"purchase","txn_id":"txn-001","player_id":"p-aaa"}
    /// {"kind":"refund","refund_id":"ref-001","original_txn_id":"txn-001","player_id":"p-aaa"}
    /// </code>
    ///
    /// Lines starting with <c>//</c> are treated as comments and skipped.
    ///
    /// <para><b>RETIREMENT TRIGGER</b>: when EF-080d lands, replace this class with the real adapter
    /// and remove the <c>-- path/to/…</c> arg from the <c>StoreReconcile</c> usage docs.</para>
    /// </summary>
    public static class StoreTransactionFeed
    {
        public static IReadOnlyList<StoreTransaction> LoadJsonl(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));

            string[] lines = File.ReadAllLines(path);
            var result = new List<StoreTransaction>(lines.Length);

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(trimmed); }
                catch (JsonException ex)
                {
                    throw new FormatException($"Line {i + 1}: invalid JSON — {ex.Message}", ex);
                }

                using (doc)
                {
                    JsonElement root = doc.RootElement;
                    string kind = GetRequired(root, "kind", i + 1);

                    if (string.Equals(kind, "purchase", StringComparison.OrdinalIgnoreCase))
                    {
                        string txnId = GetRequired(root, "txn_id", i + 1);
                        string playerId = GetRequired(root, "player_id", i + 1);
                        result.Add(new StoreTransaction(
                            StoreTransactionKind.Purchase, txnId, "", playerId));
                    }
                    else if (string.Equals(kind, "refund", StringComparison.OrdinalIgnoreCase))
                    {
                        string refundId = GetRequired(root, "refund_id", i + 1);
                        string originalTxnId = GetRequired(root, "original_txn_id", i + 1);
                        string playerId = GetRequired(root, "player_id", i + 1);
                        result.Add(new StoreTransaction(
                            StoreTransactionKind.Refund, refundId, originalTxnId, playerId));
                    }
                    else
                    {
                        throw new FormatException(
                            $"Line {i + 1}: unknown kind '{kind}'; expected 'purchase' or 'refund'.");
                    }
                }
            }

            return result.AsReadOnly();
        }

        private static string GetRequired(JsonElement root, string propertyName, int lineNumber)
        {
            if (!root.TryGetProperty(propertyName, out JsonElement el) ||
                el.ValueKind != JsonValueKind.String)
                throw new FormatException(
                    $"Line {lineNumber}: required string property '{propertyName}' is missing or not a string.");
            string? value = el.GetString();
            if (string.IsNullOrWhiteSpace(value))
                throw new FormatException(
                    $"Line {lineNumber}: property '{propertyName}' must not be blank.");
            return value;
        }
    }
}
