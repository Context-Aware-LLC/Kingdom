using System.Linq;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using UnityEngine;

namespace Emberfall.Editor
{
    /// <summary>
    /// Batch "validate entire card set" (tooling/validation-and-import.md §6), scoped down for M1:
    /// logs a grouped console report rather than emitting CSV/JUnit artifacts (no CI runner wired
    /// to this yet — that's the not-yet-built headless CardCI entry point from §7).
    /// </summary>
    public static class CardSetValidator
    {
        public static void RunAndLog()
        {
            var rows = CardJsonRowIO.LoadRows(CardJsonRowIO.DefaultM1JsonPath);
            var defs = rows.Select(r => r.def).ToList();
            var db = new InMemoryCardDatabase(defs);
            var validator = new CardValidator();
            var linter = new ContentLinter();
            var art = new StubArtCatalog();
            var guardrails = new StubBalanceGuardrails();

            int clean = 0, warnings = 0, errors = 0, needsPrimitive = 0;

            foreach (var def in defs)
            {
                var core = validator.Validate(def, db);
                var lint = linter.Lint(def, art, guardrails, db);

                if (core.RequiresEngineeringPrimitive) needsPrimitive++;
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

            bool gateOk = errors == 0 && needsPrimitive == 0 && dupIssues.Count == 0;
            string summary = $"[CardValidate] Total={defs.Count} Clean={clean} Warnings={warnings} Errors={errors} NeedsPrimitive={needsPrimitive} Duplicates={dupIssues.Count} GateOk={gateOk}";
            if (gateOk) Debug.Log(summary);
            else Debug.LogWarning(summary);
        }
    }
}
