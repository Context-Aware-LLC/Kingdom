using System;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client.Shop;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// The pack-opening moment: a <see cref="PackOpenReceipt"/> dealt out as a row of cards that
    /// arrive one after another. Split out of <see cref="ShopPanelController"/> on 2026-08-20,
    /// where it had been a second responsibility joined to the store by exactly two calls.
    ///
    /// <para><b>Host-agnostic, and that is the whole point of the split.</b> It queries its
    /// <c>reveal-*</c> elements out of whatever root it is handed, so the same panel serves the
    /// store's overlay and <see cref="PackVaultController"/>'s screen. A pack opened anywhere other
    /// than the store — the onboarding grant is the one already waiting — could not have had a
    /// reveal at all while this lived inside the store's controller. That prediction came true on
    /// 2026-09-03: the vault opens granted packs, and the frame this used to name
    /// (<c>PackRevealScreenController</c>) was deleted in the same commit.</para>
    ///
    /// <para><b>Not <see cref="CardRevealController"/>.</b> That is EF-049, the in-match hover
    /// reveal: MonoBehaviours, TextMeshPro, board colliders. Same word, unrelated subsystem. The
    /// <c>Pack</c> prefix here is load-bearing.</para>
    ///
    /// <para>Nothing here reads the wallet, the catalog or the gateway. A receipt is the entire
    /// input, and the receipt is server-authored — so this cannot invent an award.</para>
    /// </summary>
    public sealed class PackRevealPanel
    {
        private const string HiddenClass = "reveal-overlay--hidden";

        private readonly VisualElement _overlay;
        private readonly VisualElement _cardRow;
        private readonly Label _title;
        private readonly Label _summary;
        private readonly Action _onDone;

        private ICardDatabase _cards;
        private IArtProvider _art;

        /// <summary>Bumped by every <see cref="Show"/>. An art callback carrying a stale generation
        /// is painting onto a card that has already been cleared off the row — the same version
        /// guard <see cref="CardWall"/> keeps, and for the same reason: <see cref="IArtProvider"/>
        /// may answer synchronously or frames later, and a second pack opened during the wait would
        /// otherwise take the first one's illustrations.</summary>
        private int _generation;

        /// <param name="root">Any element the <c>reveal-*</c> tree sits under.</param>
        /// <param name="onDone">What dismissing does after hiding. Null for a host that owns its own
        /// dismissal — the store just wants its overlay back — and a way home for a host where the
        /// reveal IS the screen.</param>
        public PackRevealPanel(VisualElement root, Action onDone = null)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));

            _overlay = root.Q<VisualElement>("reveal-overlay");
            _cardRow = root.Q<VisualElement>("reveal-cards");
            _title = root.Q<Label>("reveal-title");
            _summary = root.Q<Label>("reveal-summary");
            _onDone = onDone;

            // A tap anywhere off a card (Sam, 2026-09-27): there is no Done button. The dismiss layer
            // sits behind the cards, so a card swallows its own tap and only the gaps reach this.
            VisualElement dismiss = root.Q<VisualElement>("reveal-dismiss");
            dismiss?.RegisterCallback<ClickEvent>(_ => Done());

            // Start hidden regardless of what the markup said. A host that forgets the hidden class
            // would otherwise open on an empty stage, which reads as a bug in the pack, not the UXML.
            Hide();
        }

        /// <summary>True when the host's markup actually carried a reveal tree. A host without one
        /// gets a panel that silently does nothing rather than a null to guard at every call.</summary>
        public bool IsBound => _overlay != null && _cardRow != null;

        public void Show(PackOpenReceipt receipt)
        {
            if (receipt == null || !IsBound) return;
            _cardRow.Clear();
            _generation++;
            if (_title != null) _title.text = TokenText.Pretty(receipt.PackId) + " opened";
            if (_summary != null)
                _summary.text = receipt.Awards.Count == 1
                    ? "1 card"
                    : $"{receipt.Awards.Count} cards";

            for (int i = 0; i < receipt.Awards.Count; i++)
            {
                VisualElement card = BuildRevealCard(receipt.Awards[i]);
                _cardRow.Add(card);
                int delay = 140 + (i * 170);
                card.schedule.Execute(() => card.AddToClassList("reveal-card--shown")).StartingIn(delay);
            }

            _overlay.RemoveFromClassList(HiddenClass);
        }

        public void Hide() => _overlay?.AddToClassList(HiddenClass);

        private void Done()
        {
            Hide();
            _onDone?.Invoke();
        }

        private VisualElement BuildRevealCard(PackCardAward award)
        {
            CardDefinition definition = Definition(award.CardId);

            var card = new VisualElement();
            card.AddToClassList("reveal-card");
            card.AddToClassList(CardClassTokens.RarityToken(award.Rarity, "reveal-card--"));

            var flourish = new VisualElement();
            flourish.AddToClassList("reveal-card__flourish");
            card.Add(flourish);

            // No rarity line (Sam, 2026-09-27): the border colour already says it.

            // THE ILLUSTRATION, not a baked card face. CardFaceBaker photographs the whole printed
            // front — frame, stats, rules text — off a 3D rig, and it needs that rig, a capture
            // camera and the CardCapture layer present in the scene. The reveal is its own card
            // shape drawn in USS (border by rarity, rarity line, name), so it wants the picture
            // alone; asking for a baked face would bind this host-agnostic panel to a scene it may
            // not be in. RETIREMENT TRIGGER: if the reveal is ever restyled to show real printed
            // cards, this is where it swaps to CardFaceBaker — and PackVault's scene grows a rig.
            var art = new VisualElement();
            art.AddToClassList("reveal-card__art");
            card.Add(art);
            PaintArt(art, definition);

            var name = new Label(definition != null ? definition.Name : $"Card {award.CardId}");
            name.AddToClassList("reveal-card__name");
            card.Add(name);

            // No award line. It read "NEW CARD" or "DUPE -> n SHARDS", and since the no-auto-sharding
            // ruling (Sam, 2026-08-12) every slot is a card — leaving "NEW CARD" would assert newness the
            // receipt no longer carries, and a third copy would be labelled new. Rarity, name and the
            // pity flourish are what the reveal has to say.
            if (award.Upgrade != PackUpgradeReason.None)
            {
                var upgrade = new Label(UpgradeLabel(award.Upgrade));
                upgrade.AddToClassList("reveal-card__upgrade");
                card.Add(upgrade);
            }

            return card;
        }

        /// <summary>The printed card behind an award, or null when the database will not open. Both
        /// the name and the illustration come off it, so one lookup answers both — and a card that
        /// cannot be resolved falls back to "Card {id}" rather than pretending to be a name.</summary>
        private CardDefinition Definition(int cardId)
        {
            try
            {
                _cards ??= CardDatabaseLoader.Load();
                if (_cards.TryGet(cardId, out CardDefinition definition)) return definition;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[PackReveal] card database unavailable: {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Paint one card's illustration, if it has one.
        ///
        /// <para><b>A card with no art is not a failure.</b> The element keeps its USS background and
        /// the card reads as a card without a picture — the same posture <c>CardFaceBaker</c> takes,
        /// because art lands per card and lags authoring by design.</para>
        /// </summary>
        private void PaintArt(VisualElement target, CardDefinition definition)
        {
            if (definition == null || string.IsNullOrEmpty(definition.ArtId)) return;

#if UNITY_EDITOR
            _art ??= new EditorArtProvider();
#else
            _art ??= new AddressableArtProvider();
#endif
            int generation = _generation;
            _art.LoadArt(definition.ArtId, sprite =>
            {
                if (sprite == null || generation != _generation) return;
                target.style.backgroundImage = new StyleBackground(sprite);
            });
        }

        private static string UpgradeLabel(PackUpgradeReason upgrade) => upgrade switch
        {
            PackUpgradeReason.RareFloor => "RARE+ GUARANTEED",
            PackUpgradeReason.EpicPity => "EPIC GUARANTEE",
            PackUpgradeReason.LegendaryPity => "LEGENDARY GUARANTEE",
            _ => "",
        };
    }
}
