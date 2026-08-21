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
    /// store's overlay and <see cref="PackRevealScreenController"/>'s standalone screen. A pack
    /// opened anywhere other than the store — the onboarding grant is the one already waiting —
    /// could not have had a reveal at all while this lived inside the store's controller.</para>
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

        /// <param name="root">Any element the <c>reveal-*</c> tree sits under.</param>
        /// <param name="onDone">What Done does after hiding. Null for a host that owns its own
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

            Button done = root.Q<Button>("btn-reveal-done");
            if (done != null) done.clicked += Done;

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
            var card = new VisualElement();
            card.AddToClassList("reveal-card");
            card.AddToClassList(RarityClass(award.Rarity));

            var flourish = new VisualElement();
            flourish.AddToClassList("reveal-card__flourish");
            card.Add(flourish);

            var rarity = new Label(award.Rarity.ToString());
            rarity.AddToClassList("reveal-card__rarity");
            card.Add(rarity);

            var name = new Label(CardName(award.CardId));
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

        private string CardName(string cardId)
        {
            try
            {
                _cards ??= CardDatabaseLoader.Load();
                if (_cards.TryGet(cardId, out CardDefinition definition)) return definition.Name;
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[PackReveal] card names unavailable: {ex.GetType().Name}: {ex.Message}");
            }
            return TokenText.Pretty(cardId);
        }

        private static string RarityClass(Rarity rarity) => rarity switch
        {
            Rarity.RARE => "reveal-card--rare",
            Rarity.EPIC => "reveal-card--epic",
            Rarity.LEGENDARY => "reveal-card--legendary",
            _ => "reveal-card--common",
        };

        private static string UpgradeLabel(PackUpgradeReason upgrade) => upgrade switch
        {
            PackUpgradeReason.RareFloor => "RARE+ GUARANTEED",
            PackUpgradeReason.EpicPity => "EPIC GUARANTEE",
            PackUpgradeReason.LegendaryPity => "LEGENDARY GUARANTEE",
            _ => "",
        };
    }
}
