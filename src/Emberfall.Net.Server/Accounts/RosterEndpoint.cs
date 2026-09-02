using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>What <c>GET /roster</c> answers with. Same three-status shape as
    /// <see cref="SessionResultStatus"/>, with <see cref="Known"/> in place of Ready.</summary>
    public enum RosterResultStatus
    {
        /// <summary>The account resolved and the roster was read. An empty roster is still
        /// <see cref="Known"/> — that is the whole point of the status existing.</summary>
        Known,

        /// <summary>No bearer token, or one Supabase refused. 401.</summary>
        Unauthenticated,

        /// <summary>The box has no player data store. Genuinely cannot answer. 503 — the client
        /// must never read this as "this player has no characters".</summary>
        Unavailable,
    }

    /// <summary>The roster read's answer, before it becomes JSON.</summary>
    public sealed class RosterResult
    {
        public RosterResultStatus Status { get; }

        /// <summary>Meaningful only when <see cref="Status"/> is <see cref="RosterResultStatus.Known"/>.
        /// <see cref="PlayerRoster.Empty"/> on every other status is a placeholder, NOT an answer.</summary>
        public PlayerRoster Roster { get; }

        public RosterResult(RosterResultStatus status, PlayerRoster? roster = null)
        {
            Status = status;
            Roster = roster ?? PlayerRoster.Empty;
        }

        public int HttpStatus => Status switch
        {
            RosterResultStatus.Known => 200,
            RosterResultStatus.Unauthenticated => 401,
            RosterResultStatus.Unavailable => 503,
            _ => 500,
        };
    }

    /// <summary>
    /// <c>GET /roster</c> — the characters and decks read down after the login handshake. Spec:
    /// <c>docs/dev-docs/specs/login-handshake.md</c>.
    ///
    /// <para>Resolves the account exactly as <see cref="SessionEndpoint"/> does — verifier, then
    /// <c>CredentialHash.Of(identity.Subject)</c>, then <c>ResolveOrCreateAsync</c> — then reads the
    /// player-data store keyed on that account's <c>PlayerId</c>. Does NOT fire the onboarding
    /// grant: <see cref="SessionEndpoint"/> already owns that call site and a second one here would
    /// be noise.</para>
    /// </summary>
    public static class RosterEndpoint
    {
        public static async Task<RosterResult> ReadAsync(
            string? bearerToken,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accountStore,
            IPlayerDataStore? playerDataStore,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearerToken) || verifier is null)
                return new RosterResult(RosterResultStatus.Unauthenticated);

            var verified = await verifier.VerifyAsync(bearerToken!, ct).ConfigureAwait(false);
            if (!verified.IsSuccess)
                return new RosterResult(RosterResultStatus.Unauthenticated);

            if (accountStore is null)
                return new RosterResult(RosterResultStatus.Unavailable);

            var identity = verified.Identity!;
            Account account = await accountStore.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(identity.Subject), identity.Email, ct).ConfigureAwait(false);

            if (playerDataStore is null)
                return new RosterResult(RosterResultStatus.Unavailable);

            PlayerRoster roster = await playerDataStore.ReadAsync(account.PlayerId, ct).ConfigureAwait(false);
            return new RosterResult(RosterResultStatus.Known, roster);
        }
    }
}
