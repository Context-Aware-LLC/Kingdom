using System;
using System.Collections.Generic;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// One piece of booster packaging art, as the shop and any later gallery see it.
    ///
    /// <para><b><see cref="Id"/> is the permanent name, <see cref="Sprite"/> is not.</b> The id is
    /// what anything durable stores — a gallery's "seen", a profile's chosen display — and it
    /// survives renaming, re-cropping or redrawing the file underneath it. Storing a filename
    /// instead is the mistake card ids exist to prevent: a rename orphans the record silently, in
    /// save data outside this repo where no search can reach it.</para>
    /// </summary>
    public readonly struct PackArtPiece
    {
        public string Id { get; }

        /// <summary>Printable name of the artwork. Exists for surfaces that show art on its own
        /// rather than as a pack's decoration, which need something to put under the picture.</summary>
        public string Title { get; }

        public Sprite Sprite { get; }

        public PackArtPiece(string id, string title, Sprite sprite)
        {
            Id = id;
            Title = title;
            Sprite = sprite;
        }
    }

    /// <summary>
    /// Resolves every piece of packaging art a pack has, in manifest order.
    ///
    /// <para>Named for the art rather than the shop on purpose: the store is its first consumer,
    /// not its only intended one.</para>
    /// </summary>
    public interface IPackArtProvider
    {
        /// <param name="packId">A <c>PackOffer.PackId</c>, e.g. <c>pack.emberfall</c>.</param>
        /// <param name="onLoaded">Given every piece the set's manifest declares and that resolved.
        /// An empty list is an ordinary answer — a set whose art has not been drawn yet.</param>
        void LoadPackArt(string packId, Action<IReadOnlyList<PackArtPiece>> onLoaded);
    }

    /// <summary>
    /// The one place that knows how a pack id and an art folder find each other: the folder is
    /// named for the LAST SEGMENT of the pack id, uppercased — <c>pack.emberfall</c> reads
    /// <c>Assets/Art/Shop/Packs/EMBERFALL/</c>.
    ///
    /// <para>Matching <c>Assets/Art/Cards/&lt;setId&gt;/</c> is the point: one convention for where a
    /// set's art lives, whichever kind of art it is.</para>
    /// </summary>
    public static class PackArtSet
    {
        public static string SetIdFor(string packId)
        {
            if (string.IsNullOrEmpty(packId)) return "";
            int split = packId.LastIndexOf('.');
            string token = split >= 0 ? packId.Substring(split + 1) : packId;
            return token.ToUpperInvariant();
        }
    }

    /// <summary>
    /// The on-disk manifest: <c>Assets/Art/Shop/Packs/&lt;setId&gt;/packart.json</c>.
    ///
    /// <para><b>Order is meaningful and preserved.</b> It is an array rather than a map so a set
    /// whose pieces read as a sequence keeps that sequence — the file order is the display order,
    /// and anything later that wants to play them in series already has what it needs.</para>
    ///
    /// <para><b>Why a manifest at all, when the folder is already the source of truth.</b> The
    /// folder answers "what art exists"; only the manifest answers "which piece is this" in a way
    /// that outlives a rename. It is also the only sanctioned place to say a file is expected: a
    /// declared entry whose file is missing is reported, where an undeclared missing file cannot
    /// be, because nothing knew to look for it.</para>
    ///
    /// <para>Ids are per PIECE, not per character — a character whose pack carries several pieces
    /// gets several ids. Convention is <c>&lt;set&gt;_&lt;character&gt;</c>, suffixed
    /// <c>_1</c>, <c>_2</c>… where there is more than one.</para>
    /// </summary>
    [Serializable]
    public sealed class PackArtManifest
    {
        // JsonUtility cannot deserialize a top-level array, hence the wrapper object. Keep the
        // field name `pieces` — it is the JSON key, not a private detail.
        public List<PackArtManifestEntry> pieces = new List<PackArtManifestEntry>();
    }

    [Serializable]
    public sealed class PackArtManifestEntry
    {
        /// <summary>Permanent. Nothing may renumber or re-spell this once anything has stored it.</summary>
        public string id;

        /// <summary>File name with no extension. Rename freely — this is the disposable half.</summary>
        public string file;

        public string title;
    }
}
