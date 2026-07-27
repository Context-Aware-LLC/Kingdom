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

    /// <summary>
    /// The single <see cref="PlayerProfile"/> for an identity scope — the display-name half of guest
    /// identity (accounts posture decision 6a).
    ///
    /// <para><b>Load, not LoadAll, and no Delete.</b> There is exactly one player per scope, so there
    /// is nothing to enumerate; and a profile has no lifecycle of its own — the scope exists because
    /// a device minted a token, and it stops existing when that storage is wiped. A Delete would only
    /// ever be used to clear the name, which is a Save.</para>
    ///
    /// <para><b>Load never returns null.</b> A device that has never been asked for a name is not an
    /// error state, so first run yields a profile with an empty <see cref="PlayerProfile.DisplayName"/>
    /// rather than nothing. Callers branch on <see cref="PlayerProfile.HasDisplayName"/>, which cannot
    /// be forgotten the way a null check can.</para>
    /// </summary>
    public interface IProfileStore
    {
        PlayerProfile Load();
        void Save(PlayerProfile profile);
    }
}
