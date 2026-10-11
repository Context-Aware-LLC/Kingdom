using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kingdom.Presentation
{
    /// <summary>
    /// One complete sealed booster wrapper, including foil edges and transparency.
    /// Retired illustration variants remain on disk; only active wrappers belong in the manifest.
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
        /// <param name="packId">A <c>PackOffer.PackId</c>, e.g. <c>pack.pendingham</c>.</param>
        /// <param name="onLoaded">Given every piece the set's manifest declares and that resolved.
        /// An empty list is an ordinary answer — a set whose art has not been drawn yet.</param>
        void LoadPackArt(string packId, Action<IReadOnlyList<PackArtPiece>> onLoaded);
    }

    /// <summary>
    /// The one place that knows how a pack id and an art folder find each other. <c>BoosterPack.Id</c>
    /// is <c>pack.&lt;expansion&gt;[.&lt;sku&gt;]</c>, and it names its candidate folders MOST
    /// SPECIFIC FIRST: <c>pack.pendingham.starter</c> offers <c>STARTER</c> then <c>PENDINGHAM</c>,
    /// while <c>pack.pendingham</c> offers only <c>PENDINGHAM</c>.
    ///
    /// <para><b>The fallback is what makes a SKU's packaging optional.</b> A product that has been
    /// drawn its own gets it; one that has not falls through to its expansion's, so a new SKU stays
    /// a ledger change rather than an art commission. Resolving the expansion ONLY — which this did
    /// until 2026-09-03 — made a folder drawn for a SKU inert on disk with nothing anywhere
    /// reporting it, because a folder nobody reads cannot be missing. The five STARTER faces shipped
    /// that way and the shelf wore PENDINGHAM's.</para>
    ///
    /// <para>Matching <c>Assets/Art/Cards/&lt;setId&gt;/</c> is the point: one convention for where a
    /// set's art lives, whichever kind of art it is.</para>
    /// </summary>
    public static class PackArtSet
    {
        /// <summary>Folder names to try, in order. Empty only for an empty id. The caller takes the
        /// first that has a manifest — see <see cref="PackArtProvider"/>.</summary>
        public static IReadOnlyList<string> SetIdsFor(string packId)
        {
            if (string.IsNullOrEmpty(packId)) return Array.Empty<string>();
            string[] segments = packId.Split('.');

            // Keep the old one-token fallback for authoring tools that pass a bare set id.
            bool qualified = segments.Length >= 2
                && string.Equals(segments[0], "pack", StringComparison.OrdinalIgnoreCase);
            if (!qualified)
                return new[] { segments[segments.Length - 1].ToUpperInvariant() };

            string expansion = segments[1].ToUpperInvariant();
            if (segments.Length < 3 || segments[2].Length == 0) return new[] { expansion };

            // The SKU distinguishes ledger products and opening odds. It MAY also carry its own
            // packaging; when it does not, the expansion's is the right answer.
            string sku = segments[2].ToUpperInvariant();
            return sku == expansion ? new[] { expansion } : new[] { sku, expansion };
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
