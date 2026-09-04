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
        /// <summary>
        /// How many pack faces the tile shows: one per CHARACTER, four of them (Sam, 2026-08-31).
        ///
        /// <para>The rail is a fixed-ratio strip whose slots split its width evenly, so this is a
        /// SIZE control: seven slots in a 326dp rail are 46dp each, which reads as a row of
        /// thumbnails rather than as pack art. Four is the count REKINDLING already has, so both
        /// packs render the same shape.</para>
        ///
        /// <para><b>Do not implement this as "take the first four" — it selects the wrong four.</b>
        /// EMBERFALL declares seven pieces but only FOUR characters: <c>emberfall_pyra_1..4</c> are
        /// alternate arts of ONE face, intended as holo/animated collectibles (Sam), and the
        /// remaining three pieces are Maren, Sorrow and Thalen. Truncating the list would show
        /// Cinderbound I–IV and drop three characters entirely. <see cref="FaceOf"/> collapses the
        /// variants first, and the cap then applies to faces.</para>
        ///
        /// <para>Capped HERE and not in the manifest on purpose — the manifest is the record of
        /// what art exists, and the variants are the point of it. Pieces are dropped from the
        /// display, never from the set.</para>
        /// </summary>
        private const int ArtFacesShown = 4;

        private readonly VisualElement _overlay;
        private readonly VisualElement _packList;
        private readonly VisualElement _gemList;
        private readonly VisualElement _packSection;
        private readonly VisualElement _gemSection;
        private readonly Button _packTab;
        private readonly Button _gemTab;
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

            _packSection = root.Q<VisualElement>("shop-section-packs");
            _gemSection = root.Q<VisualElement>("shop-section-gems");
            _packTab = root.Q<Button>("shop-tab-packs");
            _gemTab = root.Q<Button>("shop-tab-gems");
            if (_packTab != null) _packTab.clicked += () => ShowSection(packs: true);
            if (_gemTab != null) _gemTab.clicked += () => ShowSection(packs: false);
            // Not a no-op even though the UXML ships the same state: the markup's classes and this
            // field are two copies of one fact, and a tab bar whose highlight disagrees with what
            // is on screen is the failure this forecloses. Cheap, and it runs once.
            ShowSection(packs: true);

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
            if (_gold != null) _gold.text = "";
            if (_gems != null) _gems.text = "";
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
                    foreach (PackArtPiece piece in FirstPerFace(pieces, ArtFacesShown))
                        artRail.Add(BuildArtSlot(piece));
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

        /// <summary>
        /// The face an art piece belongs to: its id with a trailing <c>_&lt;n&gt;</c> variant
        /// suffix removed, so <c>emberfall_pyra_1</c>…<c>_4</c> all answer <c>emberfall_pyra</c>.
        /// That suffix is the manifest's documented convention for several pieces of one
        /// character (see <see cref="PackArtManifest"/>), which is why matching on it is safe.
        /// An id with no suffix is its own face.
        /// </summary>
        private static string FaceOf(string pieceId)
        {
            if (string.IsNullOrEmpty(pieceId)) return "";
            int split = pieceId.LastIndexOf('_');
            if (split <= 0 || split == pieceId.Length - 1) return pieceId;
            for (int i = split + 1; i < pieceId.Length; i++)
                if (!char.IsDigit(pieceId[i])) return pieceId;
            return pieceId.Substring(0, split);
        }

        /// <summary>
        /// The first piece of each distinct face, in manifest order, up to <paramref name="max"/>.
        /// Manifest order is meaningful, so "first" is the set's own choice of default art.
        /// </summary>
        private static List<PackArtPiece> FirstPerFace(IReadOnlyList<PackArtPiece> pieces, int max)
        {
            var shown = new List<PackArtPiece>(max);
            var seen = new HashSet<string>();
            for (int i = 0; i < pieces.Count && shown.Count < max; i++)
            {
                if (!seen.Add(FaceOf(pieces[i].Id))) continue;
                shown.Add(pieces[i]);
            }
            return shown;
        }

        private static VisualElement BuildArtSlot(PackArtPiece piece)
        {
            // Named for the PIECE, not its file: this is the element name a later gallery or a
            // profile picker would look up, and the id is the half that does not move.
            var slot = new VisualElement { name = piece.Id };
            slot.AddToClassList("shop-pack__art-slot");
            slot.AddToClassList("shop-pack__art-slot--loaded");
            PackWrapperBaker.ApplyTo(slot, piece.Sprite);

            // NO PAINTED CAPTION (Sam, 2026-08-31). The title used to render as a bold plate over
            // the bottom of the art; it covered the part of the picture the crop is composed
            // around and said nothing the player needed at the point of buying a pack. It stays as
            // the tooltip so the slot is not a nameless rectangle — same reason the label existed,
            // minus the paint. A surface that shows art on its own (a gallery) puts the title
            // UNDER the picture rather than restoring this.
            slot.tooltip = piece.Title;
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

        /// <summary>
        /// The status line, and it COLLAPSES when it has nothing to say.
        ///
        /// <para>An empty Label still reserves its box: measured on the Pixel 5a rig, this one held
        /// 32 of a 411-unit panel permanently (pre-#349 units), taken straight off the content area
        /// the sections have to fit inside. That is the same band CharacterRoster collapsed in
        /// <c>32dfdb81</c> for the same reason. The panel is 1080 now, so the 32 costs proportionally
        /// less — but a band that reserves height for nothing is wrong at any scale.</para>
        ///
        /// <para>Null-or-whitespace, not null: <see cref="SetWalletUnavailable"/> and the disconnected
        /// path both clear this by assigning "".</para>
        /// </summary>
        private void SetStatus(string message)
        {
            if (_status == null) return;
            _status.text = message ?? "";
            Toggle(_status, "shop-status--empty", string.IsNullOrWhiteSpace(_status.text));
        }

        /// <summary>Back. The overlay is deliberately left visible: the host decides what
        /// replaces this screen, and hiding it first would show a bare panel for the frame or
        /// two that a scene load takes to arrive.</summary>
        private void Close()
        {
            _reveal.Hide();
            _onClose();
        }

        /// <summary>
        /// Puts exactly one of the two sections on screen and marks the tab that selected it.
        ///
        /// <para>Both halves matter and they are one call on purpose. When the Store stacked both
        /// sections on the 411-unit landscape panel of the day (pre-#349), the packs section alone
        /// came to ~304 and Gem Bundles began below the fold. Tabs replaced that layout, and the obsolete ScrollView was
        /// later retired because it allowed empty-range overscroll. The section that is not selected
        /// still has to cost NO height, which is what
        /// <c>.shop-section--hidden</c>'s `display: none` buys.</para>
        ///
        /// <para>Null-tolerant throughout, like the rest of this controller: it queries its
        /// elements by name and a host is free to supply a tree without them.</para>
        /// </summary>
        private void ShowSection(bool packs)
        {
            Toggle(_packSection, "shop-section--hidden", !packs);
            Toggle(_gemSection, "shop-section--hidden", packs);
            Toggle(_packTab, "shop-tab--on", packs);
            Toggle(_gemTab, "shop-tab--on", !packs);
        }

        private static void Toggle(VisualElement element, string className, bool on)
        {
            if (element == null) return;
            if (on) element.AddToClassList(className);
            else element.RemoveFromClassList(className);
        }

        private static void Wire(VisualElement root, string buttonName, System.Action action)
        {
            Button button = root.Q<Button>(buttonName);
            if (button != null) button.clicked += action;
        }

        private static string FormatUsd(int cents) => $"${cents / 100}.{cents % 100:00}";
    }
}
