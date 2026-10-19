using System;
using System.Collections.Generic;
using Kingdom.Core.Cards;
using Kingdom.Core.Contracts;

namespace Kingdom.Core.Collection
{
    /// <summary>
    /// What a brand-new deck starts as (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §2.11).
    /// </summary>
    public enum StarterDeck
    {
        /// <summary>A curve-weighted legal 30 from <see cref="DefaultDeckGenerator"/>.</summary>
        Generated,

        /// <summary>
        /// A real saved deck with no cards in it. Still opens in Build mode, and a draft under §1
        /// until it reaches 30 — deliberately not a special case anywhere.
        /// </summary>
        Blank,
    }

    /// <summary>
    /// Creating a deck: the pairing is chosen, the cards are generated or left empty, and one row is
    /// written.
    ///
    /// <para><b>This replaces <c>CharacterCreation</c> (ADR-0009, Sam 2026-09-04),</b> and most of
    /// what that type existed to manage is gone rather than moved. It documented at length why its
    /// three writes had to happen in one order — generate the deck, save the DECK, then save the
    /// character carrying its id, so that a process dying between the two writes left an unreferenced
    /// deck rather than a character pointing at a deck that does not exist. <b>There is one write
    /// now.</b> A death mid-write leaves either no deck or a whole one, and there is no second object
    /// to point at anything. Its <c>CreateWithoutDeck</c> (NOR-278) is gone for the same reason:
    /// it existed so the portrait grid could make a character before its first deck, and a deck
    /// cannot be created before itself.</para>
    /// </summary>
    public static class DeckCreation
    {
        /// <summary>
        /// How many decks one account may hold (Sam, 2026-09-04 — ADR-0009; 8 → 9 on 2026-10-01).
        ///
        /// <para><b>A draft counts.</b> The cap is about how many decks a player keeps, not how many
        /// are legal, so a half-built deck occupies a slot exactly as a queue-ready one does. Any
        /// other reading would make the limit move under the player as they edited.</para>
        ///
        /// <para><b>There was no cap at all before this.</b> The old roster enforced a FLOOR — a
        /// character had to keep at least one deck — and nothing anywhere enforced a ceiling, because
        /// the deck count was bounded in practice by how many characters a player had bothered to
        /// make. Flattening the roster removed that incidental bound, so this is a real new rule and
        /// not a moved one.</para>
        ///
        /// <para><b>Spent on BOTH sides, and it has to be.</b> A cap only the client holds is not a
        /// cap, for the same reason <c>QueueEligibility</c> re-runs server-side. The two enforcement
        /// points, both reading this constant through <see cref="CanCreate"/>:</para>
        ///
        /// <list type="bullet">
        /// <item>the client, disabling the roster's "+" plate and its header twin
        /// (<c>PlayerAccount.CanCreateDeck</c>) — a courtesy, so the player is told before they
        /// spend a screen choosing a pairing;</item>
        /// <item><c>PUT /decks/{id}</c>, answering <c>AtCapacity</c> (409) to a NEW deck on a full
        /// account — the one that actually holds. A re-save of a deck the account already has is
        /// never refused, at any count, or a player at the limit could not edit what they own.</item>
        /// </list>
        /// </summary>
        public const int MaxPerAccount = 9;

        /// <summary>
        /// Has this account room for another deck? The predicate the <c>+</c> plate is disabled by and
        /// the deck write route refuses on, so the two cannot drift.
        /// </summary>
        public static bool CanCreate(IReadOnlyList<DeckList> decks) =>
            decks == null || decks.Count < MaxPerAccount;

        /// <summary>
        /// Whether the create form offers the blank-or-generated choice at all (§2.11, Sam
        /// 2026-07-26). <b>The first deck never gets the choice</b> — a player with nothing has
        /// nothing to build from, and an empty deck as the first thing the game shows is a worse
        /// introduction than a playable one. From the second deck on it is asked.
        ///
        /// <para>The rule lives here rather than in the create screen for the same reason
        /// <see cref="QueueEligibility"/> does: a policy the UI re-derives is a policy that drifts.
        /// It keys on the account holding no decks, not on a first-run flag, so a player who deletes
        /// every deck is genuinely back to having nothing to build from and is handed one again.</para>
        /// </summary>
        public static bool OffersStarterChoice(IReadOnlyList<DeckList> decks) =>
            decks != null && decks.Count > 0;

        /// <summary>
        /// Create a deck for a pairing and persist it.
        /// </summary>
        /// <param name="deckId">Caller-supplied; this assembly mints no identity.</param>
        /// <param name="rng">Seeded by the caller — the same seed produces the same deck. Unused when <paramref name="starter"/> is <see cref="StarterDeck.Blank"/>.</param>
        /// <param name="starter">Generated or blank (§2.11). The default is what the first deck always gets.</param>
        /// <param name="owned">
        /// What this account owns, so a generated deck is one it can actually queue with (L6). Null
        /// means "ownership unknown, do not constrain" — see <see cref="DefaultDeckGenerator"/>.
        /// </param>
        /// <exception cref="ArgumentException">The pairing is invalid (see <see cref="DeckList.IsValidPairing"/>).</exception>
        /// <exception cref="DeckGenerationException">The pool cannot supply a legal 30.</exception>
        public static DeckList Create(
            IDeckStore deckStore, ICardDatabase db, DeterministicRandom rng,
            string deckId, string name, CardClass primary, CardClass secondary,
            StarterDeck starter = StarterDeck.Generated, IOwnedCardCounts? owned = null)
        {
            if (deckStore == null) throw new ArgumentNullException(nameof(deckStore));
            DeckList deck = Prepare(db, rng, deckId, name, primary, secondary, starter, owned);
            deckStore.Save(deck);
            return deck;
        }

        /// <summary>Build a candidate without persisting it. Online creation saves only after server acknowledgement.</summary>
        public static DeckList Prepare(
            ICardDatabase db, DeterministicRandom rng,
            string deckId, string name, CardClass primary, CardClass secondary,
            StarterDeck starter = StarterDeck.Generated, IOwnedCardCounts? owned = null)
        {
            if (string.IsNullOrWhiteSpace(deckId))
                throw new ArgumentException("A new deck needs an id from the caller.", nameof(deckId));

            // Checked before anything is generated or written, so a rejected pairing costs nothing and
            // leaves no half-created state. JsonDeckStore.Save would catch it too; catching it here is
            // what keeps the generator from being run against a pool that cannot mean anything.
            if (!DeckList.IsValidPairing(primary, secondary))
                throw new ArgumentException(
                    $"'{primary}/{secondary}' is not a valid pairing (both classes must be real and distinct).",
                    nameof(primary));

            // A blank starter asks nothing of the card pool, so it does not consult one: a pairing too
            // thin to generate 30 can still hold a deck you build by hand.
            var deck = starter == StarterDeck.Blank
                ? new DeckList(primary, secondary) { Id = deckId, Name = NameOrDefault(name) }
                : DefaultDeckGenerator.Generate(primary, secondary, db, rng, deckId, NameOrDefault(name), owned);

            return deck;
        }

        /// <summary>
        /// "New Deck" when the caller supplies nothing. The create screen always supplies the
        /// pairing's name from <see cref="DeckNames"/>, so this is only a fallback.
        /// </summary>
        private static string NameOrDefault(string name) =>
            string.IsNullOrWhiteSpace(name) ? "New Deck" : name;
    }
}
