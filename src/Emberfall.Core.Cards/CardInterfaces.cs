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
        /// Every row eligible for a live card pool. Implementations MUST filter on
        /// <see cref="Cards.CardDefinition.IsPoolEligible"/>, not on <c>Collectible</c> alone — a row
        /// flagged <c>requiresEngineeringPrimitive</c> declares grammar the engine cannot resolve and
        /// throws if it reaches play (EF-032a). <c>TryGet</c>/<c>Get</c> deliberately still return it:
        /// a held row must stay resolvable for tokens, replays and existing saved decks.
        /// </summary>
        IReadOnlyCollection<Cards.CardDefinition> AllCollectible();
    }

    /// <summary>Shared card validator — runs headless at server/client load and in the editor (dev-docs/pipelines/card-authoring-and-import.md:20).</summary>
    public interface ICardValidator
    {
        ValidationReport Validate(Cards.CardDefinition def, ICardDatabase db);
    }
}
