using System;
using System.Collections.Generic;
using Emberfall.Core.Collection;
using Emberfall.Net.Client.Collection;
using Emberfall.Net.Client.Shop;
using UnityEngine;
using UnityEngine.UIElements;

namespace Emberfall.Presentation
{
    /// <summary>
    /// Binds the store's catalog and purchase flow. Pricing and placeholder art slots may render
    /// while disconnected; the wallet may be read from the authenticated collection projection,
    /// but purchase success still comes only from <see cref="IShopGateway"/>.
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

        /// <summary>How far into the shell's flight (as a fraction of its resolved transition
        /// duration) the buy buttons reveal -- 90%, not 100% (Sam, 2026-09-08): revealing exactly
        /// at the end raced the shell's own last layout settle and read as a bump. A fraction, not
        /// a fixed ms offset, so it stays right if the CSS duration is ever retuned again.</summary>
        private const float ButtonRevealFraction = 0.9f;

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
        private readonly VisualElement _detailOverlay;
        private readonly VisualElement _detailShell;
        private readonly VisualElement _detailArt;
        private readonly VisualElement _detailActions;
        private VisualElement _detailOriginTile;
        private readonly PackRevealPanel _reveal;
        private readonly ShopFlow _flow;
        private readonly IPackArtProvider _packArt;
        private readonly System.Action _onClose;
        private readonly Func<PackOpenReceipt, bool> _revealElsewhere;

        private bool _busy;
        private int _detailAnimationVersion;
        private bool _detailClosing;

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
            _detailOverlay = root.Q<VisualElement>("pack-detail-overlay");
            _detailShell = root.Q<VisualElement>("pack-detail-shell");
            _detailArt = root.Q<VisualElement>("pack-detail-art");
            _detailActions = root.Q<VisualElement>("pack-detail-actions");
            _detailArt?.RegisterCallback<GeometryChangedEvent>(_ => FitDetailActionsToArt());
            _flow = gateway != null ? new ShopFlow(gateway) : null;
            _packArt = packArt;
            _onClose = onClose;
            _revealElsewhere = revealElsewhere;

            // No onDone: here the reveal is an overlay over a screen the player is still on, so
            // dismissing it means returning to the store, which is what hiding it already does.
            _reveal = new PackRevealPanel(root);

            Wire(root, "btn-shop-close", Close);
            Wire(root, "pack-detail-close", () => CloseDetail());
            // The shell stops its own click from bubbling to the overlay's close-on-scrim handler
            // below -- otherwise every tap on the panel would close it, the same trap CraftModal's
            // craft-scrim comment documents.
            _detailShell?.RegisterCallback<ClickEvent>(evt => evt.StopPropagation());
            _detailOverlay?.RegisterCallback<ClickEvent>(_ => CloseDetail());

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

            // Without a purchase gateway, the scene host separately reads the authenticated
            // collection wallet. Purchase controls remain disabled.
            if (_flow == null) return;

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
            CloseDetail(immediate: true);
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
            if (_gold != null) _gold.text = WalletDisplay.IconAmount(snapshot.Wallet.Gold);
            if (_gems != null) _gems.text = WalletDisplay.IconAmount(snapshot.Wallet.Gems);
            RenderPacks(snapshot.Packs, snapshot.Wallet);
            RenderGemBundles(snapshot.GemBundles);
        }

        /// <summary>Show the read-only account wallet when purchases have no shop gateway. Never
        /// mistake an offline or incomplete collection answer for an authoritative zero.</summary>
        public void RenderCollectionWallet(CollectionSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.HasAuthoritativeWallet)
            {
                SetWalletUnavailable();
                return;
            }

            if (_gold != null) _gold.text = WalletDisplay.IconAmount(snapshot.Wallet.Gold);
            if (_gems != null) _gems.text = WalletDisplay.IconAmount(snapshot.Wallet.Gems);
        }

        private void SetWalletUnavailable()
        {
            if (_gold != null) _gold.text = "—";
            if (_gems != null) _gems.text = "—";
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

                // Trimmed and moved beside the rail, not below it (Sam, 2026-09-07): a full-width
                // caption line under each row was what the absolute bottom-left Back button
                // collided with, and "Pack" is redundant once the word is sitting on a row of pack
                // art anyway. Added first so it lands left of the rail in the tile's row layout.
                var name = new Label(ShortName(offer.DisplayName));
                name.AddToClassList("shop-pack__name");
                tile.Add(name);

                // The rail is added only if the set actually has art. An empty rail is a visible
                // hole in the tile, and a pack whose art has not been drawn yet should read as a
                // pack without a picture, not as a picture that failed to load. Appended after
                // `name` (not Insert(0, ...) any more) so a late-loading rail still lands to its
                // right instead of jumping in front of it.
                _packArt?.LoadPackArt(offer.PackId, pieces =>
                {
                    if (pieces == null || pieces.Count == 0) return;
                    var artRail = new VisualElement();
                    artRail.AddToClassList("shop-pack__art-rail");
                    foreach (PackArtPiece piece in FirstPerFace(pieces, ArtFacesShown))
                        artRail.Add(BuildArtSlot(piece));
                    tile.Add(artRail);
                });

                // THE WHOLE TILE IS THE TAP TARGET (Sam, 2026-09-05): buying moved into
                // #pack-detail-overlay, which OpenDetail populates from this same offer and
                // wallet. wallet is captured per-render, same as BuyButton below already did.
                //
                // The FLIP origin is the specific art slot under the pointer, not the tile
                // (Sam, 2026-09-07 follow-up): the tile is the whole four-wide row, already
                // nearly as wide as the detail shell it flies into, so animating FROM the tile
                // produced a barely-visible flip. Walking up from the click target to the nearest
                // .shop-pack__art-slot finds the one narrow pack image the player actually tapped;
                // a tap that lands on the name label or empty tile space (no slot ancestor) falls
                // back to the tile itself.
                tile.RegisterCallback<ClickEvent>(evt =>
                {
                    VisualElement origin = evt.target as VisualElement;
                    while (origin != null && origin != tile && !origin.ClassListContains("shop-pack__art-slot"))
                        origin = origin.parent;
                    OpenDetail(offer, wallet, origin ?? tile);
                });

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

            // The sliced chrome art already used for Back (shop.uss / ShopScreen.uxml
            // btn-shop-close) and Collection's primary actions (Sam, 2026-09-08). Only two
            // variants exist -- primary (ember) and secondary (slate) -- so Gold gets the one
            // that actually matches its color story and Gems takes the other; there's no third,
            // blue-toned chrome art to give it. shop.uss neutralizes the flat pill background/
            // border these two classes also carry, so the art is what actually paints.
            button.AddToClassList("chrome-button");
            button.AddToClassList(currency == ShopCurrency.Gold
                ? "chrome-button--primary"
                : "chrome-button--secondary");

            bool canAfford = wallet.HasValue
                && (currency == ShopCurrency.Gold
                    ? wallet.Value.Gold >= price
                    : wallet.Value.Gems >= price);
            button.SetEnabled(_flow != null && !_busy && canAfford);
            return button;
        }

        /// <summary>
        /// Populates and shows #pack-detail-overlay for one tile's tap. No title or note label
        /// here (Sam, 2026-09-08): deleted on purpose, since the art plus the two buy buttons'
        /// own currency labels already say everything a pack tile needs to.
        ///
        /// <para>NO FOUR-WIDE ART RAIL HERE (Sam, 2026-09-07 follow-up): repeating the tile's
        /// whole rail at a bigger size just showed the player the same row they had already
        /// tapped. <see cref="_detailArt"/> shows exactly the ONE piece the tap landed on
        /// instead — see the follow-up note below on why that single piece still belongs here.</para>
        ///
        /// <para><b>The shell flies in from the tapped tile and flies back out to it on close</b>
        /// (Sam, 2026-09-07 follow-up: no separate modal object appearing/disappearing -- the pack
        /// itself has to visibly translate to center and scale up, eased out, and reverse on
        /// close). This is FLIP (First-Last-Invert-Play): <see cref="TryComputeFlipTransform"/>
        /// matches the visible art using the shell's untransformed layout, <see cref="FlipToIdentity"/> applies
        /// that with NO transition active (pack-detail-shell--animating is not yet applied, so the
        /// jump is instant and unseen), lets that render for one tick, then turns the transition on
        /// and resets translate/scale to identity -- which the transition system then animates,
        /// because the starting values were already rendered in a prior frame. Doing the class-add
        /// and the value-reset in the same tick as the jump would coalesce into one style
        /// recalculation with nothing in between to transition from, and the shell would just snap
        /// to its final spot with no visible motion. <see cref="FlipFromIdentity"/> runs the same
        /// sequence backwards for close: transition on, translate/scale to the tile-matched values
        /// (this one SHOULD transition, since identity was already on screen), then hide once that
        /// has had time to finish.</para>
        /// </summary>
        private void OpenDetail(PackOffer offer, WalletBalances? wallet, VisualElement originTile)
        {
            if (_detailOverlay == null) return;

            int animationVersion = ++_detailAnimationVersion;
            _detailClosing = false;
            _detailActions?.Clear();
            if (_detailActions != null)
            {
                // Reopening owns the row's layout state too: visibility cannot undo display:none
                // left on a retained panel by the previous close implementation.
                _detailActions.style.display = DisplayStyle.Flex;
                _detailActions.style.width = StyleKeyword.Null;
                _detailActions.Add(BuyButton(offer, ShopCurrency.Gold, wallet));
                _detailActions.Add(BuyButton(offer, ShopCurrency.Gems, wallet));
            }

            // Hidden until the flight is nearly done, not gone (Sam, 2026-09-08): the buy buttons
            // showing mid-flight competed with the art for attention during the flip.
            // FlipToIdentity's onSettled callback below reveals them at ButtonRevealFraction of
            // the way through. visibility, not display: display:none removes the row from layout
            // entirely, so bringing it back at the end forces a fresh layout pass right as the
            // shell settles -- Sam saw that as a bump. visibility:hidden keeps the row's box
            // reserved the whole time, so revealing it is a paint change only, no relayout. No
            // title/note here -- deleted on purpose (Sam): the art and the two currency labels on
            // the buttons already say everything a pack tile needs to.
            if (_detailActions != null) _detailActions.style.visibility = Visibility.Hidden;

            // The one piece actually tapped travels with the shell (Sam, 2026-09-07 follow-up).
            // Copies the slot's already-baked background straight across instead of re-resolving
            // a PackArtPiece/Sprite -- BuildArtSlot already did the (possibly expensive)
            // PackWrapperBaker bake, and the slot's resolved style is that result.
            if (_detailArt != null)
            {
                if (originTile != null && originTile.ClassListContains("shop-pack__art-slot"))
                {
                    _detailArt.style.backgroundImage = originTile.resolvedStyle.backgroundImage;
                    _detailArt.style.backgroundSize = originTile.resolvedStyle.backgroundSize;
                    _detailArt.style.display = DisplayStyle.Flex;
                }
                else
                {
                    _detailArt.style.display = DisplayStyle.None;
                }
            }

            _detailOriginTile = originTile;
            _detailShell?.RemoveFromClassList("pack-detail-shell--animating");
            if (_detailShell != null)
            {
                // Lay out the final panel invisibly before measuring its actual art box.
                _detailShell.style.opacity = 0f;
                _detailShell.style.translate = new Translate(0, 0);
                _detailShell.style.scale = new Scale(Vector3.one);
            }
            _detailOverlay.RemoveFromClassList("pack-detail-overlay--hidden");

            // A geometry event alone can miss repeat opens with unchanged dimensions. Sample
            // on scheduled frames as well, and wait for stable layout before painting the flight.
            WaitForDetailLayout(animationVersion, default, default);
        }

        private void WaitForDetailLayout(int version, Rect previousShell, Rect previousArt)
        {
            if (_detailShell == null) return;
            _detailShell.schedule.Execute(() =>
            {
                if (version != _detailAnimationVersion) return;
                Rect shell = _detailShell.layout;
                Rect art = _detailArt != null ? _detailArt.layout : default;
                FitDetailActionsToArt();
                bool hasArt = _detailArt != null && _detailArt.resolvedStyle.display != DisplayStyle.None;
                if (shell.width > 0f && shell.height > 0f && shell == previousShell &&
                    (!hasArt || (art.width > 0f && art.height > 0f && art == previousArt)))
                {
                    FlipToIdentity(() =>
                    {
                        if (_detailActions != null) _detailActions.style.visibility = Visibility.Visible;
                    });
                    return;
                }
                WaitForDetailLayout(version, shell, art);
            }).ExecuteLater(16);
        }

        private void FitDetailActionsToArt()
        {
            if (_detailActions == null || _detailArt == null ||
                _detailArt.resolvedStyle.display == DisplayStyle.None) return;

            Vector2 box = _detailArt.contentRect.size;
            Vector2 image = BackgroundImageSize(_detailArt.resolvedStyle.backgroundImage, box);
            if (box.x <= 0f || box.y <= 0f || image.x <= 0f || image.y <= 0f) return;

            // Contain leaves empty space beside portrait art. The buttons span the painted
            // image, not that space or the shell, and layout coordinates ignore the flight scale.
            _detailActions.style.width = image.x * Mathf.Min(box.x / image.x, box.y / image.y);
        }

        private static Vector2 BackgroundImageSize(Background background, Vector2 fallback)
        {
            return background.renderTexture != null
                ? new Vector2(background.renderTexture.width, background.renderTexture.height)
                : background.sprite != null ? background.sprite.rect.size
                : background.texture != null ? new Vector2(background.texture.width, background.texture.height)
                : fallback;
        }

        /// <summary>
        /// <paramref name="immediate"/> skips the closing transition: <see cref="Buy"/> calls it
        /// that way because the purchase flow that follows (a network round trip, then
        /// <see cref="PackRevealPanel"/>) already provides the visual break a fly-out transition
        /// exists to give, and layering a second one on top would just be a delay. The close
        /// button and the scrim tap use the default animated close.
        /// </summary>
        private void CloseDetail(bool immediate = false)
        {
            if (_detailOverlay == null || (_detailClosing && !immediate)) return;
            int animationVersion = ++_detailAnimationVersion;
            _detailClosing = true;

            // Hidden again before the flight starts (2026-09-08): the reveal in OpenDetail's
            // onSettled leaves these visible while the shell sits at rest, and the fly-out should
            // read the same as the fly-in -- art only, no buttons racing the shell back to the tile.
            if (_detailActions != null) _detailActions.style.visibility = Visibility.Hidden;

            if (immediate || _detailShell == null || _detailOriginTile == null)
            {
                _detailShell?.RemoveFromClassList("pack-detail-shell--animating");
                _detailOverlay.AddToClassList("pack-detail-overlay--hidden");
                _detailOriginTile = null;
                return;
            }

            FlipFromIdentity();
            // Read off pack-detail-shell--animating's own resolved transition-duration rather
            // than hardcoding a matching number here (Sam, 2026-09-07 follow-up: an earlier
            // hardcoded 280ms was left behind when the CSS duration was tuned up to 1.5s, cutting
            // the close flight off almost immediately). This way the two can never drift apart.
            _detailOverlay.schedule.Execute(() =>
            {
                if (animationVersion != _detailAnimationVersion) return;
                _detailOverlay.AddToClassList("pack-detail-overlay--hidden");
                _detailShell.RemoveFromClassList("pack-detail-shell--animating");
                _detailOriginTile = null;
            }).StartingIn(ResolvedTransitionMs(_detailShell));
        }

        /// <summary>The longest of a VisualElement's currently resolved transition-duration
        /// entries, in whole milliseconds. Used to schedule a follow-up (hiding the element, here)
        /// exactly as long after starting a transition as the transition itself actually lasts,
        /// so the two can never drift out of sync as the CSS duration is tuned.</summary>
        private static int ResolvedTransitionMs(VisualElement element)
        {
            float longestMs = 0f;
            foreach (TimeValue duration in element.resolvedStyle.transitionDuration)
            {
                float ms = duration.unit == TimeUnit.Second ? duration.value * 1000f : duration.value;
                longestMs = Mathf.Max(longestMs, ms);
            }
            return Mathf.CeilToInt(longestMs);
        }

        /// <summary>
        /// Match the painted pack, not its outer panel. Both backgrounds contain the same image,
        /// so their fitted image sizes determine the uniform scale. Layout rects exclude the
        /// shell's animated transform, keeping the reverse calculation stable even mid-flight.
        /// All positions are in the shell parent's coordinates, including the tapped slot.
        /// </summary>
        private bool TryComputeFlipTransform(out Translate translate, out Scale scale)
        {
            translate = default;
            scale = default;
            if (_detailOriginTile == null || _detailShell?.parent == null) return false;

            Rect tileBound = _detailShell.parent.WorldToLocal(_detailOriginTile.worldBound);
            Rect shell = _detailShell.layout;
            if (tileBound.width <= 0f || tileBound.height <= 0f || shell.width <= 0f) return false;

            Rect art = _detailArt != null && _detailArt.resolvedStyle.display != DisplayStyle.None
                ? _detailArt.layout : new Rect(Vector2.zero, shell.size);
            if (art.width <= 0f || art.height <= 0f) return false;

            Vector2 imageSize = BackgroundImageSize(_detailOriginTile.resolvedStyle.backgroundImage, art.size);
            if (imageSize.x <= 0f || imageSize.y <= 0f) return false;

            float tileFit = Mathf.Min(tileBound.width / imageSize.x, tileBound.height / imageSize.y);
            float artFit = Mathf.Min(art.width / imageSize.x, art.height / imageSize.y);
            float scaleFactor = tileFit / artFit;
            Vector2 pivot = shell.size * 0.5f;
            Vector2 scaledArtCenter = shell.position + pivot + (art.center - pivot) * scaleFactor;
            Vector2 offset = tileBound.center - scaledArtCenter;
            translate = new Translate(offset.x, offset.y);
            scale = new Scale(new Vector3(scaleFactor, scaleFactor, 1f));
            return true;
        }

        /// <summary>
        /// The "Play" half of FLIP, forward: jumps the shell (with no transition active) to
        /// exactly overlap the tapped tile, then -- across two LATER, SEPARATE ticks -- turns
        /// the transition on, and only then resets translate/scale to identity.
        ///
        /// <para>THREE steps, not two (2026-09-08, live-diagnosed against the running Editor via
        /// unity-cli eval, sampling <c>resolvedStyle</c> every <c>EditorApplication.update</c>
        /// tick through an open): adding <c>--animating</c> and resetting translate/scale in the
        /// SAME style write does not transition, even once the jump above has already rendered
        /// for many frames -- the sampled shell went straight from the jumped values to identity
        /// in one tick, transition class already true, no interpolated frames in between. Only
        /// once the class-add and the value-reset were split across two separate rendered frames
        /// (add the class alone; wait a tick; change the values) did the sampler show a real
        /// interpolating sequence. UI Toolkit's transition system apparently needs the property's
        /// transition rule to already be active on an EARLIER resolved frame before a value
        /// change on that property will animate -- introducing the rule and changing the value in
        /// the same pass does not count, however long the state before that pass was stable.
        /// <see cref="FlipFromIdentity"/> was never wrong: --animating there is already resolved
        /// from many real frames before Close is tapped, so its single value-write already meets
        /// this requirement.</para>
        /// </summary>
        /// <param name="onSettled">Run once the flight has actually finished landing -- timed off
        /// <see cref="ResolvedTransitionMs"/>, the same way <see cref="CloseDetail"/> times hiding
        /// the overlay after its own flight. <see cref="OpenDetail"/> uses this to reveal the
        /// title/note/buy buttons only once the shell is at rest, not mid-flight.</param>
        private void FlipToIdentity(Action onSettled = null)
        {
            if (_detailShell == null) { onSettled?.Invoke(); return; }
            int animationVersion = _detailAnimationVersion;
            if (TryComputeFlipTransform(out Translate translate, out Scale scale))
            {
                _detailShell.style.translate = translate;
                _detailShell.style.scale = scale;
            }

            _detailShell.schedule.Execute(() =>
            {
                if (animationVersion != _detailAnimationVersion) return;
                _detailShell.style.opacity = 1f;
                // Class only, values unchanged -- this must render on its own before anything
                // reads as a value change, or the transition system has no earlier frame where
                // the rule was already active to compare against.
                _detailShell.AddToClassList("pack-detail-shell--animating");

                _detailShell.schedule.Execute(() =>
                {
                    if (animationVersion != _detailAnimationVersion) return;
                    _detailShell.style.translate = new Translate(0, 0);
                    _detailShell.style.scale = new Scale(Vector3.one);

                    if (onSettled != null)
                    {
                        int delayMs = Mathf.RoundToInt(ResolvedTransitionMs(_detailShell) * ButtonRevealFraction);
                        _detailShell.schedule.Execute(() =>
                        {
                            if (animationVersion == _detailAnimationVersion) onSettled();
                        }).ExecuteLater(delayMs);
                    }
                }).ExecuteLater(16);
            }).ExecuteLater(16);
        }

        /// <summary>The reverse of <see cref="FlipToIdentity"/>: the shell is already sitting at
        /// identity (rendered, on screen) and --animating is already applied from the open, so
        /// setting the tile-matched translate/scale here transitions immediately -- no jump-then-
        /// animate split needed, because there is no "instant" step to protect from an active
        /// transition this time.</summary>
        private void FlipFromIdentity()
        {
            if (_detailShell == null) return;
            if (!TryComputeFlipTransform(out Translate translate, out Scale scale)) return;
            _detailShell.AddToClassList("pack-detail-shell--animating");
            _detailShell.style.translate = translate;
            _detailShell.style.scale = scale;
        }

        /// <summary>The catalog's display names all end in " Pack" (e.g. "Emberfall Pack"); the
        /// detail title already supplies the word via "... cards for your collection", so a
        /// literal "Emberfall Pack cards" would double it. Falls back to the full name for
        /// anything that does not end that way rather than mangling it.</summary>
        private static string ShortName(string displayName)
        {
            const string suffix = " Pack";
            return !string.IsNullOrEmpty(displayName) && displayName.EndsWith(suffix, StringComparison.Ordinal)
                ? displayName.Substring(0, displayName.Length - suffix.Length)
                : displayName;
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
