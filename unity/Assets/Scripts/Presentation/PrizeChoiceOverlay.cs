using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Kingdom.Core.Collection;
using Kingdom.Net.Client.King;
using Kingdom.Session;
using UnityEngine;
using UnityEngine.UIElements;

namespace Kingdom.Presentation
{
    /// <summary>
    /// A repeat top-three place's prize (King of the Week, #421): one card back the player does not
    /// own, picked from the pool. <see cref="MainMenuController"/> opens it on arrival when
    /// <c>GET /king/prize</c> has a choice open, and only into a quiet menu.
    ///
    /// <para><b>Later leaves the choice open.</b> Nothing expires (spec, "Prizes"): the server holds
    /// the choice until it is claimed, and the next arrival offers it again.</para>
    ///
    /// <para><b>The server decides.</b> The offered backs are the server's, already less the ones the
    /// player owns, and a claim is a request. After every claim the screen reads the choices again
    /// and shows only what the server still offers, with a line saying what became of the claim:
    /// the back the server recorded, gold instead, a refusal, or, when a claim failed but its reign
    /// is gone from the list, that the gift is on its way. Several open choices are offered one
    /// after another.</para>
    ///
    /// <para>Built as its own UIDocument through Resources and read on the herald's scroll, the way
    /// <see cref="SuccessionRollOverlay"/> is.</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class PrizeChoiceOverlay : MonoBehaviour
    {
        private const int TimeoutSeconds = 10;

        /// <summary>The overlay's GameObject name. The menu's arrival sequence finds an open one by it.</summary>
        public const string ObjectName = "PrizeChoiceUI";

        private Label _body;
        private Label _status;
        private ScrollView _list;
        private Button _later;
        private Button _claim;
        private ProclamationScroll _scroll;
        private IUiSpriteProvider _sprites;

        private PrizeChoiceOffer _offer;
        private int _selected;
        private bool _busy;
        private bool _closing;

        /// <summary>Open on <paramref name="offers"/>[0]. Returns the overlay's GameObject.</summary>
        public static GameObject Open(IReadOnlyList<PrizeChoiceOffer> offers)
        {
            var go = new GameObject(ObjectName);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings   = Resources.Load<PanelSettings>("MatchSettingsPanelSettings");
            doc.visualTreeAsset = Resources.Load<VisualTreeAsset>("PrizeChoice");
            go.AddComponent<PrizeChoiceOverlay>().Init(offers);
            return go;
        }

        private void Init(IReadOnlyList<PrizeChoiceOffer> offers)
        {
            VisualElement root = GetComponent<UIDocument>().rootVisualElement;
            if (root == null)
            {
                Debug.LogError("[PrizeChoice] No rootVisualElement: is PrizeChoice.uxml in a Resources folder?");
                Destroy(gameObject);
                return;
            }
            UiLocalizer.Attach(root);
            _sprites = CardBackCatalog.NewProvider();

            _body = root.Q<Label>("prize-body");
            _status = root.Q<Label>("prize-status");
            _list = root.Q<ScrollView>("prize-backs");
            _later = root.Q<Button>("prize-later");
            _claim = root.Q<Button>("prize-claim");
            if (_later != null) _later.clicked += Close;
            if (_claim != null) _claim.clicked += () => _ = ClaimAsync();

            VisualElement sheet = root.Q<VisualElement>("prize-sheet");
            if (sheet?.parent != null) _scroll = new ProclamationScroll(sheet);

            if (!Show(offers)) { Destroy(gameObject); return; }
            _scroll?.Open();
        }

        private void Close()
        {
            // A tap while the scroll moves is a tap on a button half behind a roller.
            if (_closing || _busy || _scroll?.IsMoving == true) return;
            _closing = true;
            if (_scroll == null) { Destroy(gameObject); return; }
            _scroll.Close(() => { if (this != null) Destroy(gameObject); });
        }

        /// <summary>Shows the first offer with a back this build can draw. False when there is none.</summary>
        private bool Show(IReadOnlyList<PrizeChoiceOffer> offers)
        {
            foreach (PrizeChoiceOffer offer in offers)
            {
                // A newer server can offer a back this build has no art for; drawing it as the
                // default would show the player something they are not getting.
                var known = new List<int>();
                foreach (int id in offer.BackIds)
                    if (CardBackDefinitions.TryGet(id, out _)) known.Add(id);
                if (known.Count == 0) continue;

                _offer = new PrizeChoiceOffer(offer.Reign, offer.Place, known);
                _selected = 0;
                if (_body != null) _body.text = BodyFor(_offer);
                SetStatus(null);
                BuildBacks();
                SetFooter(claimable: true);
                return true;
            }
            _offer = null;
            return false;
        }

        /// <summary>Three literal calls, not one with a built key, so LocalizationVerify sees each.</summary>
        private static string BodyFor(PrizeChoiceOffer offer)
        {
            switch (offer.Place)
            {
                case 1: return Loc.Format("king_prize_body_first", offer.Reign);
                case 2: return Loc.Format("king_prize_body_second", offer.Reign);
                default: return Loc.Format("king_prize_body_third", offer.Reign);
            }
        }

        private void BuildBacks()
        {
            if (_list == null || _offer == null) return;
            _list.Clear();
            var grid = new VisualElement();
            grid.AddToClassList("prize-grid");
            foreach (int id in _offer.BackIds)
            {
                int choice = id;
                CardBackDefinition back = CardBackCatalog.Resolve(choice);
                var tile = new Button();
                tile.AddToClassList("prize-back");
                tile.EnableInClassList("prize-back--on", choice == _selected);
                tile.tooltip = back.Name;
                tile.clicked += () =>
                {
                    if (_busy) return;
                    _selected = choice;
                    // Re-mark in place rather than rebuild, so the list keeps its scroll.
                    foreach (VisualElement other in grid.Children())
                        other.EnableInClassList("prize-back--on", other == tile);
                    _claim?.SetEnabled(true);
                };

                var art = new VisualElement();
                art.AddToClassList("prize-back__art");
                art.pickingMode = PickingMode.Ignore;
                Sprite sprite = _sprites?.Get(back.SpriteKey);
                if (sprite != null) art.style.backgroundImage = new StyleBackground(sprite);
                tile.Add(art);
                grid.Add(tile);
            }
            _list.Add(grid);
            _claim?.SetEnabled(!_busy && _selected != 0);
        }

        private async Task ClaimAsync()
        {
            if (_busy || _closing || _offer == null || _selected == 0) return;
            _busy = true;
            _claim?.SetEnabled(false);
            _later?.SetEnabled(false);
            SetStatus(null);

            int reign = _offer.Reign;
            PrizeChooseResult result;
            IReadOnlyList<PrizeChoiceOffer> remaining = null;
            try
            {
                var gateway = new HttpKingGateway(CollectionGateway.BaseUrl, new UnityWebRequestAuthHttp(TimeoutSeconds));
                string token = await AuthGateway.TokenForConnectAsync();
                result = await gateway.ChooseBackAsync(token, reign, _selected);
                // Whatever the answer, what to show next is the server's list: even a failed claim
                // may have closed the choice. Null means it could not be read.
                remaining = await gateway.FetchPrizeChoicesAsync(token);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PrizeChoice] claim threw ({ex.GetType().Name}: {ex.Message}).");
                result = PrizeChooseResult.Failed;
            }

            if (this == null) return;   // the menu left while the claim was in flight
            _busy = false;
            _later?.SetEnabled(true);

            if (result.Outcome == PrizeChooseOutcome.Failed && (remaining == null || Offers(remaining, reign)))
            {
                // Not known to have closed: the same offer stays, to try again.
                SetStatus(Loc.Get("king_prize_failed"));
                _claim?.SetEnabled(_selected != 0);
                return;
            }

            string message = MessageFor(result);
            // Only ever the server's list, so a reign it has closed is never offered again.
            if (remaining != null && Show(remaining))
            {
                SetStatus(message);
                return;
            }

            // Nothing left to offer: say what became of this one, and leave only Close.
            _list?.Clear();
            SetStatus(message);
            SetFooter(claimable: false);
        }

        private static bool Offers(IReadOnlyList<PrizeChoiceOffer> offers, int reign)
        {
            foreach (PrizeChoiceOffer offer in offers)
                if (offer.Reign == reign) return true;
            return false;
        }

        /// <summary>
        /// What became of the claim. A granted back is named from the server's answer, never the
        /// tile tapped: a repeated claim is told the back the first one closed on. A failed claim
        /// that reaches here closed anyway (the server no longer offers its reign), so its gift is
        /// on the way and the next visit delivers it.
        /// </summary>
        private static string MessageFor(PrizeChooseResult result)
        {
            switch (result.Outcome)
            {
                case PrizeChooseOutcome.Granted:
                    return result.BackId is int id && CardBackDefinitions.TryGet(id, out CardBackDefinition back)
                        ? Loc.Format("king_prize_granted", back.Name)
                        : Loc.Get("king_prize_granted_unnamed");
                case PrizeChooseOutcome.Gold:
                    return Loc.Format("king_prize_gold", result.Gold ?? 0);
                case PrizeChooseOutcome.Invalid:
                    return Loc.Get("king_prize_refused");
                case PrizeChooseOutcome.None:
                    return Loc.Get("king_prize_gone");
                default:
                    return Loc.Get("king_prize_on_its_way");
            }
        }

        private void SetFooter(bool claimable)
        {
            if (_claim != null)
            {
                _claim.style.display = claimable ? DisplayStyle.Flex : DisplayStyle.None;
                _claim.SetEnabled(claimable && !_busy && _selected != 0);
            }
            if (_later != null)
                _later.text = claimable ? Loc.Get("king_prize_later") : Loc.Get("common_close");
        }

        private void SetStatus(string text)
        {
            if (_status != null) _status.text = string.IsNullOrEmpty(text) ? " " : text;
        }
    }
}
