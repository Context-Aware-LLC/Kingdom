using System.Collections.Generic;

namespace Emberfall.Core.Contracts
{
    /// <summary>Canonical validation-issue shape, LOCKED in v2 (dev-docs/specs/card-schema.md:63).</summary>
    public readonly struct ValidationIssue
    {
        public readonly Severity Severity;
        /// <summary>Stable machine id, e.g. "S-MINION-NEEDS-HEALTH", "E1-ART-MISSING".</summary>
        public readonly string Code;
        /// <summary>JSON-pointer-style path: "rules[2].action.cardId"; "" = card-level.</summary>
        public readonly string Field;
        public readonly string Message;
        /// <summary>Owning card (batch validation context).</summary>
        public readonly string CardId;

        public ValidationIssue(Severity severity, string code, string field, string message, string cardId)
        {
            Severity = severity;
            Code = code;
            Field = field;
            Message = message;
            CardId = cardId;
        }
    }

    /// <summary>Result of <see cref="ICardValidator.Validate"/>. Only an Error sets Ok=false.</summary>
    public readonly struct ValidationReport
    {
        public readonly bool Ok;
        public readonly IReadOnlyList<ValidationIssue> Issues;
        public readonly bool RequiresEngineeringPrimitive;

        public ValidationReport(bool ok, IReadOnlyList<ValidationIssue> issues, bool requiresEngineeringPrimitive)
        {
            Ok = ok;
            Issues = issues;
            RequiresEngineeringPrimitive = requiresEngineeringPrimitive;
        }
    }
}
