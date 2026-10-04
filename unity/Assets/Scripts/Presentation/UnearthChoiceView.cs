using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using TMPro;
using UnityEngine;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Snapshot-driven Unearth surface. The chooser receives real card-definition faces in the
    /// server's option order; the opponent sees a fixed OpponentBackCount of face-down cards,
    /// never a count inferred from the (redacted) real options.
    /// Reconnect needs no event replay because every snapshot is sufficient to rebuild this view.
    /// </summary>
    public sealed class UnearthChoiceView : MonoBehaviour
    {
        private const float CameraDistance = 10f;
        private const float CardGap = 0.28f;
        private const float MaxCardScale = 2f;
        private const float OfferWidthFraction = 0.86f;
        private const float CardHeightFraction = 0.57f;

        // Unearth is always "choose one of three" (card-schema.md "Interactive Unearth choices");
        // the opponent's back count is this constant, never a real option count.
        private const int OpponentBackCount = 3;

        private PlayerId _localPlayer;
        private InputRouter _router;
        private ICardDatabase _database;
        private BoardController _board;
        private IArtProvider _art;
        private Camera _camera;

        private GameObject _surface;
        private GameObject _cardsRoot;
        private TextMeshPro _title;
        private UnearthChoiceAudience _audience;
        private ulong _choiceSequence;
        private readonly List<GameObject> _cards = new List<GameObject>();
        private readonly List<int> _slotIndices = new List<int>();
        private int _slotCount;
        private float _cardScale = 1f;
        private FacingCardRow.Solution _row;

        public void Init(PlayerId localPlayer, InputRouter router, ICardDatabase database,
                         BoardController board, IArtProvider art)
        {
            _localPlayer = localPlayer;
            _router = router;
            _database = database;
            _board = board;
            _art = art;
            _camera = Camera.main;

            BuildSurface();
            LayoutSurface();
            SetVisible(false);
        }

        private void LateUpdate()
        {
            if (_surface != null && _surface.activeSelf)
                LayoutSurface();
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
                    BuildOpponentBacks();
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

            var titleGo = new GameObject("Title");
            titleGo.transform.SetParent(_surface.transform, false);
            _title = titleGo.AddComponent<TextMeshPro>();
            _title.alignment = TextAlignmentOptions.Center;
            _title.color = Color.white;
            _title.fontStyle = FontStyles.Bold;
            _title.fontSize = 2.1f;
            _title.textWrappingMode = TextWrappingModes.NoWrap;
            _title.rectTransform.sizeDelta = new Vector2(10.8f, 0.7f);
            BoardController.ApplyUiFont(_title);

            _cardsRoot = new GameObject("Options");
            _cardsRoot.transform.SetParent(_surface.transform, false);
            _cardsRoot.transform.localPosition = new Vector3(0f, -0.12f, -0.15f);
        }

        private void LayoutSurface()
        {
            if (_camera == null) _camera = Camera.main;
            if (_camera == null) return;

            // Keep the cards parallel to the screen and centered in its safe area. The board
            // remains visible behind them; the router still blocks gameplay until a choice lands.
            // The row solve is shared with the mulligan row (FacingCardRow, #401).
            int count = Mathf.Max(1, _slotCount);
            if (!FacingCardRow.TrySolve(_camera, FacingCardRow.SafeViewport(), CameraDistance, count,
                    CardGap, MaxCardScale, OfferWidthFraction, CardHeightFraction,
                    out FacingCardRow.Solution row))
                return;

            _row = row;
            _surface.transform.SetPositionAndRotation(row.Centre, row.Rotation);
            _cardScale = row.CardScale;
            _title.transform.localPosition = new Vector3(
                0f, BoardController.CardH * _cardScale * 0.5f + 0.52f, -0.1f);
            _title.rectTransform.sizeDelta = new Vector2(row.BandWidth * 0.9f, 0.7f);

            for (int i = 0; i < _cards.Count; i++)
                PositionSlot(_cards[i].transform, _slotIndices[i], count);
        }

        private void PositionSlot(Transform anchor, int index, int count)
        {
            anchor.localPosition = _row.SlotOffset(index, count);
            anchor.localScale = Vector3.one * _cardScale;
        }

        private void BuildOffer(IReadOnlyList<int> options, ulong choiceSequence)
        {
            if (_board == null || _database == null || options == null || options.Count == 0) return;
            _slotCount = options.Count;
            LayoutSurface();

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

                Transform anchor = PlaceCardSlot(index, options.Count, $"Option_{index}_{cardDefinitionId}");

                CardPresenter card = _board.BuildDetachedCard(anchor);
                card.gameObject.name = "Card";
                card.Bind(definition, new CardLiveState(new InstanceId(-(index + 1))));
                card.gameObject.AddComponent<UnearthChoiceCardButton>()
                    .Init(_router, cardDefinitionId, choiceSequence);

                if (_art != null && !string.IsNullOrEmpty(definition.ArtId))
                    _art.LoadArt(definition.ArtId, sprite =>
                    {
                        if (card != null) card.SetArt(sprite);
                    });

                _cards.Add(anchor.gameObject);
                _slotIndices.Add(index);
            }
        }

        // Never derived from a real option count (UnearthChoiceModel redacts it from the
        // opponent) — always OpponentBackCount, the fixed "choose one of three" offer shape.
        private void BuildOpponentBacks()
        {
            if (_board == null) return;
            _slotCount = OpponentBackCount;
            LayoutSurface();

            for (int index = 0; index < OpponentBackCount; index++)
            {
                Transform anchor = PlaceCardSlot(index, OpponentBackCount, $"Back_{index}");
                CardPresenter card = _board.BuildDetachedCard(anchor);
                card.gameObject.name = "Back";
                card.SetHidden();
                _cards.Add(anchor.gameObject);
                _slotIndices.Add(index);
            }
        }

        private Transform PlaceCardSlot(int index, int count, string name)
        {
            var anchor = new GameObject(name);
            anchor.transform.SetParent(_cardsRoot.transform, false);
            PositionSlot(anchor.transform, index, count);
            return anchor.transform;
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
            _slotIndices.Clear();
            _slotCount = 0;
            _choiceSequence = 0;
        }

        private void SetVisible(bool visible)
        {
            if (_surface != null && _surface.activeSelf != visible)
                _surface.SetActive(visible);
        }
    }

    /// <summary>Runtime-only click adapter for a detached definition card.</summary>
    internal sealed class UnearthChoiceCardButton : MonoBehaviour, IPointerPressTarget
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

        void IPointerPressTarget.OnPointerDown()
            => _router?.OnUnearthChoiceClicked(_cardDefinitionId, _choiceSequence);
    }
}
