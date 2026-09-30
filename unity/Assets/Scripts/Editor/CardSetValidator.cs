using System.Collections.Generic;
using System.IO;
using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Batch "validate entire card pool" (dev-docs/pipelines/card-authoring-and-import.md:21):
    /// loads Emberfall+Rekindling and logs a grouped console report.
    /// </summary>
    public static class CardSetValidator
    {
        private static readonly string[] SetPaths =
        {
            "Assets/CardData/Core.cards.json",
            CardJsonRowIO.DefaultEmberfallJsonPath,
            "Assets/CardData/Rekindling.cards.json",
        };

        public static void RunAndLog()
        {
            var allRows = new List<(CardDefinition def, string json)>();
            foreach (var path in SetPaths)
            {
                if (!File.Exists(path))
                {
                    Debug.LogWarning($"[CardValidate] Skipping missing set: {path}");
                    continue;
                }
                allRows.AddRange(CardJsonRowIO.LoadRows(path));
            }

            var defs = allRows.Select(r => r.def).ToList();
            var db = new InMemoryCardDatabase(defs);
            var validator = new CardValidator();
            var linter = new ContentLinter();
            var art = new AddressablesArtCatalog();
            var guardrails = new StubBalanceGuardrails();

            int clean = 0, warnings = 0, errors = 0;

            foreach (var def in defs)
            {
                var core = validator.Validate(def, db);
                var lint = linter.Lint(def, art, guardrails, db);

                if (!core.Ok) errors++;
                else if (core.Issues.Count > 0 || lint.Issues.Count > 0) warnings++;
                else clean++;

                foreach (var issue in core.Issues)
                    Debug.Log($"[CardValidate][{issue.Severity}] {def.Id} {issue.Field}: {issue.Message} ({issue.Code})");
                foreach (var issue in lint.Issues)
                    Debug.Log($"[CardValidate][Lint/{issue.Severity}] {def.Id} {issue.Field}: {issue.Message} ({issue.Code})");
            }

            var dupIssues = CardValidator.CheckDuplicateIds(defs);
            foreach (var issue in dupIssues)
                Debug.LogError($"[CardValidate][Error] {issue.CardId} {issue.Field}: {issue.Message} ({issue.Code})");

            bool gateOk = errors == 0 && dupIssues.Count == 0;
            string summary = $"[CardValidate] Total={defs.Count} Clean={clean} Warnings={warnings} Errors={errors} Duplicates={dupIssues.Count} GateOk={gateOk}";
            if (gateOk) Debug.Log(summary);
            else Debug.LogError(summary);
        }
    }
}
