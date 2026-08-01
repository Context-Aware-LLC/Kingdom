using Emberfall.Core.Cards;
using UnityEngine;

namespace Emberfall.Data
{
    /// <summary>
    /// Baked Addressable artifact for one card row (dev-docs/pipelines/card-authoring-and-import.md:10).
    /// The canonical source is always the JSON row — this asset stores it verbatim and lazily
    /// deserializes through the single shared <see cref="CardJson"/> parser so the SO and the
    /// server can never drift (R1). Read-only at runtime: there is no public field-level setter,
    /// only the editor-only bake door below.
    /// </summary>
    public sealed partial class CardDefinitionSO : ScriptableObject
    {
        [SerializeField] private string _json = "";

        [System.NonSerialized] private CardDefinition? _cached;

        /// <summary>Lazily parses and caches the canonical JSON. Runtime read-only.</summary>
        public CardDefinition ToDefinition()
        {
            if (_cached is null)
                _cached = CardJson.Deserialize(_json);
            return _cached;
        }

#if UNITY_EDITOR
        /// <summary>
        /// Editor-only bake door (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §4.2). Callable only from assemblies granted
        /// InternalsVisibleTo on Emberfall.Data (Emberfall.Editor) — never compiled into a player
        /// build or reachable from the server, so a card can only be written by the import pipeline.
        /// </summary>
        internal void SetCanonicalJson(string json)
        {
            _json = json;
            _cached = null;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
