using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kingdom.Tools.AccountDelete
{
    /// <summary>
    /// The dry run's output and the execute step's only input. Sam approves a plan by the SHA-256 of
    /// the file's exact bytes, so the file is never rewritten after <c>plan</c> writes it: execute
    /// reads it, hashes it, and refuses if the hash is not the approved one.
    ///
    /// <para><b>A company admin record, never a repo file</b> (Sam, 2026-11-02). It holds the email and
    /// ids the run removes, so <see cref="PlanFiles"/> refuses to write it inside a git work tree and
    /// execute deletes it once the deletion record is complete.</para>
    /// </summary>
    internal sealed class DeletionPlan
    {
        public const string CurrentFormat = "kingdom.account-delete.plan/1";

        public string Format { get; set; } = CurrentFormat;
        public DateTimeOffset CreatedAtUtc { get; set; }
        public string RequestMessageId { get; set; } = "";
        public DateTimeOffset RequestReceivedAtUtc { get; set; }

        /// <summary>Lower-cased, trimmed. The D1 <c>prereg</c> key and the cross-check against
        /// <c>account.email</c>.</summary>
        public string Email { get; set; } = "";

        /// <summary>The Supabase user id, which is the JWT <c>sub</c>.</summary>
        public string SupabaseUserId { get; set; } = "";

        public List<string> Providers { get; set; } = new List<string>();
        public string CredentialHash { get; set; } = "";
        public string PlayerId { get; set; } = "";

        /// <summary><c>deleted-&lt;uuid&gt;</c>, fresh per deletion and never derived from the
        /// player id, so pseudonymised rows cannot be joined back.</summary>
        public string TombstoneId { get; set; } = "";

        /// <summary>Device hashes bound to this player in telemetry when the plan was made.</summary>
        public List<string> Devices { get; set; } = new List<string>();

        /// <summary>What each step would touch, by <see cref="CountKeys"/> name.</summary>
        public SortedDictionary<string, long> Counts { get; set; } = new SortedDictionary<string, long>(StringComparer.Ordinal);

        /// <summary>The archive expiry if the run completed on the plan's date. Execute stamps the real
        /// one, from the day it completes.</summary>
        public DateOnly ArchiveExpiresOnIfRunToday { get; set; }

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

        public static DeletionPlan FromBytes(byte[] bytes)
        {
            DeletionPlan plan = JsonSerializer.Deserialize<DeletionPlan>(bytes, Json)
                ?? throw new InvalidDataException("The plan file is empty.");
            if (plan.Format != CurrentFormat)
                throw new InvalidDataException($"The plan file's format is '{plan.Format}', not '{CurrentFormat}'.");
            if (string.IsNullOrEmpty(plan.PlayerId) || string.IsNullOrEmpty(plan.SupabaseUserId)
                || string.IsNullOrEmpty(plan.TombstoneId) || string.IsNullOrEmpty(plan.RequestMessageId)
                || !plan.TombstoneId.StartsWith(TombstonePrefix, StringComparison.Ordinal))
                throw new InvalidDataException("The plan file is missing a required field.");
            return plan;
        }

        public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

        public const string TombstonePrefix = "deleted-";

        public static string NewTombstone() => TombstonePrefix + Guid.NewGuid().ToString("D");

        /// <summary>April 15 of the year after deletion, plus seven years (runbook rulings).</summary>
        public static DateOnly ArchiveExpiry(DateTimeOffset deletedAtUtc) =>
            new DateOnly(deletedAtUtc.UtcDateTime.Year + 1, 4, 15).AddYears(7);

        public static string NormalizeEmail(string email) => (email ?? "").Trim().ToLowerInvariant();

        /// <summary>The counts that must not have moved between plan and execute. Every key, in order;
        /// a key present on one side only is a difference too.</summary>
        public static IReadOnlyList<string> Differences(
            IReadOnlyDictionary<string, long> planned, IReadOnlyDictionary<string, long> now)
        {
            var diffs = new List<string>();
            foreach (string key in planned.Keys.Union(now.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                bool inPlan = planned.TryGetValue(key, out long a);
                bool inNow = now.TryGetValue(key, out long b);
                if (!inPlan || !inNow || a != b)
                    diffs.Add($"{key}: plan {(inPlan ? a.ToString() : "-")}, now {(inNow ? b.ToString() : "-")}");
            }
            return diffs;
        }
    }

    /// <summary>The names in <see cref="DeletionPlan.Counts"/>. Store first, then table, then what
    /// happens to the rows.</summary>
    internal static class CountKeys
    {
        public const string AuthUsers = "auth.users.delete";
        public const string AuthIdentities = "auth.identities.delete";

        public const string Account = "accounts.account.delete";
        public const string GrantDelete = "accounts.entitlement_grant.delete";
        public const string GrantKept = "accounts.entitlement_grant.kept";
        public const string Deck = "accounts.deck.delete";
        public const string QuestProgress = "accounts.quest_progress.delete";
        public const string CompletedMatch = "accounts.completed_match.delete";
        public const string AchievementEarned = "accounts.achievement_earned.delete";
        public const string FavorScore = "accounts.favor_score.delete";
        public const string FavorPrize = "accounts.favor_prize.delete";
        public const string KingReign = "accounts.king_reign.favorite_cleared";
        public const string LadderPlayer = "accounts.ladder_player.delete";
        public const string LadderMatch = "accounts.ladder_match.tombstone";
        public const string FriendCode = "accounts.friend_code.delete";
        public const string Friendship = "accounts.friendship.delete";
        public const string FriendBlock = "accounts.friend_block.delete";
        public const string PlayGamesLink = "accounts.play_games_link.delete";

        public const string MatchSeat = "telemetry.match_seat.tombstone";
        public const string CardSeen = "telemetry.card_seen.tombstone";
        public const string RawAccount = "telemetry.raw_event.account.delete";
        public const string RawPlayers = "telemetry.raw_event.players.delete";
        public const string RawDeviceOnly = "telemetry.raw_event.device_only.delete";
        public const string Devices = "telemetry.devices.bound";
        public const string DevicesShared = "telemetry.devices.shared_kept";

        public const string Prereg = "d1.prereg.delete";
    }
}
