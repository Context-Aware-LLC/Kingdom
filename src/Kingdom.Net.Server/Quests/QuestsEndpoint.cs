using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kingdom.Net.Server.Accounts;
using Kingdom.Net.Server.Identity;

namespace Kingdom.Net.Server.Quests
{
    /// <summary>
    /// Why <c>GET /quests</c> answered the way it did. The client branches on this string and never
    /// on the quest count, for the same reason <c>/collection</c>'s <c>ownership</c> exists: "no
    /// quests" and "the read failed" are different answers that would otherwise look identical, and
    /// the damaging reading is the one where a broken read renders as a finished day.
    /// </summary>
    public enum QuestReadStatus
    {
        Known,
        Unauthenticated,
        Unavailable,
    }

    public sealed record QuestReadResult(
        QuestReadStatus Status,
        string? PlayerId,
        IReadOnlyList<QuestView> Quests,
        int HttpStatus);

    /// <summary>
    /// <c>GET /quests</c> — the player's active quests and their progress. Read-only by
    /// construction: there is no verb here that a client could use to move progress, because the
    /// server is the only thing that may (dev-docs/specs/daily-quests.md §2).
    /// </summary>
    public static class QuestsEndpoint
    {
        public static async Task<QuestReadResult> ReadAsync(
            string? bearer,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accounts,
            QuestProgressService? quests,
            DateOnly today,
            CancellationToken ct = default)
        {
            if (verifier is null || accounts is null || quests is null)
                return Unavailable();

            if (string.IsNullOrWhiteSpace(bearer))
                return new QuestReadResult(QuestReadStatus.Unauthenticated, null, Array.Empty<QuestView>(), 401);

            TokenVerificationResult verified;
            try
            {
                verified = await verifier.VerifyAsync(bearer!, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A verifier that could not reach its JWKS is an OUTAGE, not a rejected token. 401
                // would tell the client to sign the player out over a network blip.
                return Unavailable();
            }

            if (!verified.IsSuccess || verified.Identity is null)
                return new QuestReadResult(QuestReadStatus.Unauthenticated, null, Array.Empty<QuestView>(), 401);

            try
            {
                Account account = await accounts.ResolveOrCreateAsync(
                    "supabase",
                    CredentialHash.Of(verified.Identity!.Subject),
                    verified.Identity!.Email,
                    ct).ConfigureAwait(false);

                IReadOnlyList<QuestView> view =
                    await quests.ViewAsync(account.PlayerId, today, ct).ConfigureAwait(false);

                return new QuestReadResult(QuestReadStatus.Known, account.PlayerId, view, 200);
            }
            catch (Exception)
            {
                return Unavailable();
            }
        }

        private static QuestReadResult Unavailable()
            => new QuestReadResult(QuestReadStatus.Unavailable, null, Array.Empty<QuestView>(), 503);
    }
}
