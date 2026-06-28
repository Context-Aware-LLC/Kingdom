using System.Collections.Generic;
using Emberfall.Core.Contracts;

namespace Emberfall.Core.Contracts
{
    // Declared in Emberfall.Core.Cards but kept in the Emberfall.Core.Contracts NAMESPACE so consumers
    // see one contract surface (interfaces.md §1). They live in the Cards assembly because they return
    // CardDefinition (Cards → Contracts); declaring them in Contracts would create a dependency cycle.

    /// <summary>
    /// Card data access — the only way the core sees cards (card-system.md §1.3). Client and server
    /// provide different impls; both deserialize the identical CardDefinition POCO (R1).
    /// </summary>
    public interface ICardDatabase
    {
        bool TryGet(string cardId, out Cards.CardDefinition def);
        Cards.CardDefinition Get(string cardId);
        IReadOnlyCollection<Cards.CardDefinition> AllCollectible();
    }

    /// <summary>Shared card validator — runs headless at server/client load and in the editor (interfaces.md §4.3).</summary>
    public interface ICardValidator
    {
        ValidationReport Validate(Cards.CardDefinition def, ICardDatabase db);
    }
}
