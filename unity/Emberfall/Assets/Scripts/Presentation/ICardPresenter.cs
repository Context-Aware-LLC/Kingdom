using Emberfall.Core.Cards;

namespace Emberfall.Presentation
{
    // Presentation binding surface for a card GameObject (interfaces.md §5.1).
    // Named ICardPresenter to avoid collision with the read-model ICardView in Core.Contracts.
    public interface ICardPresenter
    {
        void Bind(CardDefinition staticDef, ICardLiveState liveState);
        void Unbind();
    }
}
