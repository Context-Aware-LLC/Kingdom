using System;
using System.Collections.Generic;
using Emberfall.Net.Client.Shop;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Binds the store's catalog and purchase flow. Pricing and placeholder art slots may render
    /// while disconnected; balances and purchase success come only from <see cref="IShopGateway"/>.
    ///
    /// <para>Host-agnostic on purpose: it queries its elements by name and hands Back back to
    /// whoever built it. <see cref="ShopScreenController"/> is that host today, and leaves for its
    /// return scene — but nothing here knows it lives in a scene of its own.</para>
    ///
    /// <para><b>The reveal is not here.</b> Opening a pack is <see cref="PackRevealPanel"/>, which
    /// this owns but does not reach into — the store hands it a receipt and hides it on the way
    /// out, and that is the entire relationship. It was inlined until 2026-08-20, which left the
    /// only pack-opening surface in the game locked inside the only screen that sells them.</para>
    ///
    /// <para><b>A host may take the reveal somewhere else.</b> Pass <c>revealElsewhere</c> and the
    /// store hands the receipt over instead of showing its own overlay — that is how
    /// <see cref="ShopScreenController"/> sends it to the PackReveal scene. It returns a bool
    /// rather than being an <see cref="Action"/> because the handoff can fail for a reason the
    /// host discovers and the store must survive: a scene missing from the build list. False means
    /// "I did not take it", and the overlay runs as it always did. <b>Never drop a receipt on that
    /// path</b> — the cards are already bought and server-side, so a silent no-show is the one
    /// outcome the player cannot tell apart from being robbed.</para>
    /// </summary>
    public sealed class ShopPanelController
    {
        private readonly VisualElement _overlay;
        private readonly VisualElement _packList;
        private readonly VisualElement _gemList;
        private readonly Label _gold;
        private readonly Label _gems;
        private readonly Label _status;
        private readonly PackRevealPanel _reveal;
        private readonly ShopFlow _flow;
        private readonly IPackArtProvider _packArt;
        private readonly System.Action _onClose;
        private readonly Func<PackOpenReceipt, bool> _revealElsewhere;

        private bool _busy;

        public ShopPanelController(
            VisualElement root,
            IShopGateway gateway,
            IPackArtProvider packArt,
            System.Action onClose,
            Func<PackOpenReceipt, bool> revealElsewhere = null)
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
            _flow = gateway != null ? new ShopFlow(gateway) : null;
            _packArt = packArt;
            _onClose = onClose;
            _revealElsewhere = revealElsewhere;

            // No onDone: here the reveal is an overlay over a screen the player is still on, so
            // dismissing it means returning to the store, which is what hiding it already does.
            _reveal = new PackRevealPanel(root);

            Wire(root, "btn-shop-close", Close);
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

                // Hand off if a host wants the reveal on its own stage; fall back to the overlay
                // when it declines, so the receipt is always shown somewhere.
                if (_revealElsewhere == null || !_revealElsewhere(result.OpenedPack))
                {
                    _reveal.Show(result.OpenedPack);
                }

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

                // The rail is added only if the set actually has art. An empty rail is a visible
                // hole in the tile, and a pack whose art has not been drawn yet should read as a
                // pack without a picture, not as a picture that failed to load.
                _packArt?.LoadPackArt(offer.PackId, pieces =>
                {
                    if (pieces == null || pieces.Count == 0) return;
                    var artRail = new VisualElement();
                    artRail.AddToClassList("shop-pack__art-rail");
                    for (int art = 0; art < pieces.Count; art++)
                        artRail.Add(BuildArtSlot(pieces[art]));
                    tile.Insert(0, artRail);
                });

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

        private static VisualElement BuildArtSlot(PackArtPiece piece)
        {
            // Named for the PIECE, not its file: this is the element name a later gallery or a
            // profile picker would look up, and the id is the half that does not move.
            var slot = new VisualElement { name = piece.Id };
            slot.AddToClassList("shop-pack__art-slot");
            slot.AddToClassList("shop-pack__art-slot--loaded");
            slot.style.backgroundImage = new StyleBackground(piece.Sprite);

            // The title stays in the tree behind the art rather than being dropped once a sprite
            // lands — it is what a screen reader and a tooltip have to work with, and the slot is
            // otherwise a nameless rectangle.
            var label = new Label(piece.Title);
            label.AddToClassList("shop-pack__art-slot-label");
            slot.Add(label);
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
            _reveal.Hide();
            _onClose();
        }

        private static void Wire(VisualElement root, string buttonName, System.Action action)
        {
            Button button = root.Q<Button>(buttonName);
            if (button != null) button.clicked += action;
        }

        private static string FormatUsd(int cents) => $"${cents / 100}.{cents % 100:00}";
    }
}
