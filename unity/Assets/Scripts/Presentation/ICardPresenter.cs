using Kingdom.Core.Cards;

namespace Kingdom.Presentation
{
    // Presentation binding surface for a card GameObject (hist-docs/source-snapshots/2026-07-31-pre-reorganization/engineering/interfaces.md §5.1).
    // Named ICardPresenter to avoid collision with the read-model ICardView in Core.Contracts.
    public interface ICardPresenter
    {
        void Bind(CardDefinition staticDef, ICardLiveState liveState);
        void Unbind();
    }
}
