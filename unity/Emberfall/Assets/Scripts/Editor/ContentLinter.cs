using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;

namespace Emberfall.Editor
{
    /// <summary>Editor-time, metadata-only art lookup (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §4.4). No sprite/GPU load.</summary>
    public interface IAddressableArtCatalog
    {
        bool Exists(string artId);
        bool TryGetEntry(string artId, out ArtCatalogEntry entry);
        IReadOnlyCollection<string> AllArtIds();
    }

    public readonly struct ArtCatalogEntry
    {
        public readonly string Address;
        public readonly string Guid;
        public ArtCatalogEntry(string address, string guid) { Address = address; Guid = guid; }
    }

    /// <summary>Balance-guardrails numbers provider for lint warnings (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §4.5).</summary>
    public interface IBalanceGuardrails
    {
        int VanillaStatBudget(int cost);
        float ManaValueOf(RuleAction action, bool aoe);
        int MaxSimultaneousPersistentAuras();
        int PerTurnActionCap();
        bool IsHighFrequencyTrigger(Trigger t);
    }

    /// <summary>
    /// EDITOR/content lint — the "E-series" (dev-docs/pipelines/card-authoring-and-import.md:22). Kept fully separate from the
    /// headless ICardValidator so core legality never depends on Unity/Addressables/design tables.
    /// All findings here are Warning/Info; only Error-severity core-validator issues block a bake.
    /// </summary>
    public interface IContentLinter
    {
        ValidationReport Lint(CardDefinition def, IAddressableArtCatalog art, IBalanceGuardrails guardrails, ICardDatabase db);
    }

    /// <summary>
    /// No Addressables art group exists for cards yet (a separate, unchecked Phase C item) — this
    /// stub never blocks a bake on art. Swap for a real Addressables-settings-backed catalog once
    /// the art group is stood up; ContentLinter's E1/E2 logic doesn't need to change.
    /// </summary>
    public sealed class StubArtCatalog : IAddressableArtCatalog
    {
        public bool Exists(string artId) => true;
        public bool TryGetEntry(string artId, out ArtCatalogEntry entry) { entry = default; return false; }
        public IReadOnlyCollection<string> AllArtIds() => System.Array.Empty<string>();
    }

    /// <summary>
    /// No versioned guardrails data asset exists yet — these constants mirror
    /// hist-docs/source-snapshots/2026-07-31-pre-reorganization/design/balance-guardrails.md's stated baselines closely enough to drive warnings; replace
    /// with a real data-asset-backed provider when that asset exists (R2: balance is data, not code).
    /// </summary>
    public sealed class StubBalanceGuardrails : IBalanceGuardrails
    {
        public int VanillaStatBudget(int cost) => cost * 2 + 1;
        public float ManaValueOf(RuleAction action, bool aoe) => aoe ? 1.0f : 1.5f;
        public int MaxSimultaneousPersistentAuras() => 14;
        public int PerTurnActionCap() => 30;
        public bool IsHighFrequencyTrigger(Trigger t) => t == Trigger.ON_ATTACK || t == Trigger.ON_DAMAGED;
    }

    public sealed class ContentLinter : IContentLinter
    {
        private static readonly Regex ArtIdPattern = new Regex("^[a-z0-9_/]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

        public ValidationReport Lint(CardDefinition def, IAddressableArtCatalog art, IBalanceGuardrails guardrails, ICardDatabase db)
        {
            var issues = new List<ValidationIssue>();
            // hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §4.3 is explicit: lint issues are "Warning/Info, never Error" — only the
            // headless ICardValidator's Errors block a bake. This deliberately overrides the older
            // E-series severity table in hist-docs/source-snapshots/2026-07-31-pre-reorganization/tooling/validation-and-import.md (E1/E2/E7 as "error"),
            // which predates that ruling; hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md wins per CLAUDE.md.
            void Warn(string code, string field, string msg) => issues.Add(new ValidationIssue(Severity.Warning, code, field, msg, def.Id));

            // E2: artId format (checked before E1 so an empty/malformed id doesn't also spam E1)
            if (string.IsNullOrEmpty(def.ArtId) || !ArtIdPattern.IsMatch(def.ArtId))
                Warn("E2-ARTID-FORMAT", "artId", "artId must be non-empty and match ^[a-z0-9_/]+$.");
            else if (!art.Exists(def.ArtId))
                Warn("E1-ART-MISSING", "artId", $"artId '{def.ArtId}' does not resolve in the art catalog.");

            // E3: stat-budget deviation — only meaningful for vanilla (no keyword/effect) minions
            if (def.Type == CardType.MINION && def.Attack is int atk && def.Health is int hp
                && def.Keywords.Count == 0 && def.Rules.Count == 0)
            {
                int budget = guardrails.VanillaStatBudget(def.Cost);
                int actual = atk + hp;
                if (System.Math.Abs(actual - budget) > 2)
                    Warn("E3-STAT-BUDGET", "attack/health",
                        $"(attack+health)={actual} deviates from the vanilla budget {budget} for cost {def.Cost}.");
            }

            // E5: aura-cap warning — cards that grant a persistent-aura keyword via GIVE_KEYWORD
            var auraKeywords = new HashSet<Keyword> { Keyword.BULWARK, Keyword.SHROUD, Keyword.AEGIS };
            foreach (var rule in def.Rules)
                if (rule.Action is GrantKeywordAction grant && auraKeywords.Contains(grant.Keyword))
                    Warn("E5-AURA-CAP", "rules", $"grants persistent aura keyword {grant.Keyword} — check against the {guardrails.MaxSimultaneousPersistentAuras()}-aura board cap (guardrails §5).");

            // E7: generated card text is the sole presentation text source.
            string generatedText = CardTextGenerator.Generate(def);
            if (generatedText.Length > 140)
                Warn("E7-TEXT-LENGTH", "generatedText", "generated card text must be <=140 characters.");
            if (def.FlavorText.Length > 160)
                Warn("E7-TEXT-LENGTH", "flavorText", "flavorText must be <=160 characters.");
            if (def.Tooltip.Length > 40)
                Warn("E7-TEXT-LENGTH", "tooltip", "tooltip must be <=40 characters — it renders as a single-line banner across the top of the card.");

            // E10: rarity/collectible sanity
            if (def.Rarity == Rarity.LEGENDARY && !def.Collectible)
                Warn("E10-RARITY-SANITY", "rarity", "LEGENDARY + collectible:false is an unusual combination — likely an authoring slip.");

            bool ok = true;
            foreach (var i in issues)
                if (i.Severity == Severity.Error) { ok = false; break; }

            // Lint never sets the engineering-primitive gate — that's core-validator-owned (C20).
            return new ValidationReport(ok, issues);
        }
    }
}
