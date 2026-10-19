using System.Collections.Generic;

namespace Kingdom.Core.Collection
{
    /// <summary>
    /// CRUD for saved decks (hist-docs/source-snapshots/2026-07-31-pre-reorganization/ui/collection-deckbuilder.md §3, persistence model:
    /// local on-device, keyed by the identity scope). A deck holding a card that vanished from the
    /// pool after a balance patch must surface as invalid on load — never silently dropped or
    /// auto-repaired (§3).
    ///
    /// <para><b><c>ICharacterStore</c> stood here until 2026-09-04</b> and was deleted with the type
    /// it persisted (ADR-0009). Its rules did not move anywhere: a deck carries its own pairing, so
    /// there is no second file to keep consistent with this one and no "references a deleted or
    /// unknown CharacterId" state left to surface.</para>
    /// </summary>
    public interface IDeckStore
    {
        IReadOnlyList<DeckList> LoadAll();
        void Save(DeckList deck);
        void Delete(string deckId);

        /// <summary>
        /// Overwrite the whole file with exactly these decks. <b>Does not read what is there first,
        /// and that is the entire point.</b>
        ///
        /// <para><see cref="Save"/> and <see cref="Delete"/> are read-modify-write, so a file this
        /// reader refuses — a deck written before card ids became numbers, a truncated write, a
        /// restored backup from an older build — makes every one of them throw. That means the local
        /// cache can BLOCK ITS OWN REPAIR: the roster sync holds the server's authoritative answer
        /// and still cannot write it down. Measured on a device 2026-09-02, where an Android
        /// auto-backup restored a pre-migration <c>decks.json</c> that no code path could clear.</para>
        ///
        /// <para>For the server-authoritative sync ONLY. Everything else edits, and an edit has to
        /// know what it is editing.</para>
        /// </summary>
        void ReplaceAll(IReadOnlyList<DeckList> decks);
    }

    /// <summary>
    /// The single <see cref="PlayerProfile"/> for an identity scope.
    ///
    /// <para><b>Load, not LoadAll, and no Delete.</b> There is exactly one player per scope, so there
    /// is nothing to enumerate; and a profile has no lifecycle of its own — the scope exists because
    /// a device minted a token, and it stops existing when that storage is wiped. A Delete would only
    /// ever be used to reset a field, which is a Save.</para>
    ///
    /// <para><b>Load never returns null.</b> First run yields an empty profile rather than nothing,
    /// so no caller has a null check to forget.</para>
    /// </summary>
    public interface IProfileStore
    {
        PlayerProfile Load();
        void Save(PlayerProfile profile);
    }
}
