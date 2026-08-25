using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Snapshot-driven Unearth surface. The chooser receives real card-definition faces in the
    /// server's option order; the opponent receives only a passive status with no inferred count.
    /// Reconnect needs no event replay because every snapshot is sufficient to rebuild this view.
    /// </summary>
    public sealed class UnearthChoiceView : MonoBehaviour
    {
        private static readonly Color ScrimColor = new Color(0.035f, 0.03f, 0.055f, 0.94f);

        private const float SurfaceZ = -2f;
        private const float CardGap = 0.28f;
        private const float MaxOfferWidth = 10.8f;
        private const float MaxCardScale = 0.76f;

        private PlayerId _localPlayer;
        private InputRouter _router;
        private ICardDatabase _database;
        private BoardController _board;
        private IArtProvider _art;

        private GameObject _surface;
        private GameObject _cardsRoot;
        private TextMeshPro _title;
        private UnearthChoiceAudience _audience;
        private ulong _choiceSequence;
        private readonly List<GameObject> _cards = new List<GameObject>();

        public void Init(PlayerId localPlayer, InputRouter router, ICardDatabase database,
                         BoardController board, IArtProvider art)
        {
            _localPlayer = localPlayer;
            _router = router;
            _database = database;
            _board = board;
            _art = art;

            transform.position = new Vector3(0f, 0.15f, SurfaceZ);
            BuildSurface();
            SetVisible(false);
        }

        public void OnStateUpdated(GameState state)
        {
            if (_surface == null) return;

            UnearthChoicePresentation presentation =
                UnearthChoiceModel.ForViewer(state, _localPlayer);
            if (presentation.Audience == UnearthChoiceAudience.None)
            {
                ClearOffer();
                _audience = UnearthChoiceAudience.None;
                SetVisible(false);
                return;
            }

            SetVisible(true);
            if (presentation.Audience == UnearthChoiceAudience.Opponent)
            {
                if (_audience != UnearthChoiceAudience.Opponent)
                {
                    ClearOffer();
                    _title.text = "OPPONENT IS UNEARTHING…";
                }
                _audience = UnearthChoiceAudience.Opponent;
                return;
            }

            _title.text = "CHOOSE A CARD TO UNEARTH";
            bool sameOffer = _audience == UnearthChoiceAudience.Chooser
                && _choiceSequence == presentation.ChoiceSequence;
            _audience = UnearthChoiceAudience.Chooser;
            if (sameOffer) return;

            ClearOffer();
            _choiceSequence = presentation.ChoiceSequence;
            BuildOffer(presentation.Options, presentation.ChoiceSequence);
        }

        private void BuildSurface()
        {
            _surface = new GameObject("Surface");
            _surface.transform.SetParent(transform, false);

            var scrim = GameObject.CreatePrimitive(PrimitiveType.Quad);
            scrim.name = "Scrim";
            scrim.transform.SetParent(_surface.transform, false);
            scrim.transform.localPosition = new Vector3(0f, 0f, 0.5f);
            scrim.transform.localScale = new Vector3(15.5f, 8.5f, 1f);
            Destroy(scrim.GetComponent<MeshCollider>());
            Renderer scrimRenderer = scrim.GetComponent<Renderer>();
            MaterialCatalog.Assign(scrimRenderer, MaterialCatalog.Plate);
            var block = new MaterialPropertyBlock();
            scrimRenderer.GetPropertyBlock(block);
            block.SetColor("_BaseColor", ScrimColor);
            scrimRenderer.SetPropertyBlock(block);

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(_surface.transform, false);
            titleGo.transform.localPosition = new Vector3(0f, 2.55f, -0.1f);
            _title = titleGo.AddComponent<TextMeshPro>();
            _title.alignment = TextAlignmentOptions.Center;
            _title.color = Color.white;
            _title.fontStyle = FontStyles.Bold;
            _title.fontSize = 2.1f;
            _title.textWrappingMode = TextWrappingModes.NoWrap;
            _title.rectTransform.sizeDelta = new Vector2(10.8f, 0.7f);

            _cardsRoot = new GameObject("Options");
            _cardsRoot.transform.SetParent(_surface.transform, false);
            _cardsRoot.transform.localPosition = new Vector3(0f, -0.1f, -0.15f);
        }

        private void BuildOffer(IReadOnlyList<int> options, ulong choiceSequence)
        {
            if (_board == null || _database == null || options == null || options.Count == 0) return;

            float unscaledWidth = options.Count * BoardController.CardW
                + (options.Count - 1) * CardGap;
            float cardScale = Mathf.Min(MaxCardScale, MaxOfferWidth / unscaledWidth);
            float step = (BoardController.CardW + CardGap) * cardScale;
            float firstX = -0.5f * step * (options.Count - 1);

            // Options order is authoritative RNG/replay order. Deliberately enumerate by index and
            // never sort or project through a keyed collection.
            for (int index = 0; index < options.Count; index++)
            {
                int cardDefinitionId = options[index];
                if (!_database.TryGet(cardDefinitionId, out CardDefinition definition))
                {
                    Debug.LogError($"[UnearthChoiceView] unknown offered card definition {cardDefinitionId}.");
                    continue;
                }

                var anchor = new GameObject($"Option_{index}_{cardDefinitionId}");
                anchor.transform.SetParent(_cardsRoot.transform, false);
                anchor.transform.localPosition = new Vector3(firstX + index * step, 0f, 0f);
                anchor.transform.localScale = Vector3.one * cardScale;

                CardPresenter card = _board.BuildDetachedCard(anchor.transform);
                card.gameObject.name = "Card";
                card.Bind(definition, new CardLiveState(new InstanceId(-(index + 1))));
                card.gameObject.AddComponent<UnearthChoiceCardButton>()
                    .Init(_router, cardDefinitionId, choiceSequence);

                if (_art != null && !string.IsNullOrEmpty(definition.ArtId))
                    _art.LoadArt(definition.ArtId, sprite =>
                    {
                        if (card != null) card.SetArt(sprite);
                    });

                _cards.Add(anchor);
            }
        }

        private void ClearOffer()
        {
            foreach (GameObject card in _cards)
            {
                if (card == null) continue;
                card.SetActive(false);
                Destroy(card);
            }
            _cards.Clear();
            _choiceSequence = 0;
        }

        private void SetVisible(bool visible)
        {
            if (_surface != null && _surface.activeSelf != visible)
                _surface.SetActive(visible);
        }
    }

    /// <summary>Runtime-only click adapter for a detached definition card.</summary>
    internal sealed class UnearthChoiceCardButton : MonoBehaviour
    {
        private InputRouter _router;
        private int _cardDefinitionId;
        private ulong _choiceSequence;

        public void Init(InputRouter router, int cardDefinitionId, ulong choiceSequence)
        {
            _router = router;
            _cardDefinitionId = cardDefinitionId;
            _choiceSequence = choiceSequence;
        }

        private void OnMouseDown()
            => _router?.OnUnearthChoiceClicked(_cardDefinitionId, _choiceSequence);
    }
}
