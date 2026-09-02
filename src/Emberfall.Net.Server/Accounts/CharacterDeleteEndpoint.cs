using System.Threading;
using System.Threading.Tasks;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>What <c>DELETE /characters/{id}</c> answers with.</summary>
    public enum CharacterDeleteStatus
    {
        /// <summary>The character and every deck bound to it were removed. 200.</summary>
        Deleted,

        /// <summary>This player has no such character. 404.</summary>
        NotFound,

        /// <summary>No bearer token, or one Supabase refused. 401.</summary>
        Unauthenticated,

        /// <summary>The box has no account store or player data store. Genuinely cannot answer. 503.</summary>
        Unavailable,

        /// <summary>The character id was null, empty or whitespace. 400. Refused before the store is
        /// touched, for the same reason the write routes refuse one: an id nothing can address is
        /// not a request the server can attribute.</summary>
        InvalidId,
    }

    /// <summary>The character-delete answer, before it becomes JSON.</summary>
    public sealed class CharacterDeleteResult
    {
        public CharacterDeleteStatus Status { get; }

        public CharacterDeleteResult(CharacterDeleteStatus status)
        {
            Status = status;
        }

        public int HttpStatus => Status switch
        {
            CharacterDeleteStatus.Deleted => 200,
            CharacterDeleteStatus.NotFound => 404,
            CharacterDeleteStatus.Unauthenticated => 401,
            CharacterDeleteStatus.Unavailable => 503,
            CharacterDeleteStatus.InvalidId => 400,
            _ => 500,
        };
    }

    /// <summary>
    /// <c>DELETE /characters/{id}</c>. Spec: <c>docs/dev-docs/specs/login-handshake.md</c>.
    ///
    /// <para><b>Why this exists.</b> Every other piece of roster state had a write path and this one
    /// did not: <c>POST /characters</c> created, <c>PUT</c>/<c>DELETE /decks/{id}</c> maintained the
    /// decks, and a character the player deleted on the device stayed on the server forever — so the
    /// next <c>GET /roster</c> read it straight back down, along with the decks the client had
    /// already destroyed locally. A delete that resurrects is worse than no delete, because the
    /// player watched it work.</para>
    ///
    /// <para><b>Deleting a character deletes its decks</b> (ruling, 2026-07-26): a deck cannot be
    /// played without its character, so keeping one preserves something unusable. The client already
    /// does this locally through <c>CharacterDeletion</c> and names the count in its confirm dialog;
    /// the store does it in one atomic unit so the two halves cannot come apart. Nothing here judges
    /// the decks — the same posture as <see cref="DeckWriteEndpoint"/>.</para>
    ///
    /// <para>Resolves the account exactly as <see cref="SessionEndpoint"/>,
    /// <see cref="RosterEndpoint"/>, <see cref="CharacterCreateEndpoint"/> and
    /// <see cref="DeckWriteEndpoint"/> do — verifier, then <c>CredentialHash.Of(identity.Subject)</c>,
    /// then <c>ResolveOrCreateAsync</c> — and the store scopes the delete to that
    /// <c>PlayerId</c>, so an id another player owns answers 404 rather than deleting their row.</para>
    /// </summary>
    public static class CharacterDeleteEndpoint
    {
        public static async Task<CharacterDeleteResult> DeleteAsync(
            string? bearerToken,
            string characterId,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accountStore,
            IPlayerDataStore? playerDataStore,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearerToken) || verifier is null)
                return new CharacterDeleteResult(CharacterDeleteStatus.Unauthenticated);

            var verified = await verifier.VerifyAsync(bearerToken!, ct).ConfigureAwait(false);
            if (!verified.IsSuccess)
                return new CharacterDeleteResult(CharacterDeleteStatus.Unauthenticated);

            if (string.IsNullOrWhiteSpace(characterId))
                return new CharacterDeleteResult(CharacterDeleteStatus.InvalidId);

            if (accountStore is null)
                return new CharacterDeleteResult(CharacterDeleteStatus.Unavailable);

            var identity = verified.Identity!;
            Account account = await accountStore.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(identity.Subject), identity.Email, ct).ConfigureAwait(false);

            if (playerDataStore is null)
                return new CharacterDeleteResult(CharacterDeleteStatus.Unavailable);

            bool removed = await playerDataStore
                .DeleteCharacterAsync(account.PlayerId, characterId, ct).ConfigureAwait(false);

            return new CharacterDeleteResult(
                removed ? CharacterDeleteStatus.Deleted : CharacterDeleteStatus.NotFound);
        }
    }
}
