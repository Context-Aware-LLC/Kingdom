using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Data;
using UnityEditor;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Bake canonical JSON rows into Addressable CardDefinitionSO assets (dev-docs/pipelines/card-authoring-and-import.md:10,
    /// hist-docs/source-snapshots/2026-07-31-pre-reorganization/tooling/card-authoring-tool.md §6.3). Editor-only; the Card Authoring
    /// window's Bake / Bake All buttons are its ONLY callers.
    /// </summary>
    public interface ICardImporter
    {
        ImportResult Import(string jsonPath);
        ImportResult ImportSet(SetId setId);
    }

    /// <summary>
    /// Result of a bake (single-card or batch). No shape for this was locked in hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md
    /// beyond "Import(jsonPath) -> ImportResult" — this is Editor Tooling's own invented shape,
    /// generalized to cover both a single import and a batch (ImportSet) uniformly.
    /// </summary>
    public readonly struct ImportResult
    {
        public readonly bool Success;
        public readonly IReadOnlyList<string> AssetPaths;
        public readonly IReadOnlyList<ValidationReport> Reports;

        public ImportResult(bool success, IReadOnlyList<string> assetPaths, IReadOnlyList<ValidationReport> reports)
        {
            Success = success;
            AssetPaths = assetPaths;
            Reports = reports;
        }
    }

    /// <summary>
    /// ICardImporter impl. Refuses to bake a row when core validation fails
    /// (the R3 audit gate, dev-docs/specs/card-schema.md:40) — the window and CI both go
    /// through this single path so nothing can bake a broken card.
    ///
    /// Deviation from the spec: bakes straight from the flat Emberfall.cards.json array rather than
    /// per-file `.card.json` sources (see CardJsonRowIO header) — no ScriptedImporter is used here;
    /// baking is an explicit action (the window's "Bake" / "Bake All" buttons), not import-on-save.
    ///
    /// <b>Nothing rebakes a row you edit outside this window.</b> Opening Unity, reimporting and a
    /// domain reload all leave a stale `_json` in place, and `Emberfall/Validate All Cards` does not
    /// catch it either — CardSetValidator reads CardJsonRowIO.LoadRows only and never opens
    /// Assets/Cards, so it passes clean over a bake that disagrees with its row. A hand-edit to
    /// Emberfall.cards.json is caught by tooling/scripts/rebake_card_definition_so_assets.py (run by
    /// the pre-commit hook in .githooks/) and by CardDefinitionSoAssetParityTests — nowhere in Unity.
    /// Addressable group/label assignment (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/card-system.md §6) is deferred — no Addressables group
    /// exists for cards yet, so this writes a plain asset under Assets/Cards/&lt;setId&gt;/&lt;id&gt;.asset.
    /// </summary>
    public sealed class CardImporter : ICardImporter
    {
        private const string CardsRoot = "Assets/Cards";
        private readonly ICardValidator _validator;
        private readonly ICardDatabase _db;

        public CardImporter(ICardValidator validator, ICardDatabase db)
        {
            _validator = validator;
            _db = db;
        }

        public ImportResult Import(string jsonPath)
        {
            string json = File.ReadAllText(jsonPath);
            var def = CardJson.Deserialize(json);
            return BakeOne(def, json);
        }

        public ImportResult ImportSet(SetId setId)
        {
            var rows = CardJsonRowIO.LoadRows(CardJsonRowIO.DefaultEmberfallJsonPath)
                .Where(r => r.def.SetId == setId)
                .ToList();
            return BakeMany(rows);
        }

        /// <summary>Bakes every row in the flat Emberfall file, regardless of set. Used by the window's "Bake All".</summary>
        public ImportResult ImportAllFromFlatFile(string flatJsonPath)
        {
            var rows = CardJsonRowIO.LoadRows(flatJsonPath);
            return BakeMany(rows);
        }

        /// <summary>Bakes a single row already held in memory (e.g. a just-edited working copy).</summary>
        public ImportResult ImportRow(CardDefinition def)
        {
            return BakeOne(def, CardJsonRowIO.Serialize(def));
        }

        private ImportResult BakeMany(IReadOnlyList<(CardDefinition def, string json)> rows)
        {
            var assetPaths = new List<string>();
            var reports = new List<ValidationReport>();
            bool allOk = true;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (def, json) in rows)
                {
                    var result = BakeOne(def, json);
                    reports.AddRange(result.Reports);
                    assetPaths.AddRange(result.AssetPaths);
                    allOk &= result.Success;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            return new ImportResult(allOk, assetPaths, reports);
        }

        private ImportResult BakeOne(CardDefinition def, string json)
        {
            var report = _validator.Validate(def, _db);
            if (!report.Ok)
                return new ImportResult(false, System.Array.Empty<string>(), new[] { report });

            string dir = $"{CardsRoot}/{def.SetId}";
            EnsureFolder(dir);

            string assetPath = $"{dir}/{def.Id}.asset";
            var so = AssetDatabase.LoadAssetAtPath<CardDefinitionSO>(assetPath);
            bool isNew = so == null;
            if (isNew)
                so = ScriptableObject.CreateInstance<CardDefinitionSO>();

            so.SetCanonicalJson(json);

            if (isNew)
                AssetDatabase.CreateAsset(so, assetPath);
            else
                EditorUtility.SetDirty(so);

            return new ImportResult(true, new[] { assetPath }, new[] { report });
        }

        // Tracks folders this importer instance has already created/confirmed. Necessary because
        // BakeMany wraps the whole batch in StartAssetEditing/StopAssetEditing, during which
        // AssetDatabase.IsValidFolder does NOT see folders created earlier in the same batch
        // (import is deferred to StopAssetEditing) — without this cache, every card after the
        // first in a set would re-trigger CreateFolder, and Unity silently renames the collision
        // to "Cards 1", "Cards 2", etc. instead of reusing the folder.
        private readonly HashSet<string> _ensuredFolders = new HashSet<string>();

        private void EnsureFolder(string path)
        {
            if (_ensuredFolders.Contains(path)) return;
            if (AssetDatabase.IsValidFolder(path)) { _ensuredFolders.Add(path); return; }

            var parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!_ensuredFolders.Contains(next) && !AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                _ensuredFolders.Add(next);
                current = next;
            }
        }
    }
}
