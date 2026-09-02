using System.Threading;
using System.Threading.Tasks;
using Emberfall.Core.Collection;
using Emberfall.Net.Server.Identity;

namespace Emberfall.Net.Server.Accounts
{
    /// <summary>What <c>PUT /decks/{id}</c> and <c>DELETE /decks/{id}</c> answer with.</summary>
    public enum DeckWriteStatus
    {
        /// <summary>The deck was parsed and saved (an upsert). 200.</summary>
        Saved,

        /// <summary>The deck was removed. 200.</summary>
        Deleted,

        /// <summary>Delete of a deck id this player has no such deck for. 404.</summary>
        NotFound,

        /// <summary>No bearer token, or one Supabase refused. 401.</summary>
        Unauthenticated,

        /// <summary>The box has no account store or player data store. Genuinely cannot answer. 503.</summary>
        Unavailable,

        /// <summary>The body did not parse as a deck. Not a judgement on the deck's LEGALITY — an
        /// illegal deck (too few cards, an L6 violation, one gone illegal after a balance patch) is
        /// still stored. 400.</summary>
        Malformed,

        /// <summary>The deck id was null, empty or whitespace. A row keyed on "" is a row nothing
        /// can address again, and the store would have reported it saved. 400.</summary>
        InvalidId,

        /// <summary>The deck id already belongs to a DIFFERENT player, so the store refused the write
        /// and this must not report a save that did not happen. A refusal to ATTRIBUTE, not a refusal
        /// to store — the deck itself was never judged. 409.</summary>
        DuplicateId,
    }

    /// <summary>The deck-write answer, before it becomes JSON.</summary>
    public sealed class DeckWriteResult
    {
        public DeckWriteStatus Status { get; }

        public DeckWriteResult(DeckWriteStatus status)
        {
            Status = status;
        }

        public int HttpStatus => Status switch
        {
            DeckWriteStatus.Saved => 200,
            DeckWriteStatus.Deleted => 200,
            DeckWriteStatus.NotFound => 404,
            DeckWriteStatus.Unauthenticated => 401,
            DeckWriteStatus.Unavailable => 503,
            DeckWriteStatus.Malformed => 400,
            DeckWriteStatus.InvalidId => 400,
            DeckWriteStatus.DuplicateId => 409,
            _ => 500,
        };
    }

    /// <summary>
    /// <c>PUT /decks/{id}</c> and <c>DELETE /decks/{id}</c>. Spec: <c>docs/dev-docs/specs/login-handshake.md</c>.
    ///
    /// <para>Resolves the account exactly as <see cref="SessionEndpoint"/>, <see cref="RosterEndpoint"/>
    /// and <see cref="CharacterCreateEndpoint"/> do — verifier, then
    /// <c>CredentialHash.Of(identity.Subject)</c>, then <c>ResolveOrCreateAsync</c>.</para>
    ///
    /// <para><b>DECKS ARE STORED, NEVER REFUSED.</b> Validation is a QUEUE-time concern
    /// (<c>DeckSubmission.Validate</c> / <c>DeckValidator</c>), not a write-time one — an illegal
    /// deck is a legitimate named state (a draft, or one a balance patch made illegal after the
    /// fact) and refusing to store it would delete a player's work-in-progress. Nothing here asks
    /// whether a deck is LEGAL. What it does refuse is a write it cannot attribute: a body that will
    /// not parse as a deck at all, an empty deck id, and an id another player already owns.</para>
    ///
    /// <para><b>The route id is the single authority for the deck's id.</b> <see cref="SaveAsync"/>
    /// overwrites the parsed deck's <c>Id</c> with the route id before saving — otherwise a client
    /// could address one id and write to another.</para>
    /// </summary>
    public static class DeckWriteEndpoint
    {
        public static async Task<DeckWriteResult> SaveAsync(
            string? bearerToken,
            string deckId,
            string body,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accountStore,
            IPlayerDataStore? playerDataStore,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearerToken) || verifier is null)
                return new DeckWriteResult(DeckWriteStatus.Unauthenticated);

            var verified = await verifier.VerifyAsync(bearerToken!, ct).ConfigureAwait(false);
            if (!verified.IsSuccess)
                return new DeckWriteResult(DeckWriteStatus.Unauthenticated);

            // The route id is the deck's only id, so an unusable one is refused before anything is
            // parsed or stored. This refuses a write that cannot be ATTRIBUTED — it is not deck
            // validation, which stays a queue-time concern.
            if (string.IsNullOrWhiteSpace(deckId))
                return new DeckWriteResult(DeckWriteStatus.InvalidId);

            DeckList? deck;
            try
            {
                var parsed = CollectionJson.ReadDecks(body);
                deck = parsed.Count > 0 ? parsed[0] : null;
            }
            catch (CollectionFormatException)
            {
                deck = null;
            }

            if (deck is null)
                return new DeckWriteResult(DeckWriteStatus.Malformed);

            if (accountStore is null)
                return new DeckWriteResult(DeckWriteStatus.Unavailable);

            var identity = verified.Identity!;
            Account account = await accountStore.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(identity.Subject), identity.Email, ct).ConfigureAwait(false);

            if (playerDataStore is null)
                return new DeckWriteResult(DeckWriteStatus.Unavailable);

            // One authority for the id, or a client could write to an id it did not address.
            deck.Id = deckId;

            bool saved = await playerDataStore.SaveDeckAsync(account.PlayerId, deck, ct).ConfigureAwait(false);

            // The store refuses an id another player owns. Reporting 200 for a row that was never
            // written would tell the client its deck is safe when nothing was stored.
            if (!saved)
                return new DeckWriteResult(DeckWriteStatus.DuplicateId);

            return new DeckWriteResult(DeckWriteStatus.Saved);
        }

        public static async Task<DeckWriteResult> DeleteAsync(
            string? bearerToken,
            string deckId,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accountStore,
            IPlayerDataStore? playerDataStore,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(bearerToken) || verifier is null)
                return new DeckWriteResult(DeckWriteStatus.Unauthenticated);

            var verified = await verifier.VerifyAsync(bearerToken!, ct).ConfigureAwait(false);
            if (!verified.IsSuccess)
                return new DeckWriteResult(DeckWriteStatus.Unauthenticated);

            if (string.IsNullOrWhiteSpace(deckId))
                return new DeckWriteResult(DeckWriteStatus.InvalidId);

            if (accountStore is null)
                return new DeckWriteResult(DeckWriteStatus.Unavailable);

            var identity = verified.Identity!;
            Account account = await accountStore.ResolveOrCreateAsync(
                "supabase", CredentialHash.Of(identity.Subject), identity.Email, ct).ConfigureAwait(false);

            if (playerDataStore is null)
                return new DeckWriteResult(DeckWriteStatus.Unavailable);

            bool removed = await playerDataStore.DeleteDeckAsync(account.PlayerId, deckId, ct).ConfigureAwait(false);
            return new DeckWriteResult(removed ? DeckWriteStatus.Deleted : DeckWriteStatus.NotFound);
        }
    }
}
