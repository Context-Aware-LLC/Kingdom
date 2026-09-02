using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Core.Contracts;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>What <c>POST /characters</c> answers with.</summary>
    public enum CharacterCreateStatus
    {
        /// <summary>The character was created and saved.</summary>
        Created,

        /// <summary>No bearer token, or one Supabase refused. 401.</summary>
        Unauthenticated,

        /// <summary>The box has no account store or player data store. Genuinely cannot answer. 503.</summary>
        Unavailable,

        /// <summary>Fails <see cref="Character.IsValidPairing"/> — a NEUTRAL class or the same
        /// class twice. 400.</summary>
        InvalidPairing,

        /// <summary>This player already has a character with this id. Refused, never silently
        /// overwritten — the pairing is a permanent identity decision with no legal repair, and an
        /// overwrite would BE a repair. 409.</summary>
        DuplicateId,
    }

    /// <summary>The character-create answer, before it becomes JSON.</summary>
    public sealed class CharacterCreateResult
    {
        public CharacterCreateStatus Status { get; }

        public CharacterCreateResult(CharacterCreateStatus status)
        {
            Status = status;
        }

        public int HttpStatus => Status switch
        {
            CharacterCreateStatus.Created => 201,
            CharacterCreateStatus.Unauthenticated => 401,
            CharacterCreateStatus.Unavailable => 503,
            CharacterCreateStatus.InvalidPairing => 400,
            CharacterCreateStatus.DuplicateId => 409,
            _ => 500,
        };
    }

    /// <summary>
    /// <c>POST /characters</c> — creates a character. Spec: <c>docs/dev-docs/specs/login-handshake.md</c>.
    ///
    /// <para>Resolves the account exactly as <see cref="SessionEndpoint"/> and
    /// <see cref="RosterEndpoint"/> do — verifier, then <c>CredentialHash.Of(identity.Subject)</c>,
    /// then <c>ResolveOrCreateAsync</c>. Refuses an invalid pairing via the ONLY pairing check,
    /// <see cref="Character.IsValidPairing"/>, before touching any store. Refuses a duplicate id by
    /// reading the roster first and checking for it — <see cref="IPlayerDataStore.SaveCharacterAsync"/>
    /// is an upsert with no duplicate detection of its own, so refusing one is this endpoint's job.
    /// </para>
    ///
    /// <para><c>DefaultDeckId</c> is <c>""</c> at creation; the client sets it once it has generated
    /// and saved a deck. Does NOT fire the onboarding grant (Task 1's <see cref="SessionEndpoint"/>
    /// owns that call site) and does NOT set <c>Onboarded</c> — that flips at the end of the
    /// onboarding flow, after the entitlement grant lands, never at character creation.</para>
    /// </summary>
    public static class CharacterCreateEndpoint
    {
        public static async Task<CharacterCreateResult> CreateAsync(
            string? bearerToken,
            string characterId,
            string name,
            CardClass primary,
            CardClass secondary,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accountStore,
            IPlayerDataStore? playerDataStore,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearerToken) || verifier is null)
                return new CharacterCreateResult(CharacterCreateStatus.Unauthenticated);

            var verified = await verifier.VerifyAsync(bearerToken!, ct).ConfigureAwait(false);
            if (!verified.IsSuccess)
                return new CharacterCreateResult(CharacterCreateStatus.Unauthenticated);

            if (!Character.IsValidPairing(primary, secondary))
                return new CharacterCreateResult(CharacterCreateStatus.InvalidPairing);

            if (accountStore is null)
                return new CharacterCreateResult(CharacterCreateStatus.Unavailable);

            var identity = verified.Identity!;
            Account account = await accountStore.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(identity.Subject), identity.Email, ct).ConfigureAwait(false);

            if (playerDataStore is null)
                return new CharacterCreateResult(CharacterCreateStatus.Unavailable);

            PlayerRoster roster = await playerDataStore.ReadAsync(account.PlayerId, ct).ConfigureAwait(false);
            foreach (var existing in roster.Characters)
            {
                if (existing.Id == characterId)
                    return new CharacterCreateResult(CharacterCreateStatus.DuplicateId);
            }

            var character = new Character(characterId, name, primary, secondary);
            await playerDataStore.SaveCharacterAsync(account.PlayerId, character, ct).ConfigureAwait(false);

            return new CharacterCreateResult(CharacterCreateStatus.Created);
        }
    }
}
