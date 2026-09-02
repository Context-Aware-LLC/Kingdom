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

        /// <summary>The character id was null, empty or whitespace. A row keyed on "" is a row
        /// nothing can address again, and the store would have reported it saved. 400.</summary>
        InvalidId,

        /// <summary>The id is already taken. Either this player already has a character with it —
        /// refused, never silently overwritten, because the pairing is a permanent identity decision
        /// with no legal repair and an overwrite would BE a repair — or it belongs to a DIFFERENT
        /// player, in which case the store refused the write and the endpoint must not report a save
        /// that did not happen. 409.</summary>
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
            CharacterCreateStatus.InvalidId => 400,
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
    /// is an upsert and would silently overwrite THIS player's own row, so refusing that one is this
    /// endpoint's job. The store catches only the case the roster read cannot see, an id owned by a
    /// different player, and reports it by returning false.</para>
    ///
    /// <para><c>DefaultDeckId</c> is <c>""</c> at creation; the client sets it once it has generated
    /// and saved a deck. Does NOT fire the onboarding grant (Task 1's <see cref="SessionEndpoint"/>
    /// owns that call site) and does NOT set <c>Onboarded</c> — that flips at the end of the
    /// onboarding flow, after the entitlement grant lands, never at character creation.</para>
    /// </summary>
    public static class CharacterCreateEndpoint
    {
        /// <summary>
        /// The one place a wire class name becomes a <see cref="CardClass"/>. Rejects null/empty,
        /// an unrecognised name, AND a name that parses to a numeric value with no enum member
        /// backing it (<c>Enum.TryParse&lt;CardClass&gt;("999", true, out _)</c> returns true with
        /// an undefined value — <see cref="System.Enum.IsDefined"/> is the only thing that catches
        /// that). Lives here, not in the route, so the dotnet suite can reach it: a guard inside
        /// <c>ServerApp.cs</c> is a guard nothing in this repo's test tree can call.
        /// </summary>
        public static bool TryParseClass(string? raw, out CardClass value)
        {
            value = default;
            if (string.IsNullOrEmpty(raw))
                return false;

            if (!System.Enum.TryParse(raw, ignoreCase: true, out CardClass parsed))
                return false;

            if (!System.Enum.IsDefined(typeof(CardClass), parsed))
                return false;

            value = parsed;
            return true;
        }

        /// <summary>
        /// Route-level pre-auth. <c>POST /characters</c> is the one account route whose body has to
        /// be parsed before <see cref="CreateAsync"/> can be called at all, and a route that parses
        /// first answers 400 to an unauthenticated caller with a bad body where every other route
        /// answers 401 — and runs <c>JsonDocument.ParseAsync</c> over an unbounded body for a caller
        /// it has not authenticated. So the route asks this first.
        ///
        /// <para><see cref="CreateAsync"/> still verifies for itself: it is callable without a route,
        /// and its refusal is the one that decides the answer. This is a gate, not the check.</para>
        /// </summary>
        public static async Task<bool> IsAuthenticatedAsync(
            string? bearerToken, IIdentityTokenVerifier? verifier, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearerToken) || verifier is null)
                return false;

            var verified = await verifier.VerifyAsync(bearerToken!, ct).ConfigureAwait(false);
            return verified.IsSuccess;
        }

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

            // An id is what every later write is attributed by, so an unusable one is refused here
            // rather than stored. Nothing downstream checks it: the store would key a row on "" and
            // report it saved.
            if (string.IsNullOrWhiteSpace(characterId))
                return new CharacterCreateResult(CharacterCreateStatus.InvalidId);

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
            bool saved = await playerDataStore.SaveCharacterAsync(account.PlayerId, character, ct).ConfigureAwait(false);

            // The roster check above cannot see an id another PLAYER owns. The store can, and
            // refuses; reporting 201 for a row that was never written is the failure this returns.
            if (!saved)
                return new CharacterCreateResult(CharacterCreateStatus.DuplicateId);

            return new CharacterCreateResult(CharacterCreateStatus.Created);
        }
    }
}
