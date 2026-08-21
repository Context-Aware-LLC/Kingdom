using System;
using System.Collections.Generic;
using Emberfall.Core.Cards;
using Emberfall.Core.Contracts;
using Emberfall.Net.Client.Shop;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Unity-only seam for the approved booster faces. The art delivery can bind sprites by the
    /// catalog's stable art keys without teaching the transport layer about Unity assets.
    /// </summary>
    public interface IBoosterPackagingArtProvider
    {
        void LoadArt(string artKey, System.Action<Sprite> onLoaded);
    }

    /// <summary>
    /// Composition seam for the account transport delivered with the server wallet/purchase path.
    /// The menu can render its locked catalog without one, but all purchase controls stay disabled.
    /// </summary>
    public static class ShopRuntime
    {
        public static IShopGateway Gateway { get; set; }
        public static IBoosterPackagingArtProvider PackagingArt { get; set; }
    }

    /// <summary>
    /// Binds the store's catalog and pack reveal. Pricing and placeholder art slots may render while
    /// disconnected; balances, purchase success, and every reveal award come only from
    /// <see cref="IShopGateway"/>.
    ///
    /// <para>Host-agnostic on purpose: it queries its elements by name and hands Back back to
    /// whoever built it. <see cref="ShopScreenController"/> is that host today, and leaves for its
    /// return scene — but nothing here knows it lives in a scene of its own.</para>
    /// </summary>
    public sealed class ShopPanelController
    {
        private readonly VisualElement _overlay;
        private readonly VisualElement _packList;
        private readonly VisualElement _gemList;
        private readonly Label _gold;
        private readonly Label _gems;
        private readonly Label _status;
        private readonly VisualElement _revealOverlay;
        private readonly VisualElement _revealCards;
        private readonly Label _revealTitle;
        private readonly Label _revealSummary;
        private readonly ShopFlow _flow;
        private readonly IBoosterPackagingArtProvider _packagingArt;
        private readonly System.Action _onClose;

        private ICardDatabase _cards;
        private bool _busy;

        public ShopPanelController(
            VisualElement root,
            IShopGateway gateway,
            IBoosterPackagingArtProvider packagingArt,
            System.Action onClose)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            // Unlike the gateway, this one is structural: with no way home, Back is a dead button.
            if (onClose == null) throw new ArgumentNullException(nameof(onClose));

            _overlay = root.Q<VisualElement>("shop-overlay");
            _packList = root.Q<VisualElement>("shop-pack-list");
            _gemList = root.Q<VisualElement>("shop-gem-list");
            _gold = root.Q<Label>("shop-gold-balance");
            _gems = root.Q<Label>("shop-gem-balance");
            _status = root.Q<Label>("shop-status");
            _revealOverlay = root.Q<VisualElement>("reveal-overlay");
            _revealCards = root.Q<VisualElement>("reveal-cards");
            _revealTitle = root.Q<Label>("reveal-title");
            _revealSummary = root.Q<Label>("reveal-summary");
            _flow = gateway != null ? new ShopFlow(gateway) : null;
            _packagingArt = packagingArt;
            _onClose = onClose;

            Wire(root, "btn-shop-close", Close);
            Wire(root, "btn-reveal-done", CloseReveal);
            RenderDisconnectedCatalog();
        }

        public void Open()
        {
            if (_overlay == null) return;
            _overlay.RemoveFromClassList("shop-overlay--hidden");

            // With no gateway the constructor's disconnected catalog is already on screen, wallet
            // included — an unbound wallet reads as unavailable, never as zero.
            if (_flow == null)
            {
                SetStatus("Purchases are waiting for the server wallet connection.");
                return;
            }

            Refresh();
        }

        private async void Refresh()
        {
            if (_flow == null || _busy) return;
            SetBusy(true);
            SetStatus("Loading wallet...");
            try
            {
                await _flow.RefreshAsync();
                RenderSnapshot(_flow.Snapshot);
                SetStatus("");
            }
            catch (Exception ex)
            {
                SetStatus("The shop could not reach the account server.");
                UnityEngine.Debug.LogWarning($"[Shop] refresh failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void Buy(string packId, ShopCurrency currency)
        {
            if (_flow == null || _busy) return;
            SetBusy(true);
            SetStatus("Purchasing and opening on the server...");
            try
            {
                PackPurchaseResult result = await _flow.PurchaseAsync(packId, currency);
                RenderSnapshot(result.Shop);
                ShowReveal(result.OpenedPack);
                SetStatus("");
            }
            catch (Exception ex)
            {
                SetStatus("Purchase failed. Your displayed balance has not been changed.");
                UnityEngine.Debug.LogWarning($"[Shop] purchase failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void RenderDisconnectedCatalog()
        {
            SetWalletUnavailable();
            RenderPacks(ShopCatalog.Packs, null);
            RenderGemBundles(ShopCatalog.GemBundles);
        }

        private void RenderSnapshot(ShopSnapshot snapshot)
        {
            if (snapshot == null) return;
            if (_gold != null) _gold.text = $"Gold  {snapshot.Wallet.Gold:N0}";
            if (_gems != null) _gems.text = WalletDisplay.Gems(snapshot.Wallet.Gems);
            RenderPacks(snapshot.Packs, snapshot.Wallet);
            RenderGemBundles(snapshot.GemBundles);
        }

        private void SetWalletUnavailable()
        {
            if (_gold != null) _gold.text = "Gold  —";
            if (_gems != null) _gems.text = "Gems  —";
        }

        private void RenderPacks(IReadOnlyList<PackOffer> offers, WalletBalances? wallet)
        {
            if (_packList == null) return;
            _packList.Clear();

            for (int i = 0; i < offers.Count; i++)
            {
                PackOffer offer = offers[i];
                var tile = new VisualElement();
                tile.AddToClassList("shop-pack");

                var artRail = new VisualElement();
                artRail.AddToClassList("shop-pack__art-rail");
                for (int art = 0; art < offer.PackagingArtKeys.Count; art++)
                    artRail.Add(BuildArtSlot(offer.PackagingArtKeys[art]));
                tile.Add(artRail);

                var name = new Label(offer.DisplayName);
                name.AddToClassList("shop-pack__name");
                tile.Add(name);

                var detail = new Label("5 cards · Core + expansion · Rare or better guaranteed");
                detail.AddToClassList("shop-pack__detail");
                tile.Add(detail);

                var actions = new VisualElement();
                actions.AddToClassList("shop-pack__actions");
                actions.Add(BuyButton(offer, ShopCurrency.Gold, wallet));
                actions.Add(BuyButton(offer, ShopCurrency.Gems, wallet));
                tile.Add(actions);

                _packList.Add(tile);
            }
        }

        private VisualElement BuildArtSlot(string artKey)
        {
            var slot = new VisualElement { name = artKey };
            slot.AddToClassList("shop-pack__art-slot");

            var label = new Label(PrettyToken(artKey));
            label.AddToClassList("shop-pack__art-slot-label");
            slot.Add(label);

            _packagingArt?.LoadArt(artKey, sprite =>
            {
                if (sprite == null) return;
                slot.style.backgroundImage = new StyleBackground(sprite);
                slot.AddToClassList("shop-pack__art-slot--loaded");
            });
            return slot;
        }

        private Button BuyButton(PackOffer offer, ShopCurrency currency, WalletBalances? wallet)
        {
            int price = currency == ShopCurrency.Gold ? offer.GoldPrice : offer.GemPrice;
            string label = currency == ShopCurrency.Gold ? "Gold" : "Gems";
            var button = new Button(() => Buy(offer.PackId, currency))
            {
                text = $"{price:N0} {label}",
            };
            button.AddToClassList("shop-pack__buy");
            button.AddToClassList(currency == ShopCurrency.Gold
                ? "shop-pack__buy--gold"
                : "shop-pack__buy--gems");

            bool canAfford = wallet.HasValue
                && (currency == ShopCurrency.Gold
                    ? wallet.Value.Gold >= price
                    : wallet.Value.Gems >= price);
            button.SetEnabled(_flow != null && !_busy && canAfford);
            return button;
        }

        private void RenderGemBundles(IReadOnlyList<GemBundleOffer> bundles)
        {
            if (_gemList == null) return;
            _gemList.Clear();

            for (int i = 0; i < bundles.Count; i++)
            {
                GemBundleOffer bundle = bundles[i];
                var row = new VisualElement();
                row.AddToClassList("shop-gem");

                var amount = new Label($"{bundle.Gems:N0} Gems");
                amount.AddToClassList("shop-gem__amount");
                row.Add(amount);

                var price = new Label(FormatUsd(bundle.PriceCents));
                price.AddToClassList("shop-gem__price");
                row.Add(price);
                _gemList.Add(row);
            }
        }

        private void ShowReveal(PackOpenReceipt receipt)
        {
            if (receipt == null || _revealOverlay == null || _revealCards == null) return;
            _revealCards.Clear();
            if (_revealTitle != null) _revealTitle.text = PrettyToken(receipt.PackId) + " opened";

            // "n new cards" / "n cards + shards from duplicates" both went with the no-auto-sharding
            // ruling: a pack yields cards, and the receipt no longer says which of them are new.
            if (_revealSummary != null)
                _revealSummary.text = receipt.Awards.Count == 1
                    ? "1 card"
                    : $"{receipt.Awards.Count} cards";

            for (int i = 0; i < receipt.Awards.Count; i++)
            {
                VisualElement card = BuildRevealCard(receipt.Awards[i]);
                _revealCards.Add(card);
                int delay = 140 + (i * 170);
                card.schedule.Execute(() => card.AddToClassList("reveal-card--shown")).StartingIn(delay);
            }

            _revealOverlay.RemoveFromClassList("shop-overlay--hidden");
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
                UnityEngine.Debug.LogWarning($"[Shop] card names unavailable: {ex.GetType().Name}: {ex.Message}");
            }
            return PrettyToken(cardId);
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            ShopSnapshot snapshot = _flow?.Snapshot;
            if (snapshot != null) RenderPacks(snapshot.Packs, snapshot.Wallet);
            else RenderPacks(ShopCatalog.Packs, null);
        }

        private void SetStatus(string message)
        {
            if (_status != null) _status.text = message ?? "";
        }

        /// <summary>Back. The overlay is deliberately left visible: the host decides what
        /// replaces this screen, and hiding it first would show a bare panel for the frame or
        /// two that a scene load takes to arrive.</summary>
        private void Close()
        {
            CloseReveal();
            _onClose();
        }

        private void CloseReveal()
        {
            _revealOverlay?.AddToClassList("shop-overlay--hidden");
        }

        private static void Wire(VisualElement root, string buttonName, System.Action action)
        {
            Button button = root.Q<Button>(buttonName);
            if (button != null) button.clicked += action;
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

        private static string PrettyToken(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            int split = Math.Max(raw.LastIndexOf('.'), raw.LastIndexOf('_'));
            string token = split >= 0 ? raw.Substring(split + 1) : raw;
            token = token.Replace('-', ' ');
            return token.Length == 0
                ? token
                : char.ToUpperInvariant(token[0]) + token.Substring(1).ToLowerInvariant();
        }

        private static string FormatUsd(int cents) => $"${cents / 100}.{cents % 100:00}";
    }
}
