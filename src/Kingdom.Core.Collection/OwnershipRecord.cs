using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Collection
{
    /// <summary>How an <see cref="OwnershipRecord"/> says what is owned.</summary>
    public enum OwnershipRule
    {
        /// <summary>An explicit (card id, copies) list.</summary>
        List,

        /// <summary>Every pool-eligible card at its rarity limit — a superuser. Names no card ids, so a
        /// card added later is covered without the record (or its digest) changing.</summary>
        AllAtLimit,
    }

    /// <summary>
    /// What an account owns, in a form two sides can compare by <see cref="Digest"/> instead of by
    /// exchanging the collection.
    /// </summary>
    public sealed class OwnershipRecord
    {
        /// <summary>The superuser record: every pool-eligible card at its limit.</summary>
        public static readonly OwnershipRecord AllAtLimit = new OwnershipRecord(OwnershipRule.AllAtLimit, null);

        public OwnershipRule Rule { get; }

        /// <summary>The counts — non-null only for <see cref="OwnershipRule.List"/>.</summary>
        public OwnedCardCounts? Counts { get; }

        /// <summary>Canonical SHA-256 (lowercase hex) of this record; computed once.</summary>
        public string Digest { get; }

        private OwnershipRecord(OwnershipRule rule, OwnedCardCounts? counts)
        {
            Rule = rule;
            Counts = counts;
            Digest = OwnershipDigest.Compute(rule, counts);
        }

        public static OwnershipRecord List(OwnedCardCounts counts)
        {
            if (counts == null) throw new ArgumentNullException(nameof(counts));
            return new OwnershipRecord(OwnershipRule.List, counts);
        }

        /// <summary>The <see cref="IOwnedCardCounts"/> this record means against <paramref name="db"/>.</summary>
        public IOwnedCardCounts Resolve(ICardDatabase db)
            => Rule == OwnershipRule.AllAtLimit ? new AtLimitOwnedCounts(db) : (IOwnedCardCounts)Counts!;
    }

    /// <summary>The canonical text of an ownership record, hashed.</summary>
    /// <remarks>
    /// Text is UTF-8: <c>kingdom-ownership-v1\n</c>, then either <c>all-at-limit\n</c> or
    /// <c>list\n</c> followed by <c>&lt;id&gt;:&lt;copies&gt;\n</c> per row, copies &gt; 0 only, id
    /// ascending, invariant culture. Rows at zero or below are skipped because the server never sends
    /// them and the client's parse drops them, so both sides must hash the same thing.
    /// </remarks>
    public static class OwnershipDigest
    {
        public static string Of(OwnershipRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            return record.Digest;
        }

        /// <summary>True for exactly 64 lowercase hex characters.</summary>
        public static bool IsWellFormed(string? digest)
        {
            if (digest == null || digest.Length != 64) return false;
            foreach (char c in digest)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        internal static string Compute(OwnershipRule rule, OwnedCardCounts? counts)
        {
            var text = new StringBuilder("kingdom-ownership-v1\n");
            if (rule == OwnershipRule.AllAtLimit)
            {
                text.Append("all-at-limit\n");
            }
            else
            {
                text.Append("list\n");
                foreach (var row in counts!.Entries())
                {
                    if (row.Value <= 0) continue;
                    text.Append(row.Key.ToString(CultureInfo.InvariantCulture)).Append(':')
                        .Append(row.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
                }
            }

            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()));
            var hex = new StringBuilder(64);
            foreach (byte b in hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return hex.ToString();
        }
    }
}
