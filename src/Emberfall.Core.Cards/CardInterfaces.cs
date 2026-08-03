using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Contracts
{
    // Declared in Emberfall.Core.Cards but kept in the Emberfall.Core.Contracts NAMESPACE so consumers
    // see one contract surface (dev-docs/architecture/system-overview.md:12). They live in the Cards assembly because they return
    // CardDefinition (Cards → Contracts); declaring them in Contracts would create a dependency cycle.

    /// <summary>
    /// Card data access — the only way the core sees cards (dev-docs/specs/card-schema.md:5). Client and server
    /// provide different impls; both deserialize the identical CardDefinition POCO (R1).
    /// </summary>
    public interface ICardDatabase
    {
        bool TryGet(string cardId, out Cards.CardDefinition def);
        Cards.CardDefinition Get(string cardId);

        /// <summary>
        /// Every row eligible for a live card pool. <b>The contract is
        /// <see cref="Cards.CardDefinition.PoolEligible"/>, not <c>Collectible</c> alone</b> — a row
        /// holding a <c>requiresEngineeringPrimitive</c> reservation is excluded until the engine
        /// grows the primitive it needs. Implementations MUST filter on that property rather than
        /// re-deriving the predicate: <c>Get</c>/<c>TryGet</c> still resolve a held row by id, so a
        /// token, a replay and a set-count all still see it. Only the POOLS do not.
        /// </summary>
        IReadOnlyCollection<Cards.CardDefinition> AllCollectible();
    }

    /// <summary>Shared card validator — runs headless at server/client load and in the editor (dev-docs/pipelines/card-authoring-and-import.md:20).</summary>
    public interface ICardValidator
    {
        ValidationReport Validate(Cards.CardDefinition def, ICardDatabase db);
    }
}
