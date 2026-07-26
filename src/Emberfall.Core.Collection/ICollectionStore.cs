using System.Collections.Generic;

namespace Emberfall.Core.Collection
{
    /// <summary>
    /// CRUD for a player's character roster (ui/collection-deckbuilder.md §3 persistence model:
    /// local on-device, keyed by the guest device token until identity lands). Signatures only —
    /// the concrete store (JSON file, PlayerPrefs, whatever) is the backend session's implementation.
    /// </summary>
    public interface ICharacterStore
    {
        IReadOnlyList<Character> LoadAll();
        void Save(Character character);
        void Delete(string characterId);
    }

    /// <summary>
    /// CRUD for saved decks (ui/collection-deckbuilder.md §3). A deck that references a deleted or
    /// unknown CharacterId, or a card that vanished from the pool after a balance patch, must
    /// surface as invalid on load — never silently drop or auto-repair it (§3).
    /// </summary>
    public interface IDeckStore
    {
        IReadOnlyList<DeckList> LoadAll();
        void Save(DeckList deck);
        void Delete(string deckId);
    }
}
