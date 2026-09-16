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

        /// <summary>
        /// This account already holds <see cref="DeckCreation.MaxPerAccount"/> decks and this write
        /// would be a NEW one. 409.
        ///
        /// <para>Delete a deck and try again. This is independent of cosmetic ownership and the
        /// "decks are stored, never refused" rule breaking: that rule is about LEGALITY, and this
        /// says nothing about whether the deck is legal. A re-save of a deck the account already
        /// holds is never refused, however full the account is, because it adds nothing.</para>
        ///
        /// <para><b>RETIREMENT TRIGGER — if the cap ever becomes per-account data</b> (a purchased
        /// deck slot, a subscription tier), this stops being a constant and the check below stops
        /// being able to read it off <c>DeckCreation</c>. Move the constant's home in the same commit
        /// that gives an account its own limit, and update <c>DeckCreation.MaxPerAccount</c>'s
        /// docstring, which currently claims one flat number for everyone.</para>
        /// </summary>
        AtCapacity,
        /// <summary>The requested cosmetic identity is not in the server catalog. 400.</summary>
        UnknownCardBack,
        /// <summary>The account has no positive entitlement for the selected back. 403.</summary>
        CardBackNotOwned,
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
            DeckWriteStatus.AtCapacity => 409,
            DeckWriteStatus.UnknownCardBack => 400,
            DeckWriteStatus.CardBackNotOwned => 403,
            _ => 500,
        };
    }

    /// <summary>
    /// <c>PUT /decks/{id}</c> and <c>DELETE /decks/{id}</c>. Spec: <c>docs/dev-docs/specs/login-handshake.md</c>.
    ///
    /// <para>Resolves the account exactly as <see cref="SessionEndpoint"/> and
    /// <see cref="RosterEndpoint"/> do — verifier, then <c>CredentialHash.Of(identity.Subject)</c>,
    /// then <c>ResolveOrCreateAsync</c>.</para>
    ///
    /// <para>The deck cap (ADR-0009 ruling 2) and cosmetic ownership are enforced here.
    /// Card-back entitlement is independent of gameplay-card legality.</para>
    ///
    /// <para><b>DECKS ARE STORED, NEVER REFUSED.</b> Validation is a QUEUE-time concern
    /// (<c>DeckSubmission.Validate</c> / <c>DeckValidator</c>), not a write-time one — an illegal
    /// deck is a legitimate named state (a draft, or one a balance patch made illegal after the
    /// fact) and refusing to store it would delete a player's work-in-progress. Nothing here asks
    /// whether a deck is LEGAL. What it does refuse is a write it cannot attribute: a body that will
    /// not parse as a deck at all, an empty deck id, and an id another player already owns. Unknown or unowned cosmetics are also refused.</para>
    ///
    /// <para><b>The route id is the single authority for the deck's id.</b> <see cref="SaveAsync"/>
    /// overwrites the parsed deck's <c>Id</c> with the route id before saving — otherwise a client
    /// could address one id and write to another.</para>
    /// </summary>
    public static class DeckWriteEndpoint
    {
        /// <summary>
        /// Route-level pre-auth for <c>PUT /decks/{id}</c>. This route's body has to be read before
        /// <see cref="SaveAsync"/> can be called at all — an unbounded <c>ReadToEndAsync</c> over a
        /// body from a caller nobody has authenticated yet. So the route asks this first and answers
        /// 401 before touching the body.
        ///
        /// <para><c>CharacterCreateEndpoint.IsAuthenticatedAsync</c> was its twin and had the same
        /// reason; ADR-0009 deleted that route, so this is now the only one that needs the gate.</para>
        ///
        /// <para><see cref="SaveAsync"/> still verifies for itself: it is callable without a route,
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

        public static async Task<DeckWriteResult> SaveAsync(
            string? bearerToken,
            string deckId,
            string body,
            IIdentityTokenVerifier? verifier,
            IAccountStore? accountStore,
            IPlayerDataStore? playerDataStore,
            CancellationToken ct = default,
            IEntitlementLedger? ledger = null)
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

            // Gameplay drafts remain savable, but cosmetics require an actual entitlement.
            int backId = deck.CardBackId == 0 ? CardBackDefinitions.DefaultId : deck.CardBackId;
            if (!CardBackDefinitions.TryGet(backId, out _))
                return new DeckWriteResult(DeckWriteStatus.UnknownCardBack);
            if (ledger is null)
                return new DeckWriteResult(DeckWriteStatus.Unavailable);
            await CardBackOwnership.EnsureDefaultAsync(ledger, account, ct).ConfigureAwait(false);
            await using var ownershipLock = await ledger.AcquirePlayerMutationLockAsync(account.PlayerId, ct)
                .ConfigureAwait(false);
            if (await ledger.BalanceAsync(account.PlayerId, CardBackOwnership.Item(backId), ct)
                .ConfigureAwait(false) <= 0)
                return new DeckWriteResult(DeckWriteStatus.CardBackNotOwned);
            deck.CardBackId = backId;

            // One authority for the id, or a client could write to an id it did not address.
            deck.Id = deckId;

            // THE CAP, SERVER SIDE (ADR-0009 ruling 2). The client disables its "+" plate at
            // DeckCreation.MaxPerAccount and that is not a cap: a cap only the client holds is a
            // suggestion. Both halves read the same constant, so they cannot drift.
            //
            // Counted against a fresh read rather than trusted from the client, and scoped to NEW
            // decks: a re-save of a deck this account already holds occupies no additional slot and
            // must never be refused, or a player at the limit could not edit the decks they have.
            // A draft counts, exactly as it does client-side — the cap is about how many decks are
            // kept, not how many are legal.
            PlayerRoster existing = await playerDataStore.ReadAsync(account.PlayerId, ct).ConfigureAwait(false);
            if (!DeckCreation.CanCreate(existing.Decks) && !HoldsDeck(existing, deckId))
                return new DeckWriteResult(DeckWriteStatus.AtCapacity);

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

        /// <summary>Does this roster already carry that deck id? What separates an edit from a new
        /// deck, and therefore what the cap is applied to.</summary>
        private static bool HoldsDeck(PlayerRoster roster, string deckId)
        {
            for (int i = 0; i < roster.Decks.Count; i++)
                if (string.Equals(roster.Decks[i].Id, deckId, System.StringComparison.Ordinal))
                    return true;
            return false;
        }
    }
}
